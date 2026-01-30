using System;
using System.IO;
using System.Reflection;
using UnityEngine;

/// <summary>
/// HuniePop Font Hook bootstrap.
/// - Assembly-CSharp.dll patched trigger should call one of the public entrypoints below.
/// - This DLL is x86 and references only UnityEngine.dll (same as KRHook build).
/// </summary>
public static class FontHook
{
static FontHook()
{
    try
    {
        Directory.CreateDirectory(BaseDir);
        File.AppendAllText(Path.Combine(BaseDir, "fonthook_loaded.txt"),
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + "FontHook assembly loaded\r\n");
    }
    catch { }
}
 


   static bool _installed = false;

    // ===== Entry points (여러 개 제공: 트리거가 뭘 호출하든 걸리게) =====
    public static void Install()     { InstallOnce(); }
    public static void Init()        { InstallOnce(); }
    public static void TryInstall()  { InstallOnce(); }
    public static void Boot()        { InstallOnce(); }

    static void InstallOnce()
    {
Log("[FontHook] InstallOnce ENTER");

        if (_installed) return;
        _installed = true;

        try
        {
            // 로거 준비
            Log("[FontHook] InstallOnce()");

            // 런너 GameObject
            var go = new GameObject("__FontHook");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;

            go.AddComponent<FontHookRunner>();
            Log("[FontHook] Runner created.");
        }
        catch (Exception e)
        {
            Log("[FontHook] InstallOnce EX: " + e);
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
    try { Debug.Log(s); } catch { }

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
    float _nextScan = 0f;
    bool _didFirstScan = false;

    void Start()
    {
        FontHook.Log("[FontHookRunner] Start()");
        // 첫 프레임 이후 스캔 (오브젝트들이 생성된 뒤)
        _nextScan = Time.realtimeSinceStartup + 0.5f;
    }

    void Update()
    {
        // 너무 자주 돌리면 부담이니까 1회/또는 원하면 주기적으로
        if (!_didFirstScan && Time.realtimeSinceStartup >= _nextScan)
        {
            _didFirstScan = true;
            DumpFontsInMemory();
        }
    }

    /// <summary>
    /// 지금 단계의 목표:
    /// 1) 게임이 실제로 어떤 tk2d 폰트/머티리얼/텍스쳐를 들고 있는지 "자동 덤프"해서
    /// 2) 40개를 진짜 다 바꿔야 하는지, 아니면 소수만 쓰는지 확정.
    /// </summary>
    void DumpFontsInMemory()
    {
        try
        {
            FontHook.Log("[Dump] ===== Scan Begin =====");

            // tk2dTextMesh / tk2dTextMesh(또는 LabelObject 내부 참조) 등을 reflection으로 훑기
            DumpByTypeName("tk2dTextMesh");
            DumpByTypeName("tk2dTextMeshData");
            DumpByTypeName("tk2dFontData");
            DumpByTypeName("tk2dFont");

            FontHook.Log("[Dump] ===== Scan End =====");
        }
        catch (Exception e)
        {
            FontHook.Log("[Dump] EX: " + e);
        }
    }

    void DumpByTypeName(string typeName)
    {
        var t = FindType(typeName);
        if (t == null)
        {
            FontHook.Log("[Dump] Type not found: " + typeName);
            return;
        }

        // Unity 전체 리소스에서 해당 타입 전부 찾기
        UnityEngine.Object[] objs = Resources.FindObjectsOfTypeAll(t);
        FontHook.Log("[Dump] " + typeName + " count=" + (objs != null ? objs.Length : 0));

        if (objs == null) return;

        int limit = 200; // 로그 폭발 방지
        for (int i = 0; i < objs.Length && i < limit; i++)
        {
            var o = objs[i];
            if (o == null) continue;

            string name = "";
            try { name = o.name; } catch { }

            FontHook.Log("  [" + i + "] " + typeName + " name=" + name + " inst=" + o.GetInstanceID());

            // 흔히 폰트/머티리얼/텍스쳐가 들어가는 필드 후보를 몇 개 찍어본다
            DumpFieldIfExists(o, t, "material");
            DumpFieldIfExists(o, t, "data");
            DumpFieldIfExists(o, t, "fontData");
            DumpFieldIfExists(o, t, "font");
            DumpFieldIfExists(o, t, "texture");
            DumpFieldIfExists(o, t, "mainTexture");
        }

        if (objs.Length > limit)
            FontHook.Log("  ... truncated (" + objs.Length + " total)");
    }

    void DumpFieldIfExists(object obj, Type t, string fieldName)
    {
        try
        {
            var f = t.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return;
            var v = f.GetValue(obj);
            if (v == null) return;

            string vStr = v.ToString();
            var uo = v as UnityEngine.Object;
            if (uo != null)
                vStr = uo.GetType().Name + "(" + uo.name + ")#" + uo.GetInstanceID();

            FontHook.Log("     ." + fieldName + " = " + vStr);
        }
        catch { }
    }

Type FindType(string shortName)
{
    try
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            // 1) FullName 정확히
            var t = asm.GetType(shortName, false);
            if (t != null) return t;

            // 2) 네임스페이스 포함 대비: Name/FullName 끝부분 매칭
            Type[] types = null;
            try { types = asm.GetTypes(); } catch { continue; }
            if (types == null) continue;

            for (int i = 0; i < types.Length; i++)
            {
                var tt = types[i];
                if (tt == null) continue;
                if (tt.Name == shortName) return tt;
                if (tt.FullName != null && tt.FullName.EndsWith("." + shortName)) return tt;
            }
        }
    }
    catch { }
    return null;
}

}
public static class KRFontHook
{
    public static void Install() => FontHook.Install();
    public static void Init() => FontHook.Install();
    public static void Boot() => FontHook.Install();
    public static void TryInstall() => FontHook.Install();
}

public static class FontHookBootstrap
{
    public static void Install() => FontHook.Install();
    public static void Init() => FontHook.Install();
    public static void Boot() => FontHook.Install();
    public static void TryInstall() => FontHook.Install();
}

namespace HuniePopKR
{
    public static class FontHook
    {
        public static void Install() => global::FontHook.Install();
        public static void Init() => global::FontHook.Install();
        public static void Boot() => global::FontHook.Install();
        public static void TryInstall() => global::FontHook.Install();
    }
}
