# -*- coding: utf-8 -*-
# 허니팝 2 (GOG) 한글패치 패쳐 (단일 파일)
#
# 요구사항 요약
# - Steam 자동 경로 탐지 제거 (사용자가 exe 직접 선택)
# - exe 선택 후:
#   1) GOG buildId 확인: <game_root>\goggame-1455877361.info 의 buildId == 55898712513408984
#   2) 원본 파일 검증(2개, SHA1):
#      <game_root>\HuniePop 2 - Double Date_Data\resources.assets
#      <game_root>\HuniePop 2 - Double Date_Data\resources.assets.resS
#   3) 패치 파일 검증(압축 해제 후 2개, SHA1):
#      resources.assets / resources.assets.resS
#   4) 패치 시작:
#      - 원본 2개를 리네임 백업
#      - 패치 2개를 Data 폴더로 복사
#      - patcher 폴더의 plugin 폴더(하위 포함)를 게임 루트로 복사
#
# UI/로그에는 '해시' 같은 표현을 노출하지 않고 항상 '파일 검증' 톤 유지.

from __future__ import annotations

import json
import os
import sys
import hashlib
import shutil
import subprocess
import tempfile
from pathlib import Path
from dataclasses import dataclass

from PySide6.QtCore import Qt, QThread, Signal
from PySide6.QtGui import QIcon
from PySide6.QtWidgets import (
    QApplication, QWidget, QLabel, QLineEdit, QPushButton, QTextEdit,
    QFileDialog, QVBoxLayout, QHBoxLayout, QMessageBox
)

# -----------------------------
# 기본 설정 (UI 동일)
# -----------------------------
APP_TITLE = "허니팝2 더블 데이트 한글패치 1.0"
BLOG_URL = "https://blog.naver.com/ccs2121"  # 표시만, 필요시 교체

# 게임 판본 검증 (GOG info)
GOG_INFO_NAME = "goggame-1455877361.info"
GOG_BUILD_ID = "55898712513408984"

# HP2 데이터 경로
DATA_DIR_NAME = "HuniePop 2 - Double Date_Data"

# 원본 파일 검증(SHA1)
ORIGINAL_SHA1 = {
    "resources.assets": "3CB5C3095D13C9790279FB08B938382B80B364B7",
    "resources.assets.resS": "9A80875A219DFFCDC4E2128D94FD8B4BD9669B9B",
}

# 패치 파일 검증(SHA1) - 압축 해제 후 결과물 기준
PATCH_SHA1 = {
    "resources.assets": "FFAAF395C7B2A8DE923D78B5F8DEB3AC9C7D1BA9",
    "resources.assets.resS": "9A80875A219DFFCDC4E2128D94FD8B4BD9669B9B",
}

BACKUP_SUFFIX = "_bkup"

MSG_WRONG_GAME = (
    "GOG판 허니팝 1.1.0a 에 적용되는 패치 입니다.\n"
    "맞는 게임에 패치해 주세요."
)
MSG_PATCHFILES_INVALID = (
    "파일 검증에 실패했습니다.\n"
    "패치 파일에 문제가 있습니다.\n"
    "새로 다운로드 해 주세요."
)

# -----------------------------
# 유틸
# -----------------------------
def get_app_dir() -> Path:
    # 단일 파일 배포/실행 모두 대응
    if getattr(sys, "frozen", False) and hasattr(sys, "_MEIPASS"):
        return Path(sys._MEIPASS).resolve()
    return Path(__file__).resolve().parent

def dev_print(msg: str) -> None:
    # 배포 시 조용히
    try:
        if os.environ.get("HP_PATCHER_DEV") == "1":
            print(msg)
    except Exception:
        pass

def sha1_file(path: Path, chunk: int = 1024 * 1024) -> str:
    h = hashlib.sha1()
    with path.open("rb") as f:
        while True:
            b = f.read(chunk)
            if not b:
                break
            h.update(b)
    return h.hexdigest().upper()

def robust_rmtree(path: Path) -> None:
    try:
        if path.exists():
            shutil.rmtree(path, ignore_errors=True)
    except Exception:
        pass

def robust_rename_strict(src: Path, dst: Path) -> tuple[bool, str]:
    """
    dst가 이미 존재하면 실패.
    """
    try:
        if not src.exists():
            return False, f"파일을 찾을 수 없습니다: {src.name}"
        if dst.exists():
            return False, f"이미 백업 파일이 존재합니다: {dst.name}"
        src.rename(dst)
        return True, ""
    except Exception as e:
        return False, f"파일 백업 중 오류가 발생했습니다: {e}"

def merge_copytree(src: Path, dst: Path) -> tuple[bool, str]:
    """
    src의 모든 파일/폴더를 dst로 복사(덮어쓰기).
    """
    try:
        if not src.exists():
            return False, f"폴더를 찾을 수 없습니다: {src}"
        dst.mkdir(parents=True, exist_ok=True)

        for root, dirs, files in os.walk(src):
            rel = Path(root).relative_to(src)
            out_dir = dst / rel
            out_dir.mkdir(parents=True, exist_ok=True)
            for d in dirs:
                (out_dir / d).mkdir(parents=True, exist_ok=True)
            for fn in files:
                s = Path(root) / fn
                t = out_dir / fn
                shutil.copy2(s, t)
        return True, ""
    except Exception as e:
        return False, f"파일 복사 중 오류가 발생했습니다: {e}"

def extract_patch_to_temp(app_dir: Path) -> tuple[bool, str, Path | None]:
    """
    files\patcher.exe 로 files\patch 를 임시폴더에 압축 해제.
    성공 시 (True, "", temp_dir) / 실패 시 (False, msg, None)
    """
    patcher = app_dir / "files" / "patcher.exe"
    patch_file = app_dir / "files" / "patch"

    if not patcher.exists():
        return False, "files\\patcher.exe 파일을 찾을 수 없습니다.", None
    if not patch_file.exists():
        return False, "files\\patch 파일을 찾을 수 없습니다.", None

    temp_dir = Path(tempfile.mkdtemp(prefix="HuniePop_KR_", dir=None))

    cmd = [
        str(patcher),
        "x",
        "-y",
        f"-o{str(temp_dir)}",
        str(patch_file),
    ]

    try:
        # 콘솔창 숨김 + 출력만 수집
        creationflags = 0
        if os.name == "nt":
            creationflags = subprocess.CREATE_NO_WINDOW  # type: ignore[attr-defined]

        p = subprocess.run(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            creationflags=creationflags,
            cwd=str(app_dir),
        )
        if p.returncode != 0:
            dev_print(f"[DEV] extract rc={p.returncode} stdout={p.stdout} stderr={p.stderr}")
            robust_rmtree(temp_dir)
            return False, MSG_PATCHFILES_INVALID, None

        return True, "", temp_dir

    except Exception as e:
        dev_print(f"[DEV] extract exception: {e}")
        robust_rmtree(temp_dir)
        return False, MSG_PATCHFILES_INVALID, None

# -----------------------------
# 경로 해석 / 검증
# -----------------------------
@dataclass(frozen=True)
class GamePaths:
    game_root: Path
    gog_info: Path
    data_root: Path
    assets: Path
    resS: Path

def resolve_game_paths(exe_path: Path) -> GamePaths | None:
    if not exe_path.exists():
        return None
    game_root = exe_path.parent.resolve()
    gog_info = (game_root / GOG_INFO_NAME).resolve()
    data_root = (game_root / DATA_DIR_NAME).resolve()
    assets = (data_root / "resources.assets").resolve()
    resS = (data_root / "resources.assets.resS").resolve()

    # data_root만 최소 확인(나머지는 검증 단계에서 메시지 통일 처리)
    if not data_root.exists():
        return None
    return GamePaths(game_root=game_root, gog_info=gog_info, data_root=data_root, assets=assets, resS=resS)

def verify_gog_build(paths: GamePaths) -> bool:
    try:
        if not paths.gog_info.exists():
            return False
        obj = json.loads(paths.gog_info.read_text(encoding="utf-8", errors="replace"))
        build_id = str(obj.get("buildId", ""))
        return build_id == GOG_BUILD_ID
    except Exception:
        return False

def verify_original_files(paths: GamePaths) -> bool:
    try:
        if not paths.assets.exists() or not paths.resS.exists():
            return False
        a1 = sha1_file(paths.assets)
        b1 = sha1_file(paths.resS)
        return (a1 == ORIGINAL_SHA1["resources.assets"] and b1 == ORIGINAL_SHA1["resources.assets.resS"])
    except Exception:
        return False

def verify_patch_payload(temp_dir: Path) -> tuple[bool, str]:
    try:
        a = temp_dir / "resources.assets"
        b = temp_dir / "resources.assets.resS"
        if not a.exists() or not b.exists():
            return False, MSG_PATCHFILES_INVALID

        a1 = sha1_file(a)
        b1 = sha1_file(b)
        if a1 != PATCH_SHA1["resources.assets"] or b1 != PATCH_SHA1["resources.assets.resS"]:
            return False, MSG_PATCHFILES_INVALID
        return True, ""
    except Exception as e:
        dev_print(f"[DEV] verify_patch_payload exception: {e}")
        return False, MSG_PATCHFILES_INVALID

def detect_already_patched(paths: GamePaths) -> bool:
    # 백업 파일이 있으면 이미 적용된 것으로 간주
    return (paths.assets.with_name(paths.assets.name + BACKUP_SUFFIX).exists()
            or paths.resS.with_name(paths.resS.name + BACKUP_SUFFIX).exists())

# -----------------------------
# 워커 스레드
# -----------------------------
class VerifyWorker(QThread):
    log = Signal(str)
    done = Signal(str, str)  # (status, msg)

    def __init__(self, paths: GamePaths):
        super().__init__()
        self.paths = paths

    def _log(self, msg: str) -> None:
        self.log.emit(msg)

    def run(self):
        try:
            # 0) 이미 패치 여부(선택)
            if detect_already_patched(self.paths):
                self.done.emit("patched", "")
                return

            # 1) GOG buildId 확인
            self._log("파일 검증 중 입니다.")
            if not verify_gog_build(self.paths):
                self.done.emit("mismatch", MSG_WRONG_GAME)
                return

            # 2) 원본 파일 검증(2개)
            self._log("파일 검증 중 입니다.")
            if not verify_original_files(self.paths):
                self.done.emit("mismatch", MSG_WRONG_GAME)
                return

            # 3) 임시폴더에 압축 해제
            app_dir = get_app_dir()
            ok, msg, temp_dir = extract_patch_to_temp(app_dir)
            if not ok or temp_dir is None:
                self.done.emit("payload_fail", msg if msg else MSG_PATCHFILES_INVALID)
                return

            # 4) 패치 파일 검증(2개)
            self._log("파일 검증 중 입니다.")
            ok2, msg2 = verify_patch_payload(temp_dir)
            if not ok2:
                robust_rmtree(temp_dir)
                self.done.emit("payload_fail", msg2 if msg2 else MSG_PATCHFILES_INVALID)
                return

            # 성공: temp_dir 경로를 msg로 전달(버튼 활성화를 위해)
            self.done.emit("ok", str(temp_dir))

        except Exception as e:
            dev_print(f"[DEV] VerifyWorker exception: {e}")
            self.done.emit("error", "검증 중 오류가 발생했습니다.")


class PatchWorker(QThread):
    log = Signal(str)
    done = Signal(bool, str)

    def __init__(self, paths: GamePaths, temp_dir: Path):
        super().__init__()
        self.paths = paths
        self.temp_dir = temp_dir

    def _log(self, msg: str) -> None:
        self.log.emit(msg)

    def _rollback(self, renamed: list[tuple[Path, Path]], copied: list[Path]) -> None:
        # copied 제거
        for p in reversed(copied):
            try:
                if p.exists():
                    p.unlink()
            except Exception:
                pass
        # renamed 복구
        for src, dst in reversed(renamed):
            try:
                if dst.exists() and not src.exists():
                    dst.rename(src)
            except Exception:
                pass

    def run(self):
        renamed: list[tuple[Path, Path]] = []
        copied: list[Path] = []

        try:
            # 1) 백업(리네임)
            self._log("패치를 적용 중 입니다.")

            a_src = self.paths.assets
            b_src = self.paths.resS
            a_bak = a_src.with_name(a_src.name + BACKUP_SUFFIX)
            b_bak = b_src.with_name(b_src.name + BACKUP_SUFFIX)

            ok, msg = robust_rename_strict(a_src, a_bak)
            if not ok:
                self.done.emit(False, msg)
                return
            renamed.append((a_src, a_bak))

            ok, msg = robust_rename_strict(b_src, b_bak)
            if not ok:
                self._rollback(renamed, copied)
                self.done.emit(False, msg)
                return
            renamed.append((b_src, b_bak))

            # 2) 패치 파일 복사
            a_new = self.temp_dir / "resources.assets"
            b_new = self.temp_dir / "resources.assets.resS"

            shutil.copy2(a_new, a_src)
            copied.append(a_src)
            shutil.copy2(b_new, b_src)
            copied.append(b_src)

            # 3) plugin 폴더 복사 (패처 폴더\plugin -> 게임 루트)
            app_dir = get_app_dir()
            plugin_src = app_dir / "plugin"
            ok, msg = merge_copytree(plugin_src, self.paths.game_root)
            if not ok:
                # 복사 실패 시 원복
                self._rollback(renamed, copied)
                self.done.emit(False, msg)
                return

            # 4) 임시폴더 정리
            robust_rmtree(self.temp_dir)

            self.done.emit(True, "패치가 완료되었습니다.")

        except Exception as e:
            dev_print(f"[DEV] PatchWorker exception: {e}")
            try:
                self._rollback(renamed, copied)
            except Exception:
                pass
            try:
                robust_rmtree(self.temp_dir)
            except Exception:
                pass
            self.done.emit(False, "패치 중 오류가 발생했습니다.")

# -----------------------------
# UI
# -----------------------------
class MainWindow(QWidget):
    def __init__(self):
        super().__init__()
        self.setWindowTitle(APP_TITLE)

        icon_path = get_app_dir() / "files" / "ico.ico"
        if icon_path.exists():
            self.setWindowIcon(QIcon(str(icon_path)))

        self.setFixedSize(600, 320)

        self.paths: GamePaths | None = None
        self.worker: PatchWorker | None = None
        self._temp_dir_from_verify: Path | None = None

        self._build_ui()

    def _build_ui(self):
        title = QLabel(
            f"허니팝 2 한글패치 by 레트로겜보이 (<a href='{BLOG_URL}'>{BLOG_URL}</a>)"
        )
        title.setTextFormat(Qt.RichText)
        title.setOpenExternalLinks(True)

        self.path_edit = QLineEdit()
        self.path_edit.setReadOnly(True)

        self.browse_btn = QPushButton("찾아보기")
        self.browse_btn.clicked.connect(self.on_browse)

        top_row = QHBoxLayout()
        top_row.addWidget(self.path_edit, 1)
        top_row.addWidget(self.browse_btn)

        self.log_box = QTextEdit()
        self.log_box.setReadOnly(True)
        self.log_box.setPlaceholderText("[로그창]")

        self.start_btn = QPushButton("패치 시작")
        self.start_btn.setEnabled(False)
        self.start_btn.clicked.connect(self.on_start_patch)

        layout = QVBoxLayout()
        layout.addWidget(title)
        layout.addSpacing(10)
        layout.addWidget(QLabel("게임 실행파일을 선택 하세요"))
        layout.addLayout(top_row)
        layout.addSpacing(8)
        layout.addWidget(self.log_box, 1)
        layout.addSpacing(8)
        layout.addWidget(self.start_btn)
        self.setLayout(layout)

        self.append_log("게임 실행 파일을 선택해 주세요.")

    def append_log(self, text: str):
        self.log_box.append(text)
        self.log_box.verticalScrollBar().setValue(self.log_box.verticalScrollBar().maximum())

    def _reset_state(self):
        self.start_btn.setEnabled(False)
        self.paths = None
        self._temp_dir_from_verify = None

    def on_browse(self):
        self._reset_state()
        self.log_box.clear()

        exe, _ = QFileDialog.getOpenFileName(
            self,
            "게임 실행 파일 선택",
            "",
            "실행 파일 (*.exe);;모든 파일 (*.*)"
        )
        if not exe:
            self.append_log("게임 실행 파일을 선택해 주세요.")
            return

        exe_path = Path(exe)
        self.path_edit.setText(str(exe_path))

        paths = resolve_game_paths(exe_path)
        if paths is None:
            QMessageBox.warning(self, "오류", MSG_WRONG_GAME)
            self.append_log(MSG_WRONG_GAME.replace("\n", " "))
            return

        self.paths = paths
        self._run_verify()

    def _run_verify(self):
        if not self.paths:
            return

        self.browse_btn.setEnabled(False)
        self.start_btn.setEnabled(False)
        self.append_log("파일 검증을 시작합니다.")

        self.verify_worker = VerifyWorker(self.paths)
        self.verify_worker.log.connect(self.append_log)
        self.verify_worker.done.connect(self._on_verify_done)
        self.verify_worker.start()

    def _on_verify_done(self, status: str, msg: str):
        self.browse_btn.setEnabled(True)

        if status == "ok":
            self._temp_dir_from_verify = Path(msg)
            self.append_log("파일 검증 완료. 패치 시작 버튼을 눌러주세요.")
            self.start_btn.setEnabled(True)
            return

        # temp_dir 정리
        if status == "payload_fail" and self._temp_dir_from_verify and self._temp_dir_from_verify.exists():
            robust_rmtree(self._temp_dir_from_verify)
        self._temp_dir_from_verify = None
        self.start_btn.setEnabled(False)

        if status == "patched":
            QMessageBox.information(self, "안내", "이미 패치가 적용된 상태로 보입니다.")
            self.append_log("이미 패치가 적용된 상태로 보입니다.")
            return

        if status == "mismatch":
            QMessageBox.warning(self, "오류", msg if msg else MSG_WRONG_GAME)
            self.append_log((msg if msg else MSG_WRONG_GAME).replace("\n", " "))
            return

        if status == "payload_fail":
            QMessageBox.warning(self, "오류", msg if msg else MSG_PATCHFILES_INVALID)
            self.append_log((msg if msg else MSG_PATCHFILES_INVALID).replace("\n", " "))
            return

        QMessageBox.warning(self, "오류", "검증 중 오류가 발생했습니다.")
        self.append_log("검증 중 오류가 발생했습니다.")

    def on_start_patch(self):
        if not self.paths or not self._temp_dir_from_verify:
            return

        self.browse_btn.setEnabled(False)
        self.start_btn.setEnabled(False)

        self.worker = PatchWorker(self.paths, self._temp_dir_from_verify)
        self.worker.log.connect(self.append_log)
        self.worker.done.connect(self._on_patch_done)
        self.worker.start()

    def _on_patch_done(self, ok: bool, msg: str):
        self.browse_btn.setEnabled(True)
        self.start_btn.setEnabled(False)

        if ok:
            QMessageBox.information(self, "완료", msg)
            self.close()
        else:
            QMessageBox.warning(self, "오류", msg)
            self.append_log(msg)

def main():
    app = QApplication(sys.argv)
    w = MainWindow()
    w.show()
    sys.exit(app.exec())

if __name__ == "__main__":
    main()
