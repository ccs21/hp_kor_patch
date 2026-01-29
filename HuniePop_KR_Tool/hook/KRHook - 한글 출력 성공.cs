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
    public static float StateGCSeconds = 10f; // OnGUI는 잔상 방지가 중요 -> 짧게

    // 폰트/표시
    public static int FontSize = 26;      // OnGUI 폰트 크기 (화면 해상도 따라 조절)
    public static bool Bold = true;       // "진짜 Bold"는 어려워서 스트로크 흉내로 구현
    public static Color TextColor = Color.white;
    public static Color OutlineColor = new Color(0, 0, 0, 0.9f);
    public static int OutlinePx = 1;      // 1~2 추천

    // 라벨 사라질 때 숨김
    public static bool FollowLabelVisibility = true;

    // UI 카메라 찾기
    public static bool PreferOrthoCamera = true;
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

    static bool IsNull(object o) { return object.ReferenceEquals(o, null); }
    static bool NotNull(object o) { return !object.ReferenceEquals(o, null); }

    [ThreadStatic] static bool _inApply;

    static PropertyInfo _pi_gameObj;
    static FieldInfo _fi_gameObj;
    static PropertyInfo _pi_transform;

    // GUI
    static GUIStyle _style;
    static GUIStyle _styleOutline;

    // ====== Assembly-CSharp.LabelObject.SetText에서 호출됨 ======
    public static string OnSetText(object labelObject, string text)
    {
        try
        {
            if (_inApply) return "";
            EnsureLoaded();

            if (IsNull(labelObject)) return "";

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
                st.VisibleThisFrame = true; // 프레임 내에는 갱신됨
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
            }

            st.LastTouched = now;
            st.VisibleThisFrame = true;

            // 타이핑 조각 구간: 화면에는 숨기거나(기본) 원문을 순간 표시 옵션
            if (st.Dirty && (now - st.LastChangeTime) < DebounceSeconds)
            {
                st.TypingText = HideOriginalWhileTyping ? "" : key;
                return "";
            }

            // 안정화되면 커밋
            if (st.Dirty && (now - st.LastChangeTime) >= DebounceSeconds)
            {
                CommitState(st);
            }

            return "";
        }
        catch (Exception ex)
        {
            LogError("OnSetText", ex, text);
            return "";
        }
    }

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

    static string GetGameRoot()
    {
        try { return Directory.GetParent(Application.dataPath).FullName; }
        catch { return "."; }
    }

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

            if (IsNull(_pi_gameObj) && IsNull(_fi_gameObj) && IsNull(_pi_transform))
            {
                _pi_gameObj = t.GetProperty("gameObj", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fi_gameObj = t.GetField("gameObj", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _pi_transform = t.GetProperty("transform", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (NotNull(_pi_gameObj))
            {
                object v = null; try { v = _pi_gameObj.GetValue(labelObject, null); } catch { }
                GameObject go = v as GameObject;
                if (NotNull(go)) return go.GetInstanceID();
            }

            if (NotNull(_fi_gameObj))
            {
                object v = null; try { v = _fi_gameObj.GetValue(labelObject); } catch { }
                GameObject go = v as GameObject;
                if (NotNull(go)) return go.GetInstanceID();
            }

            if (NotNull(_pi_transform))
            {
                object v = null; try { v = _pi_transform.GetValue(labelObject, null); } catch { }
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
    }

    internal static void RunnerUpdate()
    {
        EnsureLoaded();
        float now = Time.realtimeSinceStartup;

        // 매 프레임 시작: "이번 프레임에 SetText가 호출된 라벨" 표시 리셋
        foreach (var kv in _states)
        {
            kv.Value.VisibleThisFrame = false;
        }

        // GC: SetText가 더 이상 호출되지 않는 라벨은 빨리 제거 (잔상 방지)
        if (StateGCSeconds > 0f)
        {
            List<int> remove = null;
            foreach (var kv in _states)
            {
                LabelState st = kv.Value;
                if ((now - st.LastTouched) > StateGCSeconds)
                {
                    if (remove == null) remove = new List<int>();
                    remove.Add(kv.Key);
                }
            }
            if (remove != null)
            {
                for (int i = 0; i < remove.Count; i++)
                    _states.Remove(remove[i]);
            }
        }
    }

    internal static void RunnerOnGUI()
    {
        EnsureLoaded();
        EnsureGUIStyle();

        Camera cam = FindUICamera();
        if (IsNull(cam)) cam = Camera.main;
        if (IsNull(cam)) return;

        float now = Time.realtimeSinceStartup;

        foreach (var kv in _states)
        {
            LabelState st = kv.Value;
            if (IsNull(st) || IsNull(st.LabelObject)) continue;

            // 타이핑 안정화(혹시 SetText 호출이 멈춰도)
            if (st.Dirty && (now - st.LastChangeTime) >= DebounceSeconds)
                CommitState(st);

            // 표시 문자열 결정
            string show = "";
            if (st.Dirty && (now - st.LastChangeTime) < DebounceSeconds)
                show = st.TypingText ?? "";
            else if (st.HasCommitted)
                show = st.CommittedText ?? "";

            if (string.IsNullOrEmpty(show)) continue;

            if (FollowLabelVisibility && !IsLabelVisible(st.LabelObject))
                continue;

            // Label 위치 -> Screen 좌표
            Transform tr = TryGetTransform(st.LabelObject);
            if (IsNull(tr)) continue;

            Vector3 sp = cam.WorldToScreenPoint(tr.position);
            if (sp.z <= 0.01f) continue; // 카메라 뒤

            // Unity GUI는 좌상단 원점, ScreenPoint는 좌하단 원점 -> Y 뒤집기
            float x = sp.x;
            float y = Screen.height - sp.y;

            // 대략적인 박스 폭: 글자 길이 기반 (정밀은 나중에 조절)
            float maxW = Mathf.Min(Screen.width * 0.48f, 900f);
            float maxH = 500f;

            Rect r = new Rect(x, y, maxW, maxH);

            DrawOutlinedLabel(r, show);
        }
    }

    static void EnsureGUIStyle()
    {
        if (_style != null) return;

        _style = new GUIStyle(GUI.skin.label);
        _style.fontSize = FontSize;
        _style.normal.textColor = TextColor;
        _style.wordWrap = true;
        _style.richText = false;

        _styleOutline = new GUIStyle(_style);
        _styleOutline.normal.textColor = OutlineColor;
    }

    static void DrawOutlinedLabel(Rect r, string text)
    {
        if (!Bold || OutlinePx <= 0)
        {
            GUI.Label(r, text, _style);
            return;
        }

        // 스트로크(외곽선)로 "볼드/가독성" 흉내
        int p = OutlinePx;
        GUI.Label(new Rect(r.x - p, r.y, r.width, r.height), text, _styleOutline);
        GUI.Label(new Rect(r.x + p, r.y, r.width, r.height), text, _styleOutline);
        GUI.Label(new Rect(r.x, r.y - p, r.width, r.height), text, _styleOutline);
        GUI.Label(new Rect(r.x, r.y + p, r.width, r.height), text, _styleOutline);

        // 대각선까지 하면 더 두꺼움
        GUI.Label(new Rect(r.x - p, r.y - p, r.width, r.height), text, _styleOutline);
        GUI.Label(new Rect(r.x + p, r.y - p, r.width, r.height), text, _styleOutline);
        GUI.Label(new Rect(r.x - p, r.y + p, r.width, r.height), text, _styleOutline);
        GUI.Label(new Rect(r.x + p, r.y + p, r.width, r.height), text, _styleOutline);

        GUI.Label(r, text, _style);
    }

    static Camera FindUICamera()
    {
        try
        {
            Camera[] cams = Camera.allCameras;
            if (cams == null || cams.Length == 0) return Camera.main;

            // 1) orthographic 우선
            if (PreferOrthoCamera)
            {
                for (int i = 0; i < cams.Length; i++)
                {
                    if (cams[i] != null && cams[i].orthographic)
                        return cams[i];
                }
            }

            // 2) main
            if (Camera.main != null) return Camera.main;

            // 3) 첫번째
            return cams[0];
        }
        catch { return Camera.main; }
    }

    static bool IsLabelVisible(object labelObject)
    {
        try
        {
            GameObject go = TryGetGameObject(labelObject);
            if (NotNull(go))
            {
                PropertyInfo pAIH = go.GetType().GetProperty("activeInHierarchy", BindingFlags.Public | BindingFlags.Instance);
                if (NotNull(pAIH))
                {
                    object v = null; try { v = pAIH.GetValue(go, null); } catch { }
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

        // 프레임별 표시(잔상 방지에 사용하고 싶으면 확장 가능)
        public bool VisibleThisFrame;
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
        KRHook.RunnerUpdate();
    }

    void OnGUI()
    {
        KRHook.RunnerOnGUI();
    }
}
