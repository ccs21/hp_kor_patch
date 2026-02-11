#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
HuniePop Keyword Scanner (GUI)
- HuniePop_Data 내 파일을 자동 수집하여 키워드(펜딩 단어/문장) 검색
- 1) UnityPy typetree 기반 검색: asset/path_id/obj_type/field_path/text까지 확보
- 2) raw 바이트 검색: typetree로 안 잡히는 문자열도 파일 내부 존재 여부 확인(utf-8/ascii/utf-16le)
- 3) [NEW] raw hit offset을 serialized object(path_id/type/name)로 역매핑하여 "어느 오브젝트에 들어있는지" 특정

출력:
  found_typetree.csv
  found_raw.csv
  found_raw_mapped.csv   <-- NEW
  field_path_stats.csv
  not_found_keywords.txt
  summary.txt
"""

import csv
import threading
import queue
import traceback
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Set, Tuple

import tkinter as tk
from tkinter import ttk, filedialog, messagebox

import UnityPy


# -----------------------------
# Utils
# -----------------------------

def walk_strings(node, prefix: str = "") -> Iterable[Tuple[str, str]]:
    """typetree(dict/list)에서 str 값을 (field_path, text)로 전수 탐색"""
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


def normalize_keywords(text: str) -> List[str]:
    """GUI 입력(여러 줄)에서 키워드 목록 생성"""
    kws = []
    for line in (text or "").splitlines():
        s = line.strip()
        if not s:
            continue
        kws.append(s)
    # 중복 제거(입력 순서 유지)
    seen = set()
    out = []
    for k in kws:
        lk = k.lower()
        if lk in seen:
            continue
        seen.add(lk)
        out.append(k)
    return out


def gather_huniepop_files(game_root: Path) -> List[Path]:
    """
    HuniePop_Data에서 스캔 후보 파일을 자동 수집
    - *.assets (sharedassets 포함)
    - globalgamemanagers
    - level*
    - *.resS (raw 검색 대상)
    """
    data_dir = game_root / "HuniePop_Data"
    if not data_dir.exists():
        raise RuntimeError(f"HuniePop_Data not found: {data_dir}")

    candidates: List[Path] = []

    # *.assets 전부
    candidates.extend(sorted(data_dir.glob("*.assets")))

    # globalgamemanagers
    p = data_dir / "globalgamemanagers"
    if p.exists() and p.is_file():
        candidates.append(p)

    # level* (확장자 없는 파일들)
    for lp in sorted(data_dir.glob("level*")):
        if lp.is_file() and lp.suffix == "":
            candidates.append(lp)

    # *.resS (raw only)
    candidates.extend(sorted(data_dir.glob("*.resS")))

    # 중복 제거
    uniq: List[Path] = []
    seen = set()
    for p in candidates:
        key = str(p).lower()
        if key in seen:
            continue
        seen.add(key)
        uniq.append(p)

    return uniq


def find_all_occurrences(hay: bytes, needle: bytes, max_hits: int = 2000) -> List[int]:
    """바이너리 내 needle 모든 위치(최대 max_hits)"""
    hits = []
    if not needle:
        return hits
    start = 0
    while True:
        idx = hay.find(needle, start)
        if idx == -1:
            break
        hits.append(idx)
        if len(hits) >= max_hits:
            break
        start = idx + 1
    return hits


def snippet_around(data: bytes, pos: int, radius: int = 60) -> str:
    """raw 컨텍스트를 사람이 읽을 수 있게 변환 (CSV 안전 처리 포함)"""
    a = max(0, pos - radius)
    b = min(len(data), pos + radius)
    chunk = data[a:b]
    try:
        s = chunk.decode("utf-8", errors="replace")
    except Exception:
        s = chunk.decode("latin1", errors="replace")

    # CSV/로그에 위험한 제어문자 정리
    s = s.replace("\r", "\\r").replace("\n", "\\n").replace("\t", "\\t")
    # NUL 같은 것도 제거/치환
    s = s.replace("\x00", "\\0")
    return s


def _get_obj_byte_range(obj) -> Optional[Tuple[int, int]]:
    """
    UnityPy ObjectReader에서 (start, end) 바이트 범위를 최대한 안전하게 추출합니다.
    Unity 버전/UnityPy 버전에 따라 필드명이 조금씩 달라서 여러 케이스를 커버합니다.
    """
    info = getattr(obj, "_info", None) or getattr(obj, "object_info", None) or getattr(obj, "info", None)
    start = None
    size = None

    # start 후보들
    for attr in ("byte_start", "data_offset", "offset", "m_Offset", "start"):
        if info is not None and hasattr(info, attr):
            start = getattr(info, attr)
            break
        if hasattr(obj, attr):
            start = getattr(obj, attr)
            break

    # size 후보들
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
        start_i = int(start)
        size_i = int(size)
        if start_i < 0 or size_i <= 0:
            return None
        return (start_i, start_i + size_i)
    except Exception:
        return None


def _safe_obj_name(obj) -> str:
    """
    오브젝트 이름을 최대한 안전하게 뽑습니다.
    - hit 오브젝트에 대해서만 호출하는 걸 권장합니다(많이 호출하면 느릴 수 있음).
    """
    try:
        data = obj.read()
    except Exception:
        return ""

    # UnityPy 타입 객체(TextAsset 등)
    for key in ("name", "m_Name"):
        if hasattr(data, key):
            try:
                v = getattr(data, key)
                if isinstance(v, str):
                    return v
            except Exception:
                pass

    # typetree(dict)로 읽히는 경우도 있음
    if isinstance(data, dict):
        v = data.get("m_Name") or data.get("name")
        if isinstance(v, str):
            return v

    return ""


def map_raw_hits_to_objects(asset_path: Path, raw_hits: List[Dict[str, str]], emit) -> List[Dict[str, str]]:
    """
    raw 검색으로 얻은 offset이 어떤 serialized object 범위에 속하는지 매핑합니다.
    결과는 found_raw_mapped.csv로 저장할 row 리스트.
    """
    mapped: List[Dict[str, str]] = []

    # UnityPy 로드 가능한 파일만 처리
    try:
        env = UnityPy.load(str(asset_path))
    except Exception as e:
        emit(("log", f"[WARN] map_raw_hits_to_objects: UnityPy load failed: {asset_path.name}: {e}"))
        for r in raw_hits:
            mapped.append({
                **r,
                "path_id": "",
                "obj_type": "",
                "obj_name": "",
                "obj_range_start": "",
                "obj_range_end": "",
                "note": "UnityPy load failed",
            })
        return mapped

    # 오브젝트 범위 수집
    ranges = []
    obj_by_path = {}
    for obj in env.objects:
        br = _get_obj_byte_range(obj)
        if not br:
            continue
        start, end = br
        ranges.append((start, end, obj.path_id, obj.type.name))
        obj_by_path[(obj.path_id, obj.type.name)] = obj

    if not ranges:
        emit(("log", f"[WARN] map_raw_hits_to_objects: no object ranges found: {asset_path.name}"))
        for r in raw_hits:
            mapped.append({
                **r,
                "path_id": "",
                "obj_type": "",
                "obj_name": "",
                "obj_range_start": "",
                "obj_range_end": "",
                "note": "no object ranges",
            })
        return mapped

    # start 기준 정렬 + bisect 준비
    ranges.sort(key=lambda x: x[0])
    starts = [x[0] for x in ranges]

    # hit 오브젝트 이름 캐시(읽기 비용 절감)
    name_cache: Dict[Tuple[int, str], str] = {}

    import bisect
    for r in raw_hits:
        try:
            off = int(r.get("offset", "0"))
        except Exception:
            off = -1

        # bisect로 후보 찾기
        i = bisect.bisect_right(starts, off) - 1
        if i < 0:
            mapped.append({
                **r,
                "path_id": "",
                "obj_type": "",
                "obj_name": "",
                "obj_range_start": "",
                "obj_range_end": "",
                "note": "offset before first object",
            })
            continue

        start, end, pid, otype = ranges[i]
        if not (start <= off < end):
            mapped.append({
                **r,
                "path_id": "",
                "obj_type": "",
                "obj_name": "",
                "obj_range_start": "",
                "obj_range_end": "",
                "note": "offset not in any object range",
            })
            continue

        # 이름 추출(가능한 것만)
        obj_name = ""
        key = (pid, otype)
        if key in name_cache:
            obj_name = name_cache[key]
        else:
            obj_ref = obj_by_path.get(key)
            if obj_ref is not None:
                obj_name = _safe_obj_name(obj_ref)
            name_cache[key] = obj_name

        mapped.append({
            **r,
            "path_id": str(pid),
            "obj_type": otype,
            "obj_name": obj_name,
            "obj_range_start": str(start),
            "obj_range_end": str(end),
            "note": "",
        })

    return mapped


# -----------------------------
# Scan config/result
# -----------------------------

@dataclass
class ScanConfig:
    game_root: Path
    out_dir: Path
    keywords: List[str]
    case_insensitive: bool = True
    scan_typetree: bool = True
    scan_raw: bool = True
    raw_max_hits_per_file_per_kw: int = 50


# -----------------------------
# Core scanner
# -----------------------------

def scan_all(cfg: ScanConfig, emit, stop_event: threading.Event) -> None:
    """
    전체 스캔 수행
    출력:
      found_typetree.csv
      found_raw.csv
      found_raw_mapped.csv
      field_path_stats.csv
      not_found_keywords.txt
      summary.txt
    """
    cfg.out_dir.mkdir(parents=True, exist_ok=True)

    files = gather_huniepop_files(cfg.game_root)
    emit(("log", f"[INFO] files_to_scan = {len(files)}"))
    for p in files[:10]:
        emit(("log", f"  - {p.name}"))
    if len(files) > 10:
        emit(("log", f"  ... (+{len(files)-10} more)"))

    # 키워드 준비
    if cfg.case_insensitive:
        kw_pairs = [(k, k.lower()) for k in cfg.keywords]
    else:
        kw_pairs = [(k, k) for k in cfg.keywords]

    typetree_rows: List[Dict[str, str]] = []
    raw_rows: List[Dict[str, str]] = []
    field_path_count: Dict[str, int] = {}

    found_by_kw_typetree: Dict[str, int] = {k: 0 for k in cfg.keywords}
    found_by_kw_raw: Dict[str, int] = {k: 0 for k in cfg.keywords}

    emit(("progress_init", len(files)))
    done_files = 0

    for fpath in files:
        if stop_event.is_set():
            emit(("log", "[INFO] stopped by user"))
            break

        done_files += 1
        emit(("progress", done_files))
        emit(("status", f"Scanning: {fpath.name} ({done_files}/{len(files)})"))

        # raw bytes
        file_bytes: Optional[bytes] = None
        if cfg.scan_raw:
            try:
                file_bytes = fpath.read_bytes()
            except Exception as e:
                emit(("log", f"[WARN] raw read failed: {fpath.name}: {e}"))
                file_bytes = None

        # 1) typetree 검색
        if cfg.scan_typetree and (fpath.suffix == ".assets" or fpath.name == "globalgamemanagers" or fpath.name.startswith("level")):
            try:
                env = UnityPy.load(str(fpath))
                for obj in env.objects:
                    if stop_event.is_set():
                        break
                    try:
                        tree = obj.read_typetree()
                    except Exception:
                        continue

                    obj_type = obj.type.name
                    path_id = obj.path_id

                    for fp, text in walk_strings(tree):
                        if not text:
                            continue
                        hay = text.lower() if cfg.case_insensitive else text
                        for kw, kw_cmp in kw_pairs:
                            if kw_cmp in hay:
                                typetree_rows.append({
                                    "keyword": kw,
                                    "asset_file": fpath.name,
                                    "path_id": str(path_id),
                                    "obj_type": obj_type,
                                    "field_path": fp,
                                    "text": text,
                                })
                                found_by_kw_typetree[kw] += 1
                                field_path_count[fp] = field_path_count.get(fp, 0) + 1
            except Exception as e:
                emit(("log", f"[WARN] UnityPy load/typetree failed: {fpath.name}: {e}"))

        # 2) raw 검색
        if cfg.scan_raw and file_bytes:
            for kw, _kw_cmp in kw_pairs:
                if stop_event.is_set():
                    break

                needles: List[Tuple[str, bytes]] = []
                try:
                    needles.append(("utf8", kw.encode("utf-8")))
                except Exception:
                    pass
                try:
                    needles.append(("ascii", kw.encode("ascii")))
                except Exception:
                    pass
                try:
                    needles.append(("utf16le", kw.encode("utf-16le")))
                except Exception:
                    pass

                file_hit_total = 0
                for enc_name, needle in needles:
                    if not needle:
                        continue
                    hits = find_all_occurrences(file_bytes, needle, max_hits=cfg.raw_max_hits_per_file_per_kw)
                    if not hits:
                        continue

                    for h in hits:
                        raw_rows.append({
                            "keyword": kw,
                            "asset_file": fpath.name,
                            "encoding": enc_name,
                            "offset": str(h),
                            "context": snippet_around(file_bytes, h, radius=70),
                        })
                        file_hit_total += 1

                    if file_hit_total > 0:
                        found_by_kw_raw[kw] += file_hit_total

    # 3) raw offset → object(path_id/type/name) 매핑 (typetree가 0인 케이스 추적용)
    mapped_rows: List[Dict[str, str]] = []
    if cfg.scan_raw and raw_rows:
        # 파일별로 raw row 묶기
        by_file: Dict[str, List[Dict[str, str]]] = {}
        for r in raw_rows:
            by_file.setdefault(r["asset_file"], []).append(r)

        # UnityPy로 로드 가능한 파일만 매핑
        for fname, rows in by_file.items():
            if stop_event.is_set():
                break
            f = None
            for p in files:
                if p.name == fname:
                    f = p
                    break
            if f is None:
                continue

            # .resS는 매핑 불가(별도 스트리밍 데이터)
            if f.suffix.lower() == ".ress":
                for rr in rows:
                    mapped_rows.append({
                        **rr,
                        "path_id": "",
                        "obj_type": "",
                        "obj_name": "",
                        "obj_range_start": "",
                        "obj_range_end": "",
                        "note": "resS (no object table)",
                    })
                continue

            # UnityPy 오브젝트 범위 매핑
            mapped_rows.extend(map_raw_hits_to_objects(f, rows, emit))

    # ---- 결과 저장 ----
    out_typetree = cfg.out_dir / "found_typetree.csv"
    out_raw = cfg.out_dir / "found_raw.csv"
    out_raw_mapped = cfg.out_dir / "found_raw_mapped.csv"
    out_fp = cfg.out_dir / "field_path_stats.csv"
    out_nf = cfg.out_dir / "not_found_keywords.txt"
    out_sum = cfg.out_dir / "summary.txt"

    # ✅ CSV 저장을 안전하게: quoting + escapechar
    csv_kwargs = dict(
        encoding="utf-8-sig",
        newline="",
    )
    writer_kwargs = dict(
        quoting=csv.QUOTE_ALL,
        escapechar="\\",
        doublequote=True,
        lineterminator="\n",
    )

    with out_typetree.open("w", **csv_kwargs) as f:
        w = csv.DictWriter(
            f,
            fieldnames=["keyword", "asset_file", "path_id", "obj_type", "field_path", "text"],
            **writer_kwargs
        )
        w.writeheader()
        w.writerows(typetree_rows)

    with out_raw.open("w", **csv_kwargs) as f:
        w = csv.DictWriter(
            f,
            fieldnames=["keyword", "asset_file", "encoding", "offset", "context"],
            **writer_kwargs
        )
        w.writeheader()
        w.writerows(raw_rows)

    with out_raw_mapped.open("w", **csv_kwargs) as f:
        w = csv.DictWriter(
            f,
            fieldnames=["keyword", "asset_file", "encoding", "offset", "context",
                        "path_id", "obj_type", "obj_name", "obj_range_start", "obj_range_end", "note"],
            **writer_kwargs
        )
        w.writeheader()
        w.writerows(mapped_rows)

    # field_path 통계(내림차순)
    fp_items = sorted(field_path_count.items(), key=lambda x: x[1], reverse=True)
    with out_fp.open("w", **csv_kwargs) as f:
        w = csv.writer(f, delimiter=",", **writer_kwargs)
        w.writerow(["field_path", "count"])
        for fp, c in fp_items:
            w.writerow([fp, c])

    # not found: typetree에도 없고 raw에도 없는 키워드
    not_found = []
    for kw in cfg.keywords:
        if found_by_kw_typetree.get(kw, 0) == 0 and found_by_kw_raw.get(kw, 0) == 0:
            not_found.append(kw)

    with out_nf.open("w", encoding="utf-8", newline="") as f:
        for kw in not_found:
            f.write(kw + "\n")

    with out_sum.open("w", encoding="utf-8", newline="") as f:
        f.write("=== HuniePop Keyword Scanner Summary ===\n")
        f.write(f"game_root: {cfg.game_root}\n")
        f.write(f"out_dir: {cfg.out_dir}\n")
        f.write(f"files_scanned: {len(files)}\n")
        f.write(f"keywords: {len(cfg.keywords)}\n\n")
        f.write("[typetree hits per keyword]\n")
        for kw in cfg.keywords:
            f.write(f"- {kw}: {found_by_kw_typetree.get(kw, 0)}\n")
        f.write("\n[raw hits per keyword]\n")
        for kw in cfg.keywords:
            f.write(f"- {kw}: {found_by_kw_raw.get(kw, 0)}\n")
        f.write("\n[not found]\n")
        for kw in not_found:
            f.write(f"- {kw}\n")

    emit(("log", f"[OK] wrote: {out_typetree}"))
    emit(("log", f"[OK] wrote: {out_raw}"))
    emit(("log", f"[OK] wrote: {out_raw_mapped}"))
    emit(("log", f"[OK] wrote: {out_fp}"))
    emit(("log", f"[OK] wrote: {out_nf} (count={len(not_found)})"))
    emit(("log", f"[OK] wrote: {out_sum}"))
    emit(("status", "Done"))


# -----------------------------
# GUI
# -----------------------------

class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("HuniePop Keyword Scanner (KR) - Diagnose for Exporter Upgrade")
        self.geometry("1040x760")

        self.var_game_root = tk.StringVar(value=r"D:\SteamLibrary\steamapps\common\HuniePop")
        self.var_out_dir = tk.StringVar(value=str(Path.cwd() / "hp_keyword_scan_out"))
        self.var_casei = tk.BooleanVar(value=True)
        self.var_typetree = tk.BooleanVar(value=True)
        self.var_raw = tk.BooleanVar(value=True)
        self.var_raw_hits = tk.IntVar(value=50)

        self._q = queue.Queue()
        self._worker = None
        self._stop_event = threading.Event()

        self._build_ui()
        self.after(100, self._poll_queue)

    def _build_ui(self):
        root = ttk.Frame(self)
        root.pack(fill="both", expand=True, padx=10, pady=10)

        # Row 1: game root
        f1 = ttk.Frame(root)
        f1.pack(fill="x")
        ttk.Label(f1, text="HuniePop 설치 경로 (game root)").pack(side="left")
        e1 = ttk.Entry(f1, textvariable=self.var_game_root)
        e1.pack(side="left", fill="x", expand=True, padx=8)
        ttk.Button(f1, text="찾아보기", command=self._browse_game_root).pack(side="left")

        # Row 2: out dir
        f2 = ttk.Frame(root)
        f2.pack(fill="x", pady=(6, 0))
        ttk.Label(f2, text="출력 폴더 (out dir)").pack(side="left")
        e2 = ttk.Entry(f2, textvariable=self.var_out_dir)
        e2.pack(side="left", fill="x", expand=True, padx=8)
        ttk.Button(f2, text="찾아보기", command=self._browse_out_dir).pack(side="left")

        # Row 3: options
        f3 = ttk.Frame(root)
        f3.pack(fill="x", pady=(10, 0))
        ttk.Checkbutton(f3, text="대소문자 무시 (case-insensitive)", variable=self.var_casei).pack(side="left")
        ttk.Checkbutton(f3, text="typetree 스캔", variable=self.var_typetree).pack(side="left", padx=10)
        ttk.Checkbutton(f3, text="raw 스캔", variable=self.var_raw).pack(side="left", padx=10)
        ttk.Label(f3, text="raw hits/file/kw").pack(side="left", padx=(10, 2))
        ttk.Spinbox(f3, from_=1, to=999, textvariable=self.var_raw_hits, width=6).pack(side="left")

        # Keywords input
        ttk.Label(root, text="키워드(문장) 목록 (한 줄에 하나씩)").pack(anchor="w", pady=(10, 2))
        self.txt_kw = tk.Text(root, height=10)
        self.txt_kw.pack(fill="both", expand=False)

        # Buttons
        fbtn = ttk.Frame(root)
        fbtn.pack(fill="x", pady=(10, 0))
        self.btn_start = ttk.Button(fbtn, text="스캔 시작", command=self._start)
        self.btn_start.pack(side="left")
        self.btn_stop = ttk.Button(fbtn, text="중지", command=self._stop, state="disabled")
        self.btn_stop.pack(side="left", padx=8)

        # Progress
        self.var_status = tk.StringVar(value="Ready")
        ttk.Label(root, textvariable=self.var_status).pack(anchor="w", pady=(10, 2))
        self.pb = ttk.Progressbar(root, mode="determinate")
        self.pb.pack(fill="x")

        # Log
        ttk.Label(root, text="로그").pack(anchor="w", pady=(10, 2))
        self.txt_log = tk.Text(root, height=18)
        self.txt_log.pack(fill="both", expand=True)

    def _browse_game_root(self):
        p = filedialog.askdirectory(title="Select HuniePop game root")
        if p:
            self.var_game_root.set(p)

    def _browse_out_dir(self):
        p = filedialog.askdirectory(title="Select output folder")
        if p:
            self.var_out_dir.set(p)

    def _emit(self, item):
        self._q.put(item)

    def _poll_queue(self):
        try:
            while True:
                kind, payload = self._q.get_nowait()
                if kind == "log":
                    self.txt_log.insert("end", payload + "\n")
                    self.txt_log.see("end")
                elif kind == "status":
                    self.var_status.set(payload)
                elif kind == "progress_init":
                    self.pb["value"] = 0
                    self.pb["maximum"] = int(payload)
                elif kind == "progress":
                    self.pb["value"] = int(payload)
        except queue.Empty:
            pass
        self.after(100, self._poll_queue)

    def _start(self):
        if self._worker and self._worker.is_alive():
            return

        kws = normalize_keywords(self.txt_kw.get("1.0", "end"))
        if not kws:
            messagebox.showwarning("No keywords", "키워드를 1개 이상 입력해 주세요.")
            return

        game_root = Path(self.var_game_root.get().strip())
        out_dir = Path(self.var_out_dir.get().strip())

        cfg = ScanConfig(
            game_root=game_root,
            out_dir=out_dir,
            keywords=kws,
            case_insensitive=bool(self.var_casei.get()),
            scan_typetree=bool(self.var_typetree.get()),
            scan_raw=bool(self.var_raw.get()),
            raw_max_hits_per_file_per_kw=int(self.var_raw_hits.get()),
        )

        self._stop_event.clear()
        self.btn_start.config(state="disabled")
        self.btn_stop.config(state="normal")
        self.txt_log.delete("1.0", "end")

        def worker():
            try:
                scan_all(cfg, self._emit, self._stop_event)
            except Exception:
                tb = traceback.format_exc()
                self._emit(("log", "[ERROR] Exception:\n" + tb))
                self._emit(("status", "Error"))
            finally:
                self._emit(("status", "Idle"))
                self._emit(("progress", 0))
                self.btn_start.config(state="normal")
                self.btn_stop.config(state="disabled")

        self._worker = threading.Thread(target=worker, daemon=True)
        self._worker.start()

    def _stop(self):
        self._stop_event.set()
        self._emit(("log", "[INFO] stop requested"))


def main():
    app = App()
    app.mainloop()


if __name__ == "__main__":
    main()
