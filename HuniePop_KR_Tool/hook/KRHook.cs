using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

<<<<<<< Updated upstream
public class KRHook : MonoBehaviour
{
    // --------------------------------------------
    // Config
    // --------------------------------------------
    public static bool EnableLog = false;

    // translations.ko 는 DLL과 같은 폴더(Managed)에서만 읽는다.
    static string _dllDir = null;
    static string _dictPath = null;

    // pending.tsv 는 문서 폴더 HuniePop_KR에 저장 (권한/관리 편의)
    static string BaseDir
    {
        get
        {
            try
            {
                string doc = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                return Path.Combine(doc, "HuniePop_KR");
            }
            catch
            {
                return ".";
            }
        }
    }

    static string PendingPath
    {
        get { return Path.Combine(BaseDir, "pending.tsv"); }
    }

    // --------------------------------------------
    // State
    // --------------------------------------------
    static bool _loaded = false;
    static bool _loadFailed = false;

    static Dictionary<string, string> _dict = new Dictionary<string, string>(StringComparer.Ordinal);
    static HashSet<string> _pending = new HashSet<string>(StringComparer.Ordinal);

    // 라벨(텍스트 오브젝트)별 마지막 번역 캐시: 타자기 중에도 번역문을 고정 표시하기 위함
    const int MAX_LABEL_CACHE = 2048;
    static Dictionary<int, string> _lastTranslatedById = new Dictionary<int, string>(512);
    static Dictionary<int, string> _lastKeyById = new Dictionary<int, string>(512);

    // --------------------------------------------
    // Entry point (patched into LabelObject.SetText(string))
    // --------------------------------------------
    public static string OnSetText(object labelObject, string text)
    {
        string raw = text ?? "";
        if (raw.Length == 0) return raw;

        try
        {
            EnsureLoaded();
            if (!_loaded) return raw;

            // HuniePop 텍스트 포맷 안정성: 원문 caret 색상 prefix(있으면)만 유지
            string caretPrefix = ExtractCaretColorPrefix(raw);

            // ^Cxxxxxx / ^Cxxxxxxxx 등 컬러 코드 제거 후 키 생성
            string key = Normalize(RemoveAllColorCodes(raw));
            if (key.Length == 0) return raw;

            int id = GetStableId(labelObject);
=======
/// <summary>
/// HuniePop Unity 4.2.x OnGUI overlay hook.
/// 적용 사항:
/// 1) 숨겨진 UI(타이틀에서 설정/로드 문구 미리 뜸) 필터 강화
///    - activeInHierarchy / renderer.enabled 뿐 아니라
///    - lossyScale(스케일 0) 체크
///    - 카메라 viewport에 실제 걸리는지(b.center) 체크
/// 2) 텍스트가 중앙 UI에서 창 밖으로 튀는 문제 완화
///    - bounds 기반 rect 계산이 비정상일 때 pivot 기반 rect로 fallback
/// </summary>
public static class KRHook
{
    // =========================
    // CONFIG
    // =========================
    public static bool DebugWatermark = false;
    public static bool DebugCounters = false;
    public static bool DebugTopLeftList = false;
    public static bool DebugShowGoName = true;

    public static int DebugTopLeftLines = 12;
    public static float DebounceSeconds = 0.15f;

    // =========================
    // STATE
    // =========================
    static bool _loaded = false;
    static string _root = null;
    static string _krDir = null;
    static string _tsvPath = null;
    static string _pendingPath = null;
    static string _errLogPath = null;
    static string _dbgLogPath = null;

    static int _lastLoadedLevel = -1;

    static Dictionary<int, LabelState> _states = new Dictionary<int, LabelState>(256);

    // translation dict: key -> kr
    static Dictionary<string, string> _dict = new Dictionary<string, string>(8192);
    // pending keys (for later extraction)
    static HashSet<string> _pending = new HashSet<string>();

    // styles
    static GUIStyle _labelStyle;
    static GUIStyle _outlineStyle;
    static GUIStyle _dbgStyle;
    static GUIStyle _wmStyle;

    // debug counters
    static int _dbgDrawn = 0;
    static int _dbgCommitted = 0;
    static int _dbgDirty = 0;

    // =========================
    // HOOK ENTRY (replace TextMesh / UILabel / etc)
    // =========================
    // Example usage from patched game code:
    //   someLabel.text = KRHook.OnSetText(someLabel, text);
    //
    // labelObj can be any object with property/field:
    //   - gameObject
    //   - text
    //   - GetComponent / transform
    //
    public static string OnSetText(object labelObj, string text)
    {
        try
        {
            EnsureLoaded();
            if (IsNull(labelObj)) return "";

            int id = GetStableId(labelObj);
            LabelState st;
            if (!_states.TryGetValue(id, out st))
            {
                st = new LabelState();
                st.Id = id;
                st.LabelObject = labelObj;
                _states[id] = st;
            }
>>>>>>> Stashed changes

            // 1) 완성문장이 들어오면 즉시 번역 + 캐시 갱신
            if (_dict.TryGetValue(key, out string translated) && !string.IsNullOrEmpty(translated))
            {
                _lastKeyById[id] = key;
                _lastTranslatedById[id] = translated;

                // 캐시가 과도하게 커지면 리셋 (단순 안전장치)
                if (_lastTranslatedById.Count > MAX_LABEL_CACHE)
                {
                    _lastTranslatedById.Clear();
                    _lastKeyById.Clear();
                }

                return caretPrefix + translated;
            }

<<<<<<< Updated upstream
            // 2) 타자기 중간 호출: 같은 라벨에서 이전 번역이 있었으면 계속 그 번역을 보여준다
            //    - raw에 ^C가 포함되거나(타자기 마커/색상 코드)
            //    - 이전 키와 현재 키가 prefix 관계면(부분 문자열) 타자기로 판단
            if (_lastTranslatedById.TryGetValue(id, out string lastTr) && !string.IsNullOrEmpty(lastTr))
            {
                _lastKeyById.TryGetValue(id, out string lastKey);

                bool looksLikeTypewriter =
                    raw.IndexOf("^C", StringComparison.Ordinal) >= 0 ||
                    (!string.IsNullOrEmpty(lastKey) && (lastKey.StartsWith(key) || key.StartsWith(lastKey)));

                if (looksLikeTypewriter)
                {
                    return caretPrefix + lastTr;
                }
            }

            // 3) 미번역: pending 저장 후 원문 그대로
            if (key.Length >= 4)
                AddPending(key);

            return raw;
=======
            if (!string.Equals(st.LastSeenRaw, raw, StringComparison.Ordinal))
            {
                st.LastSeenRaw = raw;
                st.LastSeenKey = key;

                // 번역 매핑
                string kr;
                if (!_dict.TryGetValue(key, out kr))
                {
                    // 미번역 저장 (pending.tsv)
                    if (_pending.Add(key))
                        AppendPending(key, raw);

                    kr = ""; // 아직 번역 없음 => 숨김
                }

                st.TypingText = hasCaretColor ? ApplyCaretColor(raw, kr) : kr;
                st.LastChangeTime = now;
                st.Dirty = true;
                st.LastSeenFrame = Time.frameCount;
                st.LastTouched = now;

                // Debounce 경과 이후 커밋은 RunnerOnGUI에서
                return "";
            }

            // 같은 raw가 계속 들어오면 노이즈로 취급
            st.LastSeenFrame = Time.frameCount;
            st.LastTouched = now;

            return ""; // 원본 숨김
>>>>>>> Stashed changes
        }
        catch (Exception ex)
        {
            LogError("OnSetText", ex, raw);
            return raw;
        }
    }

<<<<<<< Updated upstream
    static int GetStableId(object labelObject)
=======
    // =========================
    // RUNNER (Update / OnGUI)
    // =========================
    public static void RunnerUpdate()
>>>>>>> Stashed changes
    {
        if (labelObject == null) return 0;

        // UnityEngine.Object는 인스턴스ID가 가장 안정적
        var uo = labelObject as UnityEngine.Object;
        if (uo != null)
        {
<<<<<<< Updated upstream
            try { return uo.GetInstanceID(); }
            catch { /* fallthrough */ }
=======
            _states.Clear();
            _lastLoadedLevel = level;

            // 씬/레벨 전환 시에는 스냅샷을 강제로 1회 기록
            _forceOverlaySnapshot = true;
>>>>>>> Stashed changes
        }

        // 일반 object는 RuntimeHelpers 해시로 안정적(오버라이드된 GetHashCode 영향 없음)
        return RuntimeHelpers.GetHashCode(labelObject);
    }

<<<<<<< Updated upstream
    // --------------------------------------------
    // Loading
    // --------------------------------------------
=======
    public static void RunnerOnGUI()
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

        // 현재 화면(씬/레벨) 정보
        int curLevel = 0;
        string curScene = "";
        try { curLevel = Application.loadedLevel; } catch { }
        try { curScene = Application.loadedLevelName; } catch { }

        // 이번 프레임에 실제로 그려진 오버레이 항목 스냅샷
        var overlayEntries = new List<OverlayEntry>(64);

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

            // 스냅샷 기록용 (rect/텍스트/이름)
            overlayEntries.Add(new OverlayEntry
            {
                id = st.Id,
                name = (go != null ? go.name : "nullGO"),
                rect = r,
                text = show
            });

            DrawOutlinedLabel(r, show);
            _dbgDrawn++;
        }

        // 변화가 있을 때만 스냅샷 로그를 남김
        MaybeLogOverlaySnapshot(curScene, curLevel, cam, overlayEntries, _forceOverlaySnapshot);
        _forceOverlaySnapshot = false;

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
>>>>>>> Stashed changes
    static void EnsureLoaded()
    {
        if (_loaded || _loadFailed) return;

        try
        {
            Directory.CreateDirectory(BaseDir);

            _dllDir = GetThisDllDir();
            _dictPath = Path.Combine(_dllDir, "translations.ko");

            if (!File.Exists(_dictPath))
            {
                // 경로가 맞다고 했으니 여기서 바로 끝. (원인 파악용 로그만 남김)
                Log("[KRHook] translations.ko not found: " + _dictPath);
                _loaded = true; // 로드는 했지만 dict는 비어있음
                return;
            }

            _dict.Clear();
            using (var sr = new StreamReader(_dictPath, Encoding.UTF8, true))
            {
                while (!sr.EndOfStream)
                {
                    string line = sr.ReadLine();
                    if (string.IsNullOrEmpty(line)) continue;
                    if (line.StartsWith("#")) continue;

                    // 포맷: key\tvalue  또는 key=value
                    string key = null;
                    string val = null;

                    int tab = line.IndexOf('\t');
                    if (tab >= 0)
                    {
                        key = line.Substring(0, tab);
                        val = line.Substring(tab + 1);
                    }
                    else
                    {
                        int eq = line.IndexOf('=');
                        if (eq >= 0)
                        {
                            key = line.Substring(0, eq);
                            val = line.Substring(eq + 1);
                        }
                    }

                    if (key == null) continue;

                    key = key.Trim();
                    if (key.Length == 0) continue;

                    val = val ?? "";
                    val = val.Replace("\\n", "\n");

                    if (_dict.ContainsKey(key))
                        _dict[key] = val;
                    else
                        _dict.Add(key, val);
                }
            }

            Log("[KRHook] Loaded translations: " + _dict.Count);
            _loaded = true;
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            LogError("EnsureLoaded", ex, "");
        }
    }

    static string GetThisDllDir()
    {
        try
        {
            string loc = Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(loc))
                return Path.GetDirectoryName(loc);
        }
        catch { }

        try { return Directory.GetCurrentDirectory(); } catch { }
        return ".";
    }

<<<<<<< Updated upstream
    // --------------------------------------------
    // Pending
    // --------------------------------------------
    static void AddPending(string key)
    {
        try
        {
            if (string.IsNullOrEmpty(key)) return;

            // 중복 방지
            if (_pending.Contains(key)) return;
            _pending.Add(key);

            File.AppendAllText(PendingPath, key + "\n", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LogError("AddPending", ex, key);
        }
    }

    // --------------------------------------------
    // Helpers
    // --------------------------------------------
    static void Log(string msg)
    {
        if (!EnableLog) return;
        try { Debug.Log(msg); } catch { }
    }

    static void LogError(string where, Exception ex, string ctx)
    {
        try
        {
            Debug.Log("[KRHook] " + where + " ERROR: " + ex);
            if (!string.IsNullOrEmpty(ctx))
                Debug.Log("[KRHook] ctx=" + ctx);
        }
        catch { }
=======
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
        try
        {
            GameObject go = TryGetGameObject(labelObj);
            if (go == null) return false;

            // 1) active 상태
            if (!go.activeInHierarchy) return false;

            // 2) scale 0 (부모 포함)
            Vector3 ls = go.transform.lossyScale;
            if (Mathf.Abs(ls.x) < 0.0001f || Mathf.Abs(ls.y) < 0.0001f) return false;

            // 3) renderer enabled (자식 포함)
            Renderer r = go.GetComponent<Renderer>();
            if (r == null) r = go.GetComponentInChildren<Renderer>();
            if (r != null && !r.enabled) return false;

            // 4) 카메라 뷰포트에 걸리는지 (bounds center)
            Camera cam = FindBestUICamera();
            if (cam == null) return true;

            Bounds b;
            if (!TryGetAnyBounds(go, out b)) return true;

            Vector3 v = cam.WorldToViewportPoint(b.center);
            // z<0 => 카메라 뒤
            if (v.z < 0f) return false;
            if (v.x < -0.05f || v.x > 1.05f || v.y < -0.05f || v.y > 1.05f) return false;

            return true;
        }
        catch { return true; }
    }

    static bool TryGetAnyBounds(GameObject go, out Bounds b)
    {
        b = new Bounds(go.transform.position, Vector3.zero);
        try
        {
            Renderer r = go.GetComponent<Renderer>();
            if (r != null)
            {
                b = r.bounds;
                return true;
            }
            Renderer cr = go.GetComponentInChildren<Renderer>();
            if (cr != null)
            {
                b = cr.bounds;
                return true;
            }
        }
        catch { }
        return false;
    }

    // =========================
    // CAMERA CHOICE
    // =========================
    static Camera FindBestUICamera()
    {
        try
        {
            Camera[] cams = Camera.allCameras;
            if (cams == null || cams.Length == 0) return Camera.main;

            Camera best = null;
            float bestScore = float.MinValue;

            for (int i = 0; i < cams.Length; i++)
            {
                Camera c = cams[i];
                if (c == null) continue;
                if (!c.enabled) continue;

                // Orthographic UI 카메라 우선
                float score = 0f;
                score += c.orthographic ? 1000f : 0f;
                score += c.depth * 10f;

                if (score > bestScore)
                {
                    best = c;
                    bestScore = score;
                }
            }

            return best != null ? best : Camera.main;
        }
        catch { return Camera.main; }
    }

    // =========================
    // RECT CALC
    // =========================
    static bool TryGetScreenRectFromBounds(GameObject go, Camera cam, out Rect r)
    {
        r = new Rect(0, 0, 0, 0);
        try
        {
            Bounds b;
            if (!TryGetAnyBounds(go, out b))
                return false;

            Vector3 c = b.center;
            Vector3 e = b.extents;

            // 8 corners
            Vector3[] pts = new Vector3[8];
            pts[0] = cam.WorldToScreenPoint(c + new Vector3(-e.x, -e.y, -e.z));
            pts[1] = cam.WorldToScreenPoint(c + new Vector3(+e.x, -e.y, -e.z));
            pts[2] = cam.WorldToScreenPoint(c + new Vector3(-e.x, +e.y, -e.z));
            pts[3] = cam.WorldToScreenPoint(c + new Vector3(+e.x, +e.y, -e.z));
            pts[4] = cam.WorldToScreenPoint(c + new Vector3(-e.x, -e.y, +e.z));
            pts[5] = cam.WorldToScreenPoint(c + new Vector3(+e.x, -e.y, +e.z));
            pts[6] = cam.WorldToScreenPoint(c + new Vector3(-e.x, +e.y, +e.z));
            pts[7] = cam.WorldToScreenPoint(c + new Vector3(+e.x, +e.y, +e.z));

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            for (int i = 0; i < 8; i++)
            {
                Vector3 p = pts[i];
                minX = Mathf.Min(minX, p.x);
                minY = Mathf.Min(minY, Screen.height - p.y); // GUI y-flip
                maxX = Mathf.Max(maxX, p.x);
                maxY = Mathf.Max(maxY, Screen.height - p.y);
            }

            r = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return true;
        }
        catch { return false; }
    }

    static bool IsReasonableRect(Rect r)
    {
        if (float.IsNaN(r.x) || float.IsNaN(r.y) || float.IsNaN(r.width) || float.IsNaN(r.height)) return false;
        if (r.width < 2f || r.height < 2f) return false;
        if (r.width > Screen.width * 2f || r.height > Screen.height * 2f) return false;
        return true;
    }

    static Rect MakePivotRect(GameObject go, Camera cam)
    {
        try
        {
            Vector3 sp = cam.WorldToScreenPoint(go.transform.position);
            float x = sp.x;
            float y = Screen.height - sp.y;

            // 대충 넉넉한 박스 (wordWrap을 기대)
            float w = Mathf.Min(Screen.width - 20, 900);
            float h = 200;

            return new Rect(x - w * 0.5f, y - h * 0.5f, w, h);
        }
        catch
        {
            return new Rect(0, 0, 900, 200);
        }
    }

    // =========================
    // DRAW
    // =========================
    static void EnsureGUIStyle()
    {
        if (_labelStyle != null) return;

        _labelStyle = new GUIStyle(GUI.skin.label);
        _labelStyle.fontSize = 18;
        _labelStyle.wordWrap = true;
        _labelStyle.richText = true;
        _labelStyle.alignment = TextAnchor.UpperLeft;

        _outlineStyle = new GUIStyle(_labelStyle);
        _outlineStyle.normal.textColor = Color.black;

        _dbgStyle = new GUIStyle(GUI.skin.label);
        _dbgStyle.fontSize = 14;
        _dbgStyle.wordWrap = false;
        _dbgStyle.normal.textColor = Color.yellow;

        _wmStyle = new GUIStyle(GUI.skin.label);
        _wmStyle.fontSize = 14;
        _wmStyle.normal.textColor = Color.cyan;
    }

    static void DrawOutlinedLabel(Rect r, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // outline (4 directions)
        Rect o = r;
        o.x -= 1; GUI.Label(o, text, _outlineStyle);
        o.x += 2; GUI.Label(o, text, _outlineStyle);
        o.x -= 1; o.y -= 1; GUI.Label(o, text, _outlineStyle);
        o.y += 2; GUI.Label(o, text, _outlineStyle);

        GUI.Label(r, text, _labelStyle);
    }

    // =========================
    // STATE COMMIT
    // =========================
    static void CommitState(LabelState st)
    {
        try
        {
            st.CommittedText = st.TypingText ?? "";
            st.HasCommitted = true;
            st.Dirty = false;
        }
        catch { }
    }

    // =========================
    // PENDING / TSV
    // =========================
    static void LoadTSV(string path)
    {
        _dict.Clear();
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        for (int i = 0; i < lines.Length; i++)
        {
            string ln = lines[i];
            if (string.IsNullOrEmpty(ln)) continue;
            if (ln.StartsWith("#")) continue;

            int tab = ln.IndexOf('\t');
            if (tab <= 0) continue;

            string k = ln.Substring(0, tab).Trim();
            string v = ln.Substring(tab + 1);
            if (k.Length == 0) continue;

            _dict[k] = v;
        }
    }

    static void LoadPendingKeys(string path)
    {
        _pending.Clear();
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        for (int i = 0; i < lines.Length; i++)
        {
            string k = lines[i].Trim();
            if (k.Length == 0) continue;
            _pending.Add(k);
        }
    }

    static void AppendPending(string key, string raw)
    {
        try
        {
            if (string.IsNullOrEmpty(_pendingPath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_pendingPath));

            string line = key + "\t" + raw.Replace("\r", "\\r").Replace("\n", "\\n");
            File.AppendAllText(_pendingPath, line + "\n", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LogError("AppendPending", ex, key);
        }
    }

    // =========================
    // TEXT NORMALIZE / COLOR
    // =========================
    static bool ContainsCaretColorCode(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        // crude: ^Cxxxxxx
        int idx = s.IndexOf("^C", StringComparison.Ordinal);
        return idx >= 0;
    }

    static string ApplyCaretColor(string raw, string kr)
    {
        if (string.IsNullOrEmpty(kr)) return "";
        // 원문에 색 코드가 있었다면 그대로 앞에 붙여서 유지
        // (대충) 원문 시작의 ^Cxxxxxx만 가져오기
        int idx = raw.IndexOf("^C", StringComparison.Ordinal);
        if (idx < 0) return kr;
        if (idx + 8 <= raw.Length)
        {
            string head = raw.Substring(idx, 8);
            return head + kr;
        }
        return kr;
    }

    static string RemoveAllColorCodes(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // ^Cxxxxxx 제거
        StringBuilder sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '^' && i + 1 < s.Length && s[i + 1] == 'C')
            {
                // skip ^C + 6 hex
                int skip = 8;
                if (i + skip <= s.Length)
                {
                    i += (skip - 1);
                    continue;
                }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
>>>>>>> Stashed changes
    }

    static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
<<<<<<< Updated upstream
        s = s.Replace("\r\n", "\n").Replace("\r", "\n");
        return s.Trim();
    }

    // ^Cxxxxxx (8 chars) OR ^Cxxxxxxxx (10 chars)
    static string ExtractCaretColorPrefix(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length < 8) return "";
        if (s[0] != '^') return "";
        if (s[1] != 'C' && s[1] != 'c') return "";

        // 6-hex
        bool ok6 = true;
        for (int i = 2; i < 8; i++)
=======
        // trim + collapse whitespace
        s = s.Replace("\r", "").Replace("\n", "\n"); // keep \n
        return s.Trim();
    }

    // =========================
    // LABEL OBJECT HELPERS
    // =========================
    static int GetStableId(object labelObj)
    {
        if (IsNull(labelObj)) return 0;
        try
        {
            UnityEngine.Object uo = labelObj as UnityEngine.Object;
            if (!object.ReferenceEquals(uo, null))
                return uo.GetInstanceID();
        }
        catch { }
        // fallback: hashcode
        return labelObj.GetHashCode();
    }

    static GameObject TryGetGameObject(object labelObj)
    {
        if (IsNull(labelObj)) return null;
        try
        {
            if (labelObj is GameObject) return (GameObject)labelObj;

            // property gameObject
            var t = labelObj.GetType();
            var p = t.GetProperty("gameObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null)
            {
                var v = p.GetValue(labelObj, null) as GameObject;
                if (v != null) return v;
            }

            // field gameObject
            var f = t.GetField("gameObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null)
            {
                var v = f.GetValue(labelObj) as GameObject;
                if (v != null) return v;
            }

            // Component
            var c = labelObj as Component;
            if (!object.ReferenceEquals(c, null)) return c.gameObject;
        }
        catch { }
        return null;
    }

    // =========================
    // DEBUG SNAPSHOT LOG (Scene / Overlay items)
    // =========================
    // 파일: HuniePop_KR/krhook_debug.log
    // - 기동 후/씬 변경/오버레이 항목 변화 시에만 스냅샷을 기록
    // - 프레임마다 로그를 남기지 않도록 "스냅샷 해시" 기반으로 변화 감지
    static ulong _lastOverlaySnapshotHash = 0;
    static string _lastOverlaySceneName = null;
    static int _lastOverlayLevel = int.MinValue;
    static bool _forceOverlaySnapshot = true;

    internal struct OverlayEntry
    {
        public int id;
        public string name;
        public Rect rect;
        public string text;
    }

    static void LogDebug(string msg)
    {
        try
        {
            // 콘솔(Player.log)에도 남김
            try { UnityEngine.Debug.Log(msg); } catch { }

            string dir = _krDir;
            if (string.IsNullOrEmpty(dir))
            {
                dir = Path.Combine(GetGameRoot(), "HuniePop_KR");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }

            string path = _dbgLogPath;
            if (string.IsNullOrEmpty(path))
                path = Path.Combine(dir, "krhook_debug.log");

            File.AppendAllText(path, msg + "\n", Encoding.UTF8);
        }
        catch { }
    }

    // 64-bit FNV-1a (cheap + stable)
    static ulong Fnv1a64(ulong h, string s)
    {
        if (s == null) s = "";
        unchecked
        {
            for (int i = 0; i < s.Length; i++)
            {
                h ^= (byte)s[i];
                h *= 1099511628211UL;
            }
        }
        return h;
    }

    static ulong Fnv1a64(ulong h, int v)
    {
        unchecked
        {
            // little endian 4 bytes
            h ^= (byte)(v & 0xFF); h *= 1099511628211UL;
            h ^= (byte)((v >> 8) & 0xFF); h *= 1099511628211UL;
            h ^= (byte)((v >> 16) & 0xFF); h *= 1099511628211UL;
            h ^= (byte)((v >> 24) & 0xFF); h *= 1099511628211UL;
        }
        return h;
    }

    static string SafeOneLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\r", "\\r").Replace("\n", "\\n");
    }

    static int Q(float f)
    {
        // float 떨림으로 인한 로그 폭주 방지: 1px 단위로 양자화
        return (int)Mathf.Round(f);
    }

    static ulong ComputeOverlaySnapshotHash(string sceneName, int level, Camera cam, List<OverlayEntry> entries)
    {
        unchecked
        {
            ulong h = 1469598103934665603UL;
            h = Fnv1a64(h, sceneName ?? "");
            h = Fnv1a64(h, level);
            h = Fnv1a64(h, cam != null ? (cam.name ?? "") : "nullCam");
            h = Fnv1a64(h, Screen.width);
            h = Fnv1a64(h, Screen.height);
            h = Fnv1a64(h, entries != null ? entries.Count : 0);

            if (entries != null)
            {
                // stable order
                entries.Sort((a, b) =>
                {
                    int c = a.id.CompareTo(b.id);
                    if (c != 0) return c;
                    return string.CompareOrdinal(a.name ?? "", b.name ?? "");
                });

                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    h = Fnv1a64(h, e.id);
                    h = Fnv1a64(h, e.name ?? "");
                    h = Fnv1a64(h, Q(e.rect.x));
                    h = Fnv1a64(h, Q(e.rect.y));
                    h = Fnv1a64(h, Q(e.rect.width));
                    h = Fnv1a64(h, Q(e.rect.height));
                    h = Fnv1a64(h, e.text ?? "");
                }
            }
            return h;
        }
    }

    static void MaybeLogOverlaySnapshot(string sceneName, int level, Camera cam, List<OverlayEntry> entries, bool force)
    {
        try
        {
            ulong h = ComputeOverlaySnapshotHash(sceneName, level, cam, entries);

            bool sceneChanged = (sceneName ?? "") != (_lastOverlaySceneName ?? "");
            bool levelChanged = level != _lastOverlayLevel;

            if (force || sceneChanged || levelChanged || h != _lastOverlaySnapshotHash)
            {
                _lastOverlaySnapshotHash = h;
                _lastOverlaySceneName = sceneName;
                _lastOverlayLevel = level;

                var sb = new StringBuilder();
                sb.AppendLine("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " ====");
                sb.AppendLine("SCENE: " + (sceneName ?? "(unknown)") + "  LEVEL: " + level);
                sb.AppendLine("CAM  : " + (cam != null ? cam.name : "(null)") + "  ORTHO: " + (cam != null && cam.orthographic));
                sb.AppendLine("SCREEN: " + Screen.width + "x" + Screen.height + "  OVERLAYS: " + (entries != null ? entries.Count : 0));

                if (entries != null && entries.Count > 0)
                {
                    for (int i = 0; i < entries.Count; i++)
                    {
                        var e = entries[i];
                        sb.Append(" - [").Append(e.id).Append("] ");
                        sb.Append(e.name ?? "(noname)").Append("  ");
                        sb.Append("rect=(").Append(Q(e.rect.x)).Append(",").Append(Q(e.rect.y)).Append(",").Append(Q(e.rect.width)).Append(",").Append(Q(e.rect.height)).Append(") ");
                        sb.Append("text=\"").Append(SafeOneLine(e.text)).AppendLine("\"");
                    }
                }
                sb.AppendLine();

                LogDebug(sb.ToString().TrimEnd('\n', '\r'));
            }
        }
        catch (Exception ex)
        {
            LogError("MaybeLogOverlaySnapshot", ex, sceneName ?? "");
        }
    }

    // =========================
    // ERROR LOG
    // =========================
    static void LogError(string where, Exception ex, string sample)
    {
        try
>>>>>>> Stashed changes
        {
            if (!IsHex(s[i])) { ok6 = false; break; }
        }
        if (ok6) return s.Substring(0, 8);

        // 8-hex
        if (s.Length >= 10)
        {
            bool ok8 = true;
            for (int i = 2; i < 10; i++)
            {
                if (!IsHex(s[i])) { ok8 = false; break; }
            }
            if (ok8) return s.Substring(0, 10);
        }

        return "";
    }

    static bool IsHex(char c)
    {
        return (c >= '0' && c <= '9')
            || (c >= 'a' && c <= 'f')
            || (c >= 'A' && c <= 'F');
    }

    // ^Cxxxxxx / ^Cxxxxxxxx 제거 (문장 중간 포함 모두 제거)
    static string RemoveAllColorCodes(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // 구현은 기존 방식 유지: 간단 스캔 제거
        // (정규식 사용하면 비용이 커질 수 있어서 수동 파서)
        StringBuilder sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '^' && i + 2 < s.Length && (s[i + 1] == 'C' || s[i + 1] == 'c'))
            {
                // ^C + 6hex or 8hex
                int start = i;
                int j = i + 2;
                int hexCount = 0;
                while (j < s.Length && hexCount < 8 && IsHex(s[j]))
                {
                    j++;
                    hexCount++;
                }

                if (hexCount == 6 || hexCount == 8)
                {
                    // skip this token
                    i = j;
                    continue;
                }
                // not a valid token -> treat as normal chars
                i = start;
            }

<<<<<<< Updated upstream
            sb.Append(s[i]);
            i++;
=======
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

        public string LastSeenRaw;
        public string LastSeenKey;

        public bool Dirty;
        public float LastChangeTime;

        public bool HasCommitted;
        public string CommittedText;
        public string TypingText;

        public float LastTouched;
        public int LastSeenFrame;

        public void Reset(float now)
        {
            Dirty = false;
            HasCommitted = false;
            LastSeenRaw = "";
            LastSeenKey = "";
            CommittedText = "";
            TypingText = "";
            LastTouched = now;
            LastSeenFrame = Time.frameCount;
>>>>>>> Stashed changes
        }
        return sb.ToString();
    }
}
