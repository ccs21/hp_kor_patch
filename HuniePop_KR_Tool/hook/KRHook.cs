using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

public static class KRHook
{
    static bool _loaded;
    static Dictionary<string, string> _dict = new Dictionary<string, string>();
    static Font _font;
    static readonly Dictionary<int, TextMesh> _overlay = new Dictionary<int, TextMesh>();

    // Assembly-CSharp 쪽에서 여기로 들어오게 만들 거임 (전역)
    public static string OnSetText(object labelObject, string text)
    {
        try
        {
            EnsureLoaded();

            if (string.IsNullOrEmpty(text))
                return text;

            // HuniePop 포맷: "^C00000000" 같은 색 코드 접두 제거(오버레이용)
            string cleaned = StripColorCode(text);

            string translated = Translate(cleaned);

            RenderOverlay(labelObject, translated);

            // 원본 tk2dTextMesh(비트맵 폰트)는 숨김
            return "";
        }
        catch
        {
            // 혹시라도 문제 생기면 원문이라도 표시되게(디버그)
            return text;
        }
    }

    static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        _font = Font.CreateDynamicFontFromOSFont("Malgun Gothic", 32);

        string gameRoot = GetGameRoot();

        // 번역 파일 위치:
        // 1) 게임 루트\HuniePop_KR\translations.tsv
        // 2) 게임 루트\translations.tsv
        string p1 = Path.Combine(gameRoot, "HuniePop_KR", "translations.tsv");
        string p2 = Path.Combine(gameRoot, "translations.tsv");

        string path = File.Exists(p1) ? p1 : p2;
        if (File.Exists(path))
            LoadTSV(path);
    }

    static string Translate(string src)
    {
        if (_dict.TryGetValue(src, out var v))
            return v;
        return src; // 못 찾으면 원문 표시
    }

    static void LoadTSV(string path)
    {
        // 포맷: 원문\t번역문
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("#")) continue;

            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;

            string k = line.Substring(0, tab);
            string v = line.Substring(tab + 1);

            if (!_dict.ContainsKey(k))
                _dict[k] = v;
        }
    }

    static void RenderOverlay(object labelObject, string text)
    {
        GameObject go = TryGetGameObject(labelObject);
        if (go == null) return;

        int id = go.GetInstanceID();

        if (!_overlay.TryGetValue(id, out var tm) || tm == null)
        {
            var child = new GameObject("KRText");
            child.transform.SetParent(go.transform, false);
            child.transform.localPosition = Vector3.zero;
            child.transform.localRotation = Quaternion.identity;
            child.transform.localScale = Vector3.one;

            tm = child.AddComponent<TextMesh>();
            tm.font = _font;
            tm.anchor = TextAnchor.UpperLeft;
            tm.alignment = TextAlignment.Left;
            tm.richText = true;
            tm.characterSize = 0.05f;
            tm.fontSize = 48;
            tm.color = Color.white;

            var mr = child.GetComponent<MeshRenderer>();
            if (mr != null) mr.sortingOrder = 5000;

            _overlay[id] = tm;
        }

        tm.text = text ?? "";
    }

    static GameObject TryGetGameObject(object labelObject)
    {
        if (labelObject == null) return null;
        var t = labelObject.GetType();

        // project 스타일상 gameObj를 들고 있을 확률이 높음
        var p = t.GetProperty("gameObj");
        if (p != null)
        {
            var v = p.GetValue(labelObject, null) as GameObject;
            if (v != null) return v;
        }
        var f = t.GetField("gameObj");
        if (f != null)
        {
            var v = f.GetValue(labelObject) as GameObject;
            if (v != null) return v;
        }

        // 혹시 transform이 직접 노출되어 있으면
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

    static string StripColorCode(string s)
    {
        if (s != null && s.StartsWith("^C") && s.Length >= 10)
            return s.Substring(10); // ^C + 8 hex
        return s;
    }

    static string GetGameRoot()
    {
        // Application.dataPath = ...\HuniePop_Data
        // root = ...\HuniePop
        try { return Directory.GetParent(Application.dataPath).FullName; }
        catch { return "."; }
    }
}
