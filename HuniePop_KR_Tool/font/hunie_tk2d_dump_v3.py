#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
HuniePop tk2dFontData dump helper (Unity 4.x)
- Finds unnamed tk2dFontData-like MonoBehaviours by structure (typetree keys)
- Resolves linked Material -> Shader -> _MainTex(Texture2D) names
- Filters by texture name regex (e.g., ^font_exo_)
Outputs:
  - tk2d_fontdata_dump.json
  - tk2d_fontdata_dump.csv
"""

import argparse
import json
import os
import re
import csv
from typing import Any, Dict, Optional, Tuple

import UnityPy


def safe_get(d: Dict[str, Any], key: str, default=None):
    return d.get(key, default) if isinstance(d, dict) else default


def is_fontdata_tree(tree: Dict[str, Any]) -> bool:
    """
    Heuristic: tk2dFontData usually contains these keys in HuniePop dump:
      version, lineHeight, chars (array), material (PPtr), useDictionary, etc.
    """
    if not isinstance(tree, dict):
        return False
    must = ["version", "lineHeight", "chars", "material"]
    return all(k in tree for k in must)


def pptr_tuple(pptr: Any) -> Optional[Tuple[int, int]]:
    """
    UnityPy typetree PPtr looks like {'m_FileID':0,'m_PathID':20} or similar.
    """
    if not isinstance(pptr, dict):
        return None
    fid = pptr.get("m_FileID")
    pid = pptr.get("m_PathID")
    if isinstance(fid, int) and isinstance(pid, int):
        return fid, pid
    return None


def resolve_object_by_pathid(env: UnityPy.Environment, path_id: int):
    # env.objects is dict[path_id] -> ObjectReader
    return env.objects.get(path_id)


def get_material_info(env: UnityPy.Environment, mat_pathid: int) -> Dict[str, Any]:
    info = {"material_pathid": mat_pathid, "material_name": None, "shader_name": None, "maintex_name": None, "maintex_pathid": None}
    obj = resolve_object_by_pathid(env, mat_pathid)
    if obj is None:
        return info
    if obj.type.name != "Material":
        return info

    data = obj.read_typetree()
    info["material_name"] = safe_get(data, "m_Name")

    # shader
    shader_pptr = safe_get(data, "m_Shader")
    st = pptr_tuple(shader_pptr)
    if st and st[1]:
        sh_obj = resolve_object_by_pathid(env, st[1])
        if sh_obj and sh_obj.type.name == "Shader":
            sh_tree = sh_obj.read_typetree()
            info["shader_name"] = safe_get(sh_tree, "m_Name")

    # main texture: try to find _MainTex in m_SavedProperties.m_TexEnvs
    saved = safe_get(data, "m_SavedProperties", {})
    texenvs = safe_get(saved, "m_TexEnvs", [])
    maintex_pptr = None

    # texenvs entries often look like: {'first': {'name': '_MainTex'}, 'second': {'m_Texture': {...}, 'm_Scale':..., 'm_Offset':...}}
    if isinstance(texenvs, list):
        for entry in texenvs:
            if not isinstance(entry, dict):
                continue
            first = entry.get("first")
            second = entry.get("second")
            name = None
            if isinstance(first, dict):
                name = first.get("name") or first.get("m_Name") or first.get("data")
            if name == "_MainTex" and isinstance(second, dict):
                maintex_pptr = second.get("m_Texture") or second.get("m_Texture2D") or second.get("texture")
                break

    # fallback: sometimes m_Texture is directly under "_MainTex" style dict
    if maintex_pptr is None and isinstance(saved, dict):
        pass

    mt = pptr_tuple(maintex_pptr) if maintex_pptr else None
    if mt and mt[1]:
        tex_obj = resolve_object_by_pathid(env, mt[1])
        if tex_obj and tex_obj.type.name == "Texture2D":
            tex_tree = tex_obj.read_typetree()
            info["maintex_name"] = safe_get(tex_tree, "m_Name")
            info["maintex_pathid"] = mt[1]

    return info


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--assets", required=True, help="sharedassets0.assets path")
    ap.add_argument("--dep", action="append", default=[], help="dependency assets (resources.assets etc). Can be repeated.")
    ap.add_argument("--out", required=True, help="output directory")
    ap.add_argument("--name-regex", default=r"^font_exo_", help="regex for Texture2D m_Name to filter")
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    tex_re = re.compile(args.name_regex)

    # UnityPy: load main + deps together (important for PPtr resolution across files)
    paths = [args.assets] + (args.dep or [])
    env = UnityPy.load(paths)

    results = []
    total_mono = 0
    fontdata_like = 0
    matched = 0

    for path_id, obj in env.objects.items():
        if obj.type.name != "MonoBehaviour":
            continue
        total_mono += 1
        try:
            tree = obj.read_typetree()
        except Exception:
            continue

        if not is_fontdata_tree(tree):
            continue

        fontdata_like += 1

        line_height = tree.get("lineHeight")
        version = tree.get("version")
        chars = tree.get("chars")
        chars_count = None
        if isinstance(chars, dict) and "Array" in chars:
            arr = chars["Array"]
            if isinstance(arr, list):
                chars_count = len(arr)

        mat_pptr = tree.get("material")
        mt = pptr_tuple(mat_pptr)
        mat_pathid = mt[1] if mt else 0

        mat_info = get_material_info(env, mat_pathid) if mat_pathid else {}

        maintex_name = mat_info.get("maintex_name")
        if maintex_name and tex_re.search(maintex_name):
            matched += 1
        else:
            # filter out non-matching font textures
            continue

        rec = {
            "mono_pathid": path_id,
            "mono_name": tree.get("m_Name", ""),  # usually empty
            "version": version,
            "lineHeight": line_height,
            "chars_count": chars_count,
            "material_pathid": mat_info.get("material_pathid"),
            "material_name": mat_info.get("material_name"),
            "shader_name": mat_info.get("shader_name"),
            "maintex_pathid": mat_info.get("maintex_pathid"),
            "maintex_name": maintex_name,
        }
        results.append(rec)

        if args.verbose:
            print(f"[HIT] mono={path_id} lineH={line_height} chars={chars_count} mat={rec['material_name']} tex={maintex_name} shader={rec['shader_name']}")

    out_json = os.path.join(args.out, "tk2d_fontdata_dump.json")
    out_csv = os.path.join(args.out, "tk2d_fontdata_dump.csv")

    payload = {
        "source_assets": args.assets,
        "dependencies": args.dep or [],
        "stats": {
            "mono_total": total_mono,
            "fontdata_like": fontdata_like,
            "matched_by_texture_regex": len(results),
            "name_regex": args.name_regex,
        },
        "items": results,
    }

    with open(out_json, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False, indent=2)

    with open(out_csv, "w", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(
            f,
            fieldnames=[
                "mono_pathid", "version", "lineHeight", "chars_count",
                "material_pathid", "material_name", "shader_name",
                "maintex_pathid", "maintex_name",
            ],
        )
        w.writeheader()
        for r in results:
            w.writerow({k: r.get(k) for k in w.fieldnames})

    print("[OK] written:")
    print(" ", out_json)
    print(" ", out_csv)
    print(f"[STATS] mono_total={total_mono} fontdata_like={fontdata_like} matched={len(results)}")


if __name__ == "__main__":
    main()
