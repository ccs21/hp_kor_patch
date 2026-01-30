import argparse
import json
import os
import re
from typing import Any, Dict, List, Optional

import UnityPy


def typetree(obj) -> Optional[Dict[str, Any]]:
    try:
        return obj.read_typetree()
    except Exception:
        return None


def pptr_to_ref(pptr) -> Dict[str, int]:
    try:
        return {"file_id": int(getattr(pptr, "file_id", 0)), "path_id": int(getattr(pptr, "path_id", 0))}
    except Exception:
        return {"file_id": 0, "path_id": 0}


def resolve_pptr(pptr) -> Optional[Any]:
    try:
        return pptr.get_obj() if pptr is not None else None
    except Exception:
        return None


def get_name(tt: Dict[str, Any]) -> str:
    return str(tt.get("m_Name", "") or "")


def has_any(tt: Dict[str, Any], keys: List[str]) -> bool:
    return any(k in tt for k in keys)


def first_key(tt: Dict[str, Any], keys: List[str]) -> Any:
    for k in keys:
        if k in tt:
            return tt.get(k)
    return None


def find_maintex_from_material_tt(mat_tt: Dict[str, Any]) -> Optional[Any]:
    sp = mat_tt.get("m_SavedProperties")
    if not isinstance(sp, dict):
        return None
    texenvs = sp.get("m_TexEnvs")
    if not isinstance(texenvs, list):
        return None
    for env in texenvs:
        if not isinstance(env, dict):
            continue
        if env.get("first") == "_MainTex":
            second = env.get("second")
            if isinstance(second, dict):
                return second.get("m_Texture")
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--assets", required=True)
    ap.add_argument("--dep", action="append", default=[])
    ap.add_argument("--out", default="dump_out")
    ap.add_argument("--name-regex", default="")
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    name_re = re.compile(args.name_regex, re.IGNORECASE) if args.name_regex else None

    env = UnityPy.Environment()
    env.load_file(args.assets)
    for d in args.dep:
        if os.path.exists(d):
            env.load_file(d)

    # 진단: 전체 타입 개수
    type_counts: Dict[str, int] = {}
    for o in env.objects:
        type_counts[o.type.name] = type_counts.get(o.type.name, 0) + 1

    mono_total = type_counts.get("MonoBehaviour", 0)
    script_total = type_counts.get("MonoScript", 0)
    if args.verbose:
        print("[INFO] Type counts (top):")
        for k in sorted(type_counts, key=lambda x: -type_counts[x])[:15]:
            print(f"  {k}: {type_counts[k]}")
        print(f"[INFO] MonoBehaviour={mono_total}, MonoScript={script_total}")

    fonts = []
    fontdatas = []
    materials = {}
    textures = {}

    # 1) MonoBehaviour 중 이름으로 후보 좁히고, 필드 패턴으로 tk2dFont / tk2dFontData 판정
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue

        tt = typetree(obj)
        if not tt:
            continue

        name = get_name(tt)
        if name_re and not name_re.search(name):
            continue

        # tk2dFontData 판정: chars / lineHeight / useDictionary 같은 필드가 흔함
        is_fontdata = has_any(tt, ["chars", "lineHeight", "useDictionary"]) and not has_any(tt, ["m_Script"])  # 일부는 m_Script만 있고 typetree가 비정상일 수 있음
        # 위가 너무 빡세면 chars만으로도 잡게 보조
        if "chars" in tt:
            is_fontdata = True

        # tk2dFont 판정: fontData/data 같은 PPtr 필드가 흔함
        is_font = has_any(tt, ["fontData", "data", "m_FontData", "m_fontData"])

        # 둘 다 걸리면 우선 tk2dFontData로 본다(실제로 FontData가 chars도 들고 있는 케이스가 드묾)
        if is_fontdata:
            tex_pptr = first_key(tt, ["texture", "tex", "m_Texture"])
            mat_pptr = first_key(tt, ["material", "m_Material", "m_material"])

            chars = tt.get("chars")
            chars_count = len(chars) if isinstance(chars, list) else None

            fontdatas.append({
                "path_id": int(obj.path_id),
                "name": name,
                "kind": "tk2dFontData_like",
                "version": tt.get("version"),
                "line_height": tt.get("lineHeight"),
                "use_dictionary": tt.get("useDictionary"),
                "chars_count": chars_count,
                "texture": pptr_to_ref(tex_pptr),
                "material": pptr_to_ref(mat_pptr),
            })

            # material/texture 추적
            if mat_pptr is not None:
                mobj = resolve_pptr(mat_pptr)
                if mobj is not None and mobj.type.name == "Material":
                    mtt = typetree(mobj) or {}
                    mpid = int(mobj.path_id)
                    if mpid not in materials:
                        sh_pptr = mtt.get("m_Shader")
                        sh_name = ""
                        if sh_pptr is not None:
                            sh_obj = resolve_pptr(sh_pptr)
                            if sh_obj is not None and sh_obj.type.name == "Shader":
                                sh_tt = typetree(sh_obj) or {}
                                sh_name = str(sh_tt.get("m_Name", "") or "")

                        main_tex = find_maintex_from_material_tt(mtt)
                        materials[mpid] = {
                            "path_id": mpid,
                            "name": str(mtt.get("m_Name", "") or ""),
                            "shader_name": sh_name,
                            "shader": pptr_to_ref(sh_pptr),
                            "main_tex": pptr_to_ref(main_tex),
                        }
                        if main_tex is not None:
                            tobj = resolve_pptr(main_tex)
                            if tobj is not None and tobj.type.name == "Texture2D":
                                tpid = int(tobj.path_id)
                                if tpid not in textures:
                                    ttt = typetree(tobj) or {}
                                    textures[tpid] = {"path_id": tpid, "name": str(ttt.get("m_Name", "") or "")}

            if tex_pptr is not None:
                tobj = resolve_pptr(tex_pptr)
                if tobj is not None and tobj.type.name == "Texture2D":
                    tpid = int(tobj.path_id)
                    if tpid not in textures:
                        ttt = typetree(tobj) or {}
                        textures[tpid] = {"path_id": tpid, "name": str(ttt.get("m_Name", "") or "")}

        elif is_font:
            fd_pptr = first_key(tt, ["fontData", "data", "m_FontData", "m_fontData"])
            mat_pptr = first_key(tt, ["material", "m_Material", "m_material"])

            fonts.append({
                "path_id": int(obj.path_id),
                "name": name,
                "kind": "tk2dFont_like",
                "font_data": pptr_to_ref(fd_pptr),
                "material": pptr_to_ref(mat_pptr),
            })

    dump = {
        "source_assets": os.path.abspath(args.assets),
        "dependencies": [os.path.abspath(d) for d in args.dep if os.path.exists(d)],
        "diagnostic": {
            "type_counts": type_counts,
            "monobehaviour_total": mono_total,
            "monoscript_total": script_total,
            "name_regex": args.name_regex,
            "matched_mono_by_name": len(fonts) + len(fontdatas)
        },
        "fonts": sorted(fonts, key=lambda x: x["name"]),
        "fontdatas": sorted(fontdatas, key=lambda x: x["name"]),
        "materials": sorted(materials.values(), key=lambda x: x["name"]),
        "textures": sorted(textures.values(), key=lambda x: x["name"]),
    }

    out_json = os.path.join(args.out, "tk2d_dump.json")
    with open(out_json, "w", encoding="utf-8") as f:
        json.dump(dump, f, ensure_ascii=False, indent=2)

    # chains
    fd_by_pid = {x["path_id"]: x for x in fontdatas}
    mat_by_pid = {x["path_id"]: x for x in materials.values()}

    lines: List[str] = []
    for f in sorted(fonts, key=lambda x: x["name"]):
        lines.append(f"[Font] {f['name']} (pid={f['path_id']})")
        fd_pid = f["font_data"]["path_id"]
        if fd_pid in fd_by_pid:
            fd = fd_by_pid[fd_pid]
            lines.append(f"  -> FontData: {fd['name']} (pid={fd_pid}) lineHeight={fd.get('line_height')} chars={fd.get('chars_count')}")
            mat_pid = (fd["material"]["path_id"] or f["material"]["path_id"])
            if mat_pid in mat_by_pid:
                md = mat_by_pid[mat_pid]
                lines.append(f"     -> Material: {md['name']} (pid={mat_pid}) shader='{md.get('shader_name','')}' mainTexPid={md.get('main_tex',{}).get('path_id',0)}")
            else:
                lines.append(f"     -> Material: unresolved pid={mat_pid}")
        else:
            lines.append(f"  -> FontData: unresolved pid={fd_pid}")
        lines.append("")

    out_txt = os.path.join(args.out, "tk2d_chains.txt")
    with open(out_txt, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

    print("[OK] written:")
    print(out_json)
    print(out_txt)
    print(f"Fonts={len(fonts)} FontDatas={len(fontdatas)} Materials={len(materials)} Textures={len(textures)}")
    print(f"[DIAG] MonoBehaviour total in env: {mono_total}, MonoScript total: {script_total}, matched by name: {len(fonts)+len(fontdatas)}")


if __name__ == "__main__":
    main()
