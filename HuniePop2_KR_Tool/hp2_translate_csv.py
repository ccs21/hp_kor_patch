# hp2_translate_csv.py
# HuniePop 2 번역기 (hp2_translations.csv -> trans 채움)
# - GPT-4.1 사용 (OpenAI Responses API)
# - 배치 20줄
# - 토큰 보호: [...] , ▸▸▸ , \n, {..}, %s/%d, <...>
# - parts[*].partName 전부 스킵
# - "단어 내부에 [...]가 2개 이상 끼인" (의미 파악 어려움) -> 스킵 + 로그
# - 번역 결과에 영어 일부 남으면 저장 + 검수로그
# - 형식(토큰/▸/줄바꿈) 깨지면 리트라이 1회(배치 JSON 재요구), 그래도 실패면 스킵 + 로그
# - 자주 저장 + 재시작 시 이어서 진행 (trans 비어있는 것만 번역)
#
# 설치:
#   pip install -U openai python-dotenv
#
# 실행 예:
#   python hp2_translate_csv.py --in hp2_translations.csv --out hp2_translations.translated.csv
#
# 중간 저장 파일:
#   <out>.working.csv  (주기적으로 덮어쓰기)
# 로그:
#   logs/skip_ambiguous.txt
#   logs/failed_format.txt
#   logs/review_partial_english.txt
#   logs/progress.txt

import argparse
import csv
import json
import os
import re
import time
from pathlib import Path
from typing import Dict, List, Tuple, Optional

from dotenv import load_dotenv
from openai import OpenAI


# -----------------------------
# 고정 번역 이름 사전 (사용자 제공)
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
    """
    영문 이름 변형(이름/성/미들/조합)을 가능한 범위에서 생성해
    어떤 경우에도 같은 한국어 표기가 나오도록 치환합니다.
    """
    variants: Dict[str, str] = {}

    def add(k: str, v: str):
        k = k.strip()
        if not k:
            return
        variants[k] = v

    for full, ko_full in NAME_MAP_FULL.items():
        parts = full.split()
        add(full, ko_full)

        if len(parts) == 2:
            first, last = parts
            ko_parts = ko_full.split()
            add(first, ko_parts[0])
            add(last, ko_parts[-1])
            add(f"{first} {last}", ko_full)

        elif len(parts) == 3:
            first, middle, last = parts
            ko_parts = ko_full.split()
            add(first, ko_parts[0])
            add(middle, ko_parts[1])
            add(last, ko_parts[2])
            add(f"{first} {middle}", f"{ko_parts[0]} {ko_parts[1]}")
            add(f"{middle} {last}", f"{ko_parts[1]} {ko_parts[2]}")
            add(f"{first} {last}", f"{ko_parts[0]} {ko_parts[2]}")
            add(f"{first} {middle} {last}", ko_full)

    return variants


NAME_VARIANTS = build_name_variants()


# -----------------------------
# 보호/검증용 정규식
# -----------------------------
RE_BRACKET = re.compile(r"\[[^\]]+\]")
RE_TRI = re.compile(r"▸+")
RE_NL_ESC = re.compile(r"\\n")  # CSV에서 \n로 들어오는 케이스
RE_CURLY = re.compile(r"\{[^}]*\}")          # {0}, {SOME_TAG}
RE_FMT = re.compile(r"%(?:\d+\$)?[sdif]")    # %s %d %i %f
RE_ANGLE = re.compile(r"<[^>]+>")            # <color=...> 등

# parts[*].partName 전부 스킵
RE_PARTS_PARTNAME = re.compile(r"^parts\[\d+\]\.partName$")

# 번역 후 영어 일부 남음 검출(보호 토큰 제거 후)
RE_ASCII_WORD = re.compile(r"[A-Za-z]{3,}")

# "알파벳/대괄호"만으로 이뤄진 단어 조각(중간에 토큰 끼는 형태) 탐지용
RE_ALPHA_BRACKET_CHUNK = re.compile(r"(?:[A-Za-z]|\[[^\]]+\])+")


def now_ts() -> str:
    return time.strftime("%Y-%m-%d %H:%M:%S")


def log_append(path: Path, line: str):
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "a", encoding="utf-8", newline="\n") as f:
        f.write(line.rstrip("\n") + "\n")


def escape_preview(s: str, n: int = 180) -> str:
    s = s.replace("\r", "").replace("\n", "\\n")
    if len(s) <= n:
        return s
    return s[: n // 2] + " ... " + s[-(n // 2):]


def extract_protected_spans(text: str) -> List[str]:
    return RE_BRACKET.findall(text)


def extract_tri_runs(text: str) -> List[int]:
    return [len(m.group(0)) for m in RE_TRI.finditer(text)]


def count_newlines_like(text: str) -> int:
    return len(RE_NL_ESC.findall(text))


def mask_tokens(text: str) -> Tuple[str, Dict[str, str]]:
    """
    보호 대상 토큰들을 __TOKn__ 으로 마스킹.
    복원 시 원형 그대로 돌아오게 합니다.
    """
    mapping: Dict[str, str] = {}
    tok_id = 0

    def _sub(match: re.Match) -> str:
        nonlocal tok_id
        raw = match.group(0)
        key = f"__TOK{tok_id}__"
        mapping[key] = raw
        tok_id += 1
        return key

    text = RE_BRACKET.sub(_sub, text)
    text = RE_TRI.sub(_sub, text)
    text = RE_NL_ESC.sub(_sub, text)
    text = RE_CURLY.sub(_sub, text)
    text = RE_FMT.sub(_sub, text)
    text = RE_ANGLE.sub(_sub, text)

    return text, mapping


def restore_tokens(text: str, mapping: Dict[str, str]) -> str:
    for k, v in mapping.items():
        text = text.replace(k, v)
    return text


def mask_names(text: str) -> Tuple[str, Dict[str, str]]:
    """
    이름 변형을 __NAMEi__ 로 마스킹하여 GPT가 표기를 흔들지 못하게 합니다.
    - 길이가 긴 키부터 먼저 치환 (풀네임 우선)
    - 대소문자 무시
    - 소유격 's 는 유지
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


# -----------------------------
# 스킵(모호) 판정 개선
# -----------------------------
def is_bracket_token_ambiguous(token_inside: str) -> bool:
    """
    대괄호 내부가 '의미 토큰'일 가능성이 높은지 판정.
    - 공백 포함, 알파벳 외 문자 포함, 너무 긴 경우 등은 위험
    """
    t = token_inside.strip()
    if not t:
        return True
    if " " in t:
        return True
    if len(t) >= 16:
        return True
    # 알파벳 외 문자가 섞이면 의미 토큰/코드일 수 있어 위험
    if re.fullmatch(r"[A-Za-z]+", t) is None:
        return True
    return False


def looks_ambiguous_bracket_in_word(orig: str) -> bool:
    """
    사용자가 직접 보는 게 낫다고 한 케이스만 스킵:
    - "한 단어(연속 알파벳/[] 덩어리)" 안에 [...]가 2개 이상 끼어 있으면 스킵
      (예: kaa[aa]aayy[yy], Meeee[e]ooo[o]ww[w])
    - 단, [] 내부가 공백/특수문자/너무 긴 등 '의미 토큰' 느낌이면 1개여도 스킵
    """
    s = orig.strip()
    if not s:
        return False

    for chunk_m in RE_ALPHA_BRACKET_CHUNK.finditer(s):
        chunk = chunk_m.group(0)
        bracket_contents = re.findall(r"\[([^\]]+)\]", chunk)
        if not bracket_contents:
            continue

        # 위험한 토큰이면 1개만 있어도 스킵
        for bc in bracket_contents:
            if is_bracket_token_ambiguous(bc):
                return True

        # 단어 내부 []가 2개 이상이면 사람이 확인하는 게 낫다 -> 스킵
        if len(bracket_contents) >= 2:
            return True

    return False


def detect_partial_english(trans: str) -> bool:
    tmp, map1 = mask_tokens(trans)
    for k in list(map1.keys()):
        tmp = tmp.replace(k, "")
    return bool(RE_ASCII_WORD.search(tmp))


def format_ok(orig: str, trans: str) -> bool:
    if extract_protected_spans(orig) != extract_protected_spans(trans):
        return False
    if extract_tri_runs(orig) != extract_tri_runs(trans):
        return False
    if count_newlines_like(orig) != count_newlines_like(trans):
        return False
    return True


def is_moan_like(orig: str) -> bool:
    """
    신음/애무 소리 라인(대체로 알파벳 반복 + 공백/점/느낌표 정도) 판별.
    """
    s = orig.strip()
    if not s:
        return False

    masked, m = mask_tokens(s)
    for k in list(m.keys()):
        masked = masked.replace(k, "")

    letters = sum(ch.isalpha() for ch in masked)

    if len(masked) > 0 and letters / max(len(masked), 1) < 0.45:
        return False

    if re.fullmatch(r"[A-Za-z\s\.\!\?\,]+", masked) is None:
        return False

    words = re.findall(r"[A-Za-z]{3,}", masked)
    if len(words) >= 4:
        return False

    return True


def build_prompt_items(batch: List[Tuple[int, str, str]]) -> str:
    items = []
    for row_idx, kind, text in batch:
        items.append({"id": row_idx, "kind": kind, "text": text})
    return json.dumps(items, ensure_ascii=False)


def parse_model_json(s: str) -> Optional[List[Dict]]:
    s = s.strip()
    if "[" in s and "]" in s:
        s2 = s[s.find("["): s.rfind("]") + 1]
    else:
        return None
    try:
        data = json.loads(s2)
        return data if isinstance(data, list) else None
    except Exception:
        return None


def call_gpt41(client: OpenAI, model: str, input_text: str) -> str:
    resp = client.responses.create(model=model, input=input_text)
    return getattr(resp, "output_text", "").strip()


def translate_batch(
    client: OpenAI,
    model: str,
    batch_rows: List[Tuple[int, str, str]],
    retry_once: bool = True,
) -> Dict[int, str]:
    """
    batch_rows: [(row_index, kind, masked_text)]
    returns: {row_index: translated_masked_text}
    """

    # -----------------------------
    # 프롬프트(욕 처리 수정 포함)
    # -----------------------------
    sys_rules = (
        "너는 성인 게임 번역가야.\n"
        "- 모든 번역은 반드시 반말.\n"
        "- 욕/비속어는 '원문에 있을 때만' 그대로 번역해. 원문에 없으면 절대 욕을 추가하지 마.\n"
        "- 원문의 공격성/저속함 강도를 임의로 올리지 말고 동일 강도로 유지해.\n"
        "- 특히 원문에 없는데 '존나/씨발/좆/병신/개새끼' 같은 단어를 절대 삽입하지 마.\n"
        "- 설명/주석 금지.\n"
        "- 출력은 JSON 배열만. 각 원소는 {\"id\":<int>,\"out\":\"<string>\"}.\n"
        "- 입력의 __TOK0__ 같은 토큰과 __NAME0__ 같은 토큰은 절대 변경/삭제/이동 금지.\n"
        "- kind가 'moan'이면 문장 만들지 말고 한국어 신음/애무 의성어로 자연스럽게(으음/하아/흐읍/아앙 등). 리듬과 길이감은 유지.\n"
        "- kind가 'dialog'이면 자연스러운 한국어로 번역.\n"
    )

    payload = build_prompt_items(batch_rows)

    user_prompt = (
        f"{sys_rules}\n"
        f"아래 JSON 배열의 각 항목 text를 번역해 out으로 돌려줘.\n"
        f"입력:\n{payload}\n"
        f"출력은 JSON 배열만."
    )

    out = call_gpt41(client, model, user_prompt)
    data = parse_model_json(out)

    if data is None and retry_once:
        user_prompt2 = (
            f"{sys_rules}\n"
            f"출력은 반드시 JSON 배열만. 다른 글자 한 글자도 쓰지 마.\n"
            f"입력:\n{payload}\n"
        )
        out2 = call_gpt41(client, model, user_prompt2)
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
            rid = int(item["id"])
        except Exception:
            continue
        result[rid] = str(item["out"])
    return result


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="inp", required=True, help="입력 CSV (hp2_translations.csv)")
    ap.add_argument("--out", dest="out", required=True, help="출력 CSV (trans 채운 파일)")
    ap.add_argument("--model", default="gpt-4.1", help="모델 (기본 gpt-4.1)")
    ap.add_argument("--batch", type=int, default=20, help="배치 크기 (기본 20)")
    ap.add_argument("--save-every", type=int, default=20, help="몇 행 처리마다 중간 저장할지 (기본 20)")
    ap.add_argument("--logs", default="logs", help="로그 폴더 (기본 logs)")
    ap.add_argument("--skip-underscore", action="store_true", help="orig에 _ 포함이면 번역 스킵(기본 false, 원하면 켜기)")
    args = ap.parse_args()

    load_dotenv()
    if not os.getenv("OPENAI_API_KEY"):
        raise SystemExit("[ERROR] OPENAI_API_KEY가 없습니다. .env 또는 환경변수에 설정해 주세요.")

    client = OpenAI()

    inp = Path(args.inp)
    out = Path(args.out)
    working = Path(str(out) + ".working.csv")
    logs_dir = Path(args.logs)

    skip_amb = logs_dir / "skip_ambiguous.txt"
    fail_fmt = logs_dir / "failed_format.txt"
    review_eng = logs_dir / "review_partial_english.txt"
    progress = logs_dir / "progress.txt"

    # 로드: out.working.csv가 있으면 그걸 이어서 사용(중간 저장)
    src_path = working if working.exists() else inp
    with open(src_path, "r", encoding="utf-8-sig", newline="") as f:
        rows = list(csv.DictReader(f))
        fieldnames = list(rows[0].keys()) if rows else []

    required_cols = ["key", "field_path", "orig", "trans"]
    for c in required_cols:
        if c not in fieldnames:
            raise SystemExit(f"[ERROR] CSV에 필요한 컬럼이 없습니다: {c}")

    total = len(rows)
    done_before = sum(1 for r in rows if (r.get("trans") or "").strip())
    log_append(progress, f"[{now_ts()}] start total={total} already_translated={done_before} source={src_path}")

    # 번역 대상 인덱스 수집
    pending_idx: List[int] = []
    for i, r in enumerate(rows):
        trans = (r.get("trans") or "").strip()
        if trans:
            continue

        fp = (r.get("field_path") or "").strip()
        if RE_PARTS_PARTNAME.match(fp):
            continue

        orig = r.get("orig") or ""
        if args.skip_underscore and "_" in orig:
            continue

        # ✅ 개선된 스킵: "단어 내부 [] 2개 이상" 중심으로만 스킵
        if looks_ambiguous_bracket_in_word(orig):
            log_append(skip_amb, f"[{now_ts()}] AMBIG_IN_WORD key={r.get('key','')} fp={fp} orig={escape_preview(orig)}")
            continue

        pending_idx.append(i)

    log_append(progress, f"[{now_ts()}] pending={len(pending_idx)} batch={args.batch} save_every={args.save_every}")

    processed = 0
    changed = 0

    for bi in range(0, len(pending_idx), args.batch):
        batch_indices = pending_idx[bi: bi + args.batch]

        send_rows: List[Tuple[int, str, str]] = []
        restore_maps: Dict[int, Dict[str, str]] = {}
        name_maps: Dict[int, Dict[str, str]] = {}
        orig_cache: Dict[int, str] = {}

        for idx in batch_indices:
            r = rows[idx]
            orig = r.get("orig") or ""

            kind = "moan" if is_moan_like(orig) else "dialog"

            tmp, nm = mask_names(orig)
            tmp2, tm = mask_tokens(tmp)

            send_rows.append((idx, kind, tmp2))
            restore_maps[idx] = tm
            name_maps[idx] = nm
            orig_cache[idx] = orig

        outputs = translate_batch(client, args.model, send_rows, retry_once=True)

        for idx in batch_indices:
            r = rows[idx]
            orig = orig_cache[idx]
            fp = (r.get("field_path") or "").strip()

            if idx not in outputs:
                log_append(fail_fmt, f"[{now_ts()}] NO_OUTPUT key={r.get('key','')} fp={fp} orig={escape_preview(orig)}")
                continue

            out_masked = outputs[idx]

            out_restored = restore_tokens(out_masked, restore_maps[idx])
            out_restored = restore_tokens(out_restored, name_maps[idx])

            if not format_ok(orig, out_restored):
                log_append(
                    fail_fmt,
                    f"[{now_ts()}] FORMAT_MISMATCH key={r.get('key','')} fp={fp} orig={escape_preview(orig)} out={escape_preview(out_restored)}"
                )
                continue

            # 영어 일부 남음: 저장 + 검수로그
            if detect_partial_english(out_restored):
                log_append(
                    review_eng,
                    f"[{now_ts()}] PARTIAL_EN key={r.get('key','')} fp={fp} orig={escape_preview(orig)} out={escape_preview(out_restored)}"
                )

            r["trans"] = out_restored
            changed += 1

        processed += len(batch_indices)

        if processed % args.save_every == 0 or (bi + args.batch) >= len(pending_idx):
            with open(working, "w", encoding="utf-8-sig", newline="") as f:
                w = csv.DictWriter(f, fieldnames=fieldnames)
                w.writeheader()
                w.writerows(rows)

            with open(out, "w", encoding="utf-8-sig", newline="") as f:
                w = csv.DictWriter(f, fieldnames=fieldnames)
                w.writeheader()
                w.writerows(rows)

            log_append(progress, f"[{now_ts()}] saved processed={processed}/{len(pending_idx)} changed_total={changed} out={out}")

    log_append(progress, f"[{now_ts()}] done changed_total={changed} out={out}")
    print("[OK] done")
    print(f"  total rows           : {total}")
    print(f"  already translated   : {done_before}")
    print(f"  pending attempted    : {len(pending_idx)}")
    print(f"  newly translated     : {changed}")
    print(f"  out                  : {out}")
    print(f"  working              : {working}")
    print(f"  logs                 : {logs_dir}")


if __name__ == "__main__":
    main()
