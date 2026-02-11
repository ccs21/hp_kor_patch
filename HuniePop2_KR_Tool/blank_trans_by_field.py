# blank_trans_by_field.py
# hp2_translations.translated.csv 에서 특정 field_path 행들의 trans 값을 전부 비웁니다.
#
# 사용법:
#   python blank_trans_by_field.py --in hp2_translations.translated.csv --out hp2_translations.translated.blanked.csv
#   (out 생략 시: <in파일명>.blanked.csv 로 저장)

import argparse
import os
import re
import pandas as pd


TARGET_PATTERNS = [
    # 버릴 필드들
    "girlName",
    "shoesAdj",
    "uniqueAdj",
    "hairstyles[*].hairstyleName",
    "outfits[*].outfitName",
    "specialParts[*].specialPartName",
    "specialLabels[*].labelText",
    "itemName",
    "locationName",
    "itemDescription",
    "onMessage",
    "girlNickName",
    "categoryDescription",
]


def compile_patterns(patterns):
    """
    - exact 문자열은 완전일치
    - [*] 는 [숫자] 인덱스로 매칭 (예: hairstyles[0].hairstyleName)
    """
    regs = []
    for p in patterns:
        if "[*]" in p:
            # regex escape 후 \[\*\] 부분만 \[\d+\] 로 치환
            esc = re.escape(p)
            esc = esc.replace(r"\[\*\]", r"\[\d+\]")
            regs.append(re.compile(rf"^{esc}$"))
        else:
            regs.append(re.compile(rf"^{re.escape(p)}$"))
    return regs


COMPILED = compile_patterns(TARGET_PATTERNS)


def matches_any(field_path: str) -> bool:
    if field_path is None:
        return False
    for rgx in COMPILED:
        if rgx.match(field_path):
            return True
    return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="in_path", required=True, help="input csv path")
    ap.add_argument("--out", dest="out_path", default="", help="output csv path")
    args = ap.parse_args()

    in_path = args.in_path
    out_path = args.out_path.strip()
    if not out_path:
        base, ext = os.path.splitext(in_path)
        out_path = f"{base}.blanked{ext or '.csv'}"

    df = pd.read_csv(in_path, dtype=str, keep_default_na=False)

    if "field_path" not in df.columns or "trans" not in df.columns:
        raise SystemExit(
            f"[ERROR] CSV must contain columns: field_path, trans. Found: {list(df.columns)}"
        )

    mask = df["field_path"].apply(matches_any)
    changed = int(mask.sum())

    df.loc[mask, "trans"] = ""

    df.to_csv(out_path, index=False, encoding="utf-8-sig")
    print(f"[OK] written: {out_path}")
    print(f"  rows: {len(df)}")
    print(f"  blanked trans rows (matched field_path): {changed}")
    print("  patterns:")
    for p in TARGET_PATTERNS:
        print(f"   - {p}")


if __name__ == "__main__":
    main()
