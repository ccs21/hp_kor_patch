using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Reflection;
using UnityEngine;

public static class KRHook
{
    static bool _loaded;

    static string _root;
    static string _krDir;
    static string _dumpPath;
    static string _errLogPath;

    // 덤프 중복 방지: 같은 라벨 인스턴스는 너무 많이 찍지 않게
    static readonly Dictionary<int, float> _lastDumpTimeById = new Dictionary<int, float>(512);

    // “어떤 필드/프로퍼티가 있나” 확인용: 타입별로 1회 전체 멤버 덤프
    static readonly HashSet<string> _dumpedTypes = new HashSet<string>();

    // 너무 자주 찍히면 로그가 폭발하니까 라벨별 최소 간격
    public static float DumpCooldownSeconds = 0.50f;

    // patched into LabelObject.SetText(string)
    public static string OnSetText(object labelObject, string text)
    {
        try
        {
            EnsureLoaded();

            // null 방어
            if (labelObject != null)
            {
                // 라벨 인스턴스 id(가능하면 transform/gameobj 기반)
                int id = TryGetStableId(labelObject);

                float now = Time.realtimeSinceStartup;
                float last;
                if (!_lastDumpTimeById.TryGetValue(id, out last) || (now - last) >= DumpCooldownSeconds)
                {
                    _lastDumpTimeById[id] = now;
                    UIDump(labelObject, text);
                }
            }

            // 지금 단계에서는 번역/오버레이 목적이 아님.
            // 원문은 일단 그대로 보여야 테스트가 쉬움.
            return text;
        }
        catch (Exception ex)
        {
            LogError("OnSetText", ex, text);
            return text;
        }
    }

    static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        _root = GetGameRoot();
        _krDir = Path.Combine(_root, "HuniePop_KR");
        _dumpPath = Path.Combine(_krDir, "ui_dump.log");
        _errLogPath = Path.Combine(_krDir, "krhook_error.log");

        try
        {
            if (!Directory.Exists(_krDir))
                Directory.CreateDirectory(_krDir);
        }
        catch { }

        SafeAppend(_dumpPath,
            "==== KRHook UI DUMP START ====\n" +
            "time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
            "dataPath=" + Application.dataPath + "\n" +
            "unity=" + Application.unityVersion + "\n" +
            "================================\n\n"
        );
    }

    static string GetGameRoot()
    {
        try { return Directory.GetParent(Application.dataPath).FullName; }
        catch { return "."; }
    }

    static int TryGetStableId(object labelObject)
    {
        // transform/gameObj가 있으면 그 InstanceID를 쓰는 게 가장 안정적
        Transform tr = TryGetTransform(labelObject);
        if (tr != null) return tr.GetInstanceID();

        GameObject go = TryGetGameObject(labelObject);
        if (go != null) return go.GetInstanceID();

        return labelObject.GetHashCode();
    }

    static void UIDump(object labelObject, string incomingText)
    {
        Type t = labelObject.GetType();
        string typeName = t.FullName ?? t.Name;

        // 타입별 전체 멤버 목록은 1번만 뽑아두면 나중에 “정답 필드” 찾기 쉬움
        if (!_dumpedTypes.Contains(typeName))
        {
            _dumpedTypes.Add(typeName);
            DumpTypeMembersOnce(t);
        }

        Transform tr = TryGetTransform(labelObject);
        GameObject go = (tr != null) ? tr.gameObject : TryGetGameObject(labelObject);

        string goName = (go != null) ? go.name : "(null)";
        string goPath = (go != null) ? GetHierarchyPath(go.transform) : "(null)";

        // 핵심 후보들: 좌표/크기/가시성/알파/스케일/레이어
        string[] interestingNames = new string[]
        {
            // 위치/크기
            "x","y","z","localX","localY","localZ","posX","posY","posZ",
            "width","height","localWidth","localHeight","w","h",
            "anchorX","anchorY","pivotX","pivotY",
            "bounds","rect",

            // 스케일
            "scale","scaleX","scaleY","localScale","scaleFactor",

            // 가시성/알파
            "visible","isVisible","alpha","childrenAlpha","opacity",
            "enabled","active","activeSelf","activeInHierarchy",

            // 레이어/정렬
            "depth","layer","sortingOrder","order","zOrder",

            // 부모/컨테이너
            "parent","container","owner","root","panel"
        };

        var sb = new StringBuilder(2048);
        sb.AppendLine("---- [" + DateTime.Now.ToString("HH:mm:ss.fff") + "] OnSetText ----");
        sb.AppendLine("Type: " + typeName);
        sb.AppendLine("GO: " + goName);
        sb.AppendLine("Path: " + goPath);

        if (tr != null)
        {
            Vector3 wp = tr.position;
            Vector3 lp = tr.localPosition;
            sb.AppendLine("Transform.position: " + wp.x.ToString("0.###") + ", " + wp.y.ToString("0.###") + ", " + wp.z.ToString("0.###"));
            sb.AppendLine("Transform.localPosition: " + lp.x.ToString("0.###") + ", " + lp.y.ToString("0.###") + ", " + lp.z.ToString("0.###"));
            Vector3 ls = tr.localScale;
            sb.AppendLine("Transform.localScale: " + ls.x.ToString("0.###") + ", " + ls.y.ToString("0.###") + ", " + ls.z.ToString("0.###"));
        }

        // Unity GameObject 활성 상태도 같이
        if (go != null)
        {
            sb.AppendLine("GO.activeSelf=" + go.activeSelf + " activeInHierarchy=" + go.activeInHierarchy + " layer=" + go.layer);
            var rd = go.GetComponent<Renderer>();
            if (rd != null)
                sb.AppendLine("Renderer.enabled=" + rd.enabled + " isVisible=" + rd.isVisible);
        }

        // 들어온 텍스트(원문)
        sb.AppendLine("IncomingText: " + EscapeForLog(incomingText));

        // 후보 멤버 값 덤프
        sb.AppendLine("[Interesting members]");
        DumpInterestingMembers(labelObject, t, interestingNames, sb);

        // 부모 체인 추정: parent/container가 있으면 몇 단계만 따라가서 localX/Y 변화를 볼 수 있게
        sb.AppendLine("[Parent chain probe]");
        DumpParentChain(labelObject, sb);

        sb.AppendLine();

        SafeAppend(_dumpPath, sb.ToString());
    }

    static void DumpTypeMembersOnce(Type t)
    {
        try
        {
            var sb = new StringBuilder(4096);
            sb.AppendLine("==== TYPE MEMBERS: " + (t.FullName ?? t.Name) + " ====");

            // 프로퍼티
            PropertyInfo[] props = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            sb.AppendLine("Properties(" + props.Length + "):");
            for (int i = 0; i < props.Length; i++)
            {
                var p = props[i];
                sb.AppendLine("  P " + p.Name + " : " + (p.PropertyType != null ? p.PropertyType.FullName : "?"));
            }

            // 필드
            FieldInfo[] fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            sb.AppendLine("Fields(" + fields.Length + "):");
            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                sb.AppendLine("  F " + f.Name + " : " + (f.FieldType != null ? f.FieldType.FullName : "?"));
            }

            sb.AppendLine("==== END TYPE MEMBERS ====\n");
            SafeAppend(_dumpPath, sb.ToString());
        }
        catch (Exception ex)
        {
            LogError("DumpTypeMembersOnce", ex, t.FullName);
        }
    }

    static void DumpInterestingMembers(object obj, Type t, string[] names, StringBuilder sb)
    {
        // 이름을 소문자 비교로 유연하게 매칭
        var nameSet = new HashSet<string>();
        for (int i = 0; i < names.Length; i++) nameSet.Add(names[i].ToLowerInvariant());

        // 프로퍼티 우선
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        for (int i = 0; i < props.Length; i++)
        {
            var p = props[i];
            if (p == null) continue;
            if (!nameSet.Contains(p.Name.ToLowerInvariant())) continue;

            object v = null;
            bool ok = TryGetProperty(obj, p, out v);
            sb.AppendLine("  P " + p.Name + " = " + (ok ? SafeToString(v) : "(unreadable)"));
        }

        // 필드
        var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        for (int i = 0; i < fields.Length; i++)
        {
            var f = fields[i];
            if (f == null) continue;
            if (!nameSet.Contains(f.Name.ToLowerInvariant())) continue;

            object v = null;
            bool ok = TryGetField(obj, f, out v);
            sb.AppendLine("  F " + f.Name + " = " + (ok ? SafeToString(v) : "(unreadable)"));
        }
    }

    static void DumpParentChain(object obj, StringBuilder sb)
    {
        // parent/container/owner 같은 멤버를 찾아서 최대 6단계까지만 따라감
        object cur = obj;
        for (int depth = 0; depth < 6; depth++)
        {
            if (cur == null) break;

            Type t = cur.GetType();
            string tn = t.FullName ?? t.Name;

            object parent = FindFirstParentLike(cur, t);
            sb.AppendLine("  [" + depth + "] " + tn + " -> parent=" + (parent != null ? (parent.GetType().FullName ?? parent.GetType().Name) : "(null)"));

            if (parent == null) break;
            cur = parent;
        }
    }

    static object FindFirstParentLike(object obj, Type t)
    {
        // 후보 순서: parent, container, owner, root, panel
        string[] keys = new string[] { "parent", "container", "owner", "root", "panel" };

        // 프로퍼티
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        for (int k = 0; k < keys.Length; k++)
        {
            string key = keys[k];
            for (int i = 0; i < props.Length; i++)
            {
                var p = props[i];
                if (p == null) continue;
                if (!string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)) continue;

                object v;
                if (TryGetProperty(obj, p, out v))
                    return v;
            }
        }

        // 필드
        var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        for (int k = 0; k < keys.Length; k++)
        {
            string key = keys[k];
            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                if (f == null) continue;
                if (!string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase)) continue;

                object v;
                if (TryGetField(obj, f, out v))
                    return v;
            }
        }

        return null;
    }

    static bool TryGetProperty(object obj, PropertyInfo p, out object value)
    {
        value = null;
        try
        {
            if (p.GetIndexParameters() != null && p.GetIndexParameters().Length > 0)
                return false;

            value = p.GetValue(obj, null);
            return true;
        }
        catch { return false; }
    }

    static bool TryGetField(object obj, FieldInfo f, out object value)
    {
        value = null;
        try
        {
            value = f.GetValue(obj);
            return true;
        }
        catch { return false; }
    }

    static string SafeToString(object v)
    {
        if (v == null) return "(null)";

        // 숫자/불리언 등 단순 타입은 그대로
        Type t = v.GetType();
        if (t.IsPrimitive || v is decimal || v is string)
            return v.ToString();

        // Vector/Color 같은 건 보기 좋게
        if (v is Vector2)
        {
            Vector2 a = (Vector2)v;
            return "Vector2(" + a.x.ToString("0.###") + "," + a.y.ToString("0.###") + ")";
        }
        if (v is Vector3)
        {
            Vector3 a = (Vector3)v;
            return "Vector3(" + a.x.ToString("0.###") + "," + a.y.ToString("0.###") + "," + a.z.ToString("0.###") + ")";
        }
        if (v is Color)
        {
            Color c = (Color)v;
            return "Color(" + c.r.ToString("0.###") + "," + c.g.ToString("0.###") + "," + c.b.ToString("0.###") + "," + c.a.ToString("0.###") + ")";
        }

        // 그 외는 타입명만
        return "<" + (t.FullName ?? t.Name) + ">";
    }

    static string EscapeForLog(string s)
    {
        if (s == null) return "(null)";
        return s.Replace("\r", "\\r").Replace("\n", "\\n");
    }

    static void SafeAppend(string path, string text)
    {
        try { File.AppendAllText(path, text, Encoding.UTF8); }
        catch { }
    }

    static string GetHierarchyPath(Transform tr)
    {
        if (tr == null) return "(null)";
        var names = new List<string>(16);
        Transform cur = tr;
        int guard = 0;
        while (cur != null && guard++ < 32)
        {
            names.Add(cur.name);
            cur = cur.parent;
        }
        names.Reverse();
        return string.Join("/", names.ToArray());
    }

    static Transform TryGetTransform(object labelObject)
    {
        if (labelObject == null) return null;

        try
        {
            Type t = labelObject.GetType();

            // property: transform
            var pTr = t.GetProperty("transform", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (pTr != null)
            {
                object v;
                if (TryGetProperty(labelObject, pTr, out v))
                    return v as Transform;
            }

            // field: transform
            var fTr = t.GetField("transform", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fTr != null)
            {
                object v;
                if (TryGetField(labelObject, fTr, out v))
                    return v as Transform;
            }

            // property/field: gameObj -> transform
            GameObject go = TryGetGameObject(labelObject);
            if (go != null) return go.transform;
        }
        catch (Exception ex)
        {
            LogError("TryGetTransform", ex, "");
        }

        return null;
    }

    static GameObject TryGetGameObject(object labelObject)
    {
        if (labelObject == null) return null;

        try
        {
            Type t = labelObject.GetType();

            var pGo = t.GetProperty("gameObj", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (pGo != null)
            {
                object v;
                if (TryGetProperty(labelObject, pGo, out v))
                    return v as GameObject;
            }

            var fGo = t.GetField("gameObj", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fGo != null)
            {
                object v;
                if (TryGetField(labelObject, fGo, out v))
                    return v as GameObject;
            }
        }
        catch { }

        return null;
    }

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
