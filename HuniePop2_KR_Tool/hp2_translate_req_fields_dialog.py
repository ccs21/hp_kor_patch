# hp2_translate_req_fields_dialog.py
# - req_fields.txt 에 있는 field_path 만 번역해서 CSV의 trans를 채움
# - "대사 톤" = 반말 고정
# - 원문에 없는 욕/신음/추임새(하아/아앙/흐읍/음흠 등) 절대 추가 금지
# - 토큰/▸/줄바꿈/포맷(%s 등) 100% 보존 (위치까지 고정)
# - 기존 hp2_translate_csv.py의 "모호 토큰 스킵"을 없애고, 세그먼트 번역으로 형식 깨짐을 원천 차단
#
# 설치:
#   pip install -U openai python-dotenv
#
# 실행 예:
#   python hp2_translate_req_fields_dialog.py ^
#     --csv hp2_translations.ui_translated.csv ^
#     --req req_fields.txt ^
#     --out hp2_translations.req_translated.csv
#
# 중간 저장:
#   <out>.working.csv
# 로그:
#   logs_req/progress.txt
#   logs_req/failed_format.txt
#   logs_req/partial_english.txt

import argparse
import csv
import json
import os
import re
import time
from pathlib import Path
from typing import Dict, List, Optional, Tuple

from dotenv import load_dotenv
from openai import OpenAI

# -----------------------------
# 고정 번역 이름 사전 (필요시 확장)
# -----------------------------
NAME_MAP_FULL = {
    "Lola Rembrite": "롤라 렘브라이트",
    "Jessie Maye": "제시 메이",
    "Lillian Aurawell": "릴리안 오로웰",
    "Denise Zoey Greene": "데니스 조이 그린",
    "Sarah Suki Stevens": "사라 스키 스티븐스",
    "Lailani Kealoha": "라일라니 키알로하",
    "Candace Candy Crush": "캔디스 캔디 크러시",
    "Nora Delrio": "노라 델리오",
    "Brooke Belrose": "브룩 벨로즈",
    "Ashley Rosemarry": "애슐리 로즈마리",
    "Abia Nawazi": "아비아 나와지",
    "Polly Bendleson": "폴리 벤들슨",
    "Kyu Sugardust": "큐 슈가더스트",
    "Nymphojinn": "님포진",
    "Moxie": "막시",
    "Jewn": "준",
}

def build_name_variants() -> Dict[str, str]:
    variants: Dict[str, str] = {}

    def add(k: str, v: str):
        k = k.strip()
        if k:
            variants[k] = v

    for full, ko_full in NAME_MAP_FULL.items():
        add(full, ko_full)
        parts = full.split()
        ko_parts = ko_full.split()

        if len(parts) == 2 and len(ko_parts) >= 2:
            first, last = parts
            add(first, ko_parts[0])
            add(last, ko_parts[-1])
            add(f"{first} {last}", ko_full)

        if len(parts) == 3 and len(ko_parts) >= 3:
            first, mid, last = parts
            add(first, ko_parts[0])
            add(mid, ko_parts[1])
            add(last, ko_parts[2])
            add(f"{first} {last}", f"{ko_parts[0]} {ko_parts[2]}")
            add(f"{first} {mid} {last}", ko_full)

    return variants

NAME_VARIANTS = build_name_variants()

# -----------------------------
# 보호/검증용 정규식
# -----------------------------
RE_DBL_BRACKETS = re.compile(r"\[\[[^\]]+\]\]")  # [[...]]
RE_BRACKETS     = re.compile(r"\[[^\]]+\]")     # [...]
RE_AT_TAG       = re.compile(r"@[A-Za-z0-9_]+") # @Tag
RE_TRI          = re.compile(r"▸+")             # ▸▸▸
RE_NL_ESC       = re.compile(r"\\n")            # literal \n in CSV
RE_CURLY        = re.compile(r"\{[^}]*\}")      # {0} {TAG}
RE_FMT          = re.compile(r"%(?:\d+\$)?[sdif]")  # %s %d %i %f
RE_ANGLE        = re.compile(r"<[^>]+>")        # <color=...>
RE_ASCII_WORD   = re.compile(r"[A-Za-z]{3,}")

def now_ts() -> str:
    return time.strftime("%Y-%m-%d %H:%M:%S")

def log_append(path: Path, line: str):
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "a", encoding="utf-8", newline="\n") as f:
        f.write(line.rstrip("\n") + "\n")

def escape_preview(s: str, n: int = 180) -> str:
    s = (s or "").replace("\r", "").replace("\n", "\\n")
    if len(s) <= n:
        return s
    return s[: n // 2] + " ... " + s[-(n // 2):]

def extract_protected_spans(text: str) -> List[str]:
    # "나온 순서"를 비교(내용까지 동일해야 함)
    spans = []
    for m in RE_DBL_BRACKETS.finditer(text or ""):
        spans.append(m.group(0))
    for m in RE_BRACKETS.finditer(text or ""):
        spans.append(m.group(0))
    for m in RE_AT_TAG.finditer(text or ""):
        spans.append(m.group(0))
    for m in RE_CURLY.finditer(text or ""):
        spans.append(m.group(0))
    for m in RE_FMT.finditer(text or ""):
        spans.append(m.group(0))
    for m in RE_ANGLE.finditer(text or ""):
        spans.append(m.group(0))
    return spans

def extract_tri_runs(text: str) -> List[int]:
    return [len(m.group(0)) for m in RE_TRI.finditer(text or "")]

def count_newlines_like(text: str) -> int:
    return len(RE_NL_ESC.findall(text or ""))

def format_ok(orig: str, trans: str) -> bool:
    return (
        extract_protected_spans(orig) == extract_protected_spans(trans)
        and extract_tri_runs(orig) == extract_tri_runs(trans)
        and count_newlines_like(orig) == count_newlines_like(trans)
    )

def mask_tokens(text: str) -> Tuple[str, Dict[str, str]]:
    """
    보호 토큰들을 __TOKi__ 로 치환 (순서 고정)
    """
    mapping: Dict[str, str] = {}
    idx = 0

    def _sub(m: re.Match) -> str:
        nonlocal idx
        key = f"__TOK{idx}__"
        mapping[key] = m.group(0)
        idx += 1
        return key

    # 순서 중요: 큰/특수 토큰 먼저
    text = RE_DBL_BRACKETS.sub(_sub, text or "")
    text = RE_BRACKETS.sub(_sub, text)
    text = RE_AT_TAG.sub(_sub, text)
    text = RE_TRI.sub(_sub, text)
    text = RE_NL_ESC.sub(_sub, text)
    text = RE_CURLY.sub(_sub, text)
    text = RE_FMT.sub(_sub, text)
    text = RE_ANGLE.sub(_sub, text)
    return text, mapping

def restore_tokens(text: str, mapping: Dict[str, str]) -> str:
    out = text or ""
    # 큰 index부터 복원(겹침 방지)
    keys = sorted(mapping.keys(), key=lambda k: int(re.findall(r"\d+", k)[0]), reverse=True)
    for k in keys:
        out = out.replace(k, mapping[k])
    return out

def mask_names(text: str) -> Tuple[str, Dict[str, str]]:
    """
    이름/고유명사를 __NAMEi__ 로 마스킹해서 표기 흔들림 방지
    """
    mapping: Dict[str, str] = {}
    idx = 0
    keys = sorted(NAME_VARIANTS.keys(), key=len, reverse=True)

    for k in keys:
        ko = NAME_VARIANTS[k]
        pat = re.compile(rf"(?<![A-Za-z])({re.escape(k)})(?:'s)?(?![A-Za-z])", re.IGNORECASE)

        def repl(m: re.Match) -> str:
            nonlocal idx
            raw = m.group(0)
            base = m.group(1)
            suffix = raw[len(base):]
            token = f"__NAME{idx}__"
            mapping[token] = ko + ("의" if suffix.lower() == "'s" else "")
            idx += 1
            return token

        text = pat.sub(repl, text)

    return text, mapping

def detect_partial_english(trans: str) -> bool:
    # 토큰 제거 후 영어 단어가 남아있는지 체크(검수용)
    tmp, tm = mask_tokens(trans)
    for k in tm.keys():
        tmp = tmp.replace(k, "")
    return bool(RE_ASCII_WORD.search(tmp))

def parse_model_json(s: str) -> Optional[List[Dict]]:
    s = (s or "").strip()
    if "[" in s and "]" in s:
        s = s[s.find("[") : s.rfind("]") + 1]
    else:
        return None
    try:
        data = json.loads(s)
        return data if isinstance(data, list) else None
    except Exception:
        return None

def call_model(client: OpenAI, model: str, prompt: str) -> str:
    resp = client.responses.create(model=model, input=prompt)
    return getattr(resp, "output_text", "").strip()

def translate_segments(
    client: OpenAI,
    model: str,
    seg_items: List[Tuple[int, str]],  # (seg_id, seg_text)
    retry_once: bool = True,
) -> Dict[int, str]:
    """
    '토큰 없는 일반 텍스트 조각'만 번역한다.
    => 토큰 위치는 호출 밖에서 고정되므로 형식 깨짐이 거의 사라짐.
    """
    rules = (
        "너는 성인 게임의 대사를 한국어로 번역하는 번역가다.\n"
        "- 모든 번역은 반드시 반말.\n"
        "- 원문에 없는 욕/비속어를 절대 추가하지 마.\n"
        "- 원문에 없는 신음/추임새(하아/아앙/흐읍/음흠/크흠 등)를 절대 추가하지 마.\n"
        "- 과장 금지(강도 유지). 의미만 자연스럽게.\n"
        "- 출력은 JSON 배열만. 각 원소는 {\"id\":<int>,\"out\":\"<string>\"}.\n"
        "- 입력 텍스트에 있는 __NAME0__ 같은 토큰은 절대 변경/삭제/이동하지 마.\n"
        "- 설명/주석 금지.\n"
    )

    payload = json.dumps([{"id": i, "text": t} for i, t in seg_items], ensure_ascii=False)
    prompt = f"{rules}\n아래 JSON 배열의 text를 번역해 out으로 돌려줘.\n입력:\n{payload}\n출력은 JSON 배열만."

    out = call_model(client, model, prompt)
    data = parse_model_json(out)

    if data is None and retry_once:
        prompt2 = f"{rules}\n출력은 반드시 JSON 배열만. 다른 글자 한 글자도 쓰지 마.\n입력:\n{payload}\n"
        out2 = call_model(client, model, prompt2)
        data = parse_model_json(out2)

    if data is None:
        return {}

    result: Dict[int, str] = {}
    for item in data:
        if not isinstance(item, dict):
            continue
        if "id" not in item or "out" not in item:
            continue
        try:
            sid = int(item["id"])
        except Exception:
            continue
        result[sid] = str(item["out"])
    return result

def translate_line_by_segmentation(client: OpenAI, model: str, orig: str) -> Optional[str]:
    """
    1) 이름 마스킹
    2) 토큰 마스킹
    3) __TOKi__ 기준으로 텍스트를 세그먼트로 쪼개서(토큰은 그대로 둠)
    4) 세그먼트만 번역 후 재조립
    5) 토큰/이름 복원
    6) 형식 검증
    """
    if not (orig or "").strip():
        return ""

    tmp, name_map = mask_names(orig)
    masked, tok_map = mask_tokens(tmp)

    # split keeping TOK tokens
    parts = re.split(r"(__TOK\d+__)", masked)
    segs: List[Tuple[int, str]] = []
    seg_id = 0
    for p in parts:
        if not p:
            continue
        if re.fullmatch(r"__TOK\d+__", p):
            continue
        # 빈/공백만은 번역할 필요 없음
        if p.strip():
            segs.append((seg_id, p))
        seg_id += 1  # parts 인덱스 기반으로 안정성 유지

    # seg_id는 parts 인덱스와 맞춰야 하므로, 실제 번역은 "parts index"를 id로 사용
    # 위 로직에서 seg_id를 parts 순서대로 증가시키며, 번역할 텍스트가 없는 칸도 id는 건너뜀.
    # translate_segments에는 "번역이 필요한 id만" 들어가고, 결과로 매핑받아 재조립한다.
    seg_map = translate_segments(client, model, segs, retry_once=True)
    if not seg_map and segs:
        return None

    # reassemble
    out_parts: List[str] = []
    seg_id = 0
    for p in parts:
        if not p:
            continue
        if re.fullmatch(r"__TOK\d+__", p):
            out_parts.append(p)
        else:
            if p.strip():
                out_parts.append(seg_map.get(seg_id, p))
            else:
                out_parts.append(p)
            seg_id += 1

    out_masked = "".join(out_parts)
    out_restored = restore_tokens(out_masked, tok_map)
    out_restored = restore_tokens(out_restored, name_map)

    if not format_ok(orig, out_restored):
        return None
    return out_restored

def load_req_fields(req_path: Path) -> List[str]:
    fields: List[str] = []
    with open(req_path, "r", encoding="utf-8") as f:
        for line in f:
            s = line.strip()
            if not s or s.startswith("#"):
                continue
            fields.append(s)
    return fields

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--csv", required=True, help="입력 CSV (hp2_translations.ui_translated.csv)")
    ap.add_argument("--req", required=True, help="req_fields.txt (번역 대상 field_path 목록)")
    ap.add_argument("--out", required=True, help="출력 CSV")
    ap.add_argument("--model", default="gpt-4.1", help="모델 (기본 gpt-4.1)")
    ap.add_argument("--save-every", type=int, default=50, help="중간 저장 주기(기본 50)")
    ap.add_argument("--logs", default="logs_req", help="로그 폴더 (기본 logs_req)")
    ap.add_argument("--overwrite", action="store_true", help="trans가 있어도 덮어쓰기(기본: 비어있는 것만)")
    args = ap.parse_args()

    load_dotenv()
    if not os.getenv("OPENAI_API_KEY"):
        raise SystemExit("[ERROR] OPENAI_API_KEY가 없습니다. .env 또는 환경변수에 설정해 주세요.")

    client = OpenAI()

    inp = Path(args.csv)
    out = Path(args.out)
    working = Path(str(out) + ".working.csv")
    logs_dir = Path(args.logs)

    progress = logs_dir / "progress.txt"
    fail_fmt = logs_dir / "failed_format.txt"
    partial_eng = logs_dir / "partial_english.txt"

    req_fields = set(load_req_fields(Path(args.req)))
    log_append(progress, f"[{now_ts()}] req_fields loaded: {len(req_fields)}")

    # 이어하기 지원
    src_path = working if working.exists() else inp

    with open(src_path, "r", encoding="utf-8-sig", newline="") as f:
        rows = list(csv.DictReader(f))
        if not rows:
            raise SystemExit("[ERROR] CSV가 비어 있습니다.")
        fieldnames = list(rows[0].keys())

    required_cols = ["key", "field_path", "orig", "trans"]
    for c in required_cols:
        if c not in fieldnames:
            raise SystemExit(f"[ERROR] CSV에 필요한 컬럼이 없습니다: {c}")

    total = len(rows)
    already = sum(1 for r in rows if (r.get("trans") or "").strip())
    log_append(progress, f"[{now_ts()}] start total={total} already_translated={already} source={src_path}")

    # 대상만 골라서 처리
    pending_idx: List[int] = []
    for i, r in enumerate(rows):
        fp = (r.get("field_path") or "").strip()
        if fp not in req_fields:
            continue

        orig = r.get("orig") or ""
        trans = (r.get("trans") or "").strip()

        if (not args.overwrite) and trans:
            continue
        if not orig.strip():
            continue

        pending_idx.append(i)

    log_append(progress, f"[{now_ts()}] pending={len(pending_idx)} model={args.model}")

    changed = 0
    processed = 0

    for idx in pending_idx:
        r = rows[idx]
        fp = (r.get("field_path") or "").strip()
        key = r.get("key", "")
        orig = r.get("orig") or ""

        out_text = translate_line_by_segmentation(client, args.model, orig)
        if out_text is None:
            log_append(fail_fmt, f"[{now_ts()}] FAIL key={key} fp={fp} orig={escape_preview(orig)}")
            continue

        r["trans"] = out_text
        changed += 1

        if detect_partial_english(out_text):
            log_append(partial_eng, f"[{now_ts()}] PARTIAL_EN key={key} fp={fp} orig={escape_preview(orig)} out={escape_preview(out_text)}")

        processed += 1
        if processed % args.save_every == 0:
            out.parent.mkdir(parents=True, exist_ok=True)
            with open(working, "w", encoding="utf-8-sig", newline="") as f:
                w = csv.DictWriter(f, fieldnames=fieldnames)
                w.writeheader()
                w.writerows(rows)
            with open(out, "w", encoding="utf-8-sig", newline="") as f:
                w = csv.DictWriter(f, fieldnames=fieldnames)
                w.writeheader()
                w.writerows(rows)
            log_append(progress, f"[{now_ts()}] saved processed={processed}/{len(pending_idx)} changed={changed}")

    # 최종 저장
    out.parent.mkdir(parents=True, exist_ok=True)
    with open(out, "w", encoding="utf-8-sig", newline="") as f:
        w = csv.DictWriter(f, fieldnames=fieldnames)
        w.writeheader()
        w.writerows(rows)
    with open(working, "w", encoding="utf-8-sig", newline="") as f:
        w = csv.DictWriter(f, fieldnames=fieldnames)
        w.writeheader()
        w.writerows(rows)

    log_append(progress, f"[{now_ts()}] DONE out={out} changed={changed} pending={len(pending_idx)}")
    print("[OK] done")
    print(f"  pending attempted : {len(pending_idx)}")
    print(f"  newly translated : {changed}")
    print(f"  out              : {out}")
    print(f"  working          : {working}")
    print(f"  logs             : {logs_dir}")

if __name__ == "__main__":
    main()
