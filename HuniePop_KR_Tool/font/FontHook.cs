// FontHook.cs (Unity 4.2 safe / NO System.IO.Path usage)
// - Fix: NEVER use Type == null / != null (avoids System.Type.op_Inequality)
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
                InstallUnityHooks();
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

        private static string SafeStr(object o)
        {
            try
            {
                return (o == null) ? "(null)" : o.ToString();
            }
            catch
            {
                return "(toString failed)";
            }
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
    

        // Capture Unity exceptions/logs even when Player.log/output_log.txt is not created.
        private static bool _unityHooked;
        private static void InstallUnityHooks()
        {
            if (_unityHooked) return;
            _unityHooked = true;

            try
            {
                // Unity 4.2: Application.RegisterLogCallback(LogCallback)
                Application.RegisterLogCallback((string condition, string stackTrace, LogType type) =>
                {
                    try
                    {
                        Append("[U] " + type.ToString() + " " + condition);
                        if (!string.IsNullOrEmpty(stackTrace))
                            Append("[U] " + stackTrace);
                    }
                    catch { }
                });
            }
            catch { }

            try
            {
                AppDomain.CurrentDomain.UnhandledException += (object sender, UnhandledExceptionEventArgs e) =>
                {
                    try { Append("[UE] " + SafeStr(e.ExceptionObject)); } catch { }
                };
            }
            catch { }
        }
}

    internal class FontHookRunner : MonoBehaviour
    {
        private bool _booted;

        private string _fontsDir;

        private Type _tFontData;
        private Type _tFontChar;

        private Dictionary<string, BMFontPack> _packs = new Dictionary<string, BMFontPack>(StringComparer.OrdinalIgnoreCase);

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
            // C# 제한: IEnumerator(yield) 본문에는 catch가 포함된 try를 둘 수 없습니다.
            while (true)
            {
                yield return new WaitForSeconds(2f);
                LoopApplyTick();
            }
        }

        private void LoopApplyTick()
        {
            try
            {
                bool isLoading = false;
                try { isLoading = Application.isLoadingLevel; } catch { isLoading = false; }
                if (isLoading) return;

                ApplyAllFontsOnce();
            }
            catch (Exception e)
            {
                Log.E("[Runner] LoopApply EX: " + e);
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

                    bool did = ApplyPackToFontData(fontObj, pack);
                    if (did) changed++;

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

                // tk2d expects p0 = bottom-left, p1 = top-right (p0.y <= p1.y).
                float top = pack.BaseLine - g.yoffset;
                float bottom = top - g.height;

                Vector3 p0 = new Vector3(p0x, bottom, 0);
                Vector3 p1 = new Vector3(p1x, top, 0);
                SetField(ch, "p0", p0);
                SetField(ch, "p1", p1);

                // BMFont 'y' is top-down; Unity UV is bottom-up.
                float u0 = (float)g.x / (float)pack.ScaleW;
                float u1 = (float)(g.x + g.width) / (float)pack.ScaleW;
                float vTop = 1f - ((float)g.y / (float)pack.ScaleH);
                float vBottom = 1f - ((float)(g.y + g.height) / (float)pack.ScaleH);

                // tk2d expects uv0 = bottom-left, uv1 = top-right (uv0.y <= uv1.y).
                Vector3 uv0 = new Vector3(u0, vBottom, 0);
                Vector3 uv1 = new Vector3(u1, vTop, 0);
                SetField(ch, "uv0", uv0);
                SetField(ch, "uv1", uv1);

                SetField(ch, "advance", (float)g.xadvance);

                TrySetByte(ch, "flipped", 0);

                if (!object.ReferenceEquals(miAdd, null))
                    miAdd.Invoke(dict, new object[] { code, ch });
            }

            return dict;
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
                if (!object.ReferenceEquals(f, null) && f.FieldType == typeof(byte))
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
