import argparse
import json
import os
import re
from dataclasses import dataclass, asdict
from typing import Any, Dict, List, Optional, Tuple

import UnityPy


def safe_get(obj: Any, key: str, default=None):
    try:
        if obj is None:
            return default
        if isinstance(obj, dict):
            return obj.get(key, default)
        # UnityPy typetree objects support attribute-like access sometimes
        return getattr(obj, key, default)
    except Exception:
        return default


def pptr_to_ref(pptr) -> Dict[str, int]:
    # UnityPy PPtr usually has file_id/path_id
    try:
        return {"file_id": int(getattr(pptr, "file_id", 0)), "path_id": int(getattr(pptr, "path_id", 0))}
    except Exception:
        return {"file_id": 0, "path_id": 0}


def ref_key(file_id: int, path_id: int) -> Tuple[int, int]:
    return (int(file_id), int(path_id))


def resolve_pptr(env: UnityPy.Environment, pptr) -> Optional[Any]:
    """Try to resolve PPtr to an object. Returns UnityPy object or None."""
    try:
        if pptr is None:
            return None
        pid = getattr(pptr, "path_id", 0)
        if not pid:
            return None
        # UnityPy can resolve when dependencies are loaded into same Environment
        return pptr.get_obj()
    except Exception:
        return None


def typetree(obj) -> Optional[Dict[str, Any]]:
    try:
        return obj.read_typetree()
    except Exception:
        return None


@dataclass
class MonoEntry:
    path_id: int
    name: str
    class_name: str


@dataclass
class Tk2dFontDump:
    path_id: int
    name: str
    class_name: str
    font_data: Dict[str, int]
    material: Dict[str, int]


@dataclass
class Tk2dFontDataDump:
    path_id: int
    name: str
    class_name: str
    version: Optional[int]
    line_height: Optional[float]
    use_dictionary: Optional[int]
    chars_count: Optional[int]
    texture: Dict[str, int]
    material: Dict[str, int]


@dataclass
class MaterialDump:
    path_id: int
    name: str
    shader: Dict[str, int]
    shader_name: str
    main_tex: Dict[str, int]


@dataclass
class TextureDump:
    path_id: int
    name: str


def get_object_name(obj) -> str:
    tt = typetree(obj)
    if not tt:
        return ""
    return str(tt.get("m_Name", "") or "")


def get_monoscript_class_name(env: UnityPy.Environment, mono_tt: Dict[str, Any]) -> str:
    """
    MonoBehaviour typetree has m_Script PPtr. Resolve MonoScript and read m_ClassName, m_Namespace.
    """
    ms_pptr = mono_tt.get("m_Script")
    if ms_pptr is None:
        return ""

    ms_obj = resolve_pptr(env, ms_pptr)
    if ms_obj is None:
        return ""

    ms_tt = typetree(ms_obj)
    if not ms_tt:
        return ""

    ns = str(ms_tt.get("m_Namespace", "") or "")
    cn = str(ms_tt.get("m_ClassName", "") or "")
    if not cn:
        return ""
    return f"{ns}.{cn}".strip(".") if ns else cn


def find_maintex_from_material_tt(mat_tt: Dict[str, Any]) -> Optional[Any]:
    """
    Material typetree:
      m_SavedProperties -> m_TexEnvs = [ { first: "_MainTex", second: { m_Texture: PPtr, m_Scale, m_Offset } }, ... ]
    Returns PPtr or None.
    """
    sp = mat_tt.get("m_SavedProperties")
    if not isinstance(sp, dict):
        return None
    texenvs = sp.get("m_TexEnvs")
    if not isinstance(texenvs, list):
        return None

    for env in texenvs:
        if not isinstance(env, dict):
            continue
        key = env.get("first")
        if key == "_MainTex":
            second = env.get("second")
            if isinstance(second, dict):
                return second.get("m_Texture")
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--assets", required=True, help="path to sharedassets0.assets (or any .assets)")
    ap.add_argument("--dep", action="append", default=[], help="dependency assets (resources.assets etc). repeatable")
    ap.add_argument("--out", default="dump_out", help="output directory")
    ap.add_argument("--name-regex", default="", help="filter by object name (regex, case-insensitive). e.g. font_exo_")
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)

    # Load into one Environment so PPtr resolving works best.
    env = UnityPy.Environment()
    env.load_file(args.assets)
    for d in args.dep:
        if os.path.exists(d):
            env.load_file(d)

    name_re = re.compile(args.name_regex, re.IGNORECASE) if args.name_regex else None

    tk2d_fonts: List[MonoEntry] = []
    tk2d_fontdatas: List[MonoEntry] = []

    # First pass: find MonoBehaviours that are tk2dFont / tk2dFontData
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue

        mono_tt = typetree(obj)
        if not mono_tt:
            continue

        cls = get_monoscript_class_name(env, mono_tt)
        if not cls:
            continue

        # only interested in tk2dFont / tk2dFontData
        short = cls.split(".")[-1]
        if short not in ("tk2dFont", "tk2dFontData"):
            continue

        name = str(mono_tt.get("m_Name", "") or "")
        if name_re and not name_re.search(name):
            continue

        entry = MonoEntry(path_id=int(obj.path_id), name=name, class_name=cls)
        if short == "tk2dFont":
            tk2d_fonts.append(entry)
        else:
            tk2d_fontdatas.append(entry)

    # Build quick lookup by (file_id,path_id) for Materials/Textures/Shaders from loaded env
    # UnityPy objects all live in env; PPtr.get_obj() should work if deps loaded.
    font_dumps: List[Tk2dFontDump] = []
    fontdata_dumps: List[Tk2dFontDataDump] = []
    material_dumps: Dict[int, MaterialDump] = {}
    texture_dumps: Dict[int, TextureDump] = {}

    # helper to fetch object by path_id (within env)
    obj_by_pid: Dict[int, Any] = {int(o.path_id): o for o in env.objects}

    def add_texture_if_present(pptr):
        t_obj = resolve_pptr(env, pptr)
        if t_obj is None:
            return
        if t_obj.type.name != "Texture2D":
            return
        pid = int(t_obj.path_id)
        if pid not in texture_dumps:
            texture_dumps[pid] = TextureDump(path_id=pid, name=get_object_name(t_obj))

    def add_material_dump(mat_pptr):
        mat_obj = resolve_pptr(env, mat_pptr)
        if mat_obj is None or mat_obj.type.name != "Material":
            return

        pid = int(mat_obj.path_id)
        if pid in material_dumps:
            return

        mat_tt = typetree(mat_obj) or {}
        mat_name = str(mat_tt.get("m_Name", "") or "")

        shader_pptr = mat_tt.get("m_Shader")
        shader_name = ""
        if shader_pptr is not None:
            sh_obj = resolve_pptr(env, shader_pptr)
            if sh_obj is not None and sh_obj.type.name == "Shader":
                sh_tt = typetree(sh_obj) or {}
                shader_name = str(sh_tt.get("m_Name", "") or "")

        main_tex_pptr = find_maintex_from_material_tt(mat_tt)
        if main_tex_pptr is not None:
            add_texture_if_present(main_tex_pptr)

        material_dumps[pid] = MaterialDump(
            path_id=pid,
            name=mat_name,
            shader=pptr_to_ref(shader_pptr) if shader_pptr is not None else {"file_id": 0, "path_id": 0},
            shader_name=shader_name,
            main_tex=pptr_to_ref(main_tex_pptr) if main_tex_pptr is not None else {"file_id": 0, "path_id": 0},
        )

    # Dump tk2dFont (best-effort field candidates)
    for f in tk2d_fonts:
        o = obj_by_pid.get(f.path_id)
        if o is None:
            continue
        tt = typetree(o) or {}

        # field name candidates (tk2d versions마다 다름)
        fontdata_pptr = tt.get("fontData") or tt.get("data") or tt.get("m_FontData") or tt.get("m_fontData")
        material_pptr = tt.get("material") or tt.get("m_Material") or tt.get("m_material")

        fd_ref = pptr_to_ref(fontdata_pptr)
        mat_ref = pptr_to_ref(material_pptr)

        font_dumps.append(
            Tk2dFontDump(
                path_id=f.path_id,
                name=f.name,
                class_name=f.class_name,
                font_data=fd_ref,
                material=mat_ref,
            )
        )

        if material_pptr is not None:
            add_material_dump(material_pptr)

    # Dump tk2dFontData 핵심 값
    for fd in tk2d_fontdatas:
        o = obj_by_pid.get(fd.path_id)
        if o is None:
            continue
        tt = typetree(o) or {}

        version = tt.get("version")
        line_height = tt.get("lineHeight")
        use_dict = tt.get("useDictionary")

        chars = tt.get("chars")
        chars_count = len(chars) if isinstance(chars, list) else None

        tex_pptr = tt.get("texture") or tt.get("tex") or tt.get("m_Texture")
        mat_pptr = tt.get("material") or tt.get("m_Material") or tt.get("m_material")

        fontdata_dumps.append(
            Tk2dFontDataDump(
                path_id=fd.path_id,
                name=fd.name,
                class_name=fd.class_name,
                version=int(version) if isinstance(version, int) else (int(version) if version is not None else None),
                line_height=float(line_height) if line_height is not None else None,
                use_dictionary=int(use_dict) if use_dict is not None else None,
                chars_count=chars_count,
                texture=pptr_to_ref(tex_pptr),
                material=pptr_to_ref(mat_pptr),
            )
        )

        if mat_pptr is not None:
            add_material_dump(mat_pptr)
        if tex_pptr is not None:
            add_texture_if_present(tex_pptr)

    dump = {
        "source_assets": os.path.abspath(args.assets),
        "dependencies": [os.path.abspath(d) for d in args.dep if os.path.exists(d)],
        "fonts": [asdict(x) for x in sorted(font_dumps, key=lambda x: x["name"] if isinstance(x, dict) else x.name)],
        "fontdatas": [asdict(x) for x in sorted(fontdata_dumps, key=lambda x: x.name)],
        "materials": [asdict(x) for x in sorted(material_dumps.values(), key=lambda x: x.name)],
        "textures": [asdict(x) for x in sorted(texture_dumps.values(), key=lambda x: x.name)],
    }

    out_json = os.path.join(args.out, "tk2d_dump.json")
    with open(out_json, "w", encoding="utf-8") as f:
        json.dump(dump, f, ensure_ascii=False, indent=2)

    # chain text
    fd_by_pid = {x.path_id: x for x in fontdata_dumps}
    mat_by_pid = {x.path_id: x for x in material_dumps.values()}

    def fmt_ref(r: Dict[str, int]) -> str:
        return f"(file={r.get('file_id',0)} pid={r.get('path_id',0)})"

    lines: List[str] = []
    for f in sorted(font_dumps, key=lambda x: x.name):
        lines.append(f"[Font] {f.name} (pid={f.path_id})")
        fd_pid = f.font_data.get("path_id", 0)
        if fd_pid and fd_pid in fd_by_pid:
            fdd = fd_by_pid[fd_pid]
            lines.append(f"  -> FontData: {fdd.name} (pid={fdd.path_id}) v={fdd.version} lineHeight={fdd.line_height} chars={fdd.chars_count}")
            # prefer fontdata.material else font.material
            mat_pid = fdd.material.get("path_id", 0) or f.material.get("path_id", 0)
            if mat_pid and mat_pid in mat_by_pid:
                md = mat_by_pid[mat_pid]
                lines.append(f"     -> Material: {md.name} (pid={md.path_id}) shader='{md.shader_name}' mainTexPid={md.main_tex.get('path_id',0)}")
            else:
                lines.append(f"     -> Material: unresolved {fmt_ref(fdd.material) if fdd.material.get('path_id',0) else fmt_ref(f.material)}")
        else:
            lines.append(f"  -> FontData: unresolved {fmt_ref(f.font_data)}")
        lines.append("")

    out_txt = os.path.join(args.out, "tk2d_chains.txt")
    with open(out_txt, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

    print("[OK] written:")
    print(out_json)
    print(out_txt)
    print(f"Fonts={len(font_dumps)} FontDatas={len(fontdata_dumps)} Materials={len(material_dumps)} Textures={len(texture_dumps)}")


if __name__ == "__main__":
    main()
