# hp2_translate_csv_v3.py
# HuniePop 2 KR 번역기 v3
# - field_path_retrans.txt 화이트리스트에 포함된 field_path
# - trans가 비어있는 행만 번역(사용자가 비워둔 것만)
# - 토큰/마커(▸, [..], <..>, {..}, %s 등) 보존
# - 표시용 정규화(번역 입력) -> 번역 -> 토큰 복원(게임 적용 가능)
# - 출력은 반말
# - 영어 잔재(라틴) 남으면 자동 재시도
# - 플레이스홀더(⟪TOKn⟫, ⟪NAME_n⟫)는 입력에 있는 것만 허용 (환각 생성 차단)
# - 이름(풀네임/부분이름) 일관 번역 고정
# - 보X/페X 같은 검열 마스킹 자동 교정(보지/페니스)

import argparse
import csv
import os
import re
import json
import time
from pathlib import Path
from typing import Dict, List, Tuple, Set

from dotenv import load_dotenv
from openai import OpenAI


# -----------------------------
# 1) 사용자 고정 이름 매핑(풀네임)
# -----------------------------
FULLNAME_MAP = {
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

# 부분 이름(이름/미들/성) 매핑을 자동 확장하기 위해 캐싱
# ex) "Denise"->"데니스", "Zoey"->"조이", "Greene"->"그린"
def build_name_variants(full_map: Dict[str, str]) -> Dict[str, str]:
    variants: Dict[str, str] = {}

    for en_full, ko_full in full_map.items():
        en_parts = en_full.split()
        ko_parts = ko_full.split()

        # Nymphojinn, Moxie 같은 단일 토큰
        if len(en_parts) == 1 and len(ko_parts) == 1:
            variants[en_parts[0]] = ko_parts[0]
            continue

        # 일반적으로 2~3 파트 풀네임을 가정
        # 길이가 다를 수도 있으니 가능한 범위에서 대응
        for i in range(min(len(en_parts), len(ko_parts))):
            variants[en_parts[i]] = ko_parts[i]

        # 풀네임 자체
        variants[en_full] = ko_full

    # 대소문자 변형 대응용: variants는 원형만 두고 치환 시 IGNORECASE 처리
    return variants

NAME_VARIANTS = build_name_variants(FULLNAME_MAP)


# -----------------------------
# 2) 토큰/마커 처리
# -----------------------------
# 보호 토큰 패턴: 게임 제어/태그/포맷/타자기마커
TOKEN_PATTERNS = [
    r"\[[^\]]*\]",        # [oo], [hahahaha], [hhhh] ...
    r"▸+",                # ▸▸▸...
    r"\n",                # newline
    r"\{[^}]*\}",         # {0}, {name} ...
    r"%[sdif]",           # %s %d ...
    r"<[^>]*>",           # <color=...> ...
]

TOKEN_RE = re.compile("|".join(f"({p})" for p in TOKEN_PATTERNS))

# 단어 내부 트리거: O[oo]f / a[xx]b 등
INWORD_BRACKET_RE = re.compile(r"(?i)([A-Za-z])(\[[A-Za-z]+\])([A-Za-z])")

# 플레이스홀더 형식 (모델이 __T0__ 같은 걸 환각으로 만들던 문제를 피하려고 ⟪...⟫ 사용)
PH_TOK_RE = re.compile(r"⟪TOK(\d+)⟫")
PH_NAME_RE = re.compile(r"⟪NAME_(\d+)⟫")

LATIN_RE = re.compile(r"[A-Za-z]")

# 보X/페X 마스킹 교정
CENSOR_FIXES = [
    (re.compile(r"보[\s]*[Xx\*\-✕×]", re.IGNORECASE), "보지"),
    (re.compile(r"페[\s]*[Xx\*\-✕×]", re.IGNORECASE), "페니스"),
]

# 영어 잔재를 검사할 때, 원문에서 보존해야 하는 토큰(예: [hahahaha])은 제거한 뒤 검사해야 함
def strip_preserved_tokens_for_latin_check(text: str, preserved_token_values: List[str]) -> str:
    out = text
    for tv in preserved_token_values:
        if not tv:
            continue
        out = out.replace(tv, " ")
    out = re.sub(r"\s+", " ", out).strip()
    return out


def normalize_for_translation(orig: str) -> Tuple[str, Dict[str, str]]:
    """
    번역 입력용 정규화:
    - 단어 내부 트리거(O[oo]f)는 [oo]만 토큰으로 떼어내고 앞뒤 영문자를 제거(표시용 문장에 가깝게)
    - 각종 토큰/마커는 ⟪TOKn⟫ 플레이스홀더로 보존
    """
    if orig is None:
        orig = ""
    text = orig
    tok_map: Dict[str, str] = {}
    idx = 0

    # 1) 단어 내부 트리거 처리: O[oo]f -> ⟪TOKn⟫ (값은 "[oo]"만)
    def _inword(m: re.Match) -> str:
        nonlocal idx
        bracket = m.group(2)
        key = f"⟪TOK{idx}⟫"
        tok_map[key] = bracket
        idx += 1
        return key

    text = INWORD_BRACKET_RE.sub(_inword, text)

    # 2) 나머지 토큰/마커 처리
    def _tok(m: re.Match) -> str:
        nonlocal idx
        tok = m.group(0)
        key = f"⟪TOK{idx}⟫"
        tok_map[key] = tok
        idx += 1
        return key

    text = TOKEN_RE.sub(_tok, text)

    # 3) 공백 정리
    text = re.sub(r"[ \t]+", " ", text).strip()
    return text, tok_map


def mask_names(text: str) -> Tuple[str, Dict[str, str]]:
    """
    이름을 모델이 흔들지 못하게 ⟪NAME_n⟫로 마스킹.
    - 풀네임 우선(긴 매치부터)
    - 부분 이름(이름/성/미들)도 동일 표기로 일관되게 치환
    """
    if not text:
        return text, {}

    # 긴 것부터 매치되도록 길이 내림차순
    keys_sorted = sorted(NAME_VARIANTS.keys(), key=len, reverse=True)

    name_map: Dict[str, str] = {}
    idx = 0
    out = text

    for k in keys_sorted:
        # 단어 경계 기반 치환(문장 부호 주변도 대응)
        # 예: "Jessie," "Jessie!" 같은 케이스
        pat = re.compile(rf"(?<![A-Za-z]){re.escape(k)}(?![A-Za-z])", flags=re.IGNORECASE)

        def _sub(m: re.Match) -> str:
            nonlocal idx
            key = f"⟪NAME_{idx}⟫"
            # 원문 그대로가 아니라 "원래 표기"로 저장하지 말고, 최종 한국어 표기를 저장
            # => 복원 시 일관성 유지
            ko = NAME_VARIANTS[k]
            name_map[key] = ko
            idx += 1
            return key

        out = pat.sub(_sub, out)

    return out, name_map


def restore_placeholders(text: str, tok_map: Dict[str, str], name_map: Dict[str, str]) -> str:
    out = text
    # 이름 먼저 복원(한국어)
    for k, v in name_map.items():
        out = out.replace(k, v)
    # 토큰 복원(원문 값 그대로)
    for k, v in tok_map.items():
        out = out.replace(k, v)
    return out


def extract_allowed_placeholders(norm_text: str) -> Set[str]:
    allowed: Set[str] = set(re.findall(r"⟪TOK\d+⟫", norm_text))
    allowed |= set(re.findall(r"⟪NAME_\d+⟫", norm_text))
    return allowed


def output_has_forbidden_placeholders(out_text: str, allowed: Set[str]) -> bool:
    found = set(re.findall(r"⟪TOK\d+⟫", out_text)) | set(re.findall(r"⟪NAME_\d+⟫", out_text))
    # 입력에 없던 플레이스홀더 생성 = 실패
    return not found.issubset(allowed)


def fix_censors(text: str) -> str:
    out = text
    for pat, rep in CENSOR_FIXES:
        out = pat.sub(rep, out)
    return out


def has_latin_left(text: str, preserved_token_values: List[str]) -> bool:
    """
    번역문에서 라틴 알파벳이 남았는지 검사.
    단, 게임 토큰(예: [hahahaha])은 원문에서 보존해야 하므로 제외하고 검사.
    """
    scrub = strip_preserved_tokens_for_latin_check(text, preserved_token_values)
    return bool(LATIN_RE.search(scrub))


# -----------------------------
# 3) OpenAI 호출
# -----------------------------
SYS_PROMPT = """너는 HuniePop 2의 한국어 현지화 번역가야.
목표: 자연스럽고 매끄러운 '반말' 대사 번역. 기술서적 같은 문어체/설명체 금지.

절대 규칙(중요):
1) 출력은 100% 한국어로만 써. 라틴 알파벳(A-Z/a-z)을 한 글자도 남기지 마.
   (단, 입력에 포함된 ⟪TOKn⟫ / ⟪NAME_n⟫ 플레이스홀더는 그대로 유지해야 한다.)
2) ⟪TOKn⟫ / ⟪NAME_n⟫ 는 입력에 있는 것만 그대로 출력해. 새 플레이스홀더를 만들면 실패야.
3) 입력의 플레이스홀더는 삭제/변경/이동하지 마.
4) 원문에 없는 신음(아앙/하아/흐읏 등)이나 과도한 저속어를 임의로 추가하지 마.
5) 욕설은 원문에 욕이 있을 때만 쓰되, 한국어에서 자연스러운 강도로 조절해.
   - "What the fuck..." 같은 황당/경악은 보통 "대체 뭐야/뭐 하는 거야"가 자연스러워.
   - '씨발'은 모욕/격앙이 정말 분명할 때만.
6) 성적 단어를 X/별표로 검열하지 마. (보X/페X 같은 표기 금지)
   - 원문이 노골적이면 한국어도 노골적으로: 좆/자지/보지/페니스 등 검열 없이.
   - 원문이 완곡하면 완곡하게 유지(더 노골적으로 만들지는 마).

출력 형식:
- 오직 JSON만 반환해.
- {"items":[{"id":<int>,"ko":"..."}...]}
"""

USER_TEMPLATE = """다음 문장들을 한국어(반말)로 번역해.
규칙:
- ⟪TOKn⟫ / ⟪NAME_n⟫ 는 절대로 건드리지 말고 그대로 출력해.
- 새 플레이스홀더를 만들지 마.
- 라틴 알파벳을 남기지 마.

문장 목록(JSON Lines):
{json_lines}
"""


def call_openai_batch(client: OpenAI, model: str, items: List[Dict]) -> Dict[int, str]:
    json_lines = "\n".join(json.dumps(it, ensure_ascii=False) for it in items)
    user_prompt = USER_TEMPLATE.format(json_lines=json_lines)

    resp = client.responses.create(
        model=model,
        input=[
            {"role": "system", "content": SYS_PROMPT},
            {"role": "user", "content": user_prompt},
        ],
        temperature=0.25,
    )
    text = resp.output_text.strip()

    m = re.search(r"\{[\s\S]*\}\s*$", text)
    if not m:
        raise ValueError(f"Non-JSON response head={text[:200]}")
    obj = json.loads(m.group(0))

    if not isinstance(obj, dict) or "items" not in obj or not isinstance(obj["items"], list):
        raise ValueError("JSON must contain items[]")

    out: Dict[int, str] = {}
    for it in obj["items"]:
        if isinstance(it, dict) and "id" in it and "ko" in it:
            out[int(it["id"])] = str(it["ko"])
    return out


# -----------------------------
# 4) 메인 처리
# -----------------------------
def load_field_paths(path: Path) -> Set[str]:
    s: Set[str] = set()
    with path.open("r", encoding="utf-8", errors="replace") as f:
        for line in f:
            t = line.strip()
            if t:
                s.add(t)
    return s


def is_blank(s: str) -> bool:
    return s is None or not str(s).strip()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="in_csv", required=True)
    ap.add_argument("--out", dest="out_csv", required=True)
    ap.add_argument("--field-paths", default="field_path_retrans.txt")
    ap.add_argument("--model", default="gpt-4.1")
    ap.add_argument("--batch", type=int, default=20)
    ap.add_argument("--max-retry", type=int, default=3)  # 라틴/플레이스홀더 실패 재시도
    ap.add_argument("--sleep", type=float, default=0.35)
    args = ap.parse_args()

    load_dotenv()
    if not os.getenv("OPENAI_API_KEY"):
        raise SystemExit("OPENAI_API_KEY 가 설정되어 있지 않습니다. (.env 또는 환경변수)")

    in_path = Path(args.in_csv)
    out_path = Path(args.out_csv)
    working = out_path.with_suffix(out_path.suffix + ".working.csv")

    field_whitelist = load_field_paths(Path(args.field_paths))
    client = OpenAI()

    logs = Path("logs_v3")
    logs.mkdir(exist_ok=True)
    log_fail = (logs / "failed_rows.txt").open("w", encoding="utf-8")
    log_retry = (logs / "retries.txt").open("w", encoding="utf-8")
    log_prog = (logs / "progress.txt").open("w", encoding="utf-8")

    # CSV 로드
    with in_path.open("r", encoding="utf-8", newline="") as f:
        reader = csv.DictReader(f)
        fieldnames = reader.fieldnames
        if not fieldnames:
            raise SystemExit("CSV 헤더를 읽지 못했습니다.")
        rows = list(reader)

    # 대상 인덱스: field_path in whitelist AND trans blank
    target_idxs: List[int] = []
    for i, r in enumerate(rows):
        fp = (r.get("field_path") or "").strip()
        if fp not in field_whitelist:
            continue
        if not is_blank(r.get("trans", "")):
            continue
        target_idxs.append(i)

    log_prog.write(f"[INFO] total={len(rows)} targets={len(target_idxs)} model={args.model}\n")
    log_prog.flush()

    changed = 0
    failed = 0

    # 배치 처리
    for start in range(0, len(target_idxs), args.batch):
        batch_ids = target_idxs[start:start + args.batch]

        # items: 번역 입력(정규화 + 이름 마스킹)
        items = []
        meta = {}  # local_id -> (row_index, tok_map, name_map, allowed_ph, preserved_token_values)

        for local_id, row_i in enumerate(batch_ids):
            orig = rows[row_i].get("orig", "") or ""

            norm, tok_map = normalize_for_translation(orig)
            norm2, name_map = mask_names(norm)

            allowed_ph = extract_allowed_placeholders(norm2)
            preserved_token_values = list(tok_map.values())  # 라틴 검사시 제외할 토큰

            items.append({"id": local_id, "en": norm2})
            meta[local_id] = (row_i, tok_map, name_map, allowed_ph, preserved_token_values)

        # 번역 + 검증/재시도(불량 id만)
        attempt = 0
        result: Dict[int, str] = {}

        # 처음엔 전체 배치
        pending = items[:]

        while pending and attempt <= args.max_retry:
            try:
                got = call_openai_batch(client, args.model, pending)
            except Exception as e:
                log_retry.write(f"[ERR] batch_start={start} attempt={attempt} err={repr(e)}\n")
                log_retry.flush()
                time.sleep(0.9)
                attempt += 1
                continue

            # 결과 반영 + 불량 판정
            next_pending = []
            for it in pending:
                lid = it["id"]
                ko = (got.get(lid) or "").strip()

                row_i, tok_map, name_map, allowed_ph, preserved_vals = meta[lid]

                # 1) 빈값이면 실패로 재시도
                bad = False
                reason = []

                if not ko:
                    bad = True
                    reason.append("EMPTY")

                # 2) 플레이스홀더 환각 생성(입력에 없는 ⟪TOK/NAME⟫ 등장) -> 실패
                if ko and output_has_forbidden_placeholders(ko, allowed_ph):
                    bad = True
                    reason.append("FORBIDDEN_PLACEHOLDER")

                # 3) 라틴 잔재 검사(보존 토큰 제외) -> 실패
                #    (주의: 아직 토큰/이름 복원 전이라 ⟪TOK⟫ 형태는 허용. 라틴은 여기에 없어야 함)
                if ko and has_latin_left(ko, preserved_vals):
                    bad = True
                    reason.append("LATIN_LEFT")

                # 4) 검열 마스킹(보X/페X) -> 일단 후처리로 교정 가능하나, 모델이 계속 그러면 재시도로 개선
                if ko and (re.search(r"보\s*[Xx\*✕×\-]", ko) or re.search(r"페\s*[Xx\*✕×\-]", ko)):
                    # 1차는 교정으로 처리하되, 다른 이유가 없다면 재시도는 안 함
                    pass

                if bad and attempt < args.max_retry:
                    # 강한 재요청: 같은 문장만 다시
                    next_pending.append({"id": lid, "en": it["en"]})
                    log_retry.write(f"[RETRY] row={row_i} id={lid} attempt={attempt} reason={','.join(reason)}\n")
                    log_retry.flush()
                else:
                    # 통과 or 마지막 시도
                    result[lid] = ko

            pending = next_pending
            attempt += 1
            time.sleep(args.sleep)

        # 결과 적용
        for local_id, row_i in enumerate(batch_ids):
            if local_id not in result:
                failed += 1
                log_fail.write(f"[FAIL] row={row_i} field_path={rows[row_i].get('field_path','')} orig={rows[row_i].get('orig','')[:120]}\n")
                continue

            ko_raw = result[local_id].strip()

            # 후처리: 검열 교정
            ko_raw = fix_censors(ko_raw)

            # 복원
            _, tok_map, name_map, _, _ = meta[local_id]
            ko_restored = restore_placeholders(ko_raw, tok_map, name_map)

            # 최종 한 번 더: 검열 교정(복원 후에도 혹시 남으면)
            ko_restored = fix_censors(ko_restored)

            # 최종 라틴 검사(보존 토큰 제외 후)
            preserved_vals = list(tok_map.values())
            if has_latin_left(ko_restored, preserved_vals):
                # 여기까지 왔는데 라틴이 남으면, 이 줄은 실패 처리(재시도는 배치 밖에서 하도록)
                failed += 1
                log_fail.write(f"[LATIN_FAIL] row={row_i} orig={rows[row_i].get('orig','')[:120]} ko={ko_restored[:120]}\n")
                continue

            rows[row_i]["trans"] = ko_restored
            changed += 1

        # 중간 저장
        with working.open("w", encoding="utf-8", newline="") as f:
            w = csv.DictWriter(f, fieldnames=fieldnames)
            w.writeheader()
            w.writerows(rows)

        log_prog.write(f"[OK] batch={start}~{start+len(batch_ids)-1} changed={changed} failed={failed}\n")
        log_prog.flush()

    # 최종 저장
    with out_path.open("w", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, fieldnames=fieldnames)
        w.writeheader()
        w.writerows(rows)

    log_prog.write(f"[DONE] out={out_path} changed={changed} failed={failed}\n")
    log_prog.flush()

    log_fail.close()
    log_retry.close()
    log_prog.close()


if __name__ == "__main__":
    main()
