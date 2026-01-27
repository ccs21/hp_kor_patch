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

    // Assembly-CSharp(LabelObject.SetText)에서 여기로 들어오게 만들 함수
    // 반환값: 원본 tk2dTextMesh에 실제로 넣을 텍스트(우리는 빈 문자열로 숨김)
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

            // 오버레이 렌더
            RenderOverlay(labelObject, translated);

            // 원본 비트맵 텍스트는 숨김
            return "";
        }
        catch
        {
            // 문제 생기면 원문이라도 표시(디버깅용)
            return text;
        }
    }

    static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        // Unity 4.2 호환: OS 폰트 동적 로더 API가 없을 수 있어 new Font 사용
        _font = new Font("Malgun Gothic");
        if (_font == null || _font.material == null)
        {
            // fallback (환경 따라 Malgun Gothic이 로드 안 될 수 있음)
            _font = new Font("Arial");
        }

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
        if (src == null) return "";

        string v;
        if (_dict.TryGetValue(src, out v))
            return v;

        // 못 찾으면 원문 표시(진행은 되게)
        return src;
    }

    static void LoadTSV(string path)
    {
        // 포맷: 원문\t번역문
        // UTF-8로 저장 권장
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

            // 중복 키는 마지막 값을 사용
            _dict[k] = v;
        }
    }

    static void RenderOverlay(object labelObject, string text)
    {
        GameObject go = TryGetGameObject(labelObject);
        if (go == null) return;

        int id = go.GetInstanceID();

        TextMesh tm;
        if (!_overlay.TryGetValue(id, out tm) || tm == null)
        {
            var child = new GameObject("KRText");

            // Unity 4.2 호환: SetParent 없음 -> parent로
            child.transform.parent = go.transform;
            child.transform.localPosition = Vector3.zero;
            child.transform.localRotation = Quaternion.identity;
            child.transform.localScale = Vector3.one;

            tm = child.AddComponent<TextMesh>();
            tm.font = _font;
            tm.anchor = TextAnchor.UpperLeft;
            tm.alignment = TextAlignment.Left;
            tm.richText = true;

            // 기본값(나중에 화면 보고 조절)
            tm.characterSize = 0.05f;
            tm.fontSize = 48;
            tm.color = Color.white;

            // 핑크/안보임 방지: Renderer.material을 폰트 머티리얼로 맞춰줌
            Renderer r = child.GetComponent<Renderer>();
            if (r != null && _font != null && _font.material != null)
            {
                r.material = _font.material;

                // Unity 4.2 호환: sortingOrder 없음 -> renderQueue로 앞으로
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

        // 프로젝트 스타일상 gameObj를 들고 있을 확률이 높음
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
        // "^C" + 8 hex = 총 10글자 접두
        if (s != null && s.StartsWith("^C") && s.Length >= 10)
            return s.Substring(10);
        return s;
    }

    static string GetGameRoot()
    {
        // Application.dataPath = ...\HuniePop_Data
        // root = ...\HuniePop
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
