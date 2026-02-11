# extract_field_paths.py
import argparse
import csv
import os
import sys

def sniff_delimiter(path: str) -> str:
    # 간단 휴리스틱: 탭이 더 많으면 TSV로
    with open(path, "r", encoding="utf-8", errors="replace", newline="") as f:
        sample = f.read(4096)
    tab = sample.count("\t")
    comma = sample.count(",")
    if tab > comma:
        return "\t"
    # csv.Sniffer로 한 번 더 시도
    try:
        dialect = csv.Sniffer().sniff(sample, delimiters=[",", "\t", ";"])
        return dialect.delimiter
    except Exception:
        return ","  # fallback

def find_field_path_key(fieldnames):
    if not fieldnames:
        return None
    # 정확히 field_path 우선, 없으면 비슷한 이름도 허용(예: FieldPath)
    lowered = {name.lower(): name for name in fieldnames if name}
    if "field_path" in lowered:
        return lowered["field_path"]
    # 혹시 다른 표기(예: fieldpath)로 들어있을 때 대비
    for k in lowered:
        if k.replace("_", "") == "fieldpath":
            return lowered[k]
    return None

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="inp", required=True, help="입력 CSV/TSV 경로 (예: hp2_translations.csv, master_strings.tsv)")
    ap.add_argument("--out", dest="out", default=None, help="출력 파일 경로(기본: <입력>.field_paths.txt)")
    ap.add_argument("--no-sort", action="store_true", help="정렬하지 않고 발견 순서 유지")
    args = ap.parse_args()

    inp = args.inp
    if not os.path.exists(inp):
        print(f"[ERROR] 파일이 없습니다: {inp}", file=sys.stderr)
        sys.exit(1)

    delim = sniff_delimiter(inp)

    out_path = args.out
    if not out_path:
        out_path = inp + ".field_paths.txt"

    seen = set()
    ordered = []  # 발견 순서 유지용

    with open(inp, "r", encoding="utf-8", errors="replace", newline="") as f:
        reader = csv.DictReader(f, delimiter=delim)
        key = find_field_path_key(reader.fieldnames)
        if not key:
            print("[ERROR] 헤더에서 'field_path' 컬럼을 찾지 못했습니다.", file=sys.stderr)
            print(f"        감지된 헤더: {reader.fieldnames}", file=sys.stderr)
            sys.exit(2)

        for row in reader:
            v = (row.get(key) or "").strip()
            if not v:
                continue
            if v in seen:
                continue
            seen.add(v)
            ordered.append(v)

    result = ordered if args.no_sort else sorted(ordered)

    with open(out_path, "w", encoding="utf-8", newline="\n") as w:
        for v in result:
            w.write(v + "\n")

    print("[OK] done")
    print(f"  input: {inp}")
    print(f"  delimiter: {repr(delim)}")
    print(f"  unique field_path: {len(result)}")
    print(f"  out: {out_path}")

if __name__ == "__main__":
    main()
