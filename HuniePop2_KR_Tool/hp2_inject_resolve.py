# hp2_inject_resolve.py
# HuniePop 2 (Unity 2019.4.x) resources.assets 주입 전용 (orig_sha1 기반 '정확 위치 탐색' 포함)
# - path_id(MonoBehaviour) 단위로 1회 parse -> 다건 치환 -> 1회 save_typetree
# - field_path 불일치 시 스킵하지 않고, 같은 트리 내부에서 orig_sha1 일치 문자열 위치를 탐색해 주입
#
# CSV 스키마(필수 컬럼):
# key, path_id, field_path, type, orig, trans, orig_sha1

from __future__ import annotations

import argparse
import csv
import hashlib
import shutil
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import UnityPy  # type: ignore

# -------- 유틸 --------

def unescape_csv_newlines(s: str) -> str:
    # 기존 툴과 호환: "\n" 텍스트를 실제 줄바꿈으로 복구
    return (s or "").replace("\\n", "\n")

def escape_csv_newlines(s: str) -> str:
    return (s or "").replace("\r\n", "\n").replace("\r", "\n").replace("\n", "\\n")

def sha1_text(s: str) -> str:
    return hashlib.sha1(s.encode("utf-8")).hexdigest()

def parse_field_list_file(fp: Path) -> List[str]:
    out: List[str] = []
    for line in fp.read_text(encoding="utf-8", errors="ignore").splitlines():
        t = line.strip()
        if not t or t.startswith("#"):
            continue
        out.append(t)
    return out

def build_filter(allow_field_paths: Optional[set]) -> callable:
    if not allow_field_paths:
        return lambda fp: True
    return lambda fp: fp in allow_field_paths

# -------- field_path 접근/변환 --------
# 기존 field_path 표기: steps[32].dialogOptions[1].steps[0].dialogLine.dialogText
# 트리(dict/list)에서 동일 문법으로 get/set

def _parse_path_tokens(field_path: str) -> List[Tuple[str, Optional[int]]]:
    tokens: List[Tuple[str, Optional[int]]] = []
    if not field_path:
        return tokens
    parts = field_path.split(".")
    for p in parts:
        p = p.strip()
        if not p:
            continue
        if "[" in p and p.endswith("]"):
            name = p[: p.index("[")]
            idx_str = p[p.index("[") + 1 : -1]
            try:
                idx = int(idx_str)
            except ValueError:
                idx = None
            tokens.append((name, idx))
        else:
            tokens.append((p, None))
    return tokens

def get_value_by_field_path(tree: Any, field_path: str) -> Any:
    cur = tree
    for name, idx in _parse_path_tokens(field_path):
        if isinstance(cur, dict):
            if name not in cur:
                return None
            cur = cur[name]
        else:
            return None
        if idx is not None:
            if not isinstance(cur, list):
                return None
            if idx < 0 or idx >= len(cur):
                return None
            cur = cur[idx]
    return cur

def set_value_by_field_path(tree: Any, field_path: str, value: Any) -> bool:
    tokens = _parse_path_tokens(field_path)
    if not tokens:
        return False
    cur = tree
    for i, (name, idx) in enumerate(tokens):
        is_last = (i == len(tokens) - 1)
        if not isinstance(cur, dict) or name not in cur:
            return False
        if idx is None:
            if is_last:
                cur[name] = value
                return True
            cur = cur[name]
        else:
            arr = cur[name]
            if not isinstance(arr, list) or idx < 0 or idx >= len(arr):
                return False
            if is_last:
                arr[idx] = value
                return True
            cur = arr[idx]
    return False

# -------- 트리 문자열 leaf 탐색 --------

def iter_string_leaves(tree: Any, prefix: str = "") -> List[Tuple[str, str]]:
    """
    tree 내부 모든 str leaf를 (field_path, value)로 수집
    field_path 문법은 steps[0].x.y 처럼 맞춰서 생성
    """
    out: List[Tuple[str, str]] = []

    def rec(node: Any, path: str):
        if isinstance(node, str):
            out.append((path, node))
            return
        if isinstance(node, dict):
            for k, v in node.items():
                next_path = f"{path}.{k}" if path else str(k)
                rec(v, next_path)
            return
        if isinstance(node, list):
            for i, v in enumerate(node):
                next_path = f"{path}[{i}]" if path else f"[{i}]"
                rec(v, next_path)
            return

    rec(tree, prefix)
    return out

def normalize_leaf_path(p: str) -> str:
    # 우리가 생성한 list 표기 "[0]"를 기존 형식 "name[0]"에 맞추기 위한 보정은
    # dict 아래 list만 등장하므로, 생성 경로는 실제와 약간 다를 수 있음.
    # -> 이 스크립트는 dict 키 다음에 붙는 list는 "key[0]" 형태로 만들도록
    # iter_string_leaves 단계에서 dict->list로 내려갈 때는 dict 키가 path에 포함됨.
    # list 자체는 key 없이 단독 등장할 일이 거의 없어서 그대로 둠.
    return p

def path_similarity_score(want: str, cand: str) -> int:
    """
    간단 점수:
    - 마지막 필드명이 같으면 +200
    - dialogText/yuriDialogText 일치하면 +120
    - 공통 접두 길이(토큰 단위) * 3
    - 길이 차이 페널티
    """
    w = want or ""
    c = cand or ""
    score = 0

    w_last = w.split(".")[-1]
    c_last = c.split(".")[-1]
    if w_last == c_last:
        score += 200

    # dialogText / yuriDialogText 힌트
    for key in ("dialogText", "yuriDialogText"):
        if (key in w) and (key in c):
            score += 120

    # 토큰 공통 접두
    w_parts = w.replace("]", "").split(".")
    c_parts = c.replace("]", "").split(".")
    common = 0
    for a, b in zip(w_parts, c_parts):
        if a == b:
            common += 1
        else:
            break
    score += common * 3

    score -= abs(len(w) - len(c)) // 2
    return score

# -------- 메인 로직 --------

@dataclass
class RowItem:
    key: str
    path_id: str
    field_path: str
    orig_sha1: str
    trans: str

@dataclass
class Report:
    key: str
    path_id: str
    requested_field_path: str
    resolved_field_path: str
    status: str  # OK / RESOLVED / AMBIGUOUS / NOT_FOUND / SKIP_FILTER / BAD_ROW
    note: str

def load_typetree_generator(game_root: Path, unity_ver: str):
    """
    기존 프로젝트에 'TypeTreeGeneratorAPI'가 설치돼 있다는 전제(사용자 환경).
    hp2_extract_inject.py에서 쓰던 방식이 있으면 그걸 그대로 가져오는게 안전하지만,
    이 스크립트는 'UnityPy.helpers.TypeTreeGenerator'가 동작하는 환경을 기대합니다.
    """
    # UnityPy가 자동으로 typetree generator를 쓰는 환경이면 별도 작업 없이 진행 가능.
    return None

def load_resources_env(data_dir: Path, use_typetree: bool = True):
    """
    HuniePop 2 resources.assets 는 보통 type tree가 포함되지 않아(enable_type_tree=False),
    MonoBehaviour.parse_as_dict()가 실패합니다.
    따라서 기본값으로 use_typetree=True로 로드해서 TypeTreeGeneratorAPI 기반의
    type tree 생성(혹은 UnityPy의 내부 generator)을 강제합니다.
    """
    try:
        env = UnityPy.load(str(data_dir / "resources.assets"), use_typetree=use_typetree)
    except TypeError:
        # 구버전 UnityPy는 인자가 다를 수 있음
        env = UnityPy.load(str(data_dir / "resources.assets"))
    return env

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", required=True, help="HuniePop 2 - Double Date_Data 경로")
    ap.add_argument("--csv", required=True, help="번역 CSV (hp2_translations.req_translated.csv)")
    ap.add_argument("--outdir", required=True, help="출력 폴더 (patched_resolve 등)")
    ap.add_argument("--field-paths", default="", help="허용 field_path 리스트 파일(줄단위). 없으면 전체 허용")
    ap.add_argument("--no-orig-check", action="store_true", help="orig_sha1 검증/탐색을 끄고 field_path에 강제 주입(비추천)")
    args = ap.parse_args()

    data_dir = Path(args.data)
    in_csv = Path(args.csv)
    out_dir = Path(args.outdir)

    if not data_dir.exists():
        raise SystemExit(f"[ERROR] data dir not found: {data_dir}")
    if not in_csv.exists():
        raise SystemExit(f"[ERROR] csv not found: {in_csv}")

    allow_field_paths: Optional[set] = None
    if args.field_paths:
        fp = Path(args.field_paths)
        if not fp.exists():
            raise SystemExit(f"[ERROR] field_paths not found: {fp}")
        allow_field_paths = set(parse_field_list_file(fp))
    is_allowed = build_filter(allow_field_paths)

    # CSV 로드 (trans 비어있으면 무시)
    items: List[RowItem] = []
    with open(in_csv, "r", encoding="utf-8-sig", newline="") as f:
        r = csv.DictReader(f)
        for row in r:
            key = (row.get("key") or "").strip()
            pid = (row.get("path_id") or "").strip()
            fp = (row.get("field_path") or "").strip()
            trans = (row.get("trans") or "").strip()
            osh = (row.get("orig_sha1") or "").strip()
            if not key or not pid or not fp or not trans:
                continue
            if not is_allowed(fp):
                # 필터에 없으면 보고서에 남김
                items.append(RowItem(key, pid, fp, osh, ""))  # trans 빈값으로 표시
                continue
            items.append(RowItem(key, pid, fp, osh, unescape_csv_newlines(trans)))

    if not items:
        raise SystemExit("[ERROR] no rows to inject (trans empty or csv invalid)")

    # env 로드 (typetree 생성 강제)
    env = load_resources_env(data_dir, use_typetree=True)
    unity_ver = getattr(env.file, "unity_version", "2019.4.3f1")
    enable_tt = getattr(env.file, "_enable_type_tree", None)
    print(f"[INFO] unity_version={unity_ver} enable_type_tree={enable_tt}")


    # 파싱 가능 여부 사전 점검: typetree가 없으면 MonoBehaviour.parse_as_dict가 전부 실패합니다.
    test_ok = False
    test_err = None
    for o in env.objects:
        if o.type.name != "MonoBehaviour":
            continue
        try:
            _ = o.parse_as_dict()
            test_ok = True
            break
        except Exception as e:
            test_err = e
            continue

    if not test_ok:
        raise SystemExit(
            "[ERROR] MonoBehaviour.parse_as_dict()가 실패합니다. "
            "대부분 resources.assets에 type tree가 포함되지 않은(Strip) 빌드라서, "
            "UnityPy가 TypeTreeGeneratorAPI로 typetree를 생성할 수 있어야 주입이 가능합니다. "
            f"첫 오류: {test_err}"
        )


    # MonoBehaviour path_id -> obj
    obj_map: Dict[str, Any] = {}
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        pid = str(getattr(obj, "path_id", ""))
        if pid:
            obj_map[pid] = obj

    # pid 단위로 묶기
    by_pid: Dict[str, List[RowItem]] = {}
    for it in items:
        by_pid.setdefault(it.path_id, []).append(it)

    reports: List[Report] = []
    changed = 0
    not_found = 0
    ambiguous = 0
    skipped_filter = 0
    bad = 0

    for pid, rows in by_pid.items():
        obj = obj_map.get(pid)
        if obj is None:
            for it in rows:
                if it.trans == "":
                    skipped_filter += 1
                    reports.append(Report(it.key, it.path_id, it.field_path, "", "SKIP_FILTER", "not allowed by filter"))
                else:
                    not_found += 1
                    reports.append(Report(it.key, it.path_id, it.field_path, "", "NOT_FOUND", "path_id not found"))
            continue

        # 주입 대상이 하나도 없으면(전부 SKIP_FILTER) 파싱 자체를 하지 않음
        if all((it.trans == "") for it in rows):
            for it in rows:
                skipped_filter += 1
                reports.append(Report(it.key, it.path_id, it.field_path, "", "SKIP_FILTER", "not allowed by filter"))
            continue

        # parse 1회
        try:
            tree = obj.parse_as_dict()
        except Exception as e:
            for it in rows:
                bad += 1
                reports.append(Report(it.key, it.path_id, it.field_path, "", "BAD_ROW", f"parse_as_dict failed: {e}"))
            continue

        # string leaf 인덱스 준비 (orig_check를 쓸 때만)
        leaf_list: List[Tuple[str, str]] = []
        sha_to_paths: Dict[str, List[str]] = {}
        if not args.no_orig_check:
            leaf_list = [(normalize_leaf_path(p), v) for p, v in iter_string_leaves(tree)]
            for p, v in leaf_list:
                h = sha1_text(escape_csv_newlines(v))
                sha_to_paths.setdefault(h, []).append(p)

        # rows 처리
        obj_modified = False
        for it in rows:
            if it.trans == "":
                skipped_filter += 1
                reports.append(Report(it.key, it.path_id, it.field_path, "", "SKIP_FILTER", "not allowed by filter"))
                continue

            # 1) 먼저 요청 field_path에 직접 주입 시도
            direct_val = get_value_by_field_path(tree, it.field_path)

            if args.no_orig_check:
                ok = set_value_by_field_path(tree, it.field_path, it.trans)
                if ok:
                    obj_modified = True
                    changed += 1
                    reports.append(Report(it.key, it.path_id, it.field_path, it.field_path, "OK", "forced inject (no orig check)"))
                else:
                    not_found += 1
                    reports.append(Report(it.key, it.path_id, it.field_path, "", "NOT_FOUND", "field_path not found"))
                continue

            # orig_sha1 기반 검증/탐색
            if isinstance(direct_val, str):
                if sha1_text(escape_csv_newlines(direct_val)) == it.orig_sha1:
                    ok = set_value_by_field_path(tree, it.field_path, it.trans)
                    if ok:
                        obj_modified = True
                        changed += 1
                        reports.append(Report(it.key, it.path_id, it.field_path, it.field_path, "OK", "direct match"))
                    else:
                        not_found += 1
                        reports.append(Report(it.key, it.path_id, it.field_path, "", "NOT_FOUND", "set failed at requested field_path"))
                    continue

            # 2) direct 불일치 -> 같은 tree 안에서 sha1 일치 문자열 위치 탐색
            candidates = sha_to_paths.get(it.orig_sha1, [])
            if not candidates:
                not_found += 1
                reports.append(Report(it.key, it.path_id, it.field_path, "", "NOT_FOUND", "orig_sha1 not found in this MonoBehaviour"))
                continue

            # 후보 1개면 그곳에 주입
            if len(candidates) == 1:
                resolved = candidates[0]
                ok = set_value_by_field_path(tree, resolved, it.trans)
                if ok:
                    obj_modified = True
                    changed += 1
                    reports.append(Report(it.key, it.path_id, it.field_path, resolved, "RESOLVED", "resolved by orig_sha1 search"))
                else:
                    not_found += 1
                    reports.append(Report(it.key, it.path_id, it.field_path, resolved, "NOT_FOUND", "resolved path exists but set failed"))
                continue

            # 후보 여러 개 -> 유사도 점수로 선택
            scored = [(path_similarity_score(it.field_path, c), c) for c in candidates]
            scored.sort(key=lambda x: x[0], reverse=True)
            best_score, best_path = scored[0]
            # 동점 다수면 ambiguous
            tied = [c for s, c in scored if s == best_score]
            if len(tied) > 1:
                ambiguous += 1
                resolved = best_path
                ok = set_value_by_field_path(tree, resolved, it.trans)
                if ok:
                    obj_modified = True
                    changed += 1
                    reports.append(Report(
                        it.key, it.path_id, it.field_path, resolved,
                        "AMBIGUOUS",
                        f"multiple candidates({len(candidates)}), picked best_score={best_score}, tied={len(tied)}"
                    ))
                else:
                    not_found += 1
                    reports.append(Report(it.key, it.path_id, it.field_path, resolved, "NOT_FOUND", "ambiguous resolved but set failed"))
            else:
                resolved = best_path
                ok = set_value_by_field_path(tree, resolved, it.trans)
                if ok:
                    obj_modified = True
                    changed += 1
                    reports.append(Report(it.key, it.path_id, it.field_path, resolved, "RESOLVED", f"picked best_score={best_score}"))
                else:
                    not_found += 1
                    reports.append(Report(it.key, it.path_id, it.field_path, resolved, "NOT_FOUND", "resolved but set failed"))

        # obj 저장 1회
        if obj_modified:
            try:
                if hasattr(obj, "save_typetree"):
                    obj.save_typetree(tree)
                else:
                    raise RuntimeError("ObjectReader.save_typetree not available in this UnityPy version")
            except Exception as e:
                # 이 경우 obj의 해당 pid에 대한 변경이 통째로 실패할 수 있으니 리포트에 기록
                for r in reports:
                    if r.path_id == pid and r.status in ("OK", "RESOLVED", "AMBIGUOUS"):
                        r.status = "BAD_ROW"
                        r.note = f"save_typetree failed: {e}"

    # 결과 저장
    out_dir.mkdir(parents=True, exist_ok=True)
    out_assets = out_dir / "resources.assets"

    saved = False
    try:
        data = env.file.save()
        out_assets.write_bytes(data)
        saved = True
    except Exception:
        pass

    if not saved:
        try:
            data = env.file.write()
            out_assets.write_bytes(data)
            saved = True
        except Exception:
            pass

    if not saved:
        raise SystemExit("[ERROR] failed to save patched resources.assets (UnityPy save API mismatch)")

    # resS 복사
    resS_src = data_dir / "resources.assets.resS"
    if resS_src.exists():
        shutil.copy2(resS_src, out_dir / "resources.assets.resS")

    # 리포트 CSV
    report_path = out_dir / "inject_report.csv"
    with open(report_path, "w", encoding="utf-8-sig", newline="") as f:
        w = csv.writer(f)
        w.writerow(["key", "path_id", "requested_field_path", "resolved_field_path", "status", "note"])
        for rep in reports:
            w.writerow([rep.key, rep.path_id, rep.requested_field_path, rep.resolved_field_path, rep.status, rep.note])

    print("[OK] inject(resolve) done")
    print(f"  changed      : {changed}")
    print(f"  not_found    : {not_found}")
    print(f"  ambiguous    : {ambiguous}")
    print(f"  skip_filter  : {skipped_filter}")
    print(f"  bad          : {bad}")
    print(f"  out          : {out_assets.resolve()}")
    print(f"  report       : {report_path.resolve()}")

if __name__ == "__main__":
    main()
