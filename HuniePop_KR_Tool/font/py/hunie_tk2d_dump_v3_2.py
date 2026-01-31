#!/usr/bin/env python3
# -*- coding: utf-8 -*-

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
    # tk2dFontData-like heuristic (HuniePop 덤프 기준)
    if not isinstance(tree, dict):
        return False
    must = ["version", "lineHeight", "chars", "material"]
    return all(k in tree for k in must)


def pptr_tuple(pptr: Any) -> Optional[Tuple[int, int]]:
    if not isinstance(pptr, dict):
        return None
    fid = pptr.get("m_FileID")
    pid = pptr.get("m_PathID")
    if isinstance(fid, int) and isinstance(pid, int):
        return fid, pid
    return None


def find_maintex_from_material_tt(mat_tt: Dict[str, Any]) -> Optional[Any]:
    sp = mat_tt.get("m_SavedProperties")
    if not isinstance(sp, dict):
        return None
    texenvs = sp.get("m_TexEnvs")
    if not isinstance(texenvs, list):
        return None

    for entry in texenvs:
        if not isinstance(entry, dict):
            continue
        first = entry.get("first")
        second = entry.get("second")
        name = None
        if isinstance(first, dict):
            name = first.get("name") or first.get("m_Name") or first.get("data")
        if name == "_MainTex" and isinstance(second, dict):
            return second.get("m_Texture") or second.get("m_Texture2D") or second.get("texture")
    return None


def get_material_info(obj_by_pid: Dict[int, Any], mat_pathid: int) -> Dict[str, Any]:
    info = {
        "material_pathid": mat_pathid,
        "material_name": None,
        "shader_name": None,
        "maintex_name": None,
        "maintex_pathid": None,
    }

    obj = obj_by_pid.get(mat_pathid)
    if obj is None or obj.type.name != "Material":
        return info

    data = obj.read_typetree()
    info["material_name"] = safe_get(data, "m_Name")

    # shader name
    shader_pptr = safe_get(data, "m_Shader")
    st = pptr_tuple(shader_pptr)
    if st and st[1]:
        sh_obj = obj_by_pid.get(st[1])
        if sh_obj and sh_obj.type.name == "Shader":
            sh_tree = sh_obj.read_typetree()
            info["shader_name"] = safe_get(sh_tree, "m_Name")

    # main texture
    maintex_pptr = find_maintex_from_material_tt(data)
    mt = pptr_tuple(maintex_pptr) if maintex_pptr else None
    if mt and mt[1]:
        tex_obj = obj_by_pid.get(mt[1])
        if tex_obj and tex_obj.type.name == "Texture2D":
            tex_tree = tex_obj.read_typetree()
            info["maintex_name"] = safe_get(tex_tree, "m_Name")
            info["maintex_pathid"] = mt[1]

    return info


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--assets", required=True, help="sharedassets0.assets path")
    ap.add_argument("--dep", action="append", default=[], help="dependency assets (resources.assets etc). repeatable")
    ap.add_argument("--out", required=True, help="output directory")
    ap.add_argument("--name-regex", default=r"^font_exo_", help="regex for Texture2D m_Name to filter")
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    tex_re = re.compile(args.name_regex, re.IGNORECASE)

    env = UnityPy.Environment()
    env.load_file(args.assets)
    for d in args.dep:
        if os.path.exists(d):
            env.load_file(d)

    # ✅ UnityPy 버전마다 env.objects가 list이거나 dict일 수 있어서 통일
    objs = env.objects
    if isinstance(objs, dict):
        obj_list = list(objs.values())
    else:
        obj_list = list(objs)

    # ✅ path_id -> object 맵 (Material/Texture/Shader resolve용)
    obj_by_pid: Dict[int, Any] = {}
    for o in obj_list:
        try:
            obj_by_pid[int(o.path_id)] = o
        except Exception:
            pass

    results = []
    total_mono = 0
    fontdata_like = 0

    for obj in obj_list:
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

        chars_count = None
        chars = tree.get("chars")
        if isinstance(chars, dict) and "Array" in chars and isinstance(chars["Array"], list):
            chars_count = len(chars["Array"])
        elif isinstance(chars, list):
            chars_count = len(chars)

        mat_pptr = tree.get("material")
        mt = pptr_tuple(mat_pptr)
        mat_pathid = mt[1] if mt else 0

        mat_info = get_material_info(obj_by_pid, mat_pathid) if mat_pathid else {}
        maintex_name = mat_info.get("maintex_name")

        if not (maintex_name and tex_re.search(maintex_name)):
            continue

        rec = {
            "mono_pathid": int(obj.path_id),
            "mono_name": tree.get("m_Name", ""),  # 대부분 빈 문자열일 가능성 큼
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
            print(f"[HIT] mono={rec['mono_pathid']} lineH={line_height} chars={chars_count} "
                  f"mat='{rec['material_name']}' tex='{maintex_name}' shader='{rec['shader_name']}'")

    out_json = os.path.join(args.out, "tk2d_fontdata_dump.json")
    out_csv = os.path.join(args.out, "tk2d_fontdata_dump.csv")

    payload = {
        "source_assets": os.path.abspath(args.assets),
        "dependencies": [os.path.abspath(d) for d in args.dep if os.path.exists(d)],
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
