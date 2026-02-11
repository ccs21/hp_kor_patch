import argparse
import csv
import hashlib
import os
from pathlib import Path

def hash_file(path: Path, algo: str, chunk_size: int = 1024 * 1024) -> str:
    h = hashlib.new(algo)
    with path.open("rb") as f:
        while True:
            chunk = f.read(chunk_size)
            if not chunk:
                break
            h.update(chunk)
    return h.hexdigest()

def main():
    ap = argparse.ArgumentParser(description="Recursively hash files in a directory.")
    ap.add_argument("--dir", required=True, help="Target directory")
    ap.add_argument("--algo", default="sha256", help="Hash algorithm: sha256/sha1/md5 ... (default: sha256)")
    ap.add_argument("--out", default="hashes.csv", help="Output CSV path (default: hashes.csv)")
    ap.add_argument("--follow-symlinks", action="store_true", help="Follow symlinks (default: no)")
    ap.add_argument("--sort", action="store_true", help="Sort output by relative path")
    args = ap.parse_args()

    root = Path(args.dir).resolve()
    if not root.exists() or not root.is_dir():
        raise SystemExit(f"[ERROR] Not a directory: {root}")

    rows = []
    for dirpath, dirnames, filenames in os.walk(root, followlinks=args.follow_symlinks):
        for name in filenames:
            p = Path(dirpath) / name
            try:
                st = p.stat()
                rel = str(p.relative_to(root)).replace("\\", "/")
                digest = hash_file(p, args.algo)
                rows.append([rel, st.st_size, digest])
            except Exception as e:
                # 읽기 불가/권한 문제 등은 에러로 기록
                rel = str(p.relative_to(root)).replace("\\", "/")
                rows.append([rel, "", f"ERROR: {type(e).__name__}: {e}"])

    if args.sort:
        rows.sort(key=lambda x: x[0])

    out_path = Path(args.out).resolve()
    with out_path.open("w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["relative_path", "size_bytes", args.algo])
        w.writerows(rows)

    print(f"[OK] dir  : {root}")
    print(f"[OK] files: {len(rows)}")
    print(f"[OK] out  : {out_path}")

if __name__ == "__main__":
    main()
