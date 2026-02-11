# diagnose_unitypy_monobehaviour.py
# MonoBehaviour를 UnityPy가 왜 못 읽는지 원인을 로그로 뽑는 진단 스크립트

import argparse
from pathlib import Path
from collections import Counter
import UnityPy

def iter_target_files(data_dir: Path):
    patterns = ["resources.assets", "sharedassets*.assets", "level*"]
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

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", required=True, help="..._Data 폴더 경로")
    ap.add_argument("--max-errors", type=int, default=10, help="파일당 에러 샘플 최대 개수")
    args = ap.parse_args()

    data_dir = Path(args.data)
    if not data_dir.exists():
        raise SystemExit(f"[ERROR] data dir not found: {data_dir}")

    print(f"[INFO] UnityPy version: {getattr(UnityPy, '__version__', 'unknown')}")
    print()

    for asset_path in iter_target_files(data_dir):
        name = asset_path.name
        try:
            env = UnityPy.load(str(asset_path))
        except Exception as e:
            print(f"[FAIL] load {name}: {e}")
            continue

        # 파일 메타
        unity_ver = getattr(env.file, "unity_version", "unknown")
        enable_tt = getattr(env.file, "_enable_type_tree", "unknown")  # 중요!
        print(f"=== {name} ===")
        print(f"  unity_version      : {unity_ver}")
        print(f"  enable_type_tree   : {enable_tt}")

        mono_total = 0
        mono_ok = 0
        mono_fail = 0
        fail_msgs = Counter()

        for obj in env.objects:
            if obj.type.name != "MonoBehaviour":
                continue

            mono_total += 1
            try:
                _ = obj.read_typetree()
                mono_ok += 1
            except Exception as e:
                mono_fail += 1
                msg = f"{type(e).__name__}: {e}"
                fail_msgs[msg] += 1

        print(f"  MonoBehaviour total: {mono_total}")
        print(f"  read_typetree ok   : {mono_ok}")
        print(f"  read_typetree fail : {mono_fail}")

        if mono_fail > 0:
            print("  fail samples:")
            for i, (m, c) in enumerate(fail_msgs.most_common(args.max_errors), 1):
                print(f"    {i:02d}) x{c}  {m}")

        print()

if __name__ == "__main__":
    main()
