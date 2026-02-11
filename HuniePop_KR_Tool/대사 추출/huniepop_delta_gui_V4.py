#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
HuniePop KR - Delta Extractor (All-in-one GUI)
- UI 스레드 / 워커 스레드 분리 (Tkinter 응답없음 방지)
- 큐(Queue)로 로그/상태/진행 전달
- 확장 추출(typetree 전수 시도 + TextAsset)
- 필터 규칙 확장: 사용자 제공 필드 전부 포함
- 정규화(normalize_markup_tsv_v2 규칙) 후 translations.ko와 diff → 신규만 저장
"""

import csv
import re
import threading
import traceback
import queue
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Set, Tuple

import tkinter as tk
from tkinter import ttk, filedialog, messagebox

import UnityPy
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator


# =========================================================
# 0) Raw fallback helpers (Unity 4.2 typetree 미지원 대비)
#    - 정규화/비교(신규만 남기기) 로직은 건드리지 않고,
#    - "추출 단계"에서만 DialogScene(질문/응답) 계열 문자열이 후보군에 들어오도록 보강
# =========================================================

# utf-8로 디코드한 문자열에서 "사람이 읽을 수 있는 문장" 후보를 뽑기 위한 패턴
# (영문 + 공백/구두점/중점(·) 정도만 허용)
PRINTABLE_TEXT_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9 \t\-\'\"\,\.\!\?\:\;\(\)\[\]\{\}/\\\+\=\*\_\~\#\%\&\@\u00B7]{1,}")


def _get_obj_byte_range(obj) -> Optional[Tuple[int, int]]:
    """UnityPy ObjectReader에서 (start, end) 바이트 범위를 최대한 안전하게 추출."""
    info = getattr(obj, "_info", None) or getattr(obj, "object_info", None) or getattr(obj, "info", None)
    start = None
    size = None

    for attr in ("byte_start", "data_offset", "offset", "m_Offset", "start"):
        if info is not None and hasattr(info, attr):
            start = getattr(info, attr)
            break
        if hasattr(obj, attr):
            start = getattr(obj, attr)
            break

    for attr in ("byte_size", "data_size", "size", "m_Size", "length"):
        if info is not None and hasattr(info, attr):
            size = getattr(info, attr)
            break
        if hasattr(obj, attr):
            size = getattr(obj, attr)
            break

    if start is None or size is None:
        return None
    try:
        s = int(start)
        n = int(size)
        if s < 0 or n <= 0:
            return None
        return s, s + n
    except Exception:
        return None


def _extract_sentence_candidates_from_bytes(blob: bytes, min_len: int) -> List[str]:
    """오브젝트 바이트 범위에서 사람이 읽을만한 문장 후보를 뽑습니다(utf-8/utf-16le 둘 다 시도)."""
    out: List[str] = []

    def _add_from_decoded(s: str):
        for m in PRINTABLE_TEXT_RE.finditer(s):
            t = m.group(0)
            t = t.replace("\t", " ").strip()
            if len(t) < min_len:
                continue
            # 너무 "키/식별자" 같은 것 제외(공백이 전혀 없고, 알파/숫자만 길게 이어지는 케이스)
            if " " not in t and len(t) > 24 and t.isalnum():
                continue
            # 문장 후보만: 알파가 들어가고(대부분 영어), 공백/물음표/느낌표 중 하나라도 있으면 우선 채택
            has_alpha = any("A" <= c <= "Z" or "a" <= c <= "z" for c in t)
            if not has_alpha:
                continue
            if (" " in t) or ("?" in t) or ("!" in t):
                out.append(t)

    # utf-8/latin1 혼용 가능성 대비: 우선 utf-8 ignore
    try:
        _add_from_decoded(blob.decode("utf-8", errors="ignore"))
    except Exception:
        pass

    # utf-16le: 바이너리 정렬이 어긋나도 ignore로 최대한 회수
    try:
        _add_from_decoded(blob.decode("utf-16le", errors="ignore"))
    except Exception:
        pass

    # 중복 제거(순서 유지)
    seen = set()
    uniq: List[str] = []
    for t in out:
        k = t
        if k in seen:
            continue
        seen.add(k)
        uniq.append(t)
    return uniq


# =========================================================
# 1) Exporter: typetree 범위 확장 (v4 방식 유지)
# =========================================================

def walk_strings(node, prefix: str = "") -> Iterable[Tuple[str, str]]:
    if isinstance(node, dict):
        for k, v in node.items():
            p = f"{prefix}.{k}" if prefix else str(k)
            yield from walk_strings(v, p)
    elif isinstance(node, list):
        for i, v in enumerate(node):
            p = f"{prefix}[{i}]"
            yield from walk_strings(v, p)
    elif isinstance(node, str):
        yield prefix, node


def get_unity_version(asset_path: Path) -> str:
    env0 = UnityPy.load(str(asset_path))
    return next(iter(env0.files.values())).unity_version


@dataclass
class ExtractConfig:
    game_root: Path
    asset_files: List[Path]
    out_dir: Path
    skip_types: Set[str]
    min_len: int = 2


def extract_strings_full(
    cfg: ExtractConfig,
    emit,                 # 큐로 이벤트 전달하는 함수
    stop_event: threading.Event
) -> Path:
    """
    strings_full.csv 생성:
      asset_file, path_id, obj_type, field_path, text
    """
    cfg.out_dir.mkdir(parents=True, exist_ok=True)

    unity_ver = None
    for p in cfg.asset_files:
        if p.exists():
            unity_ver = get_unity_version(p)
            break
    if not unity_ver:
        raise RuntimeError("unity_version detect failed: asset file not found")

    emit(("log", f"[INFO] unity_version = {unity_ver}"))
    gen = TypeTreeGenerator(unity_ver)
    gen.load_local_game(str(cfg.game_root))

    rows: List[Dict[str, str]] = []
    checked = 0
    typetree_ok = 0
    typetree_fail = 0
    textasset_ok = 0
    raw_fallback_ok = 0

    def add_row(asset_name: str, path_id: int, obj_type: str, field_path: str, text: str):
        t = (text or "").strip()
        if len(t) < cfg.min_len:
            return
        rows.append({
            "asset_file": asset_name,
            "path_id": str(path_id),
            "obj_type": obj_type,
            "field_path": field_path,
            "text": text,
        })

    # 총 오브젝트 수(대략 진행률 용)
    total_objects = 0
    asset_object_counts: Dict[str, int] = {}

    for asset_path in cfg.asset_files:
        if not asset_path.exists():
            emit(("log", f"[WARN] missing asset: {asset_path}"))
            continue
        env_tmp = UnityPy.load(str(asset_path))
        c = len(env_tmp.objects)
        asset_object_counts[asset_path.name] = c
        total_objects += c

    emit(("progress_init", total_objects))

    for asset_path in cfg.asset_files:
        if stop_event.is_set():
            emit(("log", "[INFO] stopped by user (before scanning next asset)"))
            break

        if not asset_path.exists():
            continue

        asset_name = asset_path.name
        emit(("log", f"[INFO] scanning: {asset_name} (objects={asset_object_counts.get(asset_name, '?')})"))

        env = UnityPy.load(str(asset_path))
        env.typetree_generator = gen

        # raw fallback용: typetree가 깨지는 오브젝트가 많을 때를 대비
        asset_bytes: Optional[bytes] = None

        for obj in env.objects:
            if stop_event.is_set():
                emit(("log", "[INFO] stopped by user (during scanning)"))
                break

            checked += 1
            tname = obj.type.name

            # 진행 업데이트(너무 잦지 않게)
            if checked % 200 == 0:
                emit(("progress", checked))

            if tname in cfg.skip_types:
                continue

            # TextAsset
            if tname == "TextAsset":
                try:
                    data = obj.read()
                    name = getattr(data, "name", "")
                    script = getattr(data, "script", b"")
                    if script:
                        try:
                            txt = script.decode("utf-8")
                        except Exception:
                            txt = script.decode("latin1", "ignore")

                        whole = txt.strip("\ufeff")
                        if whole.strip():
                            add_row(asset_name, obj.path_id, tname, f"TextAsset:{name}.__whole__", whole)

                        for i, line in enumerate(whole.splitlines()):
                            s = line.strip()
                            if len(s) < cfg.min_len:
                                continue
                            add_row(asset_name, obj.path_id, tname, f"TextAsset:{name}.line[{i}]", s)

                        textasset_ok += 1
                except Exception:
                    pass
                continue

            # typetree 전수 시도
            try:
                tree = obj.read_typetree()
                typetree_ok += 1

                # typetree 문자열 수집
                for fp, text in walk_strings(tree):
                    add_row(asset_name, obj.path_id, tname, fp, text)

                # [UPGRADE] Unity 4.2에서 MonoBehaviour typetree가 "성공"해도
                #          커스텀 리스트(steps 등) 내부 문자열이 누락되는 케이스가 많습니다.
                #          특히 Query* (대화 질문 세트) 오브젝트는 raw에서 문장 후보를 추가 회수합니다.
                if tname == "MonoBehaviour":
                    try:
                        mb_name = ""
                        if isinstance(tree, dict):
                            mb_name = str(tree.get("m_Name") or tree.get("name") or "")
                        if mb_name.startswith("Query"):
                            rng = _get_obj_byte_range(obj)
                            if rng is not None:
                                if asset_bytes is None:
                                    try:
                                        asset_bytes = asset_path.read_bytes()
                                    except Exception:
                                        asset_bytes = None

                                if asset_bytes is not None:
                                    s, e = rng
                                    if 0 <= s < e <= len(asset_bytes):
                                        blob = asset_bytes[s:e]
                                        cands = _extract_sentence_candidates_from_bytes(blob, min_len=cfg.min_len)
                                        if cands:
                                            raw_fallback_ok += 1
                                            for t in cands:
                                                add_row(asset_name, obj.path_id, tname, "text", t)
                    except Exception:
                        pass

            except Exception:
                typetree_fail += 1

                # [ADD] Unity 4.2에서 typetree가 실패하는 MonoBehaviour는 raw에서 문장 후보를 회수
                # - field_path는 'text'로 넣어 필터를 통과시키고(후보군 확대 목적)
                # - 이후 정규화/비교 로직은 기존 그대로 유지
                if tname == "MonoBehaviour":
                    rng = _get_obj_byte_range(obj)
                    if rng is not None:
                        if asset_bytes is None:
                            try:
                                asset_bytes = asset_path.read_bytes()
                            except Exception:
                                asset_bytes = None

                        if asset_bytes is not None:
                            s, e = rng
                            if 0 <= s < e <= len(asset_bytes):
                                blob = asset_bytes[s:e]
                                cands = _extract_sentence_candidates_from_bytes(blob, min_len=cfg.min_len)
                                if cands:
                                    raw_fallback_ok += 1
                                    for t in cands:
                                        add_row(asset_name, obj.path_id, tname, "text", t)

                continue

            if checked % 5000 == 0:
                emit(("log", f"[INFO] progress: objects={checked} ok={typetree_ok} fail={typetree_fail} rows={len(rows)}"))

    out_full = cfg.out_dir / "strings_full.csv"
    with out_full.open("w", encoding="utf-8-sig", newline="") as f:
        w = csv.DictWriter(f, fieldnames=["asset_file", "path_id", "obj_type", "field_path", "text"])
        w.writeheader()
        w.writerows(rows)

    emit(("progress", checked))
    emit(("log", f"[OK] wrote: {out_full}"))
    emit(("log", f"[STAT] objects_checked={checked} typetree_ok={typetree_ok} typetree_fail={typetree_fail} textasset_ok={textasset_ok} raw_fallback_ok={raw_fallback_ok} rows={len(rows)}"))
    return out_full


# =========================================================
# 2) make_translate_csv.py 규칙 통합 (필터) - 확장판
# =========================================================

# 사용자님이 확인한 “대사 및 UI” 필드 전부 포함
ALLOW_EXACT = {
    "labelText", "name", "description",
    "altText", "appName", "data.text", "fullName", "messageText", "shortName",
    # [ADD] DialogLine / ResponseOption 핵심 필드 (steps 밖에서도 걸리게)
    "text", "secondaryText",
}

# 루트(prefix)도 허용 (lineSets / steps)
ALLOW_PREFIXES = (
    "steps[", "steps.",
    "lineSets[", "lineSets.",

    # [ADD] 질문/쿼리 계열 루트도 포괄
    "talkQuestions[", "talkQuestions.",
    "questions[", "questions.",
    "queries[", "queries.",
    "query[", "query.",
)

# 말단 키(끝부분) 허용
ALLOW_SUFFIXES = (
    ".text", ".secondaryText", ".label", ".name", ".description",
)

# 과도한 노이즈 방지용(필드명 기준)
DENY_FIELD_KEYWORDS = (
    "icon", "sprite", "thumb", "atlas", "prefab", "audio", "sound",
    "handle", "ref", "path", "guid", "file", "bundle", "shader", "material",
    "m_",  # Unity 기본 필드(대부분 노이즈)
)

HEX_RE = re.compile(r"^[0-9a-fA-F]{16,}$")
KEYLIKE_RE = re.compile(r"^[A-Za-z0-9_]+$")
CAMELDIGIT_RE = re.compile(r"^[A-Za-z]+[A-Za-z0-9]*\d+$")

# 숫자/퍼센트/콤마만 있는 “대사 아닌 찌꺼기” 감지(기본 필터에는 넣지 않음)
PURE_NUMLIKE_RE = re.compile(r"^[0-9,\s.%]+$")


def is_translation_field(field_path: str) -> bool:
    """
    번역 대상으로 인정할 field_path.
    - 사용자님이 제시한 필드/패턴 전부 포함
    - steps/lineSets 내부는 말단이 text/secondaryText/label… 류면 포함
    """
    fp = (field_path or "").strip()
    if not fp:
        return False

    # exact 허용
    if fp in ALLOW_EXACT:
        return True

    # prefix 허용(steps/lineSets)
    if fp.startswith(ALLOW_PREFIXES):
        # steps / lineSets 안에서는 아래 말단 키를 폭넓게 포함
        # 예: steps[11].responseOptions[0].text
        # 예: steps[2].responseOptions[2].secondaryText
        # 예: lineSets[0].lines[0].dialogLine[0].text
        if fp.endswith(ALLOW_SUFFIXES):
            return True
        # 일부 typetree는 말단이 ".dialogLine.text"처럼 끝이 ".text"로 떨어지지 않거나
        # 대괄호 인덱스가 끼어 endswith가 애매해지는 경우가 있어 보수적으로 포함:
        if ".dialogLine" in fp and (".text" in fp or ".secondaryText" in fp):
            return True
        return False

    # 그 외 루트지만 messageText/appName 같은 형태가 nested로 나올 수도 있어 suffix로 보수 포함
    if fp.endswith(ALLOW_SUFFIXES) and any(k in fp for k in ("messageText", "appName", "altText", "fullName", "shortName")):
        return True

    return False


def looks_dangerous_key(text: str) -> bool:
    """
    strict 모드에서만 사용:
    - 해시/키처럼 보이는 값 제외
    """
    t = (text or "").strip()
    if not t:
        return True
    if HEX_RE.match(t):
        return True
    if KEYLIKE_RE.match(t) and "_" in t and " " not in t:
        return True
    if " " not in t and CAMELDIGIT_RE.match(t):
        return True
    return False


def filter_translate_rows(
    strings_full_csv: Path,
    out_dir: Path,
    strict: bool,
    min_len: int,
    emit,
    stop_event: threading.Event
) -> Tuple[Path, Path]:
    """
    입력: strings_full.csv
    출력:
      - strings_full_translate.csv (필터 통과 rows)
      - strings_unique_translate.csv (text, ko)
    """
    out_dir.mkdir(parents=True, exist_ok=True)

    translate_full = out_dir / "strings_full_translate.csv"
    translate_unique = out_dir / "strings_unique_translate.csv"

    kept_rows: List[Dict[str, str]] = []
    uniq_keep: Dict[str, None] = {}

    with strings_full_csv.open("r", encoding="utf-8-sig", newline="") as f:
        rd = csv.DictReader(f)
        need = {"asset_file", "path_id", "field_path", "text"}
        if not need.issubset(set(rd.fieldnames or [])):
            raise RuntimeError(f"strings_full.csv missing columns: {sorted(need)}")

        count_in = 0
        for r in rd:
            if stop_event.is_set():
                emit(("log", "[INFO] stopped by user (during filter)"))
                break

            count_in += 1
            if count_in % 50000 == 0:
                emit(("log", f"[INFO] filter progress: rows_in={count_in} kept={len(kept_rows)} unique={len(uniq_keep)}"))

            fp = (r.get("field_path") or "").strip()
            txt = (r.get("text") or "")
            t = txt.strip()
            if len(t) < min_len:
                continue

            # ✅ 여기에서 사용자님 필드 전부 포함
            if not is_translation_field(fp):
                continue

            # strict 옵션
            if strict and looks_dangerous_key(t):
                continue

            kept_rows.append(r)
            uniq_keep[t] = None

    fieldnames = kept_rows[0].keys() if kept_rows else ["asset_file", "path_id", "obj_type", "field_path", "text"]
    with translate_full.open("w", encoding="utf-8-sig", newline="") as f:
        w = csv.DictWriter(f, fieldnames=fieldnames)
        w.writeheader()
        w.writerows(kept_rows)

    with translate_unique.open("w", encoding="utf-8-sig", newline="") as f:
        w = csv.DictWriter(f, fieldnames=["text", "ko"])
        w.writeheader()
        for t in uniq_keep.keys():
            w.writerow({"text": t, "ko": ""})

    emit(("log", f"[OK] wrote: {translate_full}"))
    emit(("log", f"[OK] wrote: {translate_unique} (unique={len(uniq_keep)})"))
    return translate_full, translate_unique


# =========================================================
# 3) normalize_markup_tsv_v2.py 규칙 통합 (정규화)
# =========================================================

MARKUP_RE = re.compile(r"\[\[([^\]]+)\][^\]]+\]\]?")  # [[VISIBLE]preset] -> VISIBLE


def replace_star_segments(s: str, spacer_char: str) -> str:
    if "*" not in s:
        return s
    out = []
    i = 0
    n = len(s)
    while i < n:
        if s[i] != "*":
            out.append(s[i])
            i += 1
            continue
        j = s.find("*", i + 1)
        if j == -1:
            out.append(s[i])
            i += 1
            continue
        seg_len = j - i + 1
        out.append(spacer_char * seg_len)
        i = j + 1
    return "".join(out)


def normalize_en(s: str, spacer_char: str) -> str:
    s2 = replace_star_segments(s, spacer_char=spacer_char)
    while True:
        s3 = MARKUP_RE.sub(r"\1", s2)
        if s3 == s2:
            break
        s2 = s3
    return s2


def sniff_delimiter(sample: str, fallback: str) -> str:
    try:
        dialect = csv.Sniffer().sniff(sample, delimiters="\t,")
        return dialect.delimiter
    except Exception:
        return fallback


def load_existing_translation_keys(translations_path: Path, spacer: str, emit) -> Set[str]:
    if not translations_path.exists():
        raise RuntimeError(f"translations not found: {translations_path}")

    sample = translations_path.read_text(encoding="utf-8", errors="replace")[:4096]
    default_delim = "\t" if translations_path.suffix.lower() in {".ko", ".tsv"} else ","
    delim = sniff_delimiter(sample, fallback=default_delim)

    keys: Set[str] = set()
    with translations_path.open("r", encoding="utf-8", newline="") as f:
        reader = csv.reader(f, delimiter=delim)
        for row in reader:
            if not row:
                continue
            en = row[0]
            en_n = normalize_en(en, spacer_char=spacer)
            if en_n.strip():
                keys.add(en_n)

    emit(("log", f"[INFO] loaded existing keys: {len(keys)} (delimiter={repr(delim)})"))
    return keys


def load_new_unique_texts(unique_translate_csv: Path, spacer: str, emit) -> List[str]:
    texts: List[str] = []
    with unique_translate_csv.open("r", encoding="utf-8-sig", newline="") as f:
        rd = csv.DictReader(f)
        if "text" not in (rd.fieldnames or []):
            raise RuntimeError("unique_translate_csv must have 'text' column")
        for r in rd:
            t = (r.get("text") or "")
            if not t.strip():
                continue
            texts.append(normalize_en(t, spacer_char=spacer))
    emit(("log", f"[INFO] loaded new candidate keys: {len(texts)}"))
    return texts


def write_delta_tsv(delta_keys: List[str], out_path: Path, emit):
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with out_path.open("w", encoding="utf-8", newline="") as f:
        w = csv.writer(f, delimiter="\t", lineterminator="\n")
        w.writerow(["text", "ko"])
        for k in delta_keys:
            w.writerow([k, ""])
    emit(("log", f"[OK] wrote delta: {out_path} (rows={len(delta_keys)})"))


# =========================================================
# 4) GUI + Pipeline (UI/Worker 분리)
# =========================================================

DEFAULT_SKIP_TYPES = {
    "Texture2D", "AudioClip", "Shader", "Material", "Mesh", "Sprite",
    "AnimationClip", "AnimatorController", "Font", "TextMesh",
    "GameObject", "Transform", "MonoScript",
}


class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("HuniePop Delta Extractor (KR) - All-in-one")
        self.geometry("980x720")

        # UI Vars
        self.var_game_root = tk.StringVar(value=r"D:\SteamLibrary\steamapps\common\HuniePop")
        self.var_translations = tk.StringVar(value=str(Path.cwd() / "translations.ko"))
        self.var_out_dir = tk.StringVar(value=str(Path.cwd() / "hp_work_delta"))
        self.var_strict = tk.BooleanVar(value=False)
        self.var_spacer = tk.StringVar(value="·")
        self.var_min_len = tk.IntVar(value=2)

        # Progress state
        self.total_progress = 0
        self.current_progress = 0

        # Thread comm
        self.q = queue.Queue()
        self.worker_thread: Optional[threading.Thread] = None
        self.stop_event = threading.Event()
        self.running = False

        self._build_ui()
        self.after(50, self._poll_queue)

    def _build_ui(self):
        frm = ttk.Frame(self, padding=10)
        frm.pack(fill="both", expand=True)
        frm.columnconfigure(1, weight=1)

        def add_path_row(row, label, var, browse_cmd):
            ttk.Label(frm, text=label).grid(row=row, column=0, sticky="w", pady=4)
            ent = ttk.Entry(frm, textvariable=var, width=90)
            ent.grid(row=row, column=1, sticky="we", pady=4, padx=(8, 8))
            btn = ttk.Button(frm, text="찾아보기", command=browse_cmd)
            btn.grid(row=row, column=2, sticky="e", pady=4)

        add_path_row(0, "게임 루트 (HuniePop 폴더)", self.var_game_root, self._browse_game_root)
        add_path_row(1, "기존 translations.ko (TSV)", self.var_translations, self._browse_translations)
        add_path_row(2, "출력 폴더", self.var_out_dir, self._browse_out_dir)

        opt = ttk.LabelFrame(frm, text="옵션", padding=10)
        opt.grid(row=3, column=0, columnspan=3, sticky="we", pady=(10, 8))
        ttk.Checkbutton(opt, text="Strict 필터(키/해시처럼 보이는 텍스트 추가 제외)", variable=self.var_strict).grid(row=0, column=0, sticky="w")
        ttk.Label(opt, text="정규화 spacer(기본 ·)").grid(row=0, column=1, sticky="e", padx=(16, 4))
        ttk.Entry(opt, textvariable=self.var_spacer, width=6).grid(row=0, column=2, sticky="w")
        ttk.Label(opt, text="최소 글자수").grid(row=0, column=3, sticky="e", padx=(16, 4))
        ttk.Entry(opt, textvariable=self.var_min_len, width=6).grid(row=0, column=4, sticky="w")

        btns = ttk.Frame(frm)
        btns.grid(row=4, column=0, columnspan=3, sticky="we", pady=(0, 6))

        self.btn_all = ttk.Button(btns, text="원클릭: 추출→필터→정규화→중복제거(신규만)", command=lambda: self._run("all"))
        self.btn_extract = ttk.Button(btns, text="추출만(strings_full.csv)", command=lambda: self._run("extract"))
        self.btn_post = ttk.Button(btns, text="필터+정규화+중복제거만(기존 strings_full.csv 사용)", command=lambda: self._run("post"))
        self.btn_stop = ttk.Button(btns, text="중지", command=self._stop, state="disabled")

        self.btn_all.pack(side="left")
        self.btn_extract.pack(side="left", padx=8)
        self.btn_post.pack(side="left")
        self.btn_stop.pack(side="right")

        pr = ttk.Frame(frm)
        pr.grid(row=5, column=0, columnspan=3, sticky="we")
        pr.columnconfigure(0, weight=1)

        self.pbar = ttk.Progressbar(pr, mode="determinate", maximum=100)
        self.pbar.grid(row=0, column=0, sticky="we", padx=(0, 8))
        self.lbl_progress = ttk.Label(pr, text="0%")
        self.lbl_progress.grid(row=0, column=1, sticky="e")

        self.txt = tk.Text(frm, height=26, wrap="word")
        self.txt.grid(row=6, column=0, columnspan=3, sticky="nsew", pady=(10, 0))
        frm.rowconfigure(6, weight=1)

        self.lbl_status = ttk.Label(frm, text="Ready")
        self.lbl_status.grid(row=7, column=0, columnspan=3, sticky="we", pady=(8, 0))

    def _browse_game_root(self):
        p = filedialog.askdirectory(title="HuniePop 게임 루트 폴더 선택")
        if p:
            self.var_game_root.set(p)

    def _browse_translations(self):
        p = filedialog.askopenfilename(title="translations.ko 선택", filetypes=[("TSV/KO", "*.ko *.tsv *.csv"), ("All", "*.*")])
        if p:
            self.var_translations.set(p)

    def _browse_out_dir(self):
        p = filedialog.askdirectory(title="출력 폴더 선택")
        if p:
            self.var_out_dir.set(p)

    def _ui_log(self, s: str):
        self.txt.insert("end", s + "\n")
        self.txt.see("end")

    def _set_status(self, s: str):
        self.lbl_status.config(text=s)

    def _set_running(self, running: bool):
        self.running = running
        state_run = "disabled" if running else "normal"
        self.btn_all.config(state=state_run)
        self.btn_extract.config(state=state_run)
        self.btn_post.config(state=state_run)
        self.btn_stop.config(state="normal" if running else "disabled")

    def _set_progress(self, current: int, total: int):
        if total <= 0:
            self.pbar.config(mode="indeterminate")
            self.pbar.start(10)
            self.lbl_progress.config(text="...")
            return

        if str(self.pbar["mode"]) == "indeterminate":
            self.pbar.stop()
            self.pbar.config(mode="determinate")

        pct = int((current / total) * 100) if total else 0
        pct = max(0, min(100, pct))
        self.pbar["value"] = pct
        self.lbl_progress.config(text=f"{pct}%  ({current}/{total})")

    def _stop(self):
        if self.running:
            self.stop_event.set()
            self.q.put(("log", "[UI] stop requested..."))
            self._set_status("Stopping...")

    def _run(self, mode: str):
        if self.running:
            return

        self.stop_event.clear()
        self.total_progress = 0
        self.current_progress = 0
        self._set_progress(0, 0)

        def emit(msg):
            self.q.put(msg)

        def worker():
            try:
                emit(("status", "Running..."))
                emit(("running", True))
                self._pipeline(mode, emit)
                if self.stop_event.is_set():
                    emit(("status", "Stopped"))
                else:
                    emit(("status", "Done"))
                emit(("running", False))
            except Exception as e:
                emit(("log", "[ERROR] " + str(e)))
                emit(("log", traceback.format_exc()))
                emit(("status", "Error"))
                emit(("running", False))
                emit(("error_dialog", str(e)))

        self.worker_thread = threading.Thread(target=worker, daemon=True)
        self.worker_thread.start()

    def _pipeline(self, mode: str, emit):
        game_root = Path(self.var_game_root.get().strip())
        translations_path = Path(self.var_translations.get().strip())
        out_dir = Path(self.var_out_dir.get().strip())
        strict = bool(self.var_strict.get())
        spacer = (self.var_spacer.get() or "·")
        min_len = int(self.var_min_len.get())

        data_dir = game_root / "HuniePop_Data"
        # 기존
        # asset_files = [
        #     data_dir / "sharedassets0.assets",
        #     data_dir / "resources.assets",
        # ]

        # 변경: HuniePop_Data 전체 스캔
        asset_files = []

        # 1) 모든 *.assets (sharedassetsN 포함)
        # Unity 4.x 게임은 HuniePop_Data 하위 폴더에 assets가 흩어져 있는 경우가 있어 rglob로 재귀 탐색
        asset_files.extend(sorted(data_dir.rglob("*.assets")))

        # 2) globalgamemanagers
        ggm = data_dir / "globalgamemanagers"
        if ggm.exists():
            asset_files.append(ggm)

        # 3) level* (확장자 없는 level 파일들)
        for lp in sorted(data_dir.rglob("level*")):
            if lp.is_file() and lp.suffix == "":
                asset_files.append(lp)

        # 중복 제거
        seen = set()
        uniq = []
        for p in asset_files:
            key = str(p).lower()
            if key in seen:
                continue
            seen.add(key)
            uniq.append(p)
        asset_files = uniq

        emit(("log", f"[INFO] asset_files expanded: {len(asset_files)} files"))
        for p in asset_files[:10]:
            emit(("log", f"  - {p.name}"))
        if len(asset_files) > 10:
            emit(("log", f"  ... (+{len(asset_files)-10} more)"))

        cfg = ExtractConfig(
            game_root=game_root,
            asset_files=asset_files,
            out_dir=out_dir,
            skip_types=set(DEFAULT_SKIP_TYPES),
            min_len=min_len,
        )

        strings_full_csv = out_dir / "strings_full.csv"

        if mode in ("extract", "all"):
            emit(("log", "=== [STEP 1] Extract strings_full.csv (expanded typetree scan) ==="))
            strings_full_csv = extract_strings_full(cfg, emit, self.stop_event)

        if self.stop_event.is_set():
            emit(("log", "=== stopped ==="))
            return

        if mode in ("post", "all"):
            if not strings_full_csv.exists():
                raise RuntimeError(f"strings_full.csv not found: {strings_full_csv}")

            emit(("log", "=== [STEP 2] Filter (expanded field rules) ==="))
            _, unique_translate_csv = filter_translate_rows(
                strings_full_csv=strings_full_csv,
                out_dir=out_dir,
                strict=strict,
                min_len=min_len,
                emit=emit,
                stop_event=self.stop_event
            )

            if self.stop_event.is_set():
                emit(("log", "=== stopped ==="))
                return

            emit(("log", "=== [STEP 3] Normalize & Diff vs translations.ko ==="))
            existing_keys = load_existing_translation_keys(translations_path, spacer=spacer, emit=emit)
            new_keys_all = load_new_unique_texts(unique_translate_csv, spacer=spacer, emit=emit)

            delta_keys: List[str] = []
            seen_out: Set[str] = set()
            for k in new_keys_all:
                if k in existing_keys:
                    continue
                if k in seen_out:
                    continue
                seen_out.add(k)
                delta_keys.append(k)

            delta_out = out_dir / "delta_new_only.tsv"
            write_delta_tsv(delta_keys, delta_out, emit=emit)

            emit(("log", f"[DONE] 신규만: {delta_out}"))
            emit(("log", "       -> 이 파일을 번역해서 translations.ko에 추가(append)하시면 됩니다."))

        emit(("log", "=== Finished ==="))

    def _poll_queue(self):
        try:
            while True:
                msg = self.q.get_nowait()
                kind = msg[0]

                if kind == "log":
                    self._ui_log(msg[1])
                elif kind == "status":
                    self._set_status(msg[1])
                elif kind == "running":
                    self._set_running(bool(msg[1]))
                elif kind == "progress_init":
                    self.total_progress = int(msg[1])
                    self.current_progress = 0
                    self._set_progress(0, self.total_progress)
                elif kind == "progress":
                    self.current_progress = int(msg[1])
                    self._set_progress(self.current_progress, self.total_progress)
                elif kind == "error_dialog":
                    messagebox.showerror("Error", msg[1])
                else:
                    pass
        except queue.Empty:
            pass
        finally:
            self.after(50, self._poll_queue)


def main():
    app = App()
    app.mainloop()


if __name__ == "__main__":
    main()
