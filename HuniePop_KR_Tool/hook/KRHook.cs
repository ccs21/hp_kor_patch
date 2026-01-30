using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        }
        catch (Exception ex)
        {
            LogError("OnSetText", ex, raw);
            return raw;
        }
    }

    static int GetStableId(object labelObject)
    {
        if (labelObject == null) return 0;

        // UnityEngine.Object는 인스턴스ID가 가장 안정적
        var uo = labelObject as UnityEngine.Object;
        if (uo != null)
        {
            try { return uo.GetInstanceID(); }
            catch { /* fallthrough */ }
        }

        // 일반 object는 RuntimeHelpers 해시로 안정적(오버라이드된 GetHashCode 영향 없음)
        return RuntimeHelpers.GetHashCode(labelObject);
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
    }

    static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
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

            sb.Append(s[i]);
            i++;
        }
        return sb.ToString();
    }
}
