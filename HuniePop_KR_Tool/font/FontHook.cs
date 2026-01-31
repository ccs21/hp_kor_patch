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

            // (선택) 수동 덤프는 남겨둠.
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

            // 폴링(씬/레벨 변화 감지용)
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

                object fontDataObj = TryGetFirstMemberValue(o, textMeshType,
                    "fontData", "FontData", "font", "Font", "data", "Data", "m_fontData", "m_font");

                if (object.ReferenceEquals(fontDataObj, null)) continue;

                UnityEngine.Object fontUo = fontDataObj as UnityEngine.Object;

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

                string path = GetHierarchyPath(go);
                string textDesc = DescribeTextValue(o, textMeshType);
                string fontDesc = DescribeFontData(fontDataObj, fontDataType);

                Entry.Log("[NEW_FONT] t=" + now.ToString("0.00") +
                          " font=" + fontDesc +
                          " used_by='" + SafeStr(path) + "'" +
                          " text=" + textDesc);
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

                    // Shader 정보
                    try
                    {
                        Shader sh = mat.shader;
                        Entry.Log("     .material.shader = " + (!object.ReferenceEquals(sh, null) ? ("Shader('" + sh.name + "')") : "<null>"));
                    }
                    catch (Exception ex)
                    {
                        Entry.Log("     .material.shader = <EX> " + ex.GetType().Name + " " + ex.Message);
                    }

                    // Texture 정보
                    try
                    {
                        Texture tex = mat.mainTexture;
                        if (!object.ReferenceEquals(tex, null))
                        {
                            Entry.Log("     .material.mainTexture = " + tex.GetType().Name +
                                      "('" + SafeStr(SafeName(tex)) + "')#" + tex.GetInstanceID() +
                                      " " + SafeTexSize(tex));

                            Texture2D t2d = tex as Texture2D;
                            if (!object.ReferenceEquals(t2d, null))
                            {
                                Entry.Log("       .tex2d.format=" + t2d.format + " mipmap=" + t2d.mipmapCount);
                            }
                        }
                        else Entry.Log("     .material.mainTexture = <null>");
                    }
                    catch (Exception ex)
                    {
                        Entry.Log("     .material.mainTexture = <EX> " + ex.GetType().Name + " " + ex.Message);
                    }

                    // tk2dFontData 내부 구조(문자/글리프/커닝 등) 덤프
                    try
{
    DumpFontDataStructure(o, t);
}
catch (Exception ex)
{
    Entry.Log("     [STRUCT] EX(outer): " + ex.GetType().Name + " " + ex.Message);
}

                }
            }

            if (objs.Length > limit)
                Entry.Log("  ... truncated (" + objs.Length + " total)");
        }

        // tk2dFontData 내부에서 “문자 → 글리프” 매핑이 어떤 필드로 존재하는지 확인하기 위한 구조 덤프
        void DumpFontDataStructure(object fontDataObj, Type fontDataType)
        {
            if (object.ReferenceEquals(fontDataObj, null) || object.ReferenceEquals(fontDataType, null))
                return;

            try
            {
                BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                FieldInfo[] fields = fontDataType.GetFields(BF);
                Entry.Log("     [STRUCT] fields=" + (fields != null ? fields.Length.ToString() : "0"));

                if (object.ReferenceEquals(fields, null)) return;

                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo f = fields[i];
                    if (object.ReferenceEquals(f, null)) continue;

                    string fn = f.Name ?? "";
                    Type ft = f.FieldType;

                    string low = fn.ToLowerInvariant();

                    // 관심 필드만: 문자/글리프/커닝/라인/스페이싱/데이터/딕셔너리/맵/텍스처 계열
                    bool interesting =
                        low.Contains("char") || low.Contains("glyph") || low.Contains("kern") ||
                        low.Contains("line") || low.Contains("space") || low.Contains("advance") ||
                        low.Contains("tex") || low.Contains("data") || low.Contains("dict") || low.Contains("map");

                    if (!interesting) continue;

                    object v = null;
                    try { v = f.GetValue(fontDataObj); } catch { v = null; }

                    string extra = "";

                    if (!object.ReferenceEquals(v, null))
                    {
                        // 배열 길이
                        Array arr = v as Array;
                        if (!object.ReferenceEquals(arr, null))
                        {
                            extra = " len=" + arr.Length;
                        }
                        else
                        {
                            // Count 프로퍼티가 있으면(딕셔너리/리스트 등)
                            try
                            {
                                PropertyInfo pCount = v.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
                                if (!object.ReferenceEquals(pCount, null))
                                {
                                    object c = pCount.GetValue(v, null);
                                    if (!object.ReferenceEquals(c, null)) extra = " count=" + c.ToString();
                                }
                            }
                            catch { }
                        }
                    }

                    string ftName = "<null>";
try
{
    if (!object.ReferenceEquals(ft, null))
        ftName = ft.FullName;
}
catch
{
    ftName = "<ex>";
}

Entry.Log("       - " + fn + " : " + ftName +
          (object.ReferenceEquals(v, null) ? " = <null>" : "") + extra);

                }
            }
            catch (Exception ex)
            {
                Entry.Log("     [STRUCT] EX: " + ex.GetType().Name + " " + ex.Message);
            }
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

                string fontName = "<null>";
                try
                {
                    UnityEngine.Object fuo = fontDataObj as UnityEngine.Object;
                    if (!object.ReferenceEquals(fuo, null))
                        fontName = SafeStr(SafeName(fuo));
                    else if (!object.ReferenceEquals(fontDataObj, null))
                        fontName = fontDataObj.GetType().Name;
                }
                catch { }

                if (!usageActive.ContainsKey(fontName)) usageActive[fontName] = 0;
                usageActive[fontName]++;
            }

            Entry.Log("[Dump] tk2dTextMesh activeInHierarchy=" + activeCount);

            // 정렬 출력
            List<KeyValuePair<string, int>> list = new List<KeyValuePair<string, int>>(usageActive);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));

            int top = Math.Min(80, list.Count);
            for (int i = 0; i < top; i++)
            {
                Entry.Log("  [ActiveUsage] " + list[i].Value + "x  font='" + SafeStr(list[i].Key) + "'");
            }

            if (list.Count > top)
                Entry.Log("  ... usage truncated (" + list.Count + " fonts)");
        }

        // ===== 유틸 =====

        Type FindTypeByShortName(string shortName)
        {
            try
            {
                // Unity 4.2: 로드된 어셈블리에서 직접 찾는 게 안전
                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    Assembly a = asms[i];
                    if (object.ReferenceEquals(a, null)) continue;

                    Type[] types = null;
                    try { types = a.GetTypes(); } catch { continue; }
                    if (object.ReferenceEquals(types, null)) continue;

                    for (int j = 0; j < types.Length; j++)
                    {
                        Type t = types[j];
                        if (object.ReferenceEquals(t, null)) continue;
                        if (t.Name == shortName) return t;
                    }
                }
            }
            catch { }
            return null;
        }

        object GetFieldValue(object inst, Type t, string fieldName)
        {
            if (object.ReferenceEquals(inst, null) || object.ReferenceEquals(t, null)) return null;
            try
            {
                BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                FieldInfo fi = t.GetField(fieldName, bf);
                if (object.ReferenceEquals(fi, null)) return null;
                return fi.GetValue(inst);
            }
            catch { return null; }
        }

        object TryGetFirstMemberValue(object inst, Type t, params string[] names)
        {
            if (object.ReferenceEquals(inst, null) || object.ReferenceEquals(t, null) || object.ReferenceEquals(names, null))
                return null;

            BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            for (int i = 0; i < names.Length; i++)
            {
                string n = names[i];
                if (string.IsNullOrEmpty(n)) continue;

                try
                {
                    FieldInfo fi = t.GetField(n, bf);
                    if (!object.ReferenceEquals(fi, null))
                        return fi.GetValue(inst);
                }
                catch { }

                try
                {
                    PropertyInfo pi = t.GetProperty(n, bf);
                    if (!object.ReferenceEquals(pi, null) && pi.CanRead)
                        return pi.GetValue(inst, null);
                }
                catch { }
            }

            return null;
        }

        string DescribeFontData(object fontDataObj, Type fontDataType)
        {
            if (object.ReferenceEquals(fontDataObj, null)) return "<null>";

            UnityEngine.Object uo = fontDataObj as UnityEngine.Object;

            int id = 0;
            string nm = "";

            try { id = (!object.ReferenceEquals(uo, null)) ? uo.GetInstanceID() : 0; } catch { id = 0; }
            try { nm = (!object.ReferenceEquals(uo, null)) ? uo.name : fontDataObj.GetType().Name; } catch { nm = fontDataObj.GetType().Name; }

            if (id != 0) return "'" + SafeStr(nm) + "'#" + id;
            return "'" + SafeStr(nm) + "'";
        }

        string DescribeTextValue(object textMeshObj, Type textMeshType)
        {
            if (object.ReferenceEquals(textMeshObj, null) || object.ReferenceEquals(textMeshType, null)) return "<null>";

            object txt = TryGetFirstMemberValue(textMeshObj, textMeshType,
                "text", "Text", "m_text", "m_String", "stringText", "FormattedText");

            string s = "<null>";
            try { s = txt as string; } catch { s = "<ex>"; }

            if (object.ReferenceEquals(s, null)) s = "<null>";

            if (s.Length > 70) s = s.Substring(0, 70) + "...";
            s = s.Replace("\r", "\\r").Replace("\n", "\\n");

            return "'" + s + "'";
        }

        string GetHierarchyPath(GameObject go)
        {
            if (object.ReferenceEquals(go, null)) return "<null>";
            try
            {
                Transform t = go.transform;
                if (object.ReferenceEquals(t, null)) return SafeStr(go.name);

                List<string> parts = new List<string>();
                int guard = 0;
                while (!object.ReferenceEquals(t, null) && guard++ < 64)
                {
                    parts.Add(SafeStr(t.name));
                    t = t.parent;
                }
                parts.Reverse();
                return string.Join("/", parts.ToArray());
            }
            catch
            {
                return SafeStr(go.name);
            }
        }

        static string SafeTexSize(Texture tex)
        {
            if (object.ReferenceEquals(tex, null)) return "";
            try
            {
                return tex.width + "x" + tex.height;
            }
            catch { return ""; }
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
