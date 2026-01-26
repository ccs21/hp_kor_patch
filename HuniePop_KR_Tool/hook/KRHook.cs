using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Reflection;
using UnityEngine;

public static class KRHook
{
    // ====== 설정 ======
    public static bool HideOriginalWhileTyping = true;
    public static bool ShowOriginalIfNoTranslation = true;
    public static float DebounceSeconds = 0.20f;
    public static float StateGCSeconds = 60f;

    // 폰트/표시 튜닝
    public static string[] OSFontCandidates = new string[] {
        "Malgun Gothic", "맑은 고딕", "Arial Unicode MS", "Segoe UI"
    };
    public static int FontSize = 44;
    public static float CharacterSize = 0.07f;
    public static float ZOffset = -0.05f;
    public static Color TextColor = Color.white;

    public static bool FollowLabelVisibility = true;
    // ==================

    static bool _loaded;

    static string _root;
    static string _krDir;
    static string _tsvPath;
    static string _pendingPath;
    static string _errLogPath;

    static readonly Dictionary<string, string> _dict = new Dictionary<string, string>(4096);
    static readonly HashSet<string> _pendingKeys = new HashSet<string>();
    static readonly Dictionary<int, LabelState> _states = new Dictionary<int, LabelState>(512);

    // Unity 4.2 Mono 호환 null 체크(Reflection 타입 op_Inequality 문제 회피)
    static bool IsNull(object o) { return object.ReferenceEquals(o, null); }
    static bool NotNull(object o) { return !object.ReferenceEquals(o, null); }

    // 우리가 Apply(강제 출력) 중일 때 OnSetText 재진입 방지
    [ThreadStatic] static bool _inApply;

    // 캐시된 리플렉션 핸들
    static PropertyInfo _pi_gameObj;
    static FieldInfo _fi_gameObj;
    static PropertyInfo _pi_transform;

    static Font _font;
    static Material _textMat;

    // ====== Assembly-CSharp.LabelObject.SetText에서 호출됨 ======
    public static string OnSetText(object labelObject, string text)
    {
        try
        {
            if (_inApply) return ""; // 원본 텍스트는 숨김
            EnsureLoaded();

            if (IsNull(labelObject))
                return ""; // 원본 숨김

            int id = GetStableId(labelObject);
            LabelState st = GetState(id, labelObject);

            string raw = text ?? "";
            string key = Normalize(RemoveAllColorCodes(raw));
            float now = Time.realtimeSinceStartup;

            // 비우기
            if (key.Length == 0)
            {
                st.LastSeenKey = "";
                st.Dirty = false;
                st.HasCommitted = false;
                st.CommittedKey = "";
                st.CommittedText = "";
                st.LastTouched = now;
                st.NeedsOverlayApply = true; // 오버레이도 비우기
                return "";
            }

            if (!string.Equals(st.LastSeenKey, key, StringComparison.Ordinal))
            {
                st.LastSeenKey = key;
                st.LastChangeTime = now;
                st.Dirty = true;
                st.HasCommitted = false;
                st.CommittedKey = "";
                st.CommittedText = "";
                st.NeedsOverlayApply = true;
            }

            st.LastTouched = now;

            // 타이핑 조각(Hi, Hi , Hi t...) 구간
            if (st.Dirty && (now - st.LastChangeTime) < DebounceSeconds)
            {
                st.TypingText = HideOriginalWhileTyping ? "" : key;
                st.NeedsOverlayApply = true;
                return ""; // 원본은 항상 숨김
            }

            // 안정화되면 커밋
            if (st.Dirty && (now - st.LastChangeTime) >= DebounceSeconds)
            {
                CommitState(st);
            }

            // 오버레이는 Runner에서 그림. 원본은 항상 숨김.
            return "";
        }
        catch (Exception ex)
        {
            LogError("OnSetText", ex, text);
            return ""; // 실패해도 원본 숨김
        }
    }

    // ====== 로딩/초기화 ======
    static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        _root = GetGameRoot();
        _krDir = Path.Combine(_root, "HuniePop_KR");
        _tsvPath = Path.Combine(_krDir, "translations.tsv");
        _pendingPath = Path.Combine(_krDir, "pending.tsv");
        _errLogPath = Path.Combine(_krDir, "krhook_error.log");

        try { if (!Directory.Exists(_krDir)) Directory.CreateDirectory(_krDir); } catch { }

        try { if (File.Exists(_tsvPath)) LoadTSV(_tsvPath); }
        catch (Exception ex) { LogError("LoadTSV", ex, _tsvPath); }

        try { if (File.Exists(_pendingPath)) LoadPendingKeys(_pendingPath); }
        catch (Exception ex) { LogError("LoadPendingKeys", ex, _pendingPath); }

        try { EnsureFont(); }
        catch (Exception ex) { LogError("EnsureFont", ex, ""); }

        EnsureRunner();
    }

    static void EnsureRunner()
    {
        if (NotNull(KRHookRunner.Instance)) return;

        GameObject go = GameObject.Find("KRHookRunner");
        if (IsNull(go)) go = new GameObject("KRHookRunner");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<KRHookRunner>();
    }

    // ✅ Unity 4.2 참조에서 CreateDynamicFontFromOSFont가 없을 수 있으니 "리플렉션"으로 시도
    // 1) Font.CreateDynamicFontFromOSFont(string,int) (있으면)
    // 2) new Font(string) (있으면)
    // 3) Builtin Arial.ttf (fallback)
    static void EnsureFont()
    {
        if (NotNull(_font)) return;

        // 1) CreateDynamicFontFromOSFont via reflection
        MethodInfo miCreateFromOS = typeof(Font).GetMethod(
            "CreateDynamicFontFromOSFont",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new Type[] { typeof(string), typeof(int) },
            null
        );

        if (NotNull(miCreateFromOS))
        {
            for (int i = 0; i < OSFontCandidates.Length; i++)
            {
                string name = OSFontCandidates[i];
                try
                {
                    object f = miCreateFromOS.Invoke(null, new object[] { name, FontSize });
                    Font ff = f as Font;
                    if (NotNull(ff))
                    {
                        _font = ff;
                        break;
                    }
                }
                catch { }
            }
        }

        // 2) new Font(string) 시도 (Unity 버전에 따라 OS 폰트 이름으로 잡히기도 함)
        if (IsNull(_font))
        {
            for (int i = 0; i < OSFontCandidates.Length; i++)
            {
                string name = OSFontCandidates[i];
                try
                {
                    Font ff = new Font(name);
                    if (NotNull(ff))
                    {
                        _font = ff;
                        break;
                    }
                }
                catch { }
            }
        }

        // 3) builtin Arial fallback
        if (IsNull(_font))
        {
            try
            {
                _font = Resources.GetBuiltinResource(typeof(Font), "Arial.ttf") as Font;
            }
            catch { }
        }

        // material 준비 (Instantiate 캐스팅 처리)
        if (NotNull(_font))
        {
            try
            {
                UnityEngine.Object inst = UnityEngine.Object.Instantiate(_font.material);
                _textMat = inst as Material; // ✅ 명시 캐스팅(또는 as)
                Shader sh = Shader.Find("GUI/Text Shader");
                if (NotNull(sh) && NotNull(_textMat)) _textMat.shader = sh;
            }
            catch { }
        }
    }

    static string GetGameRoot()
    {
        try { return Directory.GetParent(Application.dataPath).FullName; }
        catch { return "."; }
    }

    // ====== 라벨별 상태 ======
    static LabelState GetState(int id, object labelObject)
    {
        LabelState st;
        if (!_states.TryGetValue(id, out st))
        {
            st = new LabelState();
            st.Id = id;
            st.LabelObject = labelObject;
            st.LastTouched = Time.realtimeSinceStartup;
            _states[id] = st;
        }
        else
        {
            st.LabelObject = labelObject;
        }
        return st;
    }

    static int GetStableId(object labelObject)
    {
        try
        {
            Type t = labelObject.GetType();

            // 캐시
            if (IsNull(_pi_gameObj) && IsNull(_fi_gameObj) && IsNull(_pi_transform))
            {
                _pi_gameObj = t.GetProperty("gameObj", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fi_gameObj = t.GetField("gameObj", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _pi_transform = t.GetProperty("transform", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (NotNull(_pi_gameObj))
            {
                object v = null;
                try { v = _pi_gameObj.GetValue(labelObject, null); } catch { }
                GameObject go = v as GameObject;
                if (NotNull(go)) return go.GetInstanceID();
            }

            if (NotNull(_fi_gameObj))
            {
                object v = null;
                try { v = _fi_gameObj.GetValue(labelObject); } catch { }
                GameObject go = v as GameObject;
                if (NotNull(go)) return go.GetInstanceID();
            }

            if (NotNull(_pi_transform))
            {
                object v = null;
                try { v = _pi_transform.GetValue(labelObject, null); } catch { }
                Transform tr = v as Transform;
                if (NotNull(tr)) return tr.GetInstanceID();
            }
        }
        catch { }

        return labelObject.GetHashCode();
    }

    static void CommitState(LabelState st)
    {
        string key = st.LastSeenKey ?? "";
        st.Dirty = false;

        string translated;
        bool has = _dict.TryGetValue(key, out translated) && !string.IsNullOrEmpty(translated);

        if (!has)
        {
            AddPending(key);
            translated = ShowOriginalIfNoTranslation ? key : "";
        }

        st.CommittedKey = key;
        st.CommittedText = translated ?? "";
        st.HasCommitted = true;
        st.TypingText = "";
        st.NeedsOverlayApply = true;
    }

    // ====== Runner Tick: TextMesh 오버레이 갱신 ======
    internal static void RunnerTick()
    {
        EnsureLoaded();
        float now = Time.realtimeSinceStartup;

        // 오래된 상태 정리
        if (StateGCSeconds > 0f)
        {
            List<int> remove = null;
            foreach (var kv in _states)
            {
                LabelState st = kv.Value;
                if (IsNull(st)) continue;
                if ((now - st.LastTouched) > StateGCSeconds)
                {
                    if (remove == null) remove = new List<int>();
                    remove.Add(kv.Key);
                }
            }
            if (remove != null)
            {
                for (int i = 0; i < remove.Count; i++)
                {
                    int id = remove[i];
                    LabelState st;
                    if (_states.TryGetValue(id, out st))
                    {
                        DestroyOverlay(st);
                    }
                    _states.Remove(id);
                }
            }
        }

        foreach (var kv in _states)
        {
            LabelState st = kv.Value;
            if (IsNull(st) || IsNull(st.LabelObject)) continue;

            if (st.Dirty && (now - st.LastChangeTime) >= DebounceSeconds)
            {
                CommitState(st);
            }

            if (!st.NeedsOverlayApply) continue;

            string show = "";
            if (st.Dirty && (now - st.LastChangeTime) < DebounceSeconds)
                show = st.TypingText ?? "";
            else if (st.HasCommitted)
                show = st.CommittedText ?? "";

            bool visible = true;
            if (FollowLabelVisibility)
                visible = IsLabelVisible(st.LabelObject);

            ApplyOverlay(st, visible ? show : "");
            st.NeedsOverlayApply = false;
        }
    }

    static bool IsLabelVisible(object labelObject)
    {
        try
        {
            GameObject go = TryGetGameObject(labelObject);
            if (NotNull(go))
            {
                // Unity 버전에 따라 activeSelf/activeInHierarchy가 다를 수 있어 리플렉션으로 체크
                PropertyInfo pAIH = go.GetType().GetProperty("activeInHierarchy", BindingFlags.Public | BindingFlags.Instance);
                if (NotNull(pAIH))
                {
                    object v = null; try { v = pAIH.GetValue(go, null); } catch { }
                    if (v is bool && !((bool)v)) return false;
                }

                PropertyInfo pAS = go.GetType().GetProperty("activeSelf", BindingFlags.Public | BindingFlags.Instance);
                if (NotNull(pAS))
                {
                    object v = null; try { v = pAS.GetValue(go, null); } catch { }
                    if (v is bool && !((bool)v)) return false;
                }
            }

            Type t = labelObject.GetType();

            PropertyInfo pVis = t.GetProperty("visible", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (NotNull(pVis))
            {
                object v = null; try { v = pVis.GetValue(labelObject, null); } catch { }
                if (v is bool && !((bool)v)) return false;
            }

            PropertyInfo pAlpha = t.GetProperty("alpha", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (NotNull(pAlpha))
            {
                object v = null; try { v = pAlpha.GetValue(labelObject, null); } catch { }
                if (v is float && ((float)v) <= 0.001f) return false;
            }

            PropertyInfo pChildA = t.GetProperty("childrenAlpha", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (NotNull(pChildA))
            {
                object v = null; try { v = pChildA.GetValue(labelObject, null); } catch { }
                if (v is float && ((float)v) <= 0.001f) return false;
            }
        }
        catch { }

        return true;
    }

    static GameObject TryGetGameObject(object labelObject)
    {
        if (IsNull(labelObject)) return null;

        try
        {
            Type t = labelObject.GetType();

            if (IsNull(_pi_gameObj) && IsNull(_fi_gameObj) && IsNull(_pi_transform))
            {
                _pi_gameObj = t.GetProperty("gameObj", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fi_gameObj = t.GetField("gameObj", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _pi_transform = t.GetProperty("transform", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (NotNull(_pi_gameObj))
            {
                object v = null; try { v = _pi_gameObj.GetValue(labelObject, null); } catch { }
                return v as GameObject;
            }
            if (NotNull(_fi_gameObj))
            {
                object v = null; try { v = _fi_gameObj.GetValue(labelObject); } catch { }
                return v as GameObject;
            }
            if (NotNull(_pi_transform))
            {
                object v = null; try { v = _pi_transform.GetValue(labelObject, null); } catch { }
                Transform tr = v as Transform;
                if (NotNull(tr)) return tr.gameObject;
            }
        }
        catch { }

        return null;
    }

    static Transform TryGetTransform(object labelObject)
    {
        GameObject go = TryGetGameObject(labelObject);
        if (NotNull(go)) return go.transform;

        try
        {
            Type t = labelObject.GetType();
            PropertyInfo pTr = t.GetProperty("transform", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (NotNull(pTr))
            {
                object v = null; try { v = pTr.GetValue(labelObject, null); } catch { }
                return v as Transform;
            }
        }
        catch { }

        return null;
    }

    static void ApplyOverlay(LabelState st, string text)
    {
        try
        {
            if (IsNull(st)) return;

            Transform parent = TryGetTransform(st.LabelObject);
            if (IsNull(parent)) return;

            EnsureFont();

            if (IsNull(st.OverlayGO))
            {
                GameObject go = new GameObject("KRTextMesh");
                go.transform.parent = parent; // Unity 4.2 방식
                go.transform.localPosition = new Vector3(0f, 0f, ZOffset);
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;

                TextMesh tm = go.AddComponent<TextMesh>();
                tm.text = "";
                tm.fontSize = FontSize;
                tm.characterSize = CharacterSize;
                tm.color = TextColor;
                tm.anchor = TextAnchor.MiddleLeft;
                tm.alignment = TextAlignment.Left;

                if (NotNull(_font))
                {
                    tm.font = _font;
                    MeshRenderer mr = go.GetComponent<MeshRenderer>();
                    if (NotNull(mr))
                    {
                        try
                        {
                            if (NotNull(_textMat)) mr.material = _textMat;
                            else mr.material = _font.material;
                        }
                        catch { }
                    }
                }

                st.OverlayGO = go;
                st.OverlayTM = tm;
            }

            if (IsNull(st.OverlayTM))
                st.OverlayTM = st.OverlayGO.GetComponent<TextMesh>();

            if (NotNull(st.OverlayTM))
                st.OverlayTM.text = text ?? "";

            MeshRenderer rend = null;
            try { rend = st.OverlayGO.GetComponent<MeshRenderer>(); } catch { }

            bool on = !string.IsNullOrEmpty(text);
            if (NotNull(rend)) rend.enabled = on;
            else st.OverlayGO.SetActive(on);
        }
        catch (Exception ex)
        {
            LogError("ApplyOverlay", ex, text);
        }
    }

    static void DestroyOverlay(LabelState st)
    {
        try
        {
            if (IsNull(st)) return;
            if (NotNull(st.OverlayGO))
                UnityEngine.Object.Destroy(st.OverlayGO);

            st.OverlayGO = null;
            st.OverlayTM = null;
        }
        catch { }
    }

    // ====== TSV/Pending ======
    static void LoadTSV(string path)
    {
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith("#")) continue;

            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;

            string k = Normalize(Unescape(line.Substring(0, tab)));
            string v = Unescape(line.Substring(tab + 1));

            if (k.Length == 0) continue;
            _dict[k] = v;
        }
    }

    static void LoadPendingKeys(string path)
    {
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith("#")) continue;

            int tab = line.IndexOf('\t');
            string k = (tab >= 0) ? line.Substring(0, tab) : line;
            k = Normalize(Unescape(k));
            if (k.Length == 0) continue;
            _pendingKeys.Add(k);
        }
    }

    static void AddPending(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (_pendingKeys.Contains(key)) return;

        _pendingKeys.Add(key);
        try { File.AppendAllText(_pendingPath, Escape(key) + "\t\n", Encoding.UTF8); }
        catch (Exception ex) { LogError("AddPending", ex, key); }
    }

    // ====== 유틸 ======
    static string RemoveAllColorCodes(string s)
    {
        if (s == null) return "";
        StringBuilder sb = null;

        int i = 0;
        while (i < s.Length)
        {
            if (i + 9 < s.Length && s[i] == '^' && s[i + 1] == 'C')
            {
                bool ok = true;
                for (int k = 0; k < 8; k++)
                {
                    char c = s[i + 2 + k];
                    if (!IsHex(c)) { ok = false; break; }
                }
                if (ok)
                {
                    if (sb == null) sb = new StringBuilder(s.Length);
                    i += 10;
                    continue;
                }
            }

            if (sb != null) sb.Append(s[i]);
            i++;
        }

        return sb == null ? s : sb.ToString();
    }

    static bool IsHex(char c)
    {
        return (c >= '0' && c <= '9') ||
               (c >= 'a' && c <= 'f') ||
               (c >= 'A' && c <= 'F');
    }

    static string Normalize(string s)
    {
        if (s == null) return "";
        if (s.Length > 0 && s[0] == '\uFEFF') s = s.Substring(1);
        s = s.Replace("\r\n", "\n").Replace("\r", "\n");
        return s.Trim();
    }

    static string Escape(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\").Replace("\n", "\\n");
    }

    static string Unescape(string s)
    {
        if (s == null) return "";
        s = s.Replace("\\n", "\n");
        s = s.Replace("\\\\", "\\");
        return s;
    }

    static void LogError(string where, Exception ex, string sample)
    {
        try
        {
            string dir = _krDir;
            if (string.IsNullOrEmpty(dir))
            {
                dir = Path.Combine(GetGameRoot(), "HuniePop_KR");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }

            string path = _errLogPath;
            if (string.IsNullOrEmpty(path))
                path = Path.Combine(dir, "krhook_error.log");

            var sb = new StringBuilder();
            sb.AppendLine("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ====");
            sb.AppendLine("WHERE: " + where);
            sb.AppendLine(ex.ToString());
            if (!string.IsNullOrEmpty(sample))
            {
                sb.AppendLine("TEXT_SAMPLE:");
                sb.AppendLine(sample.Replace("\r", "\\r").Replace("\n", "\\n"));
            }
            sb.AppendLine();

            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    internal class LabelState
    {
        public int Id;
        public object LabelObject;

        public string LastSeenKey = "";
        public float LastChangeTime;
        public float LastTouched;

        public bool Dirty;
        public bool HasCommitted;

        public string CommittedKey = "";
        public string CommittedText = "";
        public string TypingText = "";

        public bool NeedsOverlayApply;

        public GameObject OverlayGO;
        public TextMesh OverlayTM;
    }
}

public class KRHookRunner : MonoBehaviour
{
    public static KRHookRunner Instance;

    void Awake()
    {
        Instance = this;
        DontDestroyOnLoad(this.gameObject);
    }

    void Update()
    {
        KRHook.RunnerTick();
    }
}
