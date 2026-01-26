using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

public static class KRHook
{
    static bool _loaded;

    // translations.tsv -> dict
    static Dictionary<string, string> _dict = new Dictionary<string, string>();
    // pending.tsv 중복 방지
    static HashSet<string> _pendingSet = new HashSet<string>();

    static string _gameRoot;
    static string _krDir;
    static string _translationsPath;
    static string _pendingPath;
    static string _unmatchedLogPath;

    // Overlay cache (LabelObject gameObj instance id -> TextMesh)
    static readonly Dictionary<int, TextMesh> _overlay = new Dictionary<int, TextMesh>();

    // TTF Font
    static Font _font;

    // ===== 엔트리: Assembly-CSharp(LabelObject.SetText)에서 호출됨 =====
    // 반환값은 "원본 비트맵 텍스트"에 들어갈 문자열.
    // 우리는 원본을 숨길 거라서 대부분 "" 반환.
    public static string OnSetText(object labelObject, string text)
    {
        try
        {
            EnsureLoaded();

            if (string.IsNullOrEmpty(text))
                return text;

            // HuniePop 색코드("^C00000000") 제거 + 정규화
            string cleaned = Normalize(StripColorCode(text));

            // 번역 조회
            string translated;
            bool has = _dict.TryGetValue(cleaned, out translated) && !string.IsNullOrEmpty(translated);

            string toShow = has ? translated : cleaned;

            if (!has)
                AddPending(cleaned);

            // 오버레이로 표시 (TTF)
            RenderOverlay(labelObject, toShow);

            // 원본 비트맵 텍스트는 숨김
            return "";
        }
        catch
        {
            // 훅에서 예외가 나면 원문이라도 표시되게(디버깅)
            return text;
        }
    }

    // ===== 초기화 =====
    static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        _gameRoot = GetGameRoot();
        _krDir = Path.Combine(_gameRoot, "HuniePop_KR");
        _translationsPath = Path.Combine(_krDir, "translations.tsv");
        _pendingPath = Path.Combine(_krDir, "pending.tsv");
        _unmatchedLogPath = Path.Combine(_krDir, "unmatched.log");

        try
        {
            if (!Directory.Exists(_krDir))
                Directory.CreateDirectory(_krDir);
        }
        catch { }

        // 폰트 로드 (Unity 4.2 안전)
        // 1) 윈도우 폰트명 시도
        _font = new Font("Malgun Gothic");
        if (_font == null || _font.material == null)
        {
            _font = new Font("Arial");
        }

        // 기존 번역 로드
        if (File.Exists(_translationsPath))
            LoadTSV(_translationsPath, _dict);

        // 기존 pending 로드(중복 방지용)
        if (File.Exists(_pendingPath))
            LoadPendingKeys(_pendingPath, _pendingSet);
    }

    // ===== TSV 로드: 원문<TAB>번역 =====
    static void LoadTSV(string path, Dictionary<string, string> dst)
    {
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith("#")) continue;

            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;

            string k = line.Substring(0, tab);
            string v = line.Substring(tab + 1);

            k = Normalize(UnescapeKey(k));
            v = UnescapeValue(v);

            if (k.Length == 0) continue;
            dst[k] = v;
        }
    }

    static void LoadPendingKeys(string path, HashSet<string> dst)
    {
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith("#")) continue;

            int tab = line.IndexOf('\t');
            string k = (tab >= 0) ? line.Substring(0, tab) : line;

            k = Normalize(UnescapeKey(k));
            if (k.Length == 0) continue;
            dst.Add(k);
        }
    }

    // ===== 미번역 자동 수집 =====
    static void AddPending(string key)
    {
        if (string.IsNullOrEmpty(key)) return;

        // 너무 짧은 잡음 제외하고 싶으면 주석 해제
        // if (key.Length < 2) return;

        if (_pendingSet.Contains(key))
            return;

        _pendingSet.Add(key);

        string safeKey = EscapeKey(key);

        try
        {
            File.AppendAllText(_pendingPath, safeKey + "\t\n", Encoding.UTF8);
        }
        catch { }

        try
        {
            File.AppendAllText(_unmatchedLogPath, safeKey + "\n", Encoding.UTF8);
        }
        catch { }
    }

    // ===== Overlay =====
    static void RenderOverlay(object labelObject, string text)
    {
        GameObject go = TryGetGameObject(labelObject);
        if (go == null) return;

        int id = go.GetInstanceID();

        TextMesh tm;
        if (!_overlay.TryGetValue(id, out tm) || tm == null)
        {
            var child = new GameObject("KRText");

            // Unity 4.2: SetParent 없음
            child.transform.parent = go.transform;

            // 대부분 LabelObject는 로컬 0,0이 텍스트 기준점이라서 일단 0으로 시작
            child.transform.localPosition = Vector3.zero;
            child.transform.localRotation = Quaternion.identity;
            child.transform.localScale = Vector3.one;

            tm = child.AddComponent<TextMesh>();
            tm.font = _font;
            tm.anchor = TextAnchor.UpperLeft;
            tm.alignment = TextAlignment.Left;
            tm.richText = true;

            // 기본값 (화면 보고 조절 가능)
            tm.characterSize = 0.05f;
            tm.fontSize = 48;
            tm.color = Color.white;

            // 렌더러 머티리얼 지정 (핑크/안보임 방지)
            Renderer r = child.GetComponent<Renderer>();
            if (r != null && _font != null && _font.material != null)
            {
                r.material = _font.material;
                if (r.material != null)
                    r.material.renderQueue = 5000;
            }

            _overlay[id] = tm;
        }

        tm.text = text ?? "";
    }

    static GameObject TryGetGameObject(object labelObject)
    {
        if (labelObject == null) return null;
        Type t = labelObject.GetType();

        var pGameObj = t.GetProperty("gameObj");
        if (pGameObj != null)
        {
            var v = pGameObj.GetValue(labelObject, null) as GameObject;
            if (v != null) return v;
        }

        var fGameObj = t.GetField("gameObj");
        if (fGameObj != null)
        {
            var v = fGameObj.GetValue(labelObject) as GameObject;
            if (v != null) return v;
        }

        var pTr = t.GetProperty("transform");
        if (pTr != null)
        {
            var tr = pTr.GetValue(labelObject, null) as Transform;
            if (tr != null) return tr.gameObject;
        }

        var fTr = t.GetField("transform");
        if (fTr != null)
        {
            var tr = fTr.GetValue(labelObject) as Transform;
            if (tr != null) return tr.gameObject;
        }

        return null;
    }

    // ===== 문자열 처리 =====
    static string StripColorCode(string s)
    {
        if (s != null && s.StartsWith("^C") && s.Length >= 10)
            return s.Substring(10);
        return s;
    }

    static string Normalize(string s)
    {
        if (s == null) return "";

        if (s.Length > 0 && s[0] == '\uFEFF')
            s = s.Substring(1);

        s = s.Replace("\r\n", "\n").Replace("\r", "\n");
        s = s.Trim();

        return s;
    }

    static string EscapeKey(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\").Replace("\n", "\\n");
    }

    static string UnescapeKey(string s)
    {
        if (s == null) return "";
        s = s.Replace("\\n", "\n");
        s = s.Replace("\\\\", "\\");
        return s;
    }

    static string UnescapeValue(string s)
    {
        if (s == null) return "";
        s = s.Replace("\\n", "\n");
        s = s.Replace("\\\\", "\\");
        return s;
    }

    static string GetGameRoot()
    {
        try
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }
        catch
        {
            return ".";
        }
    }
}
