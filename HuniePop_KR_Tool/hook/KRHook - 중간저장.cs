using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

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
    static readonly string BaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "HuniePop_KR"
    );

    static readonly string PendingPath = Path.Combine(BaseDir, "pending.tsv");
    static readonly string LogPath = Path.Combine(BaseDir, "krhook.log");

    // --------------------------------------------
    // State
    // --------------------------------------------
    static bool _loaded = false;
    static bool _loadFailed = false;

    static Dictionary<string, string> _dict = new Dictionary<string, string>(StringComparer.Ordinal);
    static HashSet<string> _pending = new HashSet<string>(StringComparer.Ordinal);

    // --------------------------------------------
    // Entry point (patched into LabelObject.SetText(string) at method start)
    // MUST return the final string to be assigned to tk2dTextMesh.text.
    // --------------------------------------------
public static string OnSetText(object labelObject, string text)
{
    string raw = text ?? "";
    if (raw.Length == 0) return raw;

    try
    {
        EnsureLoaded();
        if (!_loaded) return raw;

        // 타자기 마커(^C000000 등) 포함 모든 컬러코드 제거 후 키 생성
        string key = Normalize(RemoveAllColorCodes(raw));
        if (key.Length == 0) return raw;

        if (_dict.TryGetValue(key, out string translated) && !string.IsNullOrEmpty(translated))
        {
            // ★ 번역문을 그대로 반환: 타자기 효과를 사실상 제거하고 처음부터 한글 표시
            return translated;
        }

        // 타자기 중간조각(pending 오염) 방지: 너무 짧으면 저장 안 함
        if (key.Length >= 4)
            AddPending(key);

        return raw;
    }
    catch (Exception ex)
    {
        LogError("OnSetText", ex, raw);
        return raw;
    }
}


    // --------------------------------------------
    // Loading
    // --------------------------------------------
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
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    if (line[0] == '#') continue;

                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;

                    string k = line.Substring(0, tab);
                    string v = line.Substring(tab + 1);

                    k = Normalize(k);
                    if (k.Length == 0) continue;

                    _dict[k] = v ?? "";
                }
            }

            // Load existing pending to avoid duplicates across runs
            _pending.Clear();
            if (File.Exists(PendingPath))
            {
                using (var sr = new StreamReader(PendingPath, Encoding.UTF8, true))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        if (line[0] == '#') continue;

                        string k = Normalize(line);
                        if (k.Length != 0) _pending.Add(k);
                    }
                }
            }

            _loaded = true;
            Log("[KRHook] Loaded. dict=" + _dict.Count + " pending=" + _pending.Count + " dictPath=" + _dictPath);
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
            {
                string dir = Path.GetDirectoryName(loc);
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
        }
        catch { }

        // Fallbacks (Unity에서 Location이 비는 경우 대비)
        try
        {
            // Application.dataPath = .../HuniePop_Data
            // Managed = .../HuniePop_Data/Managed
            string managed = Path.Combine(Application.dataPath, "Managed");
            if (Directory.Exists(managed)) return managed;
        }
        catch { }

        return Directory.GetCurrentDirectory();
    }

    // --------------------------------------------
    // Pending
    // --------------------------------------------
    static void AddPending(string key)
    {
        try
        {
            if (string.IsNullOrEmpty(key)) return;

            key = Normalize(key);
            if (key.Length == 0) return;
            if (_pending.Contains(key)) return;

            _pending.Add(key);

            Directory.CreateDirectory(BaseDir);
            using (var sw = new StreamWriter(PendingPath, true, Encoding.UTF8))
            {
                sw.WriteLine(key);
            }
        }
        catch (Exception ex)
        {
            LogError("AddPending", ex, key ?? "");
        }
    }

    // --------------------------------------------
    // Text helpers
    // --------------------------------------------
    static string Normalize(string s)
    {
        if (s == null) return "";

        // Normalize line breaks
        s = s.Replace("\r\n", "\n").Replace("\r", "\n");

        // Trim outer whitespace
        s = s.Trim();

        // Collapse spaces/tabs (keep newlines)
        StringBuilder sb = null;
        bool prevSpace = false;

        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];

            if (ch == '\n')
            {
                if (sb == null) sb = new StringBuilder(s.Length);
                sb.Append('\n');
                prevSpace = false;
                continue;
            }

            bool isWs = (ch == ' ' || ch == '\t');
            if (isWs)
            {
                if (!prevSpace)
                {
                    if (sb == null) sb = new StringBuilder(s.Length);
                    sb.Append(' ');
                    prevSpace = true;
                }
                continue;
            }

            if (sb != null) sb.Append(ch);
            prevSpace = false;
        }

        return sb != null ? sb.ToString().Trim() : s;
    }

    static bool IsHex(char ch)
    {
        return (ch >= '0' && ch <= '9') ||
               (ch >= 'a' && ch <= 'f') ||
               (ch >= 'A' && ch <= 'F');
    }

    // Keep caret color prefix if present: ^C + 6hex (8 chars) OR ^C + 8hex (10 chars)
    static string ExtractCaretColorPrefix(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length < 8) return "";
        if (s[0] != '^') return "";
        if (s[1] != 'C' && s[1] != 'c') return "";

        // 6-hex
        bool ok6 = true;
        for (int i = 2; i < 8; i++)
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

    // Remove ALL caret color codes inside the string:
    // ^CFFFFFF or ^CFFFFFFFF (also supports lowercase c)
    static string RemoveAllColorCodes(string s)
    {
        if (s == null) return "";

        StringBuilder sb = null;
        int i = 0;

        while (i < s.Length)
        {
            if (s[i] == '^' && i + 7 < s.Length && (s[i + 1] == 'C' || s[i + 1] == 'c'))
            {
                // 6-hex
                bool ok6 = true;
                for (int k = 0; k < 6; k++)
                {
                    if (!IsHex(s[i + 2 + k])) { ok6 = false; break; }
                }
                if (ok6)
                {
                    if (sb == null)
                    {
                        sb = new StringBuilder(s.Length);
                        if (i > 0) sb.Append(s, 0, i);
                    }
                    i += 8;
                    continue;
                }

                // 8-hex
                if (i + 9 < s.Length)
                {
                    bool ok8 = true;
                    for (int k = 0; k < 8; k++)
                    {
                        if (!IsHex(s[i + 2 + k])) { ok8 = false; break; }
                    }
                    if (ok8)
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
            }

            if (sb != null) sb.Append(s[i]);
            i++;
        }

        return sb == null ? s : sb.ToString();
    }

    // --------------------------------------------
    // Logging
    // --------------------------------------------
    static void Log(string msg)
    {
        if (!EnableLog) return;
        try
        {
            Directory.CreateDirectory(BaseDir);
            using (var sw = new StreamWriter(LogPath, true, Encoding.UTF8))
            {
                sw.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + msg);
            }
        }
        catch { }
    }

    static void LogError(string tag, Exception ex, string ctx)
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            using (var sw = new StreamWriter(LogPath, true, Encoding.UTF8))
            {
                sw.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [ERR] " + tag);
                sw.WriteLine(ex.ToString());
                if (!string.IsNullOrEmpty(ctx))
                    sw.WriteLine("[CTX] " + ctx);
                sw.WriteLine("----");
            }
        }
        catch { }
    }
}
