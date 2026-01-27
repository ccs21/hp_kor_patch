using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Reflection;
using UnityEngine;

/// <summary>
/// HuniePop Unity 4.2.x OnGUI overlay hook.
/// 적용 사항:
/// 1) 숨겨진 UI(타이틀에서 설정/로드 문구 미리 뜸) 필터 강화
///    - activeInHierarchy / renderer.enabled 뿐 아니라
///    - lossyScale(스케일 0) 체크
///    - 카메라 viewport에 실제 걸리는지(b.center) 체크
/// 2) 텍스트가 중앙 UI에서 창 밖으로 튀는 문제 완화
///    - DrawOutlinedLabel: TextAnchor.MiddleCenter (가운데 정렬)
///    - Pivot fallback rect를 "컨테이너" 개념으로 확장(폭/높이 넉넉하게)
/// 3) Unity 4.2 / .NET 3.5의 PropertyInfo op_Equality 문제 회피(ReferenceEquals)
/// 4) 번역 파일: KRHook.dll 옆 translations.ko
/// </summary>
public static class KRHook
{
    // =========================
    // USER SETTINGS
    // =========================
    public static bool HideOriginalWhileTyping = true;
    public static bool ShowOriginalIfNoTranslation = false;
    public static float DebounceSeconds = 0.15f;

    public static int FontSize = 26;
    public static bool Bold = true;
    public static Color TextColor = Color.white;
    public static Color OutlineColor = new Color(0, 0, 0, 0.90f);
    public static int OutlinePx = 1;

    /// <summary>전역 픽셀 오프셋(미세조정)</summary>
    public static Vector2 GlobalOffsetPx = Vector2.zero;

    public static float BoundsPaddingPx = 2f;

    // fallback 컨테이너 크기
    public static float FallbackMaxWidthPx = 1100f;
    public static float FallbackWidthScreenRatio = 0.75f; // 중앙 UI는 넉넉하게
    public static float FallbackMinHeightPx = 90f;        // 선택지/대사 대비

    // =========================
    // DEBUG
    // =========================
    public static bool DebugWatermark = true;
    public static bool DebugCounters = true;
    public static bool DebugTopLeftList = false; // 이제 실제 배치 테스트가 목적이면 false 권장
    public static int DebugTopLeftLines = 12;
    public static bool DebugShowGoName = true;

    // =========================
    // INTERNAL
    // =========================
    static bool _loaded;

    static string _root;
    static string _krDir;
    static string _tsvPath;     // KRHook.dll 옆 translations.ko
    static string _pendingPath; // HuniePop_KR\pending.tsv
    static string _errLogPath;  // HuniePop_KR\krhook_error.log
    static string _dbgLogPath;  // HuniePop_KR\krhook_debug.log

    static readonly Dictionary<string, string> _dict = new Dictionary<string, string>(4096);
    static readonly HashSet<string> _pendingKeys = new HashSet<string>();
    static readonly Dictionary<int, LabelState> _states = new Dictionary<int, LabelState>(512);

    static int _lastLoadedLevel = -1;

    // LabelObject reflection cache
    static PropertyInfo _pi_gameObj;
    static FieldInfo _fi_gameObj;
    static PropertyInfo _pi_transform;

    // GUI styles
    static GUIStyle _dbgStyle;
    static GUIStyle _wmStyle;

    // Debug counters
    static int _dbgDrawn;
    static int _dbgCommitted;
    static int _dbgDirty;

    // =========================
    // HOOK ENTRY
    // =========================
    public static string OnSetText(object labelObject, string text)
    {
        try
        {
            EnsureLoaded();
            if (IsNull(labelObject)) return "";

            int id = GetStableId(labelObject);
            LabelState st = GetState(id, labelObject);

            string raw = text ?? "";
            bool hasCaretColor = ContainsCaretColorCode(raw);

            // ^C000000 같은 코드 제거
            string key = Normalize(RemoveAllColorCodes(raw));
            float now = Time.realtimeSinceStartup;

            if (key.Length == 0)
            {
                st.Reset(now);
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
                st.TypingText = "";
            }

            st.LastTouched = now;
            st.LastSeenFrame = Time.frameCount;

            // 타자기(^C...)는 즉시 커밋(대사 늦게 뜨는 문제 완화)
            if (hasCaretColor)
            {
                // (핵심) 숨겨진 UI면 커밋/펜딩 생성 자체를 막음
                if (!IsVisibleLabel(st.LabelObject))
                    return "";

                if (st.Dirty) CommitState(st);
                st.Dirty = false;
                return ""; // 원본 숨김
            }

            // 일반 텍스트 debounce
            if (st.Dirty && (now - st.LastChangeTime) < DebounceSeconds)
            {
                st.TypingText = HideOriginalWhileTyping ? "" : key;
                return "";
            }

            if (st.Dirty && (now - st.LastChangeTime) >= DebounceSeconds)
            {
                if (!IsVisibleLabel(st.LabelObject))
                    return "";

                CommitState(st);
            }

            return ""; // 원본 숨김
        }
        catch (Exception ex)
        {
            LogError("OnSetText", ex, text ?? "");
            return "";
        }
    }

    // =========================
    // RUNNER (Update / OnGUI)
    // =========================
    internal static void RunnerUpdate()
    {
        EnsureLoaded();

        int level = 0;
        try { level = Application.loadedLevel; } catch { }
        if (_lastLoadedLevel < 0) _lastLoadedLevel = level;

        if (level != _lastLoadedLevel)
        {
            _states.Clear();
            _lastLoadedLevel = level;
        }
    }

    internal static void RunnerOnGUI()
    {
        EnsureLoaded();
        EnsureGUIStyle();

        _dbgDrawn = 0;
        _dbgCommitted = 0;
        _dbgDirty = 0;

        int x0 = 10;
        int y0 = 10;

        if (DebugWatermark)
        {
            GUI.Label(new Rect(x0, y0, Screen.width - 20, 22), "KR HOOK GUI", _wmStyle);
            y0 += 22;
        }

        foreach (var kv in _states)
        {
            var st = kv.Value;
            if (st == null) continue;
            if (st.Dirty) _dbgDirty++;
            if (st.HasCommitted) _dbgCommitted++;
        }

        // 진단용: 위치 무시 리스트
        if (DebugTopLeftList)
        {
            int shown = 0;
            foreach (var kv in _states)
            {
                var st = kv.Value;
                if (st == null) continue;

                string show = GetShowText(st);
                if (string.IsNullOrEmpty(show)) continue;

                string prefix = "[DBG] ";
                if (DebugShowGoName)
                {
                    GameObject g = TryGetGameObject(st.LabelObject);
                    prefix += "(" + (g != null ? g.name : "nullGO") + ") ";
                }

                GUI.Label(new Rect(x0, y0, Screen.width - 20, 100), prefix + show, _dbgStyle);
                y0 += 20;
                shown++;
                if (shown >= DebugTopLeftLines) break;
            }
        }

        float now = Time.realtimeSinceStartup;
        Camera cam = FindBestUICamera();
        if (cam == null)
        {
            if (DebugCounters)
                GUI.Label(new Rect(x0, y0, Screen.width - 20, 22),
                    $"[DBG] No camera found. states={_states.Count}",
                    _dbgStyle);
            return;
        }

        foreach (var kv in _states)
        {
            LabelState st = kv.Value;
            if (st == null || IsNull(st.LabelObject)) continue;

            if (st.Dirty && (now - st.LastChangeTime) >= DebounceSeconds)
            {
                if (!IsVisibleLabel(st.LabelObject))
                    continue;

                CommitState(st);
            }

            string show = GetShowText(st);
            if (string.IsNullOrEmpty(show)) continue;

            // (핵심) 실제로 보이는 UI만 그림
            if (!IsVisibleLabel(st.LabelObject))
                continue;

            GameObject go = TryGetGameObject(st.LabelObject);
            if (go == null) continue;

            Rect r;
            bool ok = TryGetScreenRectFromBounds(go, cam, out r);

            // bounds가 이상하면 pivot 기반 컨테이너로 fallback
            if (!ok || !IsReasonableRect(r))
                r = MakePivotRect(go, cam);

            DrawOutlinedLabel(r, show);
            _dbgDrawn++;
        }

        if (DebugCounters)
        {
            GUI.Label(new Rect(x0, y0, Screen.width - 20, 22),
                $"[DBG] states={_states.Count} dirty={_dbgDirty} committed={_dbgCommitted} drawn={_dbgDrawn} dict={_dict.Count}",
                _dbgStyle);
        }
    }

    static string GetShowText(LabelState st)
    {
        float now = Time.realtimeSinceStartup;

        if (st.Dirty && (now - st.LastChangeTime) < DebounceSeconds)
            return st.TypingText ?? "";

        if (st.HasCommitted)
            return st.CommittedText ?? "";

        return "";
    }

    // =========================
    // LOAD / INIT
    // =========================
    static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        _root = GetGameRoot();
        _krDir = Path.Combine(_root, "HuniePop_KR");

        _tsvPath = Path.Combine(GetThisDllDir(), "translations.ko");
        _pendingPath = Path.Combine(_krDir, "pending.tsv");
        _errLogPath = Path.Combine(_krDir, "krhook_error.log");
        _dbgLogPath = Path.Combine(_krDir, "krhook_debug.log");

        try { if (!Directory.Exists(_krDir)) Directory.CreateDirectory(_krDir); } catch { }

        try { if (File.Exists(_tsvPath)) LoadTSV(_tsvPath); }
        catch (Exception ex) { LogError("LoadTSV", ex, _tsvPath); }

        try { if (File.Exists(_pendingPath)) LoadPendingKeys(_pendingPath); }
        catch (Exception ex) { LogError("LoadPendingKeys", ex, _pendingPath); }

        EnsureRunner();
    }

    static string GetGameRoot()
    {
        try { return Directory.GetParent(Application.dataPath).FullName; }
        catch { return "."; }
    }

    static string GetThisDllDir()
    {
        try
        {
            string loc = Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(loc)) return Path.GetDirectoryName(loc);
        }
        catch { }
        try { return AppDomain.CurrentDomain.BaseDirectory; } catch { }
        return ".";
    }

    static void EnsureRunner()
    {
        if (KRHookRunner.Instance != null) return;

        GameObject go = GameObject.Find("KRHookRunner");
        if (go == null) go = new GameObject("KRHookRunner");
        UnityEngine.Object.DontDestroyOnLoad(go);

        var exist = go.GetComponent<KRHookRunner>();
        if (exist == null) go.AddComponent<KRHookRunner>();
    }

    // =========================
    // VISIBILITY FILTER (강화판)
    // =========================
    static bool IsVisibleLabel(object labelObj)
    {
        GameObject go = TryGetGameObject(labelObj);
        if (go == null) return true; // 못 찾으면 통과(보수적)

        if (!go.activeInHierarchy) return false;

        // Scale 0(또는 거의 0)이면 숨김으로 취급 (tk2d에서 자주 씀)
        Vector3 ls = go.transform.lossyScale;
        if (ls.x < 0.01f || ls.y < 0.01f) return false;

        // Renderer가 꺼져 있으면 숨김
        Renderer r = go.GetComponent<Renderer>();
        if (r == null) r = go.GetComponentInChildren<Renderer>();
        if (r != null && !r.enabled) return false;

        // 카메라 viewport에 실제 걸리는지 체크 (타이틀에서 숨겨진 패널 텍스트 제거에 효과적)
        Camera cam = FindBestUICamera();
        if (cam != null && r != null)
        {
            Bounds b = r.bounds;
            Vector3 v = cam.WorldToViewportPoint(b.center);
            if (v.z < 0.01f) return false;
            if (v.x < -0.2f || v.x > 1.2f || v.y < -0.2f || v.y > 1.2f) return false;
        }

        return true;
    }

    // =========================
    // STATE / COMMIT
    // =========================
    static LabelState GetState(int id, object labelObject)
    {
        LabelState st;
        if (!_states.TryGetValue(id, out st))
        {
            st = new LabelState();
            st.Id = id;
            st.LabelObject = labelObject;
            st.LastTouched = Time.realtimeSinceStartup;
            st.LastSeenFrame = Time.frameCount;
            _states[id] = st;
        }
        else
        {
            st.LabelObject = labelObject;
        }
        return st;
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

    // =========================
    // CAMERA SELECTION
    // =========================
    static Camera FindBestUICamera()
    {
        try
        {
            Camera[] cams = Camera.allCameras;
            if (cams == null || cams.Length == 0) return Camera.main;

            Camera bestOrtho = null;
            float bestDepth = float.NegativeInfinity;

            for (int i = 0; i < cams.Length; i++)
            {
                var c = cams[i];
                if (c == null) continue;
                if (!c.enabled) continue;

                if (c.orthographic && c.depth >= bestDepth)
                {
                    bestDepth = c.depth;
                    bestOrtho = c;
                }
            }
            if (bestOrtho != null) return bestOrtho;

            Camera best = null;
            bestDepth = float.NegativeInfinity;
            for (int i = 0; i < cams.Length; i++)
            {
                var c = cams[i];
                if (c == null) continue;
                if (!c.enabled) continue;

                if (best == null || c.depth > bestDepth)
                {
                    best = c;
                    bestDepth = c.depth;
                }
            }
            if (best != null) return best;

            return Camera.main;
        }
        catch
        {
            return Camera.main;
        }
    }

    // =========================
    // BOUNDS -> SCREEN RECT
    // =========================
    static bool TryGetScreenRectFromBounds(GameObject go, Camera cam, out Rect rect)
    {
        rect = default(Rect);
        if (go == null || cam == null) return false;

        try
        {
            Renderer r = go.GetComponent<Renderer>();
            if (r == null) r = go.GetComponentInChildren<Renderer>();
            if (r == null) return false;

            Bounds b = r.bounds;

            Vector3 min = b.min;
            Vector3 max = b.max;
            Vector3[] corners = new Vector3[8]
            {
                new Vector3(min.x, min.y, min.z),
                new Vector3(max.x, min.y, min.z),
                new Vector3(min.x, max.y, min.z),
                new Vector3(max.x, max.y, min.z),
                new Vector3(min.x, min.y, max.z),
                new Vector3(max.x, min.y, max.z),
                new Vector3(min.x, max.y, max.z),
                new Vector3(max.x, max.y, max.z),
            };

            float minX = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;
            bool any = false;

            for (int i = 0; i < corners.Length; i++)
            {
                Vector3 sp = cam.WorldToScreenPoint(corners[i]);
                if (sp.z <= 0.01f) continue;
                any = true;
                minX = Mathf.Min(minX, sp.x);
                maxX = Mathf.Max(maxX, sp.x);
                minY = Mathf.Min(minY, sp.y);
                maxY = Mathf.Max(maxY, sp.y);
            }

            if (!any) return false;

            float x0 = minX + GlobalOffsetPx.x;
            float x1 = maxX + GlobalOffsetPx.x;
            float y0 = (Screen.height - maxY) + GlobalOffsetPx.y;
            float y1 = (Screen.height - minY) + GlobalOffsetPx.y;

            float pad = Mathf.Max(0f, BoundsPaddingPx);
            x0 -= pad; y0 -= pad;
            x1 += pad; y1 += pad;

            float w = Mathf.Max(2f, x1 - x0);
            float h = Mathf.Max(2f, y1 - y0);
            if (h < FontSize + 10) h = FontSize + 10;

            rect = new Rect(x0, y0, w, h);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static bool IsReasonableRect(Rect r)
    {
        if (r.width < 40 || r.height < 18) return false;
        if (r.width > Screen.width * 0.95f) return false;
        if (r.height > Screen.height * 0.80f) return false;

        if (r.x > Screen.width || r.y > Screen.height) return false;
        if (r.x + r.width < 0 || r.y + r.height < 0) return false;

        return true;
    }

    static Rect MakePivotRect(GameObject go, Camera cam)
    {
        Vector3 sp = cam.WorldToScreenPoint(go.transform.position);
        float x = sp.x + GlobalOffsetPx.x;
        float y = (Screen.height - sp.y) + GlobalOffsetPx.y;

        // 중앙 UI가 많으므로 컨테이너를 넉넉하게
        float w = Mathf.Min(Screen.width * FallbackWidthScreenRatio, FallbackMaxWidthPx);
        float h = Mathf.Max(FontSize * 3.0f, FallbackMinHeightPx);

        // 가운데 정렬이므로 rect 중심을 피벗에 맞추는게 안정적
        return new Rect(x - (w * 0.5f), y - (h * 0.5f), w, h);
    }

    // =========================
    // DRAWING (가운데 정렬)
    // =========================
    static void EnsureGUIStyle()
    {
        if (!ReferenceEquals(_dbgStyle, null)) return;

        int dbgFont = (Screen.height <= 540) ? 14 : 16;

        _dbgStyle = new GUIStyle(GUI.skin.label);
        _dbgStyle.fontSize = dbgFont;
        _dbgStyle.normal.textColor = Color.white;
        _dbgStyle.wordWrap = true;
        _dbgStyle.richText = false;

        _wmStyle = new GUIStyle(GUI.skin.label);
        _wmStyle.fontSize = dbgFont;
        _wmStyle.normal.textColor = new Color(1f, 0.55f, 0.25f, 1f);
        _wmStyle.wordWrap = false;
    }

    static void DrawOutlinedLabel(Rect r, string text)
    {
        GUIStyle baseStyle = new GUIStyle(GUI.skin.label);
        baseStyle.fontSize = FontSize;
        baseStyle.normal.textColor = TextColor;
        baseStyle.wordWrap = true;
        baseStyle.richText = false;
        baseStyle.alignment = TextAnchor.MiddleCenter; // ★가운데 정렬

        if (!Bold || OutlinePx <= 0)
        {
            GUI.Label(r, text, baseStyle);
            return;
        }

        GUIStyle outStyle = new GUIStyle(baseStyle);
        outStyle.normal.textColor = OutlineColor;
        outStyle.alignment = TextAnchor.MiddleCenter; // ★가운데 정렬

        int p = OutlinePx;

        GUI.Label(new Rect(r.x - p, r.y, r.width, r.height), text, outStyle);
        GUI.Label(new Rect(r.x + p, r.y, r.width, r.height), text, outStyle);
        GUI.Label(new Rect(r.x, r.y - p, r.width, r.height), text, outStyle);
        GUI.Label(new Rect(r.x, r.y + p, r.width, r.height), text, outStyle);

        GUI.Label(new Rect(r.x - p, r.y - p, r.width, r.height), text, outStyle);
        GUI.Label(new Rect(r.x + p, r.y - p, r.width, r.height), text, outStyle);
        GUI.Label(new Rect(r.x - p, r.y + p, r.width, r.height), text, outStyle);
        GUI.Label(new Rect(r.x + p, r.y + p, r.width, r.height), text, outStyle);

        GUI.Label(r, text, baseStyle);
    }

    // =========================
    // LabelObject -> GameObject  (op_Equality 회피)
    // =========================
    static int GetStableId(object labelObject)
    {
        try
        {
            Type t = labelObject.GetType();

            if (ReferenceEquals(_pi_gameObj, null) && ReferenceEquals(_fi_gameObj, null) && ReferenceEquals(_pi_transform, null))
            {
                _pi_gameObj = t.GetProperty("gameObj", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fi_gameObj = t.GetField("gameObj", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _pi_transform = t.GetProperty("transform", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (!ReferenceEquals(_pi_gameObj, null))
            {
                object v = null; try { v = _pi_gameObj.GetValue(labelObject, null); } catch { }
                GameObject go = v as GameObject;
                if (!ReferenceEquals(go, null)) return go.GetInstanceID();
            }

            if (!ReferenceEquals(_fi_gameObj, null))
            {
                object v = null; try { v = _fi_gameObj.GetValue(labelObject); } catch { }
                GameObject go = v as GameObject;
                if (!ReferenceEquals(go, null)) return go.GetInstanceID();
            }

            if (!ReferenceEquals(_pi_transform, null))
            {
                object v = null; try { v = _pi_transform.GetValue(labelObject, null); } catch { }
                Transform tr = v as Transform;
                if (!ReferenceEquals(tr, null)) return tr.GetInstanceID();
            }
        }
        catch { }

        return labelObject.GetHashCode();
    }

    static GameObject TryGetGameObject(object labelObject)
    {
        if (ReferenceEquals(labelObject, null)) return null;

        try
        {
            Type t = labelObject.GetType();

            if (ReferenceEquals(_pi_gameObj, null) && ReferenceEquals(_fi_gameObj, null) && ReferenceEquals(_pi_transform, null))
            {
                _pi_gameObj = t.GetProperty("gameObj", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fi_gameObj = t.GetField("gameObj", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _pi_transform = t.GetProperty("transform", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (!ReferenceEquals(_pi_gameObj, null))
            {
                object v = null; try { v = _pi_gameObj.GetValue(labelObject, null); } catch { }
                return v as GameObject;
            }
            if (!ReferenceEquals(_fi_gameObj, null))
            {
                object v = null; try { v = _fi_gameObj.GetValue(labelObject); } catch { }
                return v as GameObject;
            }
            if (!ReferenceEquals(_pi_transform, null))
            {
                object v = null; try { v = _pi_transform.GetValue(labelObject, null); } catch { }
                Transform tr = v as Transform;
                if (!ReferenceEquals(tr, null)) return tr.gameObject;
            }
        }
        catch { }

        return null;
    }

    // =========================
    // TSV / PENDING
    // =========================
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

    // =========================
    // COLOR CODE PARSING
    // =========================
    static bool ContainsCaretColorCode(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;

        for (int i = 0; i + 9 < s.Length; i++)
        {
            if (s[i] != '^') continue;
            if (s[i + 1] != 'C') continue;

            bool ok = true;
            for (int k = 0; k < 8; k++)
            {
                if (!IsHex(s[i + 2 + k])) { ok = false; break; }
            }
            if (ok) return true;
        }
        return false;
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
                    if (!IsHex(s[i + 2 + k])) { ok = false; break; }
                }

                if (ok)
                {
                    if (sb == null)
                    {
                        sb = new StringBuilder(s.Length);
                        if (i > 0) sb.Append(s, 0, i);
                    }
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

    // =========================
    // LOGGING
    // =========================
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

    static bool IsNull(object o)
    {
        if (object.ReferenceEquals(o, null)) return true;
        var uo = o as UnityEngine.Object;
        if (!object.ReferenceEquals(uo, null)) return (uo == null);
        return false;
    }

    internal class LabelState
    {
        public int Id;
        public object LabelObject;

        public string LastSeenKey = "";
        public float LastChangeTime;
        public float LastTouched;
        public int LastSeenFrame;

        public bool Dirty;
        public bool HasCommitted;

        public string CommittedKey = "";
        public string CommittedText = "";
        public string TypingText = "";

        public void Reset(float now)
        {
            LastSeenKey = "";
            Dirty = false;
            HasCommitted = false;
            CommittedKey = "";
            CommittedText = "";
            TypingText = "";
            LastTouched = now;
            LastSeenFrame = Time.frameCount;
        }
    }
}

/// <summary>
/// DontDestroyOnLoad runner.
/// </summary>
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
