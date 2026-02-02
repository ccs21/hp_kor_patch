// FontHook.cs (Unity 4.2 safe / NO System.IO.Path usage)
// - Fix: NEVER use Type == null / != null (avoids System.Type.op_Inequality)
// - Fix: NEVER use Type == Type (avoids System.Type.op_Equality MissingMethodException on old Unity/Mono)
// - Fix: NEVER use System.IO.Path.* (avoids MissingMethodException Path.Combine)
// - Log path fixed to GameRoot (parent of Application.dataPath)
// - Loads BMFont packs from HuniePop_Data\Managed\fonts\ (r16/r18/r20/r26, b20/b22/b30)
// - Rewrites tk2dFontData material/materialInst texture + dictionary

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FontHook
{
    public static class Entry
    {
        private static bool _installed;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            try
            {
                Log.Init();
                Log.I("[Entry] Install ENTER");

                GameObject go = new GameObject("__FontHook_Root");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;

                FontHookRunner runner = go.AddComponent<FontHookRunner>();
                Log.I("[Entry] Runner created");

                // Start()에 의존하지 않고 즉시 부팅
                if (!object.ReferenceEquals(runner, null))
                {
                    runner.Bootstrap();
                }

                Log.I("[Entry] Install LEAVE");
            }
            catch (Exception e)
            {
                try { Log.E("[Entry] Install EX: " + e); } catch { }
            }
        }
    }

    internal static class P
    {
        // Path.Combine 대체: 안전한 경로 결합(Windows 기준)
        public static string Join2(string a, string b)
        {
            if (a == null) a = "";
            if (b == null) b = "";

            a = a.Replace('/', '\\');
            b = b.Replace('/', '\\');

            if (a.Length == 0) return b;
            if (b.Length == 0) return a;

            bool aEnd = a[a.Length - 1] == '\\';
            bool bStart = b[0] == '\\';

            if (aEnd && bStart) return a + b.Substring(1);
            if (!aEnd && !bStart) return a + "\\" + b;
            return a + b;
        }

        public static string Join3(string a, string b, string c)
        {
            return Join2(Join2(a, b), c);
        }

        public static string GetFileNameNoExt(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return "";
            string s = fullPath.Replace('/', '\\');
            int slash = s.LastIndexOf('\\');
            string name = (slash >= 0) ? s.Substring(slash + 1) : s;

            int dot = name.LastIndexOf('.');
            if (dot > 0) return name.Substring(0, dot);
            return name;
        }
    }

    internal static class Log
    {
        private static bool _inited;
        private static string _path;

        public static void Init()
        {
            if (_inited) return;
            _inited = true;

            try
            {
                string root = ResolveGameRoot();
                _path = P.Join2(root, "FontHook_runtime.log");

                Append("==== FontHook start " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ====");
                Append("[I] root=" + root);
                Append("[I] dataPath=" + SafeStr(Application.dataPath));
            }
            catch
            {
                _path = "FontHook_runtime.log";
                try { Append("==== FontHook start (fallback) " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===="); } catch { }
            }
        }

        public static void I(string msg) { Append("[I] " + msg); }
        public static void E(string msg) { Append("[E] " + msg); }

        private static void Append(string msg)
        {
            try
            {
                File.AppendAllText(_path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + msg + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        private static string SafeStr(string s)
        {
            return (s == null) ? "(null)" : s;
        }

        // Application.dataPath = ...\HuniePop_Data
        // GameRoot = parent of that
        private static string ResolveGameRoot()
        {
            try
            {
                string dp = Application.dataPath;
                if (!string.IsNullOrEmpty(dp))
                {
                    string s = dp.Replace('/', '\\');
                    // 끝이 \HuniePop_Data 라고 가정하고 parent 구함
                    int last = s.LastIndexOf('\\');
                    if (last > 0)
                    {
                        return s.Substring(0, last);
                    }
                }
            }
            catch { }

            try { return Directory.GetCurrentDirectory(); } catch { }
            return ".";
        }
    }

    internal class FontHookRunner : MonoBehaviour
    {
        private bool _booted;

        // Avoid re-applying every 2 seconds to the same tk2dFontData instance.
        // Key: UnityEngine.Object instanceID, Value: applied pack key (e.g. r20/b22)
        private Dictionary<int, string> _applied = new Dictionary<int, string>();

        private string _fontsDir;

        private Type _tFontData;
        private Type _tFontChar;

        private Dictionary<string, BMFontPack> _packs = new Dictionary<string, BMFontPack>(StringComparer.OrdinalIgnoreCase);

        
        // --- Diagnostics (dialog freeze / missing glyphs) ---
        private Type _tTextMesh;
        private FieldInfo _fiFontCharDict; // cached field on tk2dFontData that holds Dictionary<int, tk2dFontChar>
        private MethodInfo _miDictContainsKey;
        private bool _pendingGlyphDiag;
        private float _lastGlyphDiagAt;
        private string _lastUnityExceptionSummary;

        private static FontHookRunner _instForLog;
        private static bool _unityLogHooked;
        private static readonly int[] REG_SIZES = new int[] { 16, 18, 20, 26 };
        private static readonly int[] BOLD_SIZES = new int[] { 20, 22, 30 };

        public void Bootstrap()
        {
            if (_booted) return;
            _booted = true;

            try
            {
                Log.I("[Runner] Bootstrap ENTER");

                _fontsDir = ResolveFontsDir();
                Log.I("[Runner] fontsDir=" + _fontsDir);

                _tFontData = FindTypeByName("tk2dFontData");
                _tFontChar = FindTypeByName("tk2dFontChar");

                Log.I("[Runner] tk2dFontData found=" + (!object.ReferenceEquals(_tFontData, null)));
                Log.I("[Runner] tk2dFontChar found=" + (!object.ReferenceEquals(_tFontChar, null)));


                // Hook Unity log so we can capture exceptions even when Player.log is missing.
                InstallUnityLogHook();

                _tTextMesh = FindTypeByName("tk2dTextMesh");
                Log.I("[Runner] tk2dTextMesh found=" + (!object.ReferenceEquals(_tTextMesh, null)));

                if (object.ReferenceEquals(_tFontData, null) || object.ReferenceEquals(_tFontChar, null))
                {
                    Log.E("[Runner] tk2d types missing. Abort.");
                    return;
                }

                LoadAllPacks();
                ApplyAllFontsOnce();

                StartCoroutine(LoopApply());

                Log.I("[Runner] Bootstrap LEAVE");
            }
            catch (Exception e)
            {
                Log.E("[Runner] Bootstrap EX: " + e);
            }
        }

        private IEnumerator LoopApply()
        {
            while (true)
            {
                yield return new WaitForSeconds(2f);

                try
                {
                    bool isLoading = false;
                    try { isLoading = Application.isLoadingLevel; } catch { isLoading = false; }
                    if (isLoading) continue;

                    ApplyAllFontsOnce();
                    TryRunGlyphDiagnostics();
                }
                catch (Exception e)
                {
                    Log.E("[Runner] LoopApply EX: " + e);
                }
            }
        }

        private string ResolveFontsDir()
        {
            try
            {
                // gameRoot = parent of dataPath
                string dp = Application.dataPath; // ...\HuniePop_Data
                if (!string.IsNullOrEmpty(dp))
                {
                    string data = dp.Replace('/', '\\');
                    // root
                    int last = data.LastIndexOf('\\');
                    string root = (last > 0) ? data.Substring(0, last) : data;

                    // root\HuniePop_Data\Managed\fonts
                    string fonts = P.Join3(data, "Managed", "fonts");
                    if (Directory.Exists(fonts)) return fonts;
                }
            }
            catch { }

            // fallback: current\HuniePop_Data\Managed\fonts
            try
            {
                string cur = Directory.GetCurrentDirectory();
                return P.Join3(P.Join2(cur, "HuniePop_Data"), "Managed", "fonts");
            }
            catch { }

            return "HuniePop_Data\\Managed\\fonts";
        }

        private void LoadAllPacks()
        {
            _packs.Clear();

            TryLoadPack("r16");
            TryLoadPack("r18");
            TryLoadPack("r20");
            TryLoadPack("r26");
            TryLoadPack("b20");
            TryLoadPack("b22");
            TryLoadPack("b30");

            Log.I("[Runner] packsLoaded=" + _packs.Count);
        }

        private void TryLoadPack(string key)
        {
            try
            {
                string fnt = P.Join2(_fontsDir, key + ".fnt");
                string png = P.Join2(_fontsDir, key + ".png");

                if (!File.Exists(fnt) || !File.Exists(png))
                {
                    Log.I("[Runner] pack missing: " + key);
                    return;
                }

                BMFontPack pack = BMFontPack.LoadFromFiles(key, fnt, png);
                if (object.ReferenceEquals(pack, null))
                {
                    Log.E("[Runner] pack load failed: " + key);
                    return;
                }

                _packs[key] = pack;
                Log.I("[Runner] pack OK: " + key + " tex=" + pack.Texture.width + "x" + pack.Texture.height + " glyphs=" + pack.Glyphs.Count);
            }
            catch (Exception e)
            {
                Log.E("[Runner] TryLoadPack EX (" + key + "): " + e);
            }
        }

        private void ApplyAllFontsOnce()
        {
            if (_packs.Count == 0) return;

            UnityEngine.Object[] allFonts = null;
            try { allFonts = Resources.FindObjectsOfTypeAll(_tFontData); }
            catch (Exception e)
            {
                Log.E("[Runner] FindObjectsOfTypeAll(tk2dFontData) EX: " + e);
                return;
            }

            int total = (allFonts == null) ? 0 : allFonts.Length;
            int changed = 0;

            for (int i = 0; i < total; i++)
            {
                UnityEngine.Object fontObj = allFonts[i];
                if (object.ReferenceEquals(fontObj, null)) continue;

                try
                {
                    string name = fontObj.name;
                    bool bold = GuessBold(name);
                    int px = GuessPxSize(fontObj, name);

                    string key = SelectPackKey(px, bold);
                    BMFontPack pack;
                    if (!_packs.TryGetValue(key, out pack)) continue;

                    // Skip if already applied the same pack to this instance.
                    int iid = 0;
                    try { iid = fontObj.GetInstanceID(); } catch { iid = 0; }
                    if (iid != 0)
                    {
                        string prev;
                        if (_applied.TryGetValue(iid, out prev) && string.Equals(prev, key, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    bool did = ApplyPackToFontData(fontObj, pack);
                    if (did)
                    {
                        changed++;
                        if (iid != 0) _applied[iid] = key;
                    }

                    if (i == 0)
                        Log.I("[Runner] sample font=" + name + " => key=" + key + " px=" + px + " bold=" + bold);
                }
                catch (Exception e)
                {
                    Log.E("[Runner] ApplyOne EX: " + e);
                }
            }

            Log.I("[Runner] ApplyAllFontsOnce: fonts=" + total + " changed=" + changed);
        }

        private bool ApplyPackToFontData(object fontDataObj, BMFontPack pack)
        {
            try
            {
                // material / materialInst 둘 다 교체
                Material mat = GetFieldOrProp(fontDataObj, "material") as Material;
                if (!object.ReferenceEquals(mat, null))
                {
                    mat.mainTexture = pack.Texture;
                    try { mat.SetTexture("_MainTex", pack.Texture); } catch { }
                }

                Material matInst = GetFieldOrProp(fontDataObj, "materialInst") as Material;
                if (!object.ReferenceEquals(matInst, null))
                {
                    matInst.mainTexture = pack.Texture;
                    try { matInst.SetTexture("_MainTex", pack.Texture); } catch { }
                }

                SetField(fontDataObj, "premultipliedAlpha", false);
                SetField(fontDataObj, "useDictionary", true);
                SetField(fontDataObj, "lineHeight", (float)pack.LineHeight);
                SetField(fontDataObj, "largestWidth", (float)pack.LargestWidth);
                SetField(fontDataObj, "texelSize", new Vector2(1f / (float)pack.ScaleW, 1f / (float)pack.ScaleH));

                MethodInfo miSetDict = fontDataObj.GetType().GetMethod("SetDictionary", BindingFlags.Public | BindingFlags.Instance);
                if (object.ReferenceEquals(miSetDict, null))
                {
                    Log.E("[Runner] SetDictionary not found on tk2dFontData");
                    return false;
                }

                object dictObj = BuildTk2dDict(pack);
                miSetDict.Invoke(fontDataObj, new object[] { dictObj });

                MethodInfo miInit = fontDataObj.GetType().GetMethod("InitDictionary", BindingFlags.Public | BindingFlags.Instance);
                if (!object.ReferenceEquals(miInit, null))
                {
                    miInit.Invoke(fontDataObj, null);
                }

                return true;
            }
            catch (Exception e)
            {
                Log.E("[Runner] ApplyPackToFontData EX: " + e);
                return false;
            }
        }

        private object BuildTk2dDict(BMFontPack pack)
        {
            Type dictType = typeof(Dictionary<,>).MakeGenericType(typeof(int), _tFontChar);
            object dict = Activator.CreateInstance(dictType);

            MethodInfo miAdd = dictType.GetMethod("Add", new Type[] { typeof(int), _tFontChar });

            foreach (KeyValuePair<int, BMFontGlyph> kv in pack.Glyphs)
            {
                int code = kv.Key;
                BMFontGlyph g = kv.Value;

                object ch = Activator.CreateInstance(_tFontChar);

                float p0x = g.xoffset;
                float p1x = g.xoffset + g.width;

                float top = pack.BaseLine - g.yoffset;
                float bottom = top - g.height;

                Vector3 p0 = new Vector3(p0x, top, 0);
                Vector3 p1 = new Vector3(p1x, bottom, 0);
                SetField(ch, "p0", p0);
                SetField(ch, "p1", p1);

                float u0 = (float)g.x / (float)pack.ScaleW;
                float u1 = (float)(g.x + g.width) / (float)pack.ScaleW;
                float vTop = 1f - ((float)g.y / (float)pack.ScaleH);
                float vBottom = 1f - ((float)(g.y + g.height) / (float)pack.ScaleH);

                Vector3 uv0 = new Vector3(u0, vTop, 0);
                Vector3 uv1 = new Vector3(u1, vBottom, 0);
                SetField(ch, "uv0", uv0);
                SetField(ch, "uv1", uv1);

                SetField(ch, "advance", (float)g.xadvance);

                TrySetByte(ch, "flipped", 0);

                if (!object.ReferenceEquals(miAdd, null))
                    miAdd.Invoke(dict, new object[] { code, ch });
            }

            return dict;
        }

        
        // ---------- Unity log capture ----------
        private void InstallUnityLogHook()
        {
            try
            {
                _instForLog = this;

                if (_unityLogHooked) return;
                _unityLogHooked = true;

                // Unity 4.2: Application.RegisterLogCallback(LogCallback callback)
                Application.RegisterLogCallback(OnUnityLog);
                Log.I("[Runner] UnityLogHook installed");
            }
            catch (Exception e)
            {
                Log.E("[Runner] InstallUnityLogHook EX: " + e);
            }
        }

        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            try
            {
                // Always mirror Unity logs into our runtime log file.
                Log.I("[U] " + type.ToString() + ": " + (condition ?? "(null)"));
                if (!string.IsNullOrEmpty(stackTrace))
                    Log.I("[U] " + stackTrace);

                FontHookRunner inst = _instForLog;
                if (object.ReferenceEquals(inst, null)) return;

                // Detect the exact crash pattern: tk2dFontChar dictionary missing a key.
                if (!string.IsNullOrEmpty(condition) && condition.IndexOf("KeyNotFoundException", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    inst._lastUnityExceptionSummary = condition;
                    inst._pendingGlyphDiag = true;
                }
            }
            catch { }
        }

        // ---------- Diagnostics: find which glyph codes are missing ----------
        private void TryRunGlyphDiagnostics()
        {
            try
            {
                if (!_pendingGlyphDiag) return;

                float now = Time.realtimeSinceStartup;
                if (now - _lastGlyphDiagAt < 1.0f) return; // throttle
                _lastGlyphDiagAt = now;
                _pendingGlyphDiag = false;

                DumpMissingGlyphs();
            }
            catch (Exception e)
            {
                Log.E("[Diag] TryRunGlyphDiagnostics EX: " + e);
            }
        }

        private void DumpMissingGlyphs()
        {
            if (object.ReferenceEquals(_tTextMesh, null))
            {
                Log.E("[Diag] tk2dTextMesh type not found; cannot scan texts.");
                return;
            }

            UnityEngine.Object[] allText = null;
            try { allText = Resources.FindObjectsOfTypeAll(_tTextMesh); }
            catch (Exception e)
            {
                Log.E("[Diag] FindObjectsOfTypeAll(tk2dTextMesh) EX: " + e);
                return;
            }

            int total = (allText == null) ? 0 : allText.Length;
            Log.I("[Diag] ===== Missing glyph scan BEGIN (textMeshes=" + total + ") lastEx=" + (_lastUnityExceptionSummary ?? "(null)") + " =====");

            int reportedMeshes = 0;

            for (int i = 0; i < total; i++)
            {
                object tm = allText[i];
                if (object.ReferenceEquals(tm, null)) continue;

                string txt = SafeGetTextFromTextMesh(tm);
                if (string.IsNullOrEmpty(txt)) continue;

                object fontData = SafeGetFontDataFromTextMesh(tm);
                if (object.ReferenceEquals(fontData, null)) continue;

                object dict = SafeGetFontCharDict(fontData);
                if (object.ReferenceEquals(dict, null)) continue;

                List<int> missing = CollectMissingCodes(dict, txt);
                if (missing.Count == 0) continue;

                reportedMeshes++;
                if (reportedMeshes <= 25)
                {
                    string tmName = SafeGetUnityName(tm);
                    string fdName = SafeGetUnityName(fontData);
                    Log.I("[Diag] MISSING in textMesh='" + tmName + "' fontData='" + fdName + "' textLen=" + txt.Length);
                    Log.I("[Diag] textPreview=" + PreviewText(txt, 120));
                    Log.I("[Diag] missingCodes=" + FormatMissing(missing, 80));
                }
            }

            Log.I("[Diag] ===== Missing glyph scan END (reported=" + reportedMeshes + ") =====");
        }

        private List<int> CollectMissingCodes(object dictObj, string text)
        {
            List<int> missing = new List<int>();

            MethodInfo miContains = _miDictContainsKey;
            if (object.ReferenceEquals(miContains, null))
            {
                try { miContains = dictObj.GetType().GetMethod("ContainsKey", new Type[] { typeof(int) }); }
                catch { miContains = null; }
                _miDictContainsKey = miContains;
            }

            for (int i = 0; i < text.Length; i++)
            {
                int code = (int)text[i];

                // Skip common layout control codes. If these are the cause, we still log them later.
                if (code == 10 || code == 13) continue;

                bool has = false;
                try
                {
                    if (!object.ReferenceEquals(miContains, null))
                        has = (bool)miContains.Invoke(dictObj, new object[] { code });
                }
                catch { has = true; }

                if (!has && missing.IndexOf(code) < 0)
                    missing.Add(code);

                if (missing.Count >= 200) break;
            }

            // Also check a few critical codes explicitly (space, tab, etc.)
            int[] critical = new int[] { 32, 9, 10, 13, 8230 }; // space, tab, LF, CR, ellipsis
            for (int k = 0; k < critical.Length; k++)
            {
                int code = critical[k];
                bool has = false;
                try
                {
                    if (!object.ReferenceEquals(miContains, null))
                        has = (bool)miContains.Invoke(dictObj, new object[] { code });
                }
                catch { has = true; }

                if (!has && missing.IndexOf(code) < 0)
                    missing.Add(code);
            }

            return missing;
        }

        private object SafeGetFontCharDict(object fontDataObj)
        {
            try
            {
                EnsureFontCharDictField(fontDataObj.GetType());
                if (!object.ReferenceEquals(_fiFontCharDict, null))
                {
                    object dict = _fiFontCharDict.GetValue(fontDataObj);
                    return dict;
                }
            }
            catch { }
            return null;
        }

        private void EnsureFontCharDictField(Type fontDataType)
        {
            if (!object.ReferenceEquals(_fiFontCharDict, null)) return;
            if (object.ReferenceEquals(fontDataType, null)) return;

            try
            {
                FieldInfo[] fs = fontDataType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                for (int i = 0; i < fs.Length; i++)
                {
                    FieldInfo f = fs[i];
                    if (object.ReferenceEquals(f, null)) continue;

                    Type ft = f.FieldType;
                    if (object.ReferenceEquals(ft, null)) continue;
                    if (!ft.IsGenericType) continue;

                    Type gt = ft.GetGenericTypeDefinition();
                    if (!object.ReferenceEquals(gt, typeof(Dictionary<,>))) continue;

                    Type[] args = ft.GetGenericArguments();
                    if (args == null || args.Length != 2) continue;

                    if (!object.ReferenceEquals(args[0], typeof(int))) continue;
                    if (object.ReferenceEquals(_tFontChar, null)) continue;
                    if (!object.ReferenceEquals(args[1], _tFontChar)) continue;

                    _fiFontCharDict = f;
                    Log.I("[Diag] Found tk2dFontData charDict field: " + f.Name);
                    return;
                }
            }
            catch { }
        }

        private object SafeGetFontDataFromTextMesh(object textMeshObj)
        {
            // Common tk2dTextMesh fields/properties: font / fontData
            object fd = GetFieldOrProp(textMeshObj, "font");
            if (!object.ReferenceEquals(fd, null)) return fd;

            fd = GetFieldOrProp(textMeshObj, "fontData");
            if (!object.ReferenceEquals(fd, null)) return fd;

            fd = GetFieldOrProp(textMeshObj, "_font");
            if (!object.ReferenceEquals(fd, null)) return fd;

            return null;
        }

        private string SafeGetTextFromTextMesh(object textMeshObj)
        {
            object t = GetFieldOrProp(textMeshObj, "text");
            if (!object.ReferenceEquals(t, null) && t is string) return (string)t;

            t = GetFieldOrProp(textMeshObj, "_text");
            if (!object.ReferenceEquals(t, null) && t is string) return (string)t;

            return null;
        }

        private string SafeGetUnityName(object obj)
        {
            try
            {
                UnityEngine.Object uo = obj as UnityEngine.Object;
                if (!object.ReferenceEquals(uo, null)) return uo.name;
            }
            catch { }
            return obj.GetType().Name;
        }

        private string PreviewText(string s, int max)
        {
            if (s == null) return "(null)";
            s = s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "...";
        }

        private string FormatMissing(List<int> codes, int maxItems)
        {
            if (codes == null || codes.Count == 0) return "(none)";
            StringBuilder sb = new StringBuilder();
            int n = Math.Min(codes.Count, maxItems);
            for (int i = 0; i < n; i++)
            {
                int c = codes[i];
                if (i > 0) sb.Append(", ");
                sb.Append(c.ToString());
                sb.Append("(0x");
                sb.Append(c.ToString("X"));
                sb.Append(")");
                sb.Append(":'");
                sb.Append(CodeToPrintable(c));
                sb.Append("'");
            }
            if (codes.Count > n) sb.Append(" ...");
            return sb.ToString();
        }

        private char CodeToPrintable(int code)
        {
            try
            {
                if (code < 32) return '?';
                if (code > 0xFFFF) return '?';
                return (char)code;
            }
            catch { return '?'; }
        }


        // ---------- Matching ----------
        private string SelectPackKey(int px, bool bold)
        {
            int sel = ClosestSize(px, bold ? BOLD_SIZES : REG_SIZES);
            return (bold ? "b" : "r") + sel.ToString();
        }

        private int ClosestSize(int target, int[] sizes)
        {
            if (sizes == null || sizes.Length == 0) return target;
            int best = sizes[0];
            int bestDiff = Math.Abs(best - target);
            for (int i = 1; i < sizes.Length; i++)
            {
                int d = Math.Abs(sizes[i] - target);
                if (d < bestDiff)
                {
                    bestDiff = d;
                    best = sizes[i];
                }
            }
            return best;
        }

        private bool GuessBold(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return (n.IndexOf("bold") >= 0) || (n.IndexOf("demi") >= 0) || (n.IndexOf("extra") >= 0) || (n.IndexOf("black") >= 0);
        }

        private int GuessPxSize(object fontDataObj, string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                Match m = Regex.Match(name, @"(\d+)\s*px", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    int v;
                    if (int.TryParse(m.Groups[1].Value, out v)) return v;
                }
            }

            object lh = GetFieldOrProp(fontDataObj, "lineHeight");
            if (!object.ReferenceEquals(lh, null))
            {
                if (lh is float)
                {
                    int v = Mathf.RoundToInt((float)lh);
                    if (v > 0) return v;
                }
                if (lh is int)
                {
                    int v = (int)lh;
                    if (v > 0) return v;
                }
            }

            return 20;
        }

        // ---------- Reflection helpers ----------
        private Type FindTypeByName(string simpleName)
        {
            try
            {
                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    Assembly asm = asms[i];
                    Type[] types = null;
                    try { types = asm.GetTypes(); } catch { continue; }
                    if (object.ReferenceEquals(types, null)) continue;

                    for (int j = 0; j < types.Length; j++)
                    {
                        Type t = types[j];
                        if (object.ReferenceEquals(t, null)) continue;
                        if (t.Name == simpleName) return t;
                    }
                }
            }
            catch { }
            return null;
        }

        private object GetFieldOrProp(object obj, string name)
        {
            if (object.ReferenceEquals(obj, null)) return null;
            Type t = obj.GetType();

            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (!object.ReferenceEquals(f, null))
            {
                try { return f.GetValue(obj); } catch { }
            }

            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (!object.ReferenceEquals(p, null) && p.CanRead)
            {
                try { return p.GetValue(obj, null); } catch { }
            }

            return null;
        }

        private void SetField(object obj, string name, object value)
        {
            if (object.ReferenceEquals(obj, null)) return;
            Type t = obj.GetType();

            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (!object.ReferenceEquals(f, null))
            {
                try { f.SetValue(obj, value); } catch { }
                return;
            }

            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (!object.ReferenceEquals(p, null) && p.CanWrite)
            {
                try { p.SetValue(obj, value, null); } catch { }
            }
        }

        private void TrySetByte(object obj, string name, byte val)
        {
            try
            {
                FieldInfo f = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                // IMPORTANT(Unity 4.2/old Mono): never use Type == Type (can compile to System.Type.op_Equality).
                if (!object.ReferenceEquals(f, null) && object.ReferenceEquals(f.FieldType, typeof(byte)))
                    f.SetValue(obj, val);
            }
            catch { }
        }
    }

    // ---------------- BMFont Loader ----------------

    internal class BMFontPack
    {
        public string Key;
        public Texture2D Texture;

        public int LineHeight;
        public int BaseLine;
        public int ScaleW;
        public int ScaleH;
        public int LargestWidth;

        public Dictionary<int, BMFontGlyph> Glyphs = new Dictionary<int, BMFontGlyph>();

        public static BMFontPack LoadFromFiles(string key, string fntPath, string pngPath)
        {
            BMFontPack pack = new BMFontPack();
            pack.Key = key;

            string txt = File.ReadAllText(fntPath, Encoding.UTF8);

            using (StringReader sr = new StringReader(txt))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.StartsWith("common "))
                    {
                        Dictionary<string, string> kv = ParseKVs(line);
                        pack.LineHeight = GetInt(kv, "lineHeight", 0);
                        pack.BaseLine = GetInt(kv, "base", pack.LineHeight);
                        pack.ScaleW = GetInt(kv, "scaleW", 0);
                        pack.ScaleH = GetInt(kv, "scaleH", 0);
                    }
                    else if (line.StartsWith("char "))
                    {
                        Dictionary<string, string> kv = ParseKVs(line);
                        BMFontGlyph g = new BMFontGlyph();
                        g.id = GetInt(kv, "id", -1);
                        g.x = GetInt(kv, "x", 0);
                        g.y = GetInt(kv, "y", 0);
                        g.width = GetInt(kv, "width", 0);
                        g.height = GetInt(kv, "height", 0);
                        g.xoffset = GetInt(kv, "xoffset", 0);
                        g.yoffset = GetInt(kv, "yoffset", 0);
                        g.xadvance = GetInt(kv, "xadvance", g.width);

                        if (g.id >= 0)
                        {
                            pack.Glyphs[g.id] = g;
                            if (g.width > pack.LargestWidth) pack.LargestWidth = g.width;
                        }
                    }
                }
            }

            pack.Texture = LoadPngTexture(pngPath);
            if (object.ReferenceEquals(pack.Texture, null)) return null;

            if (pack.ScaleW <= 0) pack.ScaleW = pack.Texture.width;
            if (pack.ScaleH <= 0) pack.ScaleH = pack.Texture.height;

            if (pack.LineHeight <= 0) pack.LineHeight = 20;
            if (pack.BaseLine <= 0) pack.BaseLine = pack.LineHeight;

            return pack;
        }

        private static Dictionary<string, string> ParseKVs(string line)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            string[] parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq <= 0) continue;
                string k = parts[i].Substring(0, eq).Trim();
                string v = parts[i].Substring(eq + 1).Trim().Trim('"');
                d[k] = v;
            }
            return d;
        }

        private static int GetInt(Dictionary<string, string> d, string k, int def)
        {
            string v;
            if (!object.ReferenceEquals(d, null) && d.TryGetValue(k, out v))
            {
                int r;
                if (int.TryParse(v, out r)) return r;
            }
            return def;
        }

        private static Texture2D LoadPngTexture(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);

                Texture2D tex = new Texture2D(2, 2, TextureFormat.ARGB32, false);

                MethodInfo mi = typeof(Texture2D).GetMethod("LoadImage", new Type[] { typeof(byte[]) });
                if (object.ReferenceEquals(mi, null))
                {
                    Log.E("[BMFont] Texture2D.LoadImage(byte[]) not found");
                    return null;
                }

                mi.Invoke(tex, new object[] { bytes });

                tex.name = P.GetFileNameNoExt(path);
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.anisoLevel = 0;

                return tex;
            }
            catch (Exception e)
            {
                Log.E("[BMFont] LoadPngTexture EX: " + e);
                return null;
            }
        }
    }

    internal class BMFontGlyph
    {
        public int id;
        public int x, y, width, height;
        public int xoffset, yoffset, xadvance;
    }
}
