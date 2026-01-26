using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Reflection;
using UnityEngine;

public static class KRHook
{
    static bool _loaded;

internal static readonly string[] OSFontCandidates = new string[] {
    "Malgun Gothic", "맑은 고딕", "Arial Unicode MS", "Segoe UI", "Arial"
};


    static Dictionary<string, string> _dict = new Dictionary<string, string>();
    static HashSet<string> _pending = new HashSet<string>();

    static string _root;
    static string _krDir;
    static string _tsvPath;
    static string _pendingPath;
    static string _errLogPath;

    static readonly Dictionary<int, LabelEntry> _labels = new Dictionary<int, LabelEntry>(256);

    public static float DebounceSeconds = 0.25f;
    public static float StaleSeconds = 30.0f;

    // ---- 디버그/상태 ----
    internal static int Debug_SetTextCalls = 0;
    internal static int Debug_SetTextNonEmptyCalls = 0;
    internal static string Debug_LastNonEmpty = "";
    internal static bool Debug_ForceTopLeftMirror = true; // 라벨/좌표 계산 실패해도 좌상단에도 같이 찍기

    // =========================================================
    // patched into LabelObject.SetText(string)
    // =========================================================
    public static string OnSetText(object labelObject, string text)
    {
        try
        {
            Debug_SetTextCalls++;

            EnsureLoaded();

            if (!string.IsNullOrEmpty(text))
            {
                Debug_SetTextNonEmptyCalls++;
                if (string.IsNullOrEmpty(Debug_LastNonEmpty))
                    Debug_LastNonEmpty = text;
            }

            if (string.IsNullOrEmpty(text))
            {
                UpdateLabel(labelObject, "");
                return "";
            }

            string normalized = Normalize(RemoveAllColorCodes(text));
            UpdateLabel(labelObject, normalized);

            return "";
        }
        catch (Exception ex)
        {
            LogError("OnSetText", ex, text);
            return text;
        }
    }

    static void UpdateLabel(object labelObject, string normalizedText)
    {
        Transform tr = TryGetTransform(labelObject);
        int id = GetStableId(labelObject, tr);

        LabelEntry e;
        if (!_labels.TryGetValue(id, out e) || object.ReferenceEquals(e, null))
        {
            e = new LabelEntry();
            _labels[id] = e;
        }

        e.Id = id;
        e.Target = labelObject;
        e.Transform = tr;
        e.LastRaw = normalizedText;
        e.LastUpdate = Time.realtimeSinceStartup;
        e.Dirty = true;
    }

    // =========================================================
    // Init
    // =========================================================
    static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        _root = GetGameRoot();
        _krDir = Path.Combine(_root, "HuniePop_KR");
        _tsvPath = Path.Combine(_krDir, "translations.tsv");
        _pendingPath = Path.Combine(_krDir, "pending.tsv");
        _errLogPath = Path.Combine(_krDir, "krhook_error.log");

        try
        {
            if (!Directory.Exists(_krDir))
                Directory.CreateDirectory(_krDir);
        }
        catch { }

        try
        {
            if (File.Exists(_tsvPath))
                LoadTSV(_tsvPath);
        }
        catch (Exception ex)
        {
            LogError("LoadTSV", ex, _tsvPath);
        }

        try
        {
            if (File.Exists(_pendingPath))
                LoadPendingKeys(_pendingPath);
        }
        catch (Exception ex)
        {
            LogError("LoadPendingKeys", ex, _pendingPath);
        }

        // Runner 생성
        try
        {
            if (object.ReferenceEquals(KRHookRunner.Instance, null))
            {
                GameObject go = new GameObject("KRHookRunner");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<KRHookRunner>();
            }
        }
        catch (Exception ex)
        {
            LogError("CreateRunner", ex, "");
        }
    }

    // =========================================================
    // TSV Load / Pending
    // =========================================================
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

            string k = line.Substring(0, tab);
            string v = line.Substring(tab + 1);

            k = Normalize(Unescape(k));
            v = Unescape(v);

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
            _pending.Add(k);
        }
    }

    static void AddPending(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (_pending.Contains(key)) return;

        _pending.Add(key);

        string safe = Escape(key);
        try
        {
            File.AppendAllText(_pendingPath, safe + "\t\n", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LogError("AddPending", ex, safe);
        }
    }

    // =========================================================
    // Helpers: normalize / color codes
    // =========================================================
    static string RemoveAllColorCodes(string s)
    {
        if (object.ReferenceEquals(s, null)) return "";

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
                    if (sb == null)
                        sb = new StringBuilder(s.Length);

                    i += 10;
                    continue;
                }
            }

            if (sb != null) sb.Append(s[i]);
            i++;
        }

        if (sb == null) return s;
        return sb.ToString();
    }

    static bool IsHex(char c)
    {
        return (c >= '0' && c <= '9') ||
               (c >= 'a' && c <= 'f') ||
               (c >= 'A' && c <= 'F');
    }

    static string Normalize(string s)
    {
        if (object.ReferenceEquals(s, null)) return "";

        if (s.Length > 0 && s[0] == '\uFEFF')
            s = s.Substring(1);

        s = s.Replace("\r\n", "\n").Replace("\r", "\n");
        s = s.Trim();
        return s;
    }

    static string Escape(string s)
    {
        if (object.ReferenceEquals(s, null)) return "";
        return s.Replace("\\", "\\\\").Replace("\n", "\\n");
    }

    static string Unescape(string s)
    {
        if (object.ReferenceEquals(s, null)) return "";
        s = s.Replace("\\n", "\n");
        s = s.Replace("\\\\", "\\");
        return s;
    }

    static string GetGameRoot()
    {
        try { return Directory.GetParent(Application.dataPath).FullName; }
        catch { return "."; }
    }

    // =========================================================
    // Reflection: get transform / stable id
    // =========================================================
    static int GetStableId(object labelObject, Transform tr)
    {
        if (!object.ReferenceEquals(tr, null))
            return tr.GetInstanceID();
        if (!object.ReferenceEquals(labelObject, null))
            return labelObject.GetHashCode();
        return 0;
    }

    static Transform TryGetTransform(object labelObject)
    {
        if (object.ReferenceEquals(labelObject, null)) return null;

        try
        {
            Type t = labelObject.GetType();

            PropertyInfo pTr = t.GetProperty("transform");
            if (!object.ReferenceEquals(pTr, null))
            {
                object v = null;
                try { v = pTr.GetValue(labelObject, null); } catch { }
                Transform tr = v as Transform;
                if (!object.ReferenceEquals(tr, null))
                    return tr;
            }

            FieldInfo fTr = t.GetField("transform");
            if (!object.ReferenceEquals(fTr, null))
            {
                object v = null;
                try { v = fTr.GetValue(labelObject); } catch { }
                Transform tr = v as Transform;
                if (!object.ReferenceEquals(tr, null))
                    return tr;
            }

            PropertyInfo pGo = t.GetProperty("gameObj");
            if (!object.ReferenceEquals(pGo, null))
            {
                object v = null;
                try { v = pGo.GetValue(labelObject, null); } catch { }
                GameObject go = v as GameObject;
                if (!object.ReferenceEquals(go, null))
                    return go.transform;
            }

            FieldInfo fGo = t.GetField("gameObj");
            if (!object.ReferenceEquals(fGo, null))
            {
                object v = null;
                try { v = fGo.GetValue(labelObject); } catch { }
                GameObject go = v as GameObject;
                if (!object.ReferenceEquals(go, null))
                    return go.transform;
            }
        }
        catch (Exception ex)
        {
            LogError("TryGetTransform", ex, "");
        }

        return null;
    }

    // =========================================================
    // Runner uses these
    // =========================================================
    internal static Dictionary<int, LabelEntry> GetLabelMap() { return _labels; }

    internal static string TranslateOrRaw(string key)
    {
        string v;
        if (_dict.TryGetValue(key, out v) && !string.IsNullOrEmpty(v))
            return v;

        AddPending(key);
        return key;
    }

    internal static void CleanupStale()
    {
        float now = Time.realtimeSinceStartup;
        List<int> toRemove = null;

        foreach (var kv in _labels)
        {
            LabelEntry e = kv.Value;
            if (object.ReferenceEquals(e, null)) continue;

            if (now - e.LastUpdate > StaleSeconds)
            {
                if (toRemove == null) toRemove = new List<int>();
                toRemove.Add(kv.Key);
            }
        }

        if (toRemove != null)
        {
            for (int i = 0; i < toRemove.Count; i++)
                _labels.Remove(toRemove[i]);
        }
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

    internal class LabelEntry
    {
        public int Id;
        public object Target;
        public Transform Transform;

        public string LastRaw = "";
        public float LastUpdate;

        public string CommittedKey = "";
        public string CommittedText = "";
        public float LastCommit;

        public bool Dirty;
    }
}

public class KRHookRunner : MonoBehaviour
{
    public static KRHookRunner Instance;

    // GUI는 OnGUI에서만 만들기(유니티 4 안전)
    static bool _guiReady;
    static GUIStyle _style;
    static GUIStyle _outline;
    static Font _font;

    void Awake()
    {
        Instance = this;
        DontDestroyOnLoad(this.gameObject);
    }

    void EnsureGUI()
    {
        if (_guiReady) return;
        _guiReady = true;

        _font = TryCreateFont(28);

        _style = new GUIStyle(GUI.skin.label);
        _style.font = _font;
        _style.fontSize = 28;
        _style.normal.textColor = Color.white;
        _style.wordWrap = true;
        _style.richText = false;

        _outline = new GUIStyle(_style);
        _outline.normal.textColor = Color.black;
    }

    Font TryCreateFont(int size)
    {
        try
        {
            MethodInfo mi = typeof(Font).GetMethod(
                "CreateDynamicFontFromOSFont",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new Type[] { typeof(string), typeof(int) },
                null
            );

            if (!object.ReferenceEquals(mi, null))
            {
                string[] names = KRHook.OSFontCandidates;
                for (int i = 0; i < names.Length; i++)
                {
                    try
                    {
                        object f = mi.Invoke(null, new object[] { names[i], size });
                        Font ff = f as Font;
                        if (!object.ReferenceEquals(ff, null)) return ff;
                    }
                    catch { }
                }
            }
        }
        catch { }

        try { return Resources.GetBuiltinResource(typeof(Font), "Arial.ttf") as Font; }
        catch { return null; }
    }

    void DrawOutlined(Rect r, string text)
    {
        if (object.ReferenceEquals(_style, null))
            return;

        int o = 2;
        if (!object.ReferenceEquals(_outline, null))
        {
            GUI.Label(new Rect(r.x - o, r.y, r.width, r.height), text, _outline);
            GUI.Label(new Rect(r.x + o, r.y, r.width, r.height), text, _outline);
            GUI.Label(new Rect(r.x, r.y - o, r.width, r.height), text, _outline);
            GUI.Label(new Rect(r.x, r.y + o, r.width, r.height), text, _outline);
        }
        GUI.Label(r, text, _style);
    }

    void OnGUI()
    {
        EnsureGUI();

        // ✅ 이건 labels 없어도 무조건 떠야 한다
        DrawOutlined(new Rect(10, 10, 1800, 60),
            "KRHookRunner ONGUI  calls=" + KRHook.Debug_SetTextCalls +
            "  nonEmpty=" + KRHook.Debug_SetTextNonEmptyCalls);

        KRHook.CleanupStale();

        var map = KRHook.GetLabelMap();
        float now = Time.realtimeSinceStartup;

        Camera cam = Camera.main;
        if (object.ReferenceEquals(cam, null))
        {
            Camera[] all = Camera.allCameras;
            if (!object.ReferenceEquals(all, null) && all.Length > 0)
                cam = all[0];
        }

        foreach (var kv in map)
        {
            var e = kv.Value;
            if (object.ReferenceEquals(e, null)) continue;

            if (e.Dirty && (now - e.LastUpdate) >= KRHook.DebounceSeconds)
            {
                e.Dirty = false;

                string key = e.LastRaw ?? "";
                if (key.Length > 0)
                {
                    e.CommittedKey = key;
                    e.CommittedText = KRHook.TranslateOrRaw(key);
                    e.LastCommit = now;
                }
                else
                {
                    e.CommittedKey = "";
                    e.CommittedText = "";
                }
            }

            if (string.IsNullOrEmpty(e.CommittedText))
                continue;

            // 위치 계산 실패해도 좌상단 미러로 찍기
            if (KRHook.Debug_ForceTopLeftMirror)
            {
                DrawOutlined(new Rect(10, 70, 1800, 200), e.CommittedText);
            }

            Vector2 screenPos = new Vector2(20, 250);
            bool hasPos = false;

            if (!object.ReferenceEquals(e.Transform, null) && !object.ReferenceEquals(cam, null))
            {
                Vector3 wp = e.Transform.position;
                Vector3 sp = cam.WorldToScreenPoint(wp);
                if (sp.z > 0.01f)
                {
                    screenPos = new Vector2(sp.x, Screen.height - sp.y);
                    hasPos = true;
                }
            }

            float w = 900f;
            float h = 140f;

            Rect r = hasPos
                ? new Rect(screenPos.x, screenPos.y, w, h)
                : new Rect(20, 250, w, h);

            if (r.x < 0) r.x = 0;
            if (r.y < 0) r.y = 0;
            if (r.x + r.width > Screen.width) r.x = Screen.width - r.width;
            if (r.y + r.height > Screen.height) r.y = Screen.height - r.height;

            DrawOutlined(r, e.CommittedText);
        }
    }
}
