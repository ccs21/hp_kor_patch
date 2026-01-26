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
    static HashSet<string> _pending = new HashSet<string>();

    // Overlay cache (LabelObject gameObj instance id -> TextMesh)
    static readonly Dictionary<int, TextMesh> _overlay = new Dictionary<int, TextMesh>();

    static string _root;
    static string _krDir;
    static string _tsvPath;
    static string _pendingPath;
    static string _errLogPath;

    static Font _font;

    // =========================================================
    // Entry point: patched into LabelObject.SetText(string)
    // =========================================================
    public static string OnSetText(object labelObject, string text)
    {
        try
        {
            EnsureLoaded();

            if (string.IsNullOrEmpty(text))
                return text;

            // remove HuniePop color code + normalize
            string src = Normalize(StripColorCode(text));

            string tr;
            bool has = _dict.TryGetValue(src, out tr) && !string.IsNullOrEmpty(tr);

            string toShow = has ? tr : src;
            if (!has) AddPending(src);

            // Try render overlay (TTF)
            bool rendered = RenderOverlay(labelObject, toShow);

            // If overlay succeeded, hide original bitmap font text
            if (rendered)
                return "";

            // fallback: show original (English) if overlay failed
            return text;
        }
        catch (Exception ex)
        {
            LogError("OnSetText", ex, text);
            return text;
        }
    }

    // =========================================================
    // Init
    // =========================================================
    static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        _root = GetGameRoot();
        _krDir = Path.Combine(_root, "HuniePop_KR");
        _tsvPath = Path.Combine(_krDir, "translations.tsv");
        _pendingPath = Path.Combine(_krDir, "pending.tsv");
        _errLogPath = Path.Combine(_krDir, "krhook_error.log");

        try
        {
            if (!Directory.Exists(_krDir))
                Directory.CreateDirectory(_krDir);
        }
        catch { }

        // Load font (Unity 4.2 safe)
        try
        {
            _font = new Font("Malgun Gothic");
            if (object.ReferenceEquals(_font, null))
                _font = new Font("Arial");
        }
        catch (Exception ex)
        {
            LogError("LoadFont", ex, "");
            _font = null;
        }

        try
        {
            if (File.Exists(_tsvPath))
                LoadTSV(_tsvPath);
        }
        catch (Exception ex)
        {
            LogError("LoadTSV", ex, _tsvPath);
        }

        try
        {
            if (File.Exists(_pendingPath))
                LoadPendingKeys(_pendingPath);
        }
        catch (Exception ex)
        {
            LogError("LoadPendingKeys", ex, _pendingPath);
        }
    }

    // =========================================================
    // TSV
    // =========================================================
    static void LoadTSV(string path)
    {
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);

        for (int i = 0; i < lines.Length; i++)
        {
            string lineRaw = lines[i];
            if (string.IsNullOrEmpty(lineRaw)) continue;
            if (lineRaw.StartsWith("#")) continue;

            int tab = lineRaw.IndexOf('\t');
            if (tab <= 0) continue;

            string k = lineRaw.Substring(0, tab);
            string v = lineRaw.Substring(tab + 1);

            k = Normalize(UnescapeKey(k));
            v = UnescapeValue(v);

            if (k.Length == 0) continue;
            _dict[k] = v;
        }
    }

    static void LoadPendingKeys(string path)
    {
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);

        for (int i = 0; i < lines.Length; i++)
        {
            string lineRaw = lines[i];
            if (string.IsNullOrEmpty(lineRaw)) continue;
            if (lineRaw.StartsWith("#")) continue;

            int tab = lineRaw.IndexOf('\t');
            string k = (tab >= 0) ? lineRaw.Substring(0, tab) : lineRaw;

            k = Normalize(UnescapeKey(k));
            if (k.Length == 0) continue;
            _pending.Add(k);
        }
    }

    static void AddPending(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (_pending.Contains(key)) return;

        _pending.Add(key);

        string safe = EscapeKey(key);
        try
        {
            File.AppendAllText(_pendingPath, safe + "\t\n", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LogError("AddPending", ex, safe);
        }
    }

    // =========================================================
    // Overlay (TextMesh)
    // =========================================================
    static bool RenderOverlay(object labelObject, string text)
    {
        try
        {
            GameObject host = TryGetGameObject(labelObject);
            if (object.ReferenceEquals(host, null))
                return false;

            int id = host.GetInstanceID();

            TextMesh tm;
            if (!_overlay.TryGetValue(id, out tm) || object.ReferenceEquals(tm, null))
            {
                if (object.ReferenceEquals(_font, null))
                    return false;

                GameObject child = new GameObject("KRText");
                child.transform.parent = host.transform;

                // Put it slightly forward so it's not hidden behind
                child.transform.localPosition = new Vector3(0f, 0f, -10f);
                child.transform.localRotation = Quaternion.identity;
                child.transform.localScale = Vector3.one;

                tm = child.AddComponent<TextMesh>();
                tm.text = "가나다ABC123";

                tm.font = _font;

                tm.anchor = TextAnchor.UpperLeft;
                tm.alignment = TextAlignment.Left;
                tm.richText = true;

                // reasonable defaults (you can tune later)
                tm.characterSize = 0.2f;
                tm.fontSize = 120;
                tm.color = Color.white;

                // Ensure material exists & assign
// 레이어를 부모와 동일하게
child.layer = host.layer;

Renderer r = child.GetComponent<Renderer>();
if (!object.ReferenceEquals(r, null))
{
    // Unity 4.2에서 폰트 기본 material이 안 보이는 경우가 많아서
    // GUI/Text Shader로 강제 머티리얼 생성 후 텍스처만 폰트에서 가져온다.
    try
    {
        Shader sh = Shader.Find("GUI/Text Shader");
        if (!object.ReferenceEquals(sh, null))
        {
            Material mat = new Material(sh);

            // 폰트 텍스처를 연결(이게 중요)
            if (!object.ReferenceEquals(_font, null) &&
                !object.ReferenceEquals(_font.material, null) &&
                !object.ReferenceEquals(_font.material.mainTexture, null))
            {
                mat.mainTexture = _font.material.mainTexture;
            }

            // 맨 위로 뜨게
            mat.renderQueue = 5000;

            r.material = mat;
        }
        else
        {
            // 셰이더 못 찾으면 폰트 material이라도 사용
            if (!object.ReferenceEquals(_font, null) && !object.ReferenceEquals(_font.material, null))
            {
                r.material = _font.material;
                if (!object.ReferenceEquals(r.material, null))
                    r.material.renderQueue = 5000;
            }
        }

        // 렌더러 강제 ON
        r.enabled = true;
    }
    catch (Exception ex)
    {
        LogError("RendererMaterial", ex, "");
    }
}


                // Request a few characters (helps on some old Unity builds)
                try
                {
                    _font.RequestCharactersInTexture("가나다라마바사아자차카타파하ABCabc123", tm.fontSize, FontStyle.Normal);
                }
                catch { }

                _overlay[id] = tm;
            }

            tm.text = text ?? "";
            return true;
        }
        catch (Exception ex)
        {
            LogError("RenderOverlay", ex, text);
            return false;
        }
    }

    static GameObject TryGetGameObject(object labelObject)
    {
        if (object.ReferenceEquals(labelObject, null)) return null;

        Type t = labelObject.GetType();

        // Property: gameObj
        var pGameObj = t.GetProperty("gameObj");
        if (!object.ReferenceEquals(pGameObj, null))
        {
            object vObj = null;
            try { vObj = pGameObj.GetValue(labelObject, null); }
            catch (Exception ex) { LogError("TryGetGameObject.gameObj(get)", ex, ""); }

            GameObject go = vObj as GameObject;
            if (!object.ReferenceEquals(go, null))
                return go;
        }

        // Field: gameObj
        var fGameObj = t.GetField("gameObj");
        if (!object.ReferenceEquals(fGameObj, null))
        {
            object vObj = null;
            try { vObj = fGameObj.GetValue(labelObject); }
            catch (Exception ex) { LogError("TryGetGameObject.gameObj(field)", ex, ""); }

            GameObject go = vObj as GameObject;
            if (!object.ReferenceEquals(go, null))
                return go;
        }

        // Property: transform
        var pTr = t.GetProperty("transform");
        if (!object.ReferenceEquals(pTr, null))
        {
            object vObj = null;
            try { vObj = pTr.GetValue(labelObject, null); }
            catch (Exception ex) { LogError("TryGetGameObject.transform(get)", ex, ""); }

            Transform tr = vObj as Transform;
            if (!object.ReferenceEquals(tr, null))
                return tr.gameObject;
        }

        // Field: transform
        var fTr = t.GetField("transform");
        if (!object.ReferenceEquals(fTr, null))
        {
            object vObj = null;
            try { vObj = fTr.GetValue(labelObject); }
            catch (Exception ex) { LogError("TryGetGameObject.transform(field)", ex, ""); }

            Transform tr = vObj as Transform;
            if (!object.ReferenceEquals(tr, null))
                return tr.gameObject;
        }

        return null;
    }

    // =========================================================
    // String helpers
    // =========================================================
    static string StripColorCode(string s)
    {
        // ^C + 8 hex = 10 chars
        if (!string.IsNullOrEmpty(s) && s.StartsWith("^C") && s.Length >= 10)
            return s.Substring(10);
        return s;
    }

    static string Normalize(string s)
    {
        if (object.ReferenceEquals(s, null)) return "";

        // BOM 제거
        if (s.Length > 0 && s[0] == '\uFEFF')
            s = s.Substring(1);

        s = s.Replace("\r\n", "\n").Replace("\r", "\n");
        s = s.Trim();
        return s;
    }

    static string EscapeKey(string s)
    {
        if (object.ReferenceEquals(s, null)) return "";
        return s.Replace("\\", "\\\\").Replace("\n", "\\n");
    }

    static string UnescapeKey(string s)
    {
        if (object.ReferenceEquals(s, null)) return "";
        // order matters
        s = s.Replace("\\n", "\n");
        s = s.Replace("\\\\", "\\");
        return s;
    }

    static string UnescapeValue(string s)
    {
        if (object.ReferenceEquals(s, null)) return "";
        s = s.Replace("\\n", "\n");
        s = s.Replace("\\\\", "\\");
        return s;
    }

    static string GetGameRoot()
    {
        try
        {
            // Application.dataPath == ...\HuniePop_Data
            return Directory.GetParent(Application.dataPath).FullName;
        }
        catch
        {
            return ".";
        }
    }

    // =========================================================
    // Error log
    // =========================================================
    static void LogError(string where, Exception ex, string sample)
    {
        try
        {
            string dir = _krDir;
            if (string.IsNullOrEmpty(dir))
            {
                dir = Path.Combine(GetGameRoot(), "HuniePop_KR");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }

            string path = _errLogPath;
            if (string.IsNullOrEmpty(path))
                path = Path.Combine(dir, "krhook_error.log");

            var sb = new StringBuilder();
            sb.AppendLine("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ====");
            sb.AppendLine("WHERE: " + where);
            sb.AppendLine(ex.ToString());
            if (!string.IsNullOrEmpty(sample))
            {
                sb.AppendLine("TEXT_SAMPLE:");
                sb.AppendLine(sample.Replace("\r", "\\r").Replace("\n", "\\n"));
            }
            sb.AppendLine();

            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }
}
