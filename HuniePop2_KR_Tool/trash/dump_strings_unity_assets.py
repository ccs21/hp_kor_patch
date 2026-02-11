# dump_strings_unity_assets.py
# UnityPy로 _Data 폴더의 assets/level 파일들을 순회하며 모든 string 필드를 TSV로 덤프합니다.
#
# 설치:
#   pip install UnityPy==1.10.18
#
# 실행:
#   py dump_strings_unity_assets.py --data "D:\PC Games\HuniePop 2 - Double Date\HuniePop 2 - Double Date_Data" --out master_strings.tsv
#
# 옵션:
#   --no-filter        : 최소 필터(빈 값만 제외)로 더 많이 뽑기
#   --include-gm       : globalgamemanagers도 포함
#   --keep-newlines    : 실제 개행 유지(기본은 \n 이스케이프)

import argparse
import re
from pathlib import Path

import UnityPy

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


def iter_target_files(data_dir: Path, include_globalgamemanagers: bool):
    patterns = ["resources.assets", "sharedassets*.assets", "level*"]
    if include_globalgamemanagers:
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


def walk_typetree(value, base_path: str = ""):
    if isinstance(value, dict):
        for k, v in value.items():
            next_path = f"{base_path}.{k}" if base_path else str(k)
            yield from walk_typetree(v, next_path)
    elif isinstance(value, list):
        for i, v in enumerate(value):
            next_path = f"{base_path}[{i}]"
            yield from walk_typetree(v, next_path)
    else:
        if isinstance(value, str):
            yield (base_path, value)


def safe_script_name(obj) -> str:
    # MonoBehaviour면 m_Script를 최대한 추정. 실패해도 빈 문자열 반환.
    try:
        if obj.type.name != "MonoBehaviour":
            return ""
        data = obj.read()
        try:
            scr = data.m_Script
            scr_obj = scr.read()
            for attr in ("m_Name", "name", "className"):
                if hasattr(scr_obj, attr):
                    v = getattr(scr_obj, attr)
                    if isinstance(v, str) and v.strip():
                        return v.strip()
        except Exception:
            pass
        if hasattr(data, "m_Name") and isinstance(data.m_Name, str):
            return data.m_Name.strip()
    except Exception:
        pass
    return ""


def escape_value(s: str, keep_newlines: bool) -> str:
    if keep_newlines:
        return s
    return s.replace("\r\n", "\n").replace("\r", "\n").replace("\n", r"\n")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", required=True, help="..._Data 폴더 경로")
    ap.add_argument("--out", default="master_strings.tsv", help="출력 TSV 경로")
    ap.add_argument("--no-filter", action="store_true", help="최소 필터(빈 값만 제외)")
    ap.add_argument("--include-gm", action="store_true", help="globalgamemanagers도 포함")
    ap.add_argument("--keep-newlines", action="store_true", help="실제 개행 유지(기본은 \\n 이스케이프)")
    args = ap.parse_args()

    data_dir = Path(args.data)
    if not data_dir.exists():
        raise SystemExit(f"[ERROR] data dir not found: {data_dir}")

    targets = list(iter_target_files(data_dir, args.include_gm))
    if not targets:
        raise SystemExit("[ERROR] no target files found in data dir")

    out_path = Path(args.out)

    total_rows = 0
    files_scanned = 0
    files_failed = 0

    with open(out_path, "w", encoding="utf-8", newline="") as f:
        f.write("container\tpath_id\ttype\tscript\tfield_path\tvalue\n")

        for asset_path in targets:
            container = asset_path.name

            # 1) 파일 로드
            try:
                env = UnityPy.load(str(asset_path))
            except Exception as e:
                files_failed += 1
                print(f"[WARN] failed to load: {container} ({e})")
                continue

            # 2) 파일 스캔
            obj_count = 0
            hit_count = 0

            for obj in env.objects:
                obj_count += 1
                try:
                    path_id = getattr(obj, "path_id", "")
                    type_name = obj.type.name
                    script_name = safe_script_name(obj)

                    try:
                        tt = obj.read_typetree()
                    except Exception:
                        continue

                    for field_path, val in walk_typetree(tt):
                        if not isinstance(val, str):
                            continue
                        if not val.strip():
                            continue
                        if (not args.no_filter) and is_obvious_noise(val):
                            continue

                        out_val = escape_value(val, args.keep_newlines)
                        out_val = out_val.replace("\t", "    ")
                        out_fp = field_path.replace("\t", " ")

                        f.write(
                            f"{container}\t{path_id}\t{type_name}\t{script_name}\t{out_fp}\t{out_val}\n"
                        )
                        total_rows += 1
                        hit_count += 1

                except Exception:
                    # 오브젝트 1개에서 터져도 계속 진행
                    continue

            files_scanned += 1
            print(f"[OK] scanned: {container}  objects={obj_count}  strings={hit_count}")

    print("\n=== DONE ===")
    print(f"out: {out_path.resolve()}")
    print(f"files scanned: {files_scanned}")
    print(f"files failed:  {files_failed}")
    print(f"rows written:  {total_rows}")


if __name__ == "__main__":
    main()
