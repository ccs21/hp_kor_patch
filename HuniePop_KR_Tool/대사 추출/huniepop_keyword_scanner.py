#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
HuniePop Keyword Scanner (GUI)
- HuniePop_Data 내 파일을 자동 수집하여 키워드(펜딩 단어/문장) 검색
- 1) UnityPy typetree 기반 검색: asset/path_id/obj_type/field_path/text까지 확보
- 2) raw 바이트 검색: typetree로 안 잡히는 문자열도 파일 내부 존재 여부 확인(utf-8/ascii/utf-16le)
- 결과를 CSV로 저장하여, 추출기 업그레이드 근거로 사용
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

    # ---- 결과 저장 ----
    out_typetree = cfg.out_dir / "found_typetree.csv"
    out_raw = cfg.out_dir / "found_raw.csv"
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

        self.q = queue.Queue()
        self.stop_event = threading.Event()
        self.running = False
        self.worker: Optional[threading.Thread] = None

        self.total_files = 0
        self.done_files = 0

        self._build_ui()
        self.after(50, self._poll)

    def _build_ui(self):
        frm = ttk.Frame(self, padding=10)
        frm.pack(fill="both", expand=True)
        frm.columnconfigure(1, weight=1)
        frm.rowconfigure(5, weight=1)

        def add_path_row(row, label, var, browse_cmd):
            ttk.Label(frm, text=label).grid(row=row, column=0, sticky="w", pady=4)
            ent = ttk.Entry(frm, textvariable=var)
            ent.grid(row=row, column=1, sticky="we", padx=(8, 8), pady=4)
            ttk.Button(frm, text="찾아보기", command=browse_cmd).grid(row=row, column=2, sticky="e", pady=4)

        add_path_row(0, "게임 루트 (HuniePop 폴더)", self.var_game_root, self._browse_root)
        add_path_row(1, "출력 폴더", self.var_out_dir, self._browse_out)

        opt = ttk.LabelFrame(frm, text="옵션", padding=10)
        opt.grid(row=2, column=0, columnspan=3, sticky="we", pady=(8, 8))

        ttk.Checkbutton(opt, text="대/소문자 무시(권장)", variable=self.var_casei).grid(row=0, column=0, sticky="w")
        ttk.Checkbutton(opt, text="typetree 검색(필드 경로 확보)", variable=self.var_typetree).grid(row=0, column=1, sticky="w", padx=(16, 0))
        ttk.Checkbutton(opt, text="raw 검색(파일 내 존재 확인)", variable=self.var_raw).grid(row=0, column=2, sticky="w", padx=(16, 0))

        ttk.Label(opt, text="raw: 키워드/파일 당 최대 hit").grid(row=0, column=3, sticky="e", padx=(16, 4))
        ttk.Entry(opt, textvariable=self.var_raw_hits, width=6).grid(row=0, column=4, sticky="w")

        kw_box = ttk.LabelFrame(frm, text="키워드 목록 (펜딩 단어/문장 여러 줄로 붙여넣기)", padding=10)
        kw_box.grid(row=3, column=0, columnspan=3, sticky="nsew", pady=(0, 8))
        kw_box.columnconfigure(0, weight=1)
        kw_box.rowconfigure(0, weight=1)

        self.txt_kw = tk.Text(kw_box, height=10, wrap="word")
        self.txt_kw.grid(row=0, column=0, sticky="nsew")

        btns = ttk.Frame(frm)
        btns.grid(row=4, column=0, columnspan=3, sticky="we")

        self.btn_run = ttk.Button(btns, text="스캔 시작", command=self._run)
        self.btn_stop = ttk.Button(btns, text="중지", command=self._stop, state="disabled")
        self.btn_run.pack(side="left")
        self.btn_stop.pack(side="left", padx=8)

        pr = ttk.Frame(frm)
        pr.grid(row=5, column=0, columnspan=3, sticky="nsew", pady=(8, 0))
        pr.columnconfigure(0, weight=1)
        pr.rowconfigure(2, weight=1)

        self.pbar = ttk.Progressbar(pr, mode="determinate", maximum=100)
        self.pbar.grid(row=0, column=0, sticky="we", padx=(0, 8))
        self.lbl_pr = ttk.Label(pr, text="0%")
        self.lbl_pr.grid(row=0, column=1, sticky="e")

        self.lbl_status = ttk.Label(pr, text="Ready")
        self.lbl_status.grid(row=1, column=0, columnspan=2, sticky="we", pady=(6, 6))

        self.txt_log = tk.Text(pr, height=18, wrap="word")
        self.txt_log.grid(row=2, column=0, columnspan=2, sticky="nsew")

    def _browse_root(self):
        p = filedialog.askdirectory(title="HuniePop 게임 루트 폴더 선택")
        if p:
            self.var_game_root.set(p)

    def _browse_out(self):
        p = filedialog.askdirectory(title="출력 폴더 선택")
        if p:
            self.var_out_dir.set(p)

    def _log(self, s: str):
        self.txt_log.insert("end", s + "\n")
        self.txt_log.see("end")

    def _set_status(self, s: str):
        self.lbl_status.config(text=s)

    def _set_running(self, running: bool):
        self.running = running
        self.btn_run.config(state="disabled" if running else "normal")
        self.btn_stop.config(state="normal" if running else "disabled")

    def _set_progress(self, done: int, total: int):
        if total <= 0:
            self.pbar["value"] = 0
            self.lbl_pr.config(text="...")
            return
        pct = int((done / total) * 100)
        pct = max(0, min(100, pct))
        self.pbar["value"] = pct
        self.lbl_pr.config(text=f"{pct}% ({done}/{total})")

    def _stop(self):
        if self.running:
            self.stop_event.set()
            self.q.put(("log", "[UI] stop requested..."))
            self._set_status("Stopping...")

    def _run(self):
        if self.running:
            return

        kws = normalize_keywords(self.txt_kw.get("1.0", "end"))
        if not kws:
            messagebox.showwarning("키워드 필요", "키워드를 한 줄에 하나씩 입력해 주세요.")
            return

        game_root = Path(self.var_game_root.get().strip())
        out_dir = Path(self.var_out_dir.get().strip())
        casei = bool(self.var_casei.get())
        scan_tt = bool(self.var_typetree.get())
        scan_raw = bool(self.var_raw.get())
        raw_hits = int(self.var_raw_hits.get())

        self.stop_event.clear()
        self._set_progress(0, 1)

        def emit(msg):
            self.q.put(msg)

        cfg = ScanConfig(
            game_root=game_root,
            out_dir=out_dir,
            keywords=kws,
            case_insensitive=casei,
            scan_typetree=scan_tt,
            scan_raw=scan_raw,
            raw_max_hits_per_file_per_kw=max(1, raw_hits),
        )

        def worker():
            try:
                emit(("running", True))
                emit(("status", "Preparing..."))
                scan_all(cfg, emit, self.stop_event)
                emit(("running", False))
            except Exception as e:
                emit(("log", "[ERROR] " + str(e)))
                emit(("log", traceback.format_exc()))
                emit(("status", "Error"))
                emit(("running", False))
                emit(("error", str(e)))

        self.worker = threading.Thread(target=worker, daemon=True)
        self.worker.start()

    def _poll(self):
        try:
            while True:
                msg = self.q.get_nowait()
                kind = msg[0]

                if kind == "log":
                    self._log(msg[1])
                elif kind == "status":
                    self._set_status(msg[1])
                elif kind == "running":
                    self._set_running(bool(msg[1]))
                elif kind == "progress_init":
                    self.total_files = int(msg[1])
                    self.done_files = 0
                    self._set_progress(0, self.total_files)
                elif kind == "progress":
                    self.done_files = int(msg[1])
                    self._set_progress(self.done_files, self.total_files)
                elif kind == "error":
                    messagebox.showerror("Error", msg[1])
                else:
                    pass
        except queue.Empty:
            pass
        finally:
            self.after(50, self._poll)


def main():
    app = App()
    app.mainloop()


if __name__ == "__main__":
    main()
