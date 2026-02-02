// FontHook.cs (Unity 4.2 safe / NO System.IO.Path usage)
// - Fix: NEVER use Type == null / != null (avoids System.Type.op_Inequality)
// - Fix: NEVER use Type == Type (avoids System.Type.op_Equality MissingMethodException on old Unity/Mono)
// - Fix: NEVER use System.IO.Path.* (avoids MissingMethodException Path.Combine)
// - Log path fixed to GameRoot (parent of Application.dataPath)
// - Loads BMFont packs (.fnt + .png) into tk2dFontData dictionaries
// - Plan #2 merges existing tk2dFontData dictionary with BMFont glyphs (keep special entries)
// - Multi-page .fnt support (2 pages => merge side-by-side atlas, adjust glyph x by page offset)
// - Adds a fixed screen-space offset: +15px right, +15px up (only p0/p1; does NOT touch metrics)

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
                GameObject go = new GameObject("FontHookRunner");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<FontHookRunner>();

                Log.I("[Entry] Install OK");
            }
            catch (Exception e)
            {
                Log.E("[Entry] Install EX: " + e);
            }
        }
    }

    internal static class Log
    {
        private static bool _inited;
        private static string _logPath;

        private static void Init()
        {
            if (_inited) return;
            _inited = true;

            try
            {
                string dataPath = Application.dataPath; // .../HuniePop_Data
                string root = P.GetDirName(dataPath);    // .../HuniePop
                _logPath = P.Join2(root, "FontHook_runtime.log");

                File.AppendAllText(_logPath, "==== FontHook start " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ====\n");
                File.AppendAllText(_logPath, "[I] root=" + root + "\n");
                File.AppendAllText(_logPath, "[I] dataPath=" + dataPath + "\n");
            }
            catch { }
        }

        public static void I(string s)
        {
            try
            {
                Init();
                File.AppendAllText(_logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + "[I] " + s + "\n");
            }
            catch { }
        }

        public static void E(string s)
        {
            try
            {
                Init();
                File.AppendAllText(_logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + "[E] " + s + "\n");
            }
            catch { }
        }
    }

    internal static class P
    {
        // Minimal path helpers (Unity 4.2 safe)
        public static string Join2(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b;
            if (string.IsNullOrEmpty(b)) return a;
            char c = a[a.Length - 1];
            if (c == '\\' || c == '/') return a + b;
            return a + "\\" + b;
        }

        public static string GetDirName(string p)
        {
            if (string.IsNullOrEmpty(p)) return ".";
            int i1 = p.LastIndexOf('\\');
            int i2 = p.LastIndexOf('/');
            int i = i1 > i2 ? i1 : i2;
            if (i <= 0) return ".";
            return p.Substring(0, i);
        }

        public static string GetFileNameNoExt(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            int i1 = p.LastIndexOf('\\');
            int i2 = p.LastIndexOf('/');
            int i = i1 > i2 ? i1 : i2;
            string fn = (i >= 0) ? p.Substring(i + 1) : p;
            int dot = fn.LastIndexOf('.');
            if (dot > 0) return fn.Substring(0, dot);
            return fn;
        }
    }

    internal class FontHookRunner : MonoBehaviour
    {
        private bool _booted;

        // Avoid re-applying every 2 seconds to the same tk2dFontData instance.
        // Key: UnityEngine.Object instanceID, Value: applied pack key (e.g. r20/b22)
        private Dictionary<int, string> _applied = new Dictionary<int, string>();

        private string _fontsDir;
        private Dictionary<string, BMFontPack> _packs = new Dictionary<string, BMFontPack>();

        // Target types (tk2d)
        private Type _tFontData;
        private Type _tFontChar;

        private FieldInfo _fi_material;
        private FieldInfo _fi_texture;
        private FieldInfo _fi_charDict;
        private FieldInfo _fi_lineHeight;
        private FieldInfo _fi_largestWidth;

        private MethodInfo _mi_initDictionary;

        private static readonly int[] REG_SIZES = new int[] { 16, 18, 20, 26 };
        private static readonly int[] BOLD_SIZES = new int[] { 20, 22, 30 };
        private const int OFFSET_X = 15;
        private const int OFFSET_Y = 15;

        public void Start()
        {
            Log.I("[Runner] Start()");
        }

        public void Update()
        {
            if (!_booted)
            {
                _booted = true;
                Bootstrap();
            }

            // Poll slowly to avoid performance impact / repeated application.
            // Fonts can appear later as scenes load.
            if (Time.frameCount % 120 == 0)
            {
                try
                {
                    ApplyAllFontsOnce();
                }
                catch (Exception e)
                {
                    Log.E("[Runner] Update ApplyAllFontsOnce EX: " + e);
                }
            }
        }

        private void Bootstrap()
        {
            try
            {
                // fonts folder next to Managed (user's setup)
                string dataPath = Application.dataPath;
                string root = P.GetDirName(dataPath);
                _fontsDir = P.Join2(P.Join2(dataPath, "Managed"), "fonts");

                Log.I("[Runner] Bootstrap ENTER");
                Log.I("[Runner] fontsDir=" + _fontsDir);

                ResolveTk2dTypes();

                // Load BMFont packs (only fnt required; png pages resolved from .fnt)
                TryLoadPack("r16");
                TryLoadPack("r18");
                TryLoadPack("r20");
                TryLoadPack("r26");
                TryLoadPack("b20");
                TryLoadPack("b22");
                TryLoadPack("b30");

                Log.I("[Runner] Bootstrap OK. packs=" + _packs.Count);
            }
            catch (Exception e)
            {
                Log.E("[Runner] Bootstrap EX: " + e);
            }
        }

        private void ResolveTk2dTypes()
        {
            try
            {
                // Find tk2dFontData and tk2dFontChar types by scanning loaded assemblies.
                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    Assembly a = asms[i];
                    Type[] ts;
                    try { ts = a.GetTypes(); }
                    catch { continue; }

                    for (int j = 0; j < ts.Length; j++)
                    {
                        Type t = ts[j];
                        if (object.ReferenceEquals(_tFontData, null) && t.Name == "tk2dFontData")
                            _tFontData = t;
                        if (object.ReferenceEquals(_tFontChar, null) && t.Name == "tk2dFontChar")
                            _tFontChar = t;
                    }
                }

                if (object.ReferenceEquals(_tFontData, null) || object.ReferenceEquals(_tFontChar, null))
                {
                    Log.E("[Runner] tk2d types not found. fontData=" + (_tFontData != null) + " fontChar=" + (_tFontChar != null));
                    return;
                }

                _fi_material = _tFontData.GetField("material", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fi_charDict = _tFontData.GetField("charDict", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fi_lineHeight = _tFontData.GetField("lineHeight", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fi_largestWidth = _tFontData.GetField("largestWidth", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                _mi_initDictionary = _tFontData.GetMethod("InitDictionary", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                // Resolve Material.mainTexture field via reflection-safe route
                // We'll use property on Material (mainTexture) not field, so no _fi_texture needed.
                Log.I("[Runner] ResolveTk2dTypes OK");
            }
            catch (Exception e)
            {
                Log.E("[Runner] ResolveTk2dTypes EX: " + e);
            }
        }

        private void TryLoadPack(string key)
        {
            try
            {
                string fnt = P.Join2(_fontsDir, key + ".fnt");
                if (!File.Exists(fnt))
                {
                    Log.I("[Runner] pack missing fnt: " + key);
                    return;
                }

                BMFontPack pack = BMFontPack.LoadFromFnt(key, fnt, _fontsDir);
                if (object.ReferenceEquals(pack, null))
                {
                    Log.E("[Runner] pack load failed: " + key);
                    return;
                }

                _packs[key] = pack;
                Log.I("[Runner] pack OK: " + key + " tex=" + pack.Texture.width + "x" + pack.Texture.height + " glyphs=" + pack.Glyphs.Count + " pagesMerged=" + pack.PageCount);
            }
            catch (Exception e)
            {
                Log.E("[Runner] TryLoadPack EX: " + e);
            }
        }

        private void ApplyAllFontsOnce()
        {
            if (object.ReferenceEquals(_tFontData, null) || object.ReferenceEquals(_tFontChar, null)) return;
            if (_packs.Count == 0) return;

            // Find all tk2dFontData instances
            UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(_tFontData);
            int fonts = (all != null) ? all.Length : 0;
            int changed = 0;

            for (int i = 0; i < fonts; i++)
            {
                UnityEngine.Object obj = all[i];
                if (object.ReferenceEquals(obj, null)) continue;

                int id = obj.GetInstanceID();
                string already;
                if (_applied.TryGetValue(id, out already))
                    continue;

                string pick = PickPackKey(obj);
                if (string.IsNullOrEmpty(pick))
                    continue;

                BMFontPack pack;
                if (!_packs.TryGetValue(pick, out pack) || object.ReferenceEquals(pack, null))
                    continue;

                if (ApplyPackToFontData(obj, pack))
                {
                    _applied[id] = pick;
                    changed++;
                }
            }

            Log.I("[Runner] ApplyAllFontsOnce: fonts=" + fonts + " changed=" + changed);
        }

        private string PickPackKey(UnityEngine.Object fontData)
        {
            // Keep existing strategy from the working version:
            // - Determine "bold" vs "regular" by name hints
            // - Determine size by extracting digits from tk2dFontData name (e.g. ...16px..., ...20px...)
            try
            {
                string n = fontData.name;
                if (string.IsNullOrEmpty(n)) return null;

                bool bold = false;
                string lower = n.ToLowerInvariant();
                if (lower.IndexOf("bold") >= 0 || lower.IndexOf("demibold") >= 0 || lower.IndexOf("extrabold") >= 0)
                    bold = true;

                int px = ExtractFirstInt(lower);
                if (px <= 0) return null;

                int best = FindClosestSize(px, bold ? BOLD_SIZES : REG_SIZES);
                if (best <= 0) return null;

                return (bold ? "b" : "r") + best.ToString();
            }
            catch { return null; }
        }

        private int ExtractFirstInt(string s)
        {
            try
            {
                Match m = Regex.Match(s, @"(\d+)");
                if (!object.ReferenceEquals(m, null) && m.Success)
                {
                    int v;
                    if (int.TryParse(m.Groups[1].Value, out v)) return v;
                }
            }
            catch { }
            return 0;
        }

        private int FindClosestSize(int px, int[] sizes)
        {
            if (sizes == null || sizes.Length == 0) return 0;
            int best = sizes[0];
            int bestd = Math.Abs(px - best);
            for (int i = 1; i < sizes.Length; i++)
            {
                int d = Math.Abs(px - sizes[i]);
                if (d < bestd)
                {
                    bestd = d;
                    best = sizes[i];
                }
            }
            return best;
        }

        private bool ApplyPackToFontData(UnityEngine.Object fontData, BMFontPack pack)
        {
            try
            {
                if (object.ReferenceEquals(fontData, null) || object.ReferenceEquals(pack, null)) return false;

                // material / texture replace
                Material mat = null;
                if (!object.ReferenceEquals(_fi_material, null))
                    mat = _fi_material.GetValue(fontData) as Material;

                if (object.ReferenceEquals(mat, null))
                {
                    Log.E("[Runner] material null for fontData=" + fontData.name);
                    return false;
                }

                // Swap texture
                mat.mainTexture = pack.Texture;

                // Merge existing dict with new glyphs (keeps special entries)
                object merged = BuildMergedTk2dDict(fontData, pack);
                if (object.ReferenceEquals(merged, null)) return false;

                if (!object.ReferenceEquals(_fi_charDict, null))
                    _fi_charDict.SetValue(fontData, merged);

                // Keep metrics from pack (same behavior as working version)
                if (!object.ReferenceEquals(_fi_lineHeight, null))
                    _fi_lineHeight.SetValue(fontData, (float)pack.LineHeight);
                if (!object.ReferenceEquals(_fi_largestWidth, null))
                    _fi_largestWidth.SetValue(fontData, (float)pack.LargestWidth);

                // InitDictionary if present
                if (!object.ReferenceEquals(_mi_initDictionary, null))
                    _mi_initDictionary.Invoke(fontData, new object[0]);

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

                float p0x = g.xoffset + OFFSET_X;
                float p1x = g.xoffset + g.width + OFFSET_X;

                float top = (pack.BaseLine - g.yoffset) + OFFSET_Y;
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

        // --- Plan #2: Merge existing tk2dFontData dictionary with new BMFont pack glyphs ---
        // Keeps any pre-existing special symbols / control entries that the game expects.
        private object BuildMergedTk2dDict(UnityEngine.Object fontData, BMFontPack pack)
        {
            try
            {
                object existing = null;
                if (!object.ReferenceEquals(_fi_charDict, null))
                    existing = _fi_charDict.GetValue(fontData);

                object newDict = BuildTk2dDict(pack);
                if (object.ReferenceEquals(newDict, null)) return null;

                if (object.ReferenceEquals(existing, null)) return newDict;

                // existing is Dictionary<int, tk2dFontChar>
                Type existingType = existing.GetType();
                MethodInfo miGetEnum = existingType.GetMethod("GetEnumerator");
                MethodInfo miAdd = newDict.GetType().GetMethod("Add", new Type[] { typeof(int), _tFontChar });

                if (object.ReferenceEquals(miGetEnum, null) || object.ReferenceEquals(miAdd, null))
                    return newDict;

                object en = miGetEnum.Invoke(existing, new object[0]);
                if (object.ReferenceEquals(en, null)) return newDict;

                Type enType = en.GetType();
                MethodInfo miMoveNext = enType.GetMethod("MoveNext");
                PropertyInfo piCurrent = enType.GetProperty("Current");

                if (object.ReferenceEquals(miMoveNext, null) || object.ReferenceEquals(piCurrent, null))
                    return newDict;

                // Current is KeyValuePair<int, tk2dFontChar>
                while ((bool)miMoveNext.Invoke(en, new object[0]))
                {
                    object cur = piCurrent.GetValue(en, null);
                    if (object.ReferenceEquals(cur, null)) continue;

                    Type kvType = cur.GetType();
                    PropertyInfo piKey = kvType.GetProperty("Key");
                    PropertyInfo piVal = kvType.GetProperty("Value");
                    if (object.ReferenceEquals(piKey, null) || object.ReferenceEquals(piVal, null)) continue;

                    int key = (int)piKey.GetValue(cur, null);
                    object val = piVal.GetValue(cur, null);
                    if (object.ReferenceEquals(val, null)) continue;

                    // If newDict already has this key, skip (new glyph should override)
                    // Avoid ContainsKey reflection cost; try Add and ignore exceptions.
                    try { miAdd.Invoke(newDict, new object[] { key, val }); }
                    catch { }
                }

                return newDict;
            }
            catch (Exception e)
            {
                Log.E("[Runner] BuildMergedTk2dDict EX: " + e);
                return null;
            }
        }

        private void SetField(object obj, string name, object val)
        {
            try
            {
                FieldInfo f = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (!object.ReferenceEquals(f, null))
                    f.SetValue(obj, val);
            }
            catch { }
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

    // ---------------- BMFont Loader (supports multi-page .fnt) ----------------
    // Minimal change from the "working" version:
    //  - Reads page file names from .fnt (page id file="xxx.png")
    //  - If 2 pages exist (e.g. r261.png + r262.png), merges them into one atlas texture
    //  - Adjusts glyph x/y by page offset
    // NOTE: Still Unity 4.2 safe: no System.IO.Path usage, no Type == comparisons.

    internal class BMFontPack
    {
        public string Key;
        public Texture2D Texture;

        public int LineHeight;
        public int BaseLine;
        public int ScaleW;
        public int ScaleH;
        public int LargestWidth;

        public int PageCount;

        public Dictionary<int, BMFontGlyph> Glyphs = new Dictionary<int, BMFontGlyph>();

        // fntDir: directory containing the .fnt and its page png(s)
        public static BMFontPack LoadFromFnt(string key, string fntPath, string fntDir)
        {
            BMFontPack pack = new BMFontPack();
            pack.Key = key;

            string txt = File.ReadAllText(fntPath, Encoding.UTF8);

            Dictionary<int, string> pageFiles = new Dictionary<int, string>();

            using (StringReader sr = new StringReader(txt))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0) continue;

                    if (line.StartsWith("common "))
                    {
                        Dictionary<string, string> kv = ParseKVs(line);
                        pack.LineHeight = GetInt(kv, "lineHeight", 0);
                        pack.BaseLine = GetInt(kv, "base", pack.LineHeight);
                        pack.ScaleW = GetInt(kv, "scaleW", 0);
                        pack.ScaleH = GetInt(kv, "scaleH", 0);
                        pack.PageCount = GetInt(kv, "pages", 1);
                    }
                    else if (line.StartsWith("page "))
                    {
                        Dictionary<string, string> kv = ParseKVs(line);
                        int id = GetInt(kv, "id", -1);
                        string file;
                        if (id >= 0 && kv.TryGetValue("file", out file) && !string.IsNullOrEmpty(file))
                        {
                            pageFiles[id] = file;
                        }
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
                        g.page = GetInt(kv, "page", 0);

                        if (g.id >= 0)
                        {
                            pack.Glyphs[g.id] = g;
                            if (g.width > pack.LargestWidth) pack.LargestWidth = g.width;
                        }
                    }
                }
            }

            // If the .fnt does not contain page lines, fall back to key.png (single page)
            if (pageFiles.Count == 0)
            {
                string fallbackPng = key + ".png";
                pageFiles[0] = fallbackPng;
                pack.PageCount = 1;
            }

            // Load pages
            int maxPage = -1;
            foreach (KeyValuePair<int, string> kv in pageFiles)
            {
                if (kv.Key > maxPage) maxPage = kv.Key;
            }
            int pages = maxPage + 1;
            if (pages <= 0) pages = 1;

            Texture2D[] texPages = new Texture2D[pages];
            int validPages = 0;

            for (int i = 0; i < pages; i++)
            {
                string fn;
                if (!pageFiles.TryGetValue(i, out fn) || string.IsNullOrEmpty(fn))
                {
                    texPages[i] = null;
                    continue;
                }

                string pngFull = P.Join2(fntDir, fn);
                if (!File.Exists(pngFull))
                {
                    pngFull = P.Join2(fntDir, P.GetFileNameNoExt(fn) + ".png");
                }

                if (!File.Exists(pngFull))
                {
                    Log.E("[BMFont] page png missing: key=" + key + " id=" + i + " file=" + fn);
                    texPages[i] = null;
                    continue;
                }

                texPages[i] = LoadPngTexture(pngFull);
                if (!object.ReferenceEquals(texPages[i], null)) validPages++;
            }

            if (validPages <= 0)
            {
                Log.E("[BMFont] no valid png pages: key=" + key);
                return null;
            }

            // If only one page is valid, use it directly.
            if (validPages == 1)
            {
                for (int i = 0; i < texPages.Length; i++)
                {
                    if (!object.ReferenceEquals(texPages[i], null))
                    {
                        pack.Texture = texPages[i];
                        break;
                    }
                }

                if (pack.ScaleW <= 0) pack.ScaleW = pack.Texture.width;
                if (pack.ScaleH <= 0) pack.ScaleH = pack.Texture.height;

                if (pack.LineHeight <= 0) pack.LineHeight = 20;
                if (pack.BaseLine <= 0) pack.BaseLine = pack.LineHeight;

                pack.PageCount = 1;
                return pack;
            }

            // Only implement the required case: 2 pages => merge side-by-side
            if (validPages >= 2)
            {
                BMFontPack merged = TryMerge2PagesSideBySide(pack, texPages);
                if (!object.ReferenceEquals(merged, null)) return merged;

                // If merge failed for any reason, fall back to first valid page (better than null)
                for (int i = 0; i < texPages.Length; i++)
                {
                    if (!object.ReferenceEquals(texPages[i], null))
                    {
                        pack.Texture = texPages[i];
                        pack.PageCount = 1;
                        if (pack.ScaleW <= 0) pack.ScaleW = pack.Texture.width;
                        if (pack.ScaleH <= 0) pack.ScaleH = pack.Texture.height;
                        if (pack.LineHeight <= 0) pack.LineHeight = 20;
                        if (pack.BaseLine <= 0) pack.BaseLine = pack.LineHeight;
                        return pack;
                    }
                }
            }

            return null;
        }

        // Merge 2 pages into one atlas (left = page0, right = page1)
        private static BMFontPack TryMerge2PagesSideBySide(BMFontPack src, Texture2D[] pages)
        {
            try
            {
                Texture2D p0 = null;
                Texture2D p1 = null;

                if (pages.Length > 0) p0 = pages[0];
                if (pages.Length > 1) p1 = pages[1];

                if (object.ReferenceEquals(p0, null) || object.ReferenceEquals(p1, null))
                {
                    List<Texture2D> tmp = new List<Texture2D>();
                    for (int i = 0; i < pages.Length; i++)
                        if (!object.ReferenceEquals(pages[i], null)) tmp.Add(pages[i]);
                    if (tmp.Count < 2) return null;
                    p0 = tmp[0];
                    p1 = tmp[1];
                }

                int w = p0.width;
                int h = p0.height;

                if (p1.width != w || p1.height != h)
                {
                    Log.E("[BMFont] merge pages size mismatch: " + w + "x" + h + " vs " + p1.width + "x" + p1.height);
                    return null;
                }

                int maxSize = 4096;
                try { maxSize = SystemInfo.maxTextureSize; } catch { maxSize = 4096; }
                if (maxSize <= 0) maxSize = 4096;

                int atlasW = w * 2;
                int atlasH = h;

                if (atlasW > maxSize || atlasH > maxSize)
                {
                    Log.E("[BMFont] merge atlas too big: " + atlasW + "x" + atlasH + " max=" + maxSize);
                    return null;
                }

                Texture2D atlas = new Texture2D(atlasW, atlasH, TextureFormat.ARGB32, false);
                atlas.name = src.Key + "_atlas";
                atlas.filterMode = FilterMode.Point;
                atlas.wrapMode = TextureWrapMode.Clamp;
                atlas.anisoLevel = 0;

                try
                {
                    Color[] clear = new Color[atlasW * atlasH];
                    atlas.SetPixels(clear);
                }
                catch { }

                CopyTexToAtlas(atlas, p0, 0, 0);
                CopyTexToAtlas(atlas, p1, w, 0);

                atlas.Apply(false, false);

                foreach (KeyValuePair<int, BMFontGlyph> kv in src.Glyphs)
                {
                    BMFontGlyph g = kv.Value;
                    if (g.page == 1)
                    {
                        g.x += w;
                    }
                    g.page = 0;
                }

                src.Texture = atlas;
                src.ScaleW = atlasW;
                src.ScaleH = atlasH;
                src.PageCount = 1;

                if (src.LineHeight <= 0) src.LineHeight = 20;
                if (src.BaseLine <= 0) src.BaseLine = src.LineHeight;

                return src;
            }
            catch (Exception e)
            {
                Log.E("[BMFont] TryMerge2PagesSideBySide EX: " + e);
                return null;
            }
        }

        // Copy src texture into atlas at top-left origin offset (ox, oy) in BMFont coordinates.
        // Unity SetPixels uses bottom-left origin, so we convert y.
        private static void CopyTexToAtlas(Texture2D atlas, Texture2D src, int ox, int oyTop)
        {
            if (object.ReferenceEquals(atlas, null) || object.ReferenceEquals(src, null)) return;

            int w = src.width;
            int h = src.height;

            int atlasH = atlas.height;
            int dstY = atlasH - oyTop - h;

            Color32[] pix32 = src.GetPixels32();
            Color[] pix = new Color[pix32.Length];
            for (int i = 0; i < pix32.Length; i++) pix[i] = pix32[i];

            atlas.SetPixels(ox, dstY, w, h, pix);
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
        public int page;
    }
}
