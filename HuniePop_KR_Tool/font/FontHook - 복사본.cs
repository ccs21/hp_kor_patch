using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;

namespace FontHook
{
    public static class Entry
    {
        static bool _installed;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            Log("[FontHook] InstallOnce ENTER");
	    Log("[GPU] SystemInfo.maxTextureSize=" + SystemInfo.maxTextureSize);

            try
            {
                GameObject go = new GameObject("__FontHook");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<FontHookRunner>();

                Log("[FontHook] Runner created.");
            }
            catch (Exception ex)
            {
                Log("[FontHook] InstallOnce EX: " + ex);
            }
        }

        static string BaseDir
        {
            get
            {
                try
                {
                    string doc = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    return Path.Combine(doc, "HuniePop_KR");
                }
                catch { return "."; }
            }
        }

        static string LogPath
        {
            get { return Path.Combine(BaseDir, "fonthook_log.txt"); }
        }

        public static void Log(string s)
        {
            try
            {
                Directory.CreateDirectory(BaseDir);
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + s + "\r\n");
            }
            catch { }
        }
    }

    public class FontHookRunner : MonoBehaviour
    {
        bool _dump1Done;
        bool _dump2Done;

        string _lastLevelKey = "";
        int _lastFontDataCount = -1;

        float _nextPollAt = 0f;
        const float POLL_INTERVAL = 1.0f;

        const float DUMP1_AT = 1.0f;
        const float DUMP2_FALLBACK_AT = 180.0f;

        // ===== 자동 추적(핵심) =====
        float _nextTrackScanAt = 0f;
        const float TRACK_SCAN_INTERVAL = 0.5f;  // 0.5초마다 “활성 텍스트의 폰트”를 스캔
        HashSet<int> _seenFontDataInstanceIds = new HashSet<int>(); // “한 번이라도 등장한 fontData” (instanceID 기준)
        HashSet<string> _seenFontDataNames = new HashSet<string>(); // instanceID 못 잡을 때 fallback
        Dictionary<int, string> _fontDataIdToName = new Dictionary<int, string>(); // 디버깅 편의용

        void Start()
        {
            Entry.Log("[Runner] Start()");
            _lastLevelKey = GetLevelKeySafe();
        }

        void Update()
        {
            float t = Time.realtimeSinceStartup;

            // (선택) 수동 덤프는 남겨둠. 하지만 너 상황에서는 없어도 됨.
            try
            {
                if (Input.GetKeyDown(KeyCode.F8))
                {
                    DumpAll("DUMP#HOTKEY F8 t=" + t.ToString("0.00"));
                }
            }
            catch { }

            // 1차 덤프
            if (!_dump1Done && t >= DUMP1_AT)
            {
                _dump1Done = true;
                DumpAll("DUMP#1 initial t=" + t.ToString("0.00"));
            }

            // ===== 자동: 새 폰트 등장 감지 =====
            if (t >= _nextTrackScanAt)
            {
                _nextTrackScanAt = t + TRACK_SCAN_INTERVAL;
                TrackNewFontsInActiveTextMeshes(t);
            }

            // 폴링(씬/폰트데이터 변화 감지용)
            if (t < _nextPollAt) return;
            _nextPollAt = t + POLL_INTERVAL;

            // 씬/레벨 변화 감지 → 2차 덤프
            if (!_dump2Done)
            {
                string curKey = GetLevelKeySafe();
                if (curKey != _lastLevelKey)
                {
                    Entry.Log("[Runner] Level changed: '" + SafeStr(_lastLevelKey) + "' -> '" + SafeStr(curKey) + "'");
                    _dump2Done = true;
                    DumpAll("DUMP#2 level_change key=" + SafeStr(curKey) + " t=" + t.ToString("0.00"));
                    _lastLevelKey = curKey;
                    return;
                }
                _lastLevelKey = curKey;
            }

            // tk2dFontData 개수 변화 감지 → 2차 덤프
            if (!_dump2Done)
            {
                int c = GetFontDataCountSafe();
                if (_lastFontDataCount < 0) _lastFontDataCount = c;

                if (c != _lastFontDataCount)
                {
                    Entry.Log("[Runner] tk2dFontData count changed: " + _lastFontDataCount + " -> " + c);
                    _dump2Done = true;
                    DumpAll("DUMP#2 fontdata_count_change " + _lastFontDataCount + "->" + c + " t=" + t.ToString("0.00"));
                    _lastFontDataCount = c;
                    return;
                }

                _lastFontDataCount = c;
            }

            // 최후 보험
            if (!_dump2Done && t >= DUMP2_FALLBACK_AT)
            {
                _dump2Done = true;
                DumpAll("DUMP#2 fallback t=" + t.ToString("0.00"));
            }
        }

        // ===== 자동 추적 로직 =====
        void TrackNewFontsInActiveTextMeshes(float now)
        {
            Type textMeshType = FindTypeByShortName("tk2dTextMesh");
            if (object.ReferenceEquals(textMeshType, null)) return;

            Type fontDataType = FindTypeByShortName("tk2dFontData"); // 있을 수도
            UnityEngine.Object[] meshes = null;

            try { meshes = Resources.FindObjectsOfTypeAll(textMeshType); }
            catch { return; }

            if (object.ReferenceEquals(meshes, null)) return;

            // 너무 많은 오브젝트가 잡히면 부담이 될 수 있으니 “활성만” 빠르게 스캔
            for (int i = 0; i < meshes.Length; i++)
            {
                UnityEngine.Object o = meshes[i];
                if (object.ReferenceEquals(o, null)) continue;

                Component comp = o as Component;
                if (object.ReferenceEquals(comp, null)) continue;

                GameObject go = comp.gameObject;
                if (object.ReferenceEquals(go, null)) continue;

                bool active = false;
                try { active = go.activeInHierarchy; } catch { active = false; }
                if (!active) continue;

                // 폰트데이터 추출
                object fontDataObj = TryGetFirstMemberValue(o, textMeshType,
                    "fontData", "FontData", "font", "Font", "data", "Data", "m_fontData", "m_font");

                if (object.ReferenceEquals(fontDataObj, null)) continue;

                UnityEngine.Object fontUo = fontDataObj as UnityEngine.Object;

                // instanceID가 있으면 그걸로, 아니면 이름 fallback
                int fid = 0;
                string fname = "";

                try { fid = (!object.ReferenceEquals(fontUo, null)) ? fontUo.GetInstanceID() : 0; } catch { fid = 0; }
                try { fname = (!object.ReferenceEquals(fontUo, null)) ? fontUo.name : fontDataObj.GetType().Name; } catch { fname = fontDataObj.GetType().Name; }

                bool isNew = false;

                if (fid != 0)
                {
                    if (!_seenFontDataInstanceIds.Contains(fid))
                    {
                        _seenFontDataInstanceIds.Add(fid);
                        _fontDataIdToName[fid] = fname;
                        isNew = true;
                    }
                }
                else
                {
                    if (!_seenFontDataNames.Contains(fname))
                    {
                        _seenFontDataNames.Add(fname);
                        isNew = true;
                    }
                }

                if (!isNew) continue;

                // 새 폰트 등장 로그 (핵심)
                string path = GetHierarchyPath(go);
                string textDesc = DescribeTextValue(o, textMeshType);
                string fontDesc = DescribeFontData(fontDataObj, fontDataType);

                Entry.Log("[NEW_FONT] t=" + now.ToString("0.00") +
                          " font=" + fontDesc +
                          " used_by='" + SafeStr(path) + "'" +
                          " text=" + textDesc);

                // 필요하면 여기서 “해당 fontData 전체 덤프”도 가능.
                // 지금은 로그 폭발 방지 위해 요약만.
            }
        }

        // ===== 기존 덤프들 =====

        string GetLevelKeySafe()
        {
            try
            {
                string name = Application.loadedLevelName;
                if (!object.ReferenceEquals(name, null) && name.Length > 0)
                    return "name:" + name;
            }
            catch { }

            try
            {
                int idx = Application.loadedLevel;
                return "idx:" + idx.ToString();
            }
            catch { }

            return "unknown";
        }

        int GetFontDataCountSafe()
        {
            try
            {
                Type t = FindTypeByShortName("tk2dFontData");
                if (object.ReferenceEquals(t, null)) return 0;

                UnityEngine.Object[] objs = Resources.FindObjectsOfTypeAll(t);
                if (object.ReferenceEquals(objs, null)) return 0;

                return objs.Length;
            }
            catch { return 0; }
        }

        void DumpAll(string tag)
        {
            try
            {
                Entry.Log("[Dump] ===== " + tag + " Begin =====");
                DumpFontDatas();
                DumpTextMeshesUsageSummary();
                Entry.Log("[Dump] ===== " + tag + " End =====");
            }
            catch (Exception ex)
            {
                Entry.Log("[Dump] EX: " + ex);
            }
        }

        void DumpFontDatas()
        {
            Type t = FindTypeByShortName("tk2dFontData");
            if (object.ReferenceEquals(t, null))
            {
                Entry.Log("[Dump] Type not found: tk2dFontData");
                return;
            }

            UnityEngine.Object[] objs = null;
            try { objs = Resources.FindObjectsOfTypeAll(t); }
            catch (Exception ex)
            {
                Entry.Log("[Dump] FindObjectsOfTypeAll failed for tk2dFontData : " + ex);
                return;
            }

            int count = (object.ReferenceEquals(objs, null)) ? 0 : objs.Length;
            Entry.Log("[Dump] tk2dFontData count=" + count);

            if (object.ReferenceEquals(objs, null)) return;

            int limit = 400;
            for (int i = 0; i < objs.Length && i < limit; i++)
            {
                UnityEngine.Object o = objs[i];
                if (object.ReferenceEquals(o, null)) continue;

                Entry.Log("  [" + i + "] tk2dFontData name='" + SafeStr(SafeName(o)) + "' id=" + o.GetInstanceID());

                object matObj = GetFieldValue(o, t, "material");
                Material mat = matObj as Material;
                if (!object.ReferenceEquals(mat, null))
                {
                    Entry.Log("     .material = Material('" + SafeStr(SafeName(mat)) + "')#" + mat.GetInstanceID());
                    try
                    {
                        Texture tex = mat.mainTexture;
                        if (!object.ReferenceEquals(tex, null))
                        {
                            Entry.Log("     .material.mainTexture = " + tex.GetType().Name +
                                      "('" + SafeStr(SafeName(tex)) + "')#" + tex.GetInstanceID() +
                                      " " + SafeTexSize(tex));
                        }
                        else Entry.Log("     .material.mainTexture = <null>");
                    }
                    catch (Exception ex)
                    {
                        Entry.Log("     .material.mainTexture = <EX> " + ex.GetType().Name + " " + ex.Message);
                    }
                }
            }

            if (objs.Length > limit)
                Entry.Log("  ... truncated (" + objs.Length + " total)");
        }

        // “요약용” 사용량 통계 (덤프에서만)
        void DumpTextMeshesUsageSummary()
        {
            Type textMeshType = FindTypeByShortName("tk2dTextMesh");
            if (object.ReferenceEquals(textMeshType, null))
            {
                Entry.Log("[Dump] Type not found: tk2dTextMesh");
                return;
            }

            UnityEngine.Object[] objs = null;
            try { objs = Resources.FindObjectsOfTypeAll(textMeshType); }
            catch (Exception ex)
            {
                Entry.Log("[Dump] FindObjectsOfTypeAll failed for tk2dTextMesh : " + ex);
                return;
            }

            int total = (object.ReferenceEquals(objs, null)) ? 0 : objs.Length;
            Entry.Log("[Dump] tk2dTextMesh count=" + total);

            Dictionary<string, int> usageActive = new Dictionary<string, int>();
            int activeCount = 0;

            for (int i = 0; i < total; i++)
            {
                UnityEngine.Object o = objs[i];
                if (object.ReferenceEquals(o, null)) continue;

                Component comp = o as Component;
                if (object.ReferenceEquals(comp, null)) continue;

                GameObject go = comp.gameObject;
                if (object.ReferenceEquals(go, null)) continue;

                bool active = false;
                try { active = go.activeInHierarchy; } catch { active = false; }
                if (!active) continue;

                activeCount++;

                object fontDataObj = TryGetFirstMemberValue(o, textMeshType,
                    "fontData", "FontData", "font", "Font", "data", "Data", "m_fontData", "m_font");

                string key = "<null>";
                UnityEngine.Object fuo = fontDataObj as UnityEngine.Object;
                if (!object.ReferenceEquals(fuo, null)) key = fuo.name;
                else if (!object.ReferenceEquals(fontDataObj, null)) key = fontDataObj.GetType().Name;

                int v;
                if (usageActive.TryGetValue(key, out v)) usageActive[key] = v + 1;
                else usageActive[key] = 1;
            }

            Entry.Log("[Dump] tk2dTextMesh activeInHierarchy count=" + activeCount);
            DumpUsageTop("Font usage (ACTIVE only)", usageActive, 30);
        }

        void DumpUsageTop(string title, Dictionary<string, int> dict, int topN)
        {
            try
            {
                Entry.Log("[Dump] ---- " + title + " TOP" + topN + " ----");
                List<KeyValuePair<string, int>> list = new List<KeyValuePair<string, int>>(dict);
                list.Sort((a, b) => b.Value.CompareTo(a.Value));

                int n = Math.Min(topN, list.Count);
                for (int i = 0; i < n; i++)
                {
                    var kv = list[i];
                    Entry.Log("   #" + (i + 1) + " " + kv.Value + "x  font='" + SafeStr(kv.Key) + "'");
                }
            }
            catch { }
        }

        // ===== 헬퍼들 =====

        string DescribeFontData(object fontDataObj, Type fontDataType)
        {
            if (object.ReferenceEquals(fontDataObj, null)) return "<null>";

            UnityEngine.Object uo = fontDataObj as UnityEngine.Object;
            string n = "";
            try { n = (!object.ReferenceEquals(uo, null)) ? uo.name : fontDataObj.GetType().Name; }
            catch { n = fontDataObj.GetType().Name; }

            if (!object.ReferenceEquals(fontDataType, null) &&
                fontDataType.IsInstanceOfType(fontDataObj))
            {
                try
                {
                    object matObj = GetFieldValue(fontDataObj, fontDataType, "material");
                    Material mat = matObj as Material;
                    if (!object.ReferenceEquals(mat, null))
                    {
                        Texture tex = null;
                        try { tex = mat.mainTexture; } catch { tex = null; }
                        if (!object.ReferenceEquals(tex, null))
                        {
                            return "tk2dFontData('" + n + "') mat='" + SafeName(mat) + "' tex='" + SafeName(tex) + "' " + SafeTexSize(tex);
                        }
                        return "tk2dFontData('" + n + "') mat='" + SafeName(mat) + "' tex=<null>";
                    }
                }
                catch { }
            }

            return n;
        }

        string DescribeTextValue(object textMeshObj, Type textMeshType)
        {
            object txt = TryGetFirstMemberValue(textMeshObj, textMeshType,
                "text", "Text", "m_text", "m_rawText", "rawText", "RawText");

            if (object.ReferenceEquals(txt, null)) return "<null>";

            string s = "";
            try { s = txt as string; } catch { s = ""; }
            if (string.IsNullOrEmpty(s))
            {
                try { s = txt.ToString(); } catch { s = "<non-string>"; }
            }

            // 로그 폭발 방지
            s = s.Replace("\r", "\\r").Replace("\n", "\\n");
            if (s.Length > 40) return "len=" + s.Length + " sample='" + s.Substring(0, 40) + "...'";
            return "len=" + s.Length + " '" + s + "'";
        }

        string SafeTexSize(Texture tex)
        {
            try { return tex.width + "x" + tex.height; } catch { return "?x?"; }
        }

        string GetHierarchyPath(GameObject go)
        {
            try
            {
                if (object.ReferenceEquals(go, null)) return "<null>";
                Transform t = go.transform;
                if (object.ReferenceEquals(t, null)) return SafeName(go);

                string path = SafeName(go);
                int guard = 0;
                while (!object.ReferenceEquals(t.parent, null) && guard++ < 64)
                {
                    t = t.parent;
                    path = SafeName(t.gameObject) + "/" + path;
                }
                return path;
            }
            catch { return "<EX>"; }
        }

        object TryGetFirstMemberValue(object obj, Type t, params string[] names)
        {
            if (object.ReferenceEquals(obj, null) || object.ReferenceEquals(t, null) || object.ReferenceEquals(names, null))
                return null;

            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                if (object.ReferenceEquals(name, null) || name.Length == 0) continue;

                try
                {
                    FieldInfo f = t.GetField(name, flags);
                    if (!object.ReferenceEquals(f, null)) return f.GetValue(obj);
                }
                catch { }

                try
                {
                    PropertyInfo p = t.GetProperty(name, flags);
                    if (!object.ReferenceEquals(p, null) && p.CanRead) return p.GetValue(obj, null);
                }
                catch { }
            }

            return null;
        }

        object GetFieldValue(object obj, Type t, string fieldName)
        {
            try
            {
                FieldInfo f = t.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (object.ReferenceEquals(f, null)) return null;
                return f.GetValue(obj);
            }
            catch { return null; }
        }

        Type FindTypeByShortName(string shortName)
        {
            try
            {
                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                if (object.ReferenceEquals(asms, null)) return null;

                for (int a = 0; a < asms.Length; a++)
                {
                    Assembly asm = asms[a];
                    if (object.ReferenceEquals(asm, null)) continue;

                    Type[] types = null;
                    try { types = asm.GetTypes(); } catch { continue; }
                    if (object.ReferenceEquals(types, null)) continue;

                    for (int i = 0; i < types.Length; i++)
                    {
                        Type tt = types[i];
                        if (object.ReferenceEquals(tt, null)) continue;

                        if (tt.Name == shortName) return tt;
                    }
                }
            }
            catch { }

            return null;
        }

        static string SafeName(UnityEngine.Object o)
        {
            try { return o.name; } catch { return ""; }
        }

        static string SafeStr(string s)
        {
            return object.ReferenceEquals(s, null) ? "" : s;
        }
    }
}
