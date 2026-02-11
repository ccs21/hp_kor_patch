# dump_strings_unity_assets_v2.py
# TypeTree가 없는(Unity 2019.4, enable_type_tree=False) 게임에서
# TypeTreeGeneratorAPI를 이용해 MonoBehaviour까지 포함하여 문자열을 전수 추출합니다.

import argparse
import re
from pathlib import Path
import UnityPy
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

RE_HEX_LONG = re.compile(r"^[0-9a-fA-F]{24,}$")
RE_GUID = re.compile(r"^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$")
RE_URL = re.compile(r"^(https?://|ftp://)", re.IGNORECASE)

def is_obvious_noise(s: str) -> bool:
    ss = s.strip()
    if not ss:
        return True
    if RE_URL.match(ss):
        return True
    if ("\\\\" in ss) or (":\\" in ss) or ("/" in ss and "." in ss and len(ss) > 8):
        if any(ext in ss.lower() for ext in [
            ".png", ".jpg", ".jpeg", ".tga", ".dds",
            ".wav", ".ogg", ".mp3",
            ".prefab", ".unity", ".mat", ".asset"
        ]):
            return True
    if RE_HEX_LONG.match(ss):
        return True
    if RE_GUID.match(ss):
        return True
    return False

def iter_target_files(data_dir: Path, include_gm: bool):
    patterns = ["resources.assets", "sharedassets*.assets", "level*"]
    if include_gm:
        patterns += ["globalgamemanagers", "globalgamemanagers.assets"]
    seen = set()
    for pat in patterns:
        for p in data_dir.glob(pat):
            if not p.is_file():
                continue
            if p.name.lower().endswith(".ress"):
                continue
            rp = p.resolve()
            if rp in seen:
                continue
            seen.add(rp)
            yield p

def walk_any(value, base=""):
    if isinstance(value, dict):
        for k, v in value.items():
            nxt = f"{base}.{k}" if base else str(k)
            yield from walk_any(v, nxt)
    elif isinstance(value, list):
        for i, v in enumerate(value):
            nxt = f"{base}[{i}]"
            yield from walk_any(v, nxt)
    else:
        if isinstance(value, str):
            yield base, value

def escape_value(s: str, keep_newlines: bool) -> str:
    if keep_newlines:
        return s
    return s.replace("\r\n", "\n").replace("\r", "\n").replace("\n", r"\n")

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", required=True, help="게임 루트(…\\HuniePop 2 - Double Date)")
    ap.add_argument("--data", required=True, help="…_Data 폴더 경로")
    ap.add_argument("--out", default="master_strings.tsv")
    ap.add_argument("--no-filter", action="store_true")
    ap.add_argument("--include-gm", action="store_true")
    ap.add_argument("--keep-newlines", action="store_true")
    args = ap.parse_args()

    game_root = Path(args.game)
    data_dir = Path(args.data)
    out_path = Path(args.out)

    if not game_root.exists():
        raise SystemExit(f"[ERROR] game root not found: {game_root}")
    if not data_dir.exists():
        raise SystemExit(f"[ERROR] data dir not found: {data_dir}")

    targets = list(iter_target_files(data_dir, args.include_gm))
    if not targets:
        raise SystemExit("[ERROR] no assets/level files found")

    # unity_version은 assets에서 하나 읽어서 가져오면 됩니다
    env0 = UnityPy.load(str(targets[0]))
    unity_ver = getattr(env0.file, "unity_version", "2019.4.3f1")
    print(f"[INFO] unity_version={unity_ver}  enable_type_tree={getattr(env0.file, '_enable_type_tree', None)}")

    # typetree generator 준비(핵심)
    gen = TypeTreeGenerator(unity_ver)
    gen.load_local_game(str(game_root))
    print("[INFO] typetree generator loaded")

    total_rows = 0
    files_scanned = 0
    files_failed = 0
    mono_ok_total = 0
    mono_fail_total = 0

    with open(out_path, "w", encoding="utf-8", newline="") as f:
        f.write("container\tpath_id\ttype\tfield_path\tvalue\n")

        for asset_path in targets:
            container = asset_path.name
            try:
                env = UnityPy.load(str(asset_path))
            except Exception as e:
                files_failed += 1
                print(f"[WARN] load fail: {container} ({e})")
                continue

            env.typetree_generator = gen  # 반드시 env마다 세팅
            obj_count = 0
            hit_count = 0
            mono_ok = 0
            mono_fail = 0

            for obj in env.objects:
                obj_count += 1
                path_id = getattr(obj, "path_id", "")
                tname = obj.type.name

                # MonoBehaviour는 parse_as_dict()로 (generator 활용)
                try:
                    if tname == "MonoBehaviour":
                        try:
                            tree = obj.parse_as_dict()
                            mono_ok += 1
                        except Exception:
                            mono_fail += 1
                            continue
                    else:
                        # 그 외 타입은 typetree가 있으면 read_typetree가 동작
                        try:
                            tree = obj.read_typetree()
                        except Exception:
                            continue
                except Exception:
                    continue

                for fp, val in walk_any(tree):
                    if not isinstance(val, str) or not val.strip():
                        continue
                    if (not args.no_filter) and is_obvious_noise(val):
                        continue
                    out_val = escape_value(val, args.keep_newlines).replace("\t", "    ")
                    out_fp = fp.replace("\t", " ")
                    f.write(f"{container}\t{path_id}\t{tname}\t{out_fp}\t{out_val}\n")
                    total_rows += 1
                    hit_count += 1

            files_scanned += 1
            mono_ok_total += mono_ok
            mono_fail_total += mono_fail
            print(f"[OK] scanned: {container} objects={obj_count} strings={hit_count} mono_ok={mono_ok} mono_fail={mono_fail}")

    print("\n=== DONE ===")
    print(f"out: {out_path.resolve()}")
    print(f"files scanned: {files_scanned}")
    print(f"files failed : {files_failed}")
    print(f"rows written : {total_rows}")
    print(f"mono ok/fail : {mono_ok_total}/{mono_fail_total}")

if __name__ == "__main__":
    main()
