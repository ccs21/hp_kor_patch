# hp2_translate_csv_v2.py
# HuniePop 2 번역기 v2 (hp2_translations.csv -> 지정 field_path만 재번역)
# - OpenAI Responses API (기존과 동일)
# - field_path_retrans.txt 목록과 완전 일치하는 field_path만 처리
# - 토큰/마커 정규화 후 번역 -> 토큰/마커 정확 복원
# - 번역문에 영어 잔존 시 자동 재시도(최대 2회)
# - 신음/과한 저속어 오판 방지(프롬프트 강화 + 최소 가드)
#
# 설치:
#   pip install -U openai python-dotenv
#
# 실행 예:
#   python hp2_translate_csv_v2.py --in hp2_translations.csv --out hp2_translations.retranslated.csv --field-paths field_path_retrans.txt
#
# 로그:
#   logs_v2/skip_outside_fieldpaths.txt
#   logs_v2/failed_format.txt
#   logs_v2/retry_partial_english.txt
#   logs_v2/progress.txt

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
# 사용자 스타일/용어 규칙(필요시 추가)
# -----------------------------
# *원하시면 여기에 고정 번역/금칙어/통일 표현을 더 붙여도 됩니다.
TERM_MAP = {
    # 예시:
    # "genitalia": "성기",
}

# 아주 잦은 “신음 오판” 방지용: 의미 단어(감탄/평가) 예시
NON_MOAN_HINT_WORDS = {
    "fascinating", "remarkable", "extraordinary", "amazing", "incredible",
    "unbelievable", "good", "heavens", "wow", "huh", "really", "seriously",
    "bother", "cuddling", "calculus", "animal",
}

# 번역 결과에서 “영어가 남았는지” 판단할 때, 이 토큰들은 예외로 봅니다(게임 토큰/플레이스홀더)
ALLOWED_LATIN_EXCEPTIONS = {
    # 내부 플레이스홀더 (복원 전 단계)
    "__T0__", "__T1__", "__T2__", "__T3__", "__T4__", "__T5__", "__T6__", "__T7__", "__T8__", "__T9__",
}


# -----------------------------
# 토큰/마커 처리
# -----------------------------
TOKEN_PATTERNS = [
    # [hahahaha], [oo] 등
    r"\[[^\]]*\]",
    # ▸▸▸ 타입(타자기 속도)
    r"▸+",
    # 줄바꿈
    r"\n",
    # {0}, {name} 같은 포맷
    r"\{[^}]*\}",
    # %s %d
    r"%[sdif]",
    # <color=...> 같은 태그
    r"<[^>]*>",
]

TOKEN_RE = re.compile("|".join(f"({p})" for p in TOKEN_PATTERNS))

# “단어 내부 트리거” 패턴: O[oo]f 같은 형태
INWORD_BRACKET_RE = re.compile(r"(?i)([A-Za-z])(\[[A-Za-z]+\])([A-Za-z])")

# 아주 흔한 신음(한국어) – “강제 삭제”가 아니라 “오판 방지” 힌트용
KR_MOAN_RE = re.compile(r"(아앙|하아|하앙|하앗|아앗|흐읏|흐응|으응|으음|으앗|으흣|하읏)")


def load_field_paths(fp: Path) -> set:
    s = set()
    with fp.open("r", encoding="utf-8", errors="replace") as f:
        for line in f:
            t = line.strip()
            if not t:
                continue
            s.add(t)
    return s


def normalize_for_translation(orig: str) -> Tuple[str, Dict[str, str]]:
    """
    번역기에 던질 '정규화 텍스트' 생성:
    - 토큰/마커를 __Tn__ 플레이스홀더로 치환해 보존
    - 단어 내부 트리거(O[oo]f)는 "[oo]"만 남기고 주변 영문자를 제거해서 화면에 영어가 남지 않도록 유도
      (즉, O[oo]f -> __Tn__ 로 만들되 토큰 값은 "[oo]"만 저장)
    """
    if orig is None:
        orig = ""
    text = orig

    token_map: Dict[str, str] = {}
    tok_idx = 0

    # 1) 단어 내부 트리거 우선 처리: X[xx]Y -> [xx] 토큰만 남김 (영문자 X,Y 제거)
    def _inword_sub(m: re.Match) -> str:
        nonlocal tok_idx
        bracket = m.group(2)  # "[oo]"
        key = f"__T{tok_idx}__"
        token_map[key] = bracket
        tok_idx += 1
        return key

    text = INWORD_BRACKET_RE.sub(_inword_sub, text)

    # 2) 나머지 토큰들 플레이스홀더화
    def _tok_sub(m: re.Match) -> str:
        nonlocal tok_idx
        tok = m.group(0)
        key = f"__T{tok_idx}__"
        token_map[key] = tok
        tok_idx += 1
        return key

    text = TOKEN_RE.sub(_tok_sub, text)

    # 3) 사람이 읽는 문장에 가까워지도록, 토큰 사이 과도한 공백 정리(토큰은 유지)
    text = re.sub(r"[ \t]+", " ", text).strip()

    return text, token_map


def restore_tokens(translated: str, token_map: Dict[str, str]) -> str:
    out = translated
    # 플레이스홀더가 깨져도 최대한 복원
    for k, v in token_map.items():
        out = out.replace(k, v)
    return out


def apply_term_map(ko: str) -> str:
    out = ko
    for k, v in TERM_MAP.items():
        out = re.sub(rf"\b{re.escape(k)}\b", v, out, flags=re.IGNORECASE)
    return out


def has_partial_english(ko: str) -> bool:
    """
    번역문에 영어가 남아있는지 검사.
    - 플레이스홀더(__Tn__)는 예외
    - 단, 고유명사까지 100% 제거 강제는 위험하니, 남는 경우 재시도 후에도 남으면 로그로 넘김
    """
    if not ko:
        return False
    # 플레이스홀더 제거 후 검사
    scrub = re.sub(r"__T\d+__", "", ko)
    # 허용 예외 제거
    for ex in ALLOWED_LATIN_EXCEPTIONS:
        scrub = scrub.replace(ex, "")
    return bool(re.search(r"[A-Za-z]{2,}", scrub))


def looks_like_pure_moan_en(orig: str) -> bool:
    """
    대상 범위에 섹스신이 거의 없다고 가정하지만, 혹시 남아있는 경우를 위해:
    원문이 거의 의성어/신음만인 영어(ah, mmm 등)인지 힌트로 판단.
    여기서 True면 “신음 강제 제거”를 하지 않고, 오히려 번역이 신음이어도 자연스럽게 둘 가능성을 열어둠.
    """
    if not orig:
        return False
    s = orig.strip().lower()
    # 토큰 제거 후 검사
    s = TOKEN_RE.sub("", s)
    s = re.sub(r"\s+", " ", s).strip()
    if not s:
        return False
    # 의성어 후보
    if re.fullmatch(r"(a+h+|o+h+|u+h+|m+m+|n+g+h+|h+n+g+|ah+|oh+|uh+|mmm+|hmm+|aw+w+)[\.\!\?]*", s):
        return True
    # 단어 수 극소 + 의미 단어 없음
    words = re.findall(r"[a-z]+", s)
    if len(words) <= 2 and not any(w in NON_MOAN_HINT_WORDS for w in words):
        return True
    return False


# -----------------------------
# OpenAI 호출 (기존과 동일: Responses API)
# -----------------------------
SYS_PROMPT = """당신은 성인 게임(HuniePop 2)의 한국어 현지화 번역가입니다.
목표: 자연스럽고 부끄럽지 않은 '일상 대화/데이트 문답/사전·사후 대화' 톤으로 번역하십시오.

중요 규칙:
1) 입력에는 __T0__, __T1__ 같은 플레이스홀더가 포함됩니다. 이것들은 절대로 변경/삭제/이동하지 말고 그대로 출력에 유지하십시오.
2) 한국어로만 번역하십시오. 고유명사(이름 등) 외에는 영어 단어를 남기지 마십시오.
3) 원문에 없는 신음(아앙/하아/흐읏 등)이나 과도한 저속어를 임의로 추가하지 마십시오.
4) 욕설은 원문에 있을 때만 사용하되, 한국어에서 자연스러운 강도로 조절하십시오.
   - "What the fuck..." 같은 놀람/황당 표현은 보통 "대체 뭐야/뭐 하는 거야"로 자연스럽게 처리하십시오.
   - '씨발' 같은 강한 욕은 모욕/격앙이 분명할 때만 사용하십시오.
5) 문장부호/말투는 구어체로 자연스럽게 하되 과장된 음란 톤을 기본값으로 삼지 마십시오.
6) 출력은 반드시 JSON만 반환하십시오. 형식:
   {"items":[{"id":<int>,"ko":"..."}...]}
"""

USER_TEMPLATE = """다음 영어 문장들을 한국어로 번역하세요.
- 플레이스홀더(__Tn__)는 위치 포함 그대로 유지.
- 각 항목의 id를 그대로 유지.

문장 목록:
{json_lines}
"""


def call_responses_json(client: OpenAI, model: str, items: List[Dict]) -> Dict:
    json_lines = "\n".join([json.dumps(it, ensure_ascii=False) for it in items])
    user_prompt = USER_TEMPLATE.format(json_lines=json_lines)

    resp = client.responses.create(
        model=model,
        input=[
            {"role": "system", "content": SYS_PROMPT},
            {"role": "user", "content": user_prompt},
        ],
        temperature=0.2,
    )
    text = resp.output_text.strip()
    # JSON만 오도록 강제했지만, 혹시 앞뒤 잡텍스트가 붙으면 JSON 부분만 추출
    m = re.search(r"\{[\s\S]*\}\s*$", text)
    if not m:
        raise ValueError(f"Non-JSON response: {text[:200]}")
    return json.loads(m.group(0))


def ensure_items_shape(obj: Dict) -> List[Dict]:
    if not isinstance(obj, dict) or "items" not in obj:
        raise ValueError("JSON must contain 'items'")
    items = obj["items"]
    if not isinstance(items, list):
        raise ValueError("'items' must be a list")
    out = []
    for it in items:
        if not isinstance(it, dict) or "id" not in it or "ko" not in it:
            continue
        out.append({"id": int(it["id"]), "ko": str(it["ko"])})
    return out


# -----------------------------
# 메인
# -----------------------------
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="in_csv", required=True, help="입력 hp2_translations.csv")
    ap.add_argument("--out", dest="out_csv", required=True, help="출력 CSV")
    ap.add_argument("--field-paths", default="field_path_retrans.txt", help="재번역할 field_path 목록 파일")
    ap.add_argument("--model", default="gpt-4.1", help="모델 (기본 gpt-4.1)")
    ap.add_argument("--batch", type=int, default=20, help="배치 크기 (기본 20)")
    ap.add_argument("--max-retry", type=int, default=2, help="영어 잔존/형식 문제 재시도 횟수 (기본 2)")
    ap.add_argument("--force", action="store_true", help="기존 trans가 있어도 강제로 재번역")
    ap.add_argument("--sleep", type=float, default=0.3, help="요청 간 sleep (기본 0.3s)")
    args = ap.parse_args()

    load_dotenv()
    if not os.getenv("OPENAI_API_KEY"):
        raise SystemExit("OPENAI_API_KEY 가 설정되어 있지 않습니다. (.env 또는 환경변수)")

    in_path = Path(args.in_csv)
    out_path = Path(args.out_csv)
    out_working = out_path.with_suffix(out_path.suffix + ".working.csv")

    logs_dir = Path("logs_v2")
    logs_dir.mkdir(parents=True, exist_ok=True)
    log_skip = (logs_dir / "skip_outside_fieldpaths.txt").open("a", encoding="utf-8")
    log_fmt = (logs_dir / "failed_format.txt").open("a", encoding="utf-8")
    log_retry = (logs_dir / "retry_partial_english.txt").open("a", encoding="utf-8")
    log_prog = (logs_dir / "progress.txt").open("a", encoding="utf-8")

    target_paths = load_field_paths(Path(args.field_paths))
    client = OpenAI()

    # CSV 로드 (원본 컬럼 유지)
    rows: List[Dict[str, str]] = []
    with in_path.open("r", encoding="utf-8", newline="") as f:
        reader = csv.DictReader(f)
        fieldnames = reader.fieldnames
        if not fieldnames:
            raise SystemExit("CSV fieldnames를 읽지 못했습니다.")
        for r in reader:
            rows.append(r)

    # 작업 대상 인덱스 수집
    targets: List[int] = []
    for i, r in enumerate(rows):
        fp = (r.get("field_path") or "").strip()
        if fp not in target_paths:
            continue
        if not args.force:
            if (r.get("trans") or "").strip():
                continue
        targets.append(i)

    log_prog.write(f"[INFO] total_rows={len(rows)} targets={len(targets)} model={args.model}\n")
    log_prog.flush()

    # 번역 루프
    changed = 0
    failed = 0

    # 배치 단위로 처리
    for start in range(0, len(targets), args.batch):
        batch_idx = targets[start:start + args.batch]

        items = []
        meta = {}  # id -> (row_index, token_map, orig_raw, norm_input)

        for local_id, row_i in enumerate(batch_idx):
            r = rows[row_i]
            orig = (r.get("orig") or "")
            norm, token_map = normalize_for_translation(orig)

            # 너무 짧은데 의미 단어도 없고, 의성어일 가능성이 크면(신음 등) -> 그대로 번역해도 되지만
            # 이번 범위는 일상/문답 위주이므로, “의성어-only”면 과도한 신음 추가를 막기 위해 약하게 힌트만 줌.
            hint = ""
            if looks_like_pure_moan_en(orig):
                hint = " (주의: 이 문장은 의성어/반응만일 수 있으니 과장된 신음 추가 금지)"
            # norm에 힌트를 붙이지는 않고, 시스템 프롬프트에 이미 반영되어 있음. 필요시 사용자 프롬프트에 추가 가능.

            items.append({"id": local_id, "en": norm})
            meta[local_id] = (row_i, token_map, orig, norm)

        # 1) 호출 및 형식/영어 잔존 재시도
        attempt = 0
        last_err = None
        result_items = None

        while attempt <= args.max_retry:
            try:
                obj = call_responses_json(client, args.model, items)
                got = ensure_items_shape(obj)

                # id 매칭 확인
                got_map = {it["id"]: it["ko"] for it in got}

                # 누락/영어 잔존 검사
                bad_ids = []
                for it in items:
                    i_id = it["id"]
                    ko = (got_map.get(i_id) or "").strip()
                    if not ko:
                        bad_ids.append(i_id)
                        continue
                    if has_partial_english(ko):
                        bad_ids.append(i_id)

                if not bad_ids:
                    result_items = got_map
                    break

                # 재시도: 문제 항목만 더 강하게 지시해서 다시 요청
                log_retry.write(f"[RETRY] batch_start={start} attempt={attempt} bad_ids={bad_ids}\n")
                log_retry.flush()

                # 문제 항목만 다시 번역(비용 절감)
                items_retry = []
                for i_id in bad_ids:
                    items_retry.append({"id": i_id, "en": items[i_id]["en"]})

                # 더 강한 보정 지시를 시스템에 넣기보단, 사용자 프롬프트에 간단히 강제 문구 추가
                # (SYS_PROMPT는 고정)
                obj2 = client.responses.create(
                    model=args.model,
                    input=[
                        {"role": "system", "content": SYS_PROMPT},
                        {"role": "user", "content": USER_TEMPLATE.format(
                            json_lines="\n".join(json.dumps(it, ensure_ascii=False) for it in items_retry)
                        ) + "\n\n추가 규칙: 영어 단어를 절대 남기지 말고, 플레이스홀더(__Tn__)만 그대로 유지하세요.\n반응/감탄 문장에 신음을 임의로 넣지 마세요."},
                    ],
                    temperature=0.2,
                )
                text2 = obj2.output_text.strip()
                m2 = re.search(r"\{[\s\S]*\}\s*$", text2)
                if not m2:
                    raise ValueError(f"Non-JSON retry response: {text2[:200]}")
                got2 = ensure_items_shape(json.loads(m2.group(0)))
                for it in got2:
                    got_map[it["id"]] = it["ko"]

                # 재검사
                still_bad = []
                for i_id in bad_ids:
                    ko = (got_map.get(i_id) or "").strip()
                    if not ko or has_partial_english(ko):
                        still_bad.append(i_id)

                if not still_bad:
                    result_items = got_map
                    break

                attempt += 1
                continue

            except Exception as e:
                last_err = e
                attempt += 1
                time.sleep(0.8)

        if result_items is None:
            failed += len(batch_idx)
            log_fmt.write(f"[FAIL] batch_start={start} err={repr(last_err)}\n")
            log_fmt.flush()
            time.sleep(args.sleep)
            continue

        # 2) 복원/적용
        for i_id, ko_norm in result_items.items():
            row_i, token_map, orig_raw, norm_input = meta[int(i_id)]
            ko = ko_norm.strip()
            ko = apply_term_map(ko)

            # 토큰 복원
            ko_restored = restore_tokens(ko, token_map)

            # 아주 최소한의 “오판 신음” 가드:
            # - 원문에 의미 단어가 있고(감탄/일상), 번역이 신음 위주면 한번 완화 시도(삭제가 아니라 자연스러운 감탄으로 유도)
            #   *강제 삭제는 하지 않음*
            if (not looks_like_pure_moan_en(orig_raw)) and KR_MOAN_RE.search(ko_restored):
                # 너무 공격적으로 바꾸지 않기 위해, “문두 신음만” 정리(필요 최소)
                ko_restored = re.sub(r"^(아앙|하아|하앙|하앗|아앗|흐읏|흐응|으응|으음)[\s,~!\.]+", "", ko_restored).strip()

            rows[row_i]["trans"] = ko_restored
            changed += 1

        # 3) 주기적 저장
        with out_working.open("w", encoding="utf-8", newline="") as f:
            writer = csv.DictWriter(f, fieldnames=fieldnames)
            writer.writeheader()
            writer.writerows(rows)

        log_prog.write(f"[OK] batch {start}~{start+len(batch_idx)-1} changed_total={changed} failed_total={failed}\n")
        log_prog.flush()

        time.sleep(args.sleep)

    # 최종 저장
    with out_path.open("w", encoding="utf-8", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)

    log_prog.write(f"[DONE] out={out_path} changed={changed} failed={failed}\n")
    log_prog.flush()

    log_skip.close()
    log_fmt.close()
    log_retry.close()
    log_prog.close()


if __name__ == "__main__":
    main()
