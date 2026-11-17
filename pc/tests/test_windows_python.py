"""Windows の Python で動かすときの振る舞い（issue #22: tick を WSL 経由にしない）．"""
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
import shiftlock as sl


def test_to_local_path_keeps_windows_path_on_windows(monkeypatch):
    # Windows の Python で動くとき（sys.platform == "win32"）は C:\ のパスを /mnt/c に変換しない
    monkeypatch.setattr(sys, "platform", "win32")
    assert sl.to_local_path(r"C:\Users\x\mood.csv") == Path(r"C:\Users\x\mood.csv")


def test_to_local_path_converts_windows_path_on_wsl(monkeypatch):
    # WSL（Linux）では従来どおり /mnt/c/... に変換する．POSIX パスはそのまま
    monkeypatch.setattr(sys, "platform", "linux")
    assert sl.to_local_path(r"C:\Users\x\mood.csv") == Path("/mnt/c/Users/x/mood.csv")
    assert sl.to_local_path("/home/x/mood.csv") == Path("/home/x/mood.csv")
