using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace FontHook
{
    public static class Entry
    {
        static bool _installed;

        // Assembly-CSharp.dll 트리거가 호출하는 진입점
        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            Log("[FontHook] InstallOnce ENTER");

            try
            {
                GameObject go = new GameObject("__FontHook");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<FontHookRunner>();

                Log("[FontHook] Runner created. waiting for scan...");
            }
            catch (Exception ex)
            {
                Log("[FontHook] InstallOnce EX: " + ex);
            }
        }

        // 로그 위치: 문서\HuniePop_KR\fonthook_log.txt (원하는대로 유지)
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

        const float DUMP1_AT = 1.0f;      // 1초 후 1차 덤프
        const float DUMP2_FALLBACK_AT = 180.0f; // 3분 후 강제 2차 덤프(보험)

        void Start()
        {
            Entry.Log("[Runner] Start()");
            _lastLevelKey = GetLevelKeySafe();
        }

        void Update()
        {
            float t = Time.realtimeSinceStartup;

            // 1차 덤프: 시작 직후 UI 로드 상태
            if (!_dump1Done && t >= DUMP1_AT)
            {
                _dump1Done = true;
                DumpAll("DUMP#1 initial t=" + t.ToString("0.00"));
            }

            // 폴링(무거운 FindObjectsOfTypeAll을 자주 안 돌리기)
            if (t < _nextPollAt) return;
            _nextPollAt = t + POLL_INTERVAL;

            // 씬/레벨 변화 감지 → 2차 덤프
            if (!_dump2Done)
            {
                string curKey = GetLevelKeySafe();
                if (!object.ReferenceEquals(curKey, null) &&
                    !object.ReferenceEquals(_lastLevelKey, null) &&
                    curKey != _lastLevelKey)
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

            // 최후 보험: 아무 변화가 없어도 일정 시간 지나면 2차 덤프
            if (!_dump2Done && t >= DUMP2_FALLBACK_AT)
            {
                _dump2Done = true;
                DumpAll("DUMP#2 fallback t=" + t.ToString("0.00"));
            }
        }

        // "현재 레벨 상태"를 문자열 키로 만들기 (씬명 없을 때도 대비)
        string GetLevelKeySafe()
        {
            try
            {
                // Unity 4.x에서 대개 존재
                string name = Application.loadedLevelName;
                if (!object.ReferenceEquals(name, null) && name.Length > 0)
                    return "name:" + name;
            }
            catch { }

            try
            {
                // 없으면 인덱스라도
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
                DumpTextMeshHead(); // 너무 많으니 앞부분만

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

            int limit = 300; // 폰트는 다 보는 게 낫다
            for (int i = 0; i < objs.Length && i < limit; i++)
            {
                UnityEngine.Object o = objs[i];
                if (object.ReferenceEquals(o, null)) continue;

                Entry.Log("  [" + i + "] tk2dFontData name='" + SafeStr(SafeName(o)) + "' id=" + o.GetInstanceID());

                // material
                object matObj = GetFieldValue(o, t, "material");
                Material mat = matObj as Material;
                if (!object.ReferenceEquals(mat, null))
                {
                    Entry.Log("     .material = Material('" + SafeStr(SafeName(mat)) + "')#" + mat.GetInstanceID());

                    // shader
                    try
                    {
                        Shader sh = mat.shader;
                        if (!object.ReferenceEquals(sh, null))
                            Entry.Log("     .material.shader = Shader('" + SafeStr(sh.name) + "')");
                    }
                    catch { }

                    // mainTexture
                    try
                    {
                        Texture tex = mat.mainTexture;
                        if (!object.ReferenceEquals(tex, null))
                        {
                            Entry.Log("     .material.mainTexture = " + tex.GetType().Name +
                                      "('" + SafeStr(SafeName(tex)) + "')#" + tex.GetInstanceID());
                            DumpTextureDetails(tex);
                        }
                        else
                        {
                            Entry.Log("     .material.mainTexture = <null>");
                        }
                    }
                    catch (Exception ex)
                    {
                        Entry.Log("     .material.mainTexture = <EX> " + ex.GetType().Name + " " + ex.Message);
                    }
                }
                else
                {
                    if (!object.ReferenceEquals(matObj, null))
                        Entry.Log("     .material = <non-Material> " + matObj.GetType().FullName);
                    else
                        Entry.Log("     .material = <null>");
                }

                // chars length
                try
                {
                    object charsObj = GetFieldValue(o, t, "chars");
                    if (!object.ReferenceEquals(charsObj, null))
                    {
                        Array arr = charsObj as Array;
                        if (!object.ReferenceEquals(arr, null))
                            Entry.Log("     .chars.Length = " + arr.Length);
                        else
                            Entry.Log("     .chars = " + charsObj.GetType().FullName);
                    }
                    else
                    {
                        Entry.Log("     .chars = <null>");
                    }
                }
                catch { }
            }

            if (objs.Length > limit)
                Entry.Log("  ... truncated (" + objs.Length + " total)");
        }

        void DumpTextureDetails(Texture tex)
        {
            try
            {
                Texture2D t2d = tex as Texture2D;
                if (!object.ReferenceEquals(t2d, null))
                {
                    int w = 0, h = 0;
                    try { w = t2d.width; h = t2d.height; } catch { }

                    Entry.Log("        [Tex2D] size=" + w + "x" + h);

                    try
                    {
                        // Unity 4.x: Texture2D.format 존재
                        TextureFormat fmt = t2d.format;
                        Entry.Log("        [Tex2D] format=" + fmt.ToString());
                    }
                    catch { }

                    try
                    {
                        int mc = t2d.mipmapCount;
                        Entry.Log("        [Tex2D] mipmapCount=" + mc);
                    }
                    catch { }
                }
                else
                {
                    int w2 = 0, h2 = 0;
                    try { w2 = tex.width; h2 = tex.height; } catch { }
                    Entry.Log("        [Tex] size=" + w2 + "x" + h2);
                }
            }
            catch { }
        }

        void DumpTextMeshHead()
        {
            Type t = FindTypeByShortName("tk2dTextMesh");
            if (object.ReferenceEquals(t, null))
            {
                Entry.Log("[Dump] Type not found: tk2dTextMesh");
                return;
            }

            UnityEngine.Object[] objs = null;
            try { objs = Resources.FindObjectsOfTypeAll(t); }
            catch { return; }

            int count = (object.ReferenceEquals(objs, null)) ? 0 : objs.Length;
            Entry.Log("[Dump] tk2dTextMesh count=" + count);

            if (object.ReferenceEquals(objs, null)) return;

            int limit = 30;
            for (int i = 0; i < objs.Length && i < limit; i++)
            {
                UnityEngine.Object o = objs[i];
                if (object.ReferenceEquals(o, null)) continue;

                Entry.Log("  [" + i + "] tk2dTextMesh name='" + SafeStr(SafeName(o)) + "' id=" + o.GetInstanceID());
            }

            if (objs.Length > limit)
                Entry.Log("  ... truncated (" + objs.Length + " total)");
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

                        string n = tt.Name;
                        if (!object.ReferenceEquals(n, null) && n == shortName)
                            return tt;
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
