"""時間を測るアプリ（開発・Adobe）の登録。shifts.json の apps.registered だけを書き換える（ネイティブの Agent が読む）。
ゲームは自動で判定するので登録しない。"""
from __future__ import annotations

from pathlib import Path

from . import i18n as M
from .config import load_raw, save_raw

APP_CATEGORIES = ("dev", "adobe")


def normalize_exe(name: str) -> str | None:
    """C# の MeterSettings.NormalizeExe と同じ決まり。前後の空白を取り小文字にし，.exe で終わらせる。
    空・フォルダ区切り・コロンを含むものは None。"""
    n = (name or "").strip().lower()
    if not n or any(c in n for c in ("\\", "/", ":")):
        return None
    return n if n.endswith(".exe") else n + ".exe"


def _registered(raw: dict) -> dict | None:
    """raw["apps"]["registered"] を返す（無ければ空の辞書）。形がおかしければ None。"""
    apps = raw.get("apps", {})
    if not isinstance(apps, dict):
        return None
    reg = apps.get("registered", {})
    return reg if isinstance(reg, dict) else None


def app_add(cfg_path: Path, name: str, cat: str) -> tuple[int, str]:
    if cat == "game":
        return 1, M.APPS_NO_GAME
    if cat not in APP_CATEGORIES:
        return 1, M.APPS_BAD_CAT
    exe = normalize_exe(name)
    if exe is None:
        return 1, M.APPS_BAD_NAME
    raw = load_raw(cfg_path)
    reg = _registered(raw)
    if reg is None:
        return 1, M.APPS_BAD_SECTION
    old = reg.get(exe)
    reg[exe] = cat
    raw.setdefault("apps", {})["registered"] = reg
    save_raw(cfg_path, raw)
    if old is not None and old != cat:
        return 0, M.APPS_CHANGED.format(name=exe, old=old, cat=cat)
    return 0, M.APPS_ADDED.format(name=exe, cat=cat)


def app_remove(cfg_path: Path, name: str) -> tuple[int, str]:
    exe = normalize_exe(name)
    if exe is None:
        return 1, M.APPS_BAD_NAME
    raw = load_raw(cfg_path)
    reg = _registered(raw)
    if reg is None:
        return 1, M.APPS_BAD_SECTION
    hit = [k for k in reg if normalize_exe(k) == exe]   # 手で大文字・.exe 無しで書いたキーも外す
    if not hit:
        return 1, M.APPS_NOT_FOUND.format(name=exe)
    for k in hit:
        del reg[k]
    save_raw(cfg_path, raw)
    return 0, M.APPS_REMOVED.format(name=exe)


def app_list(cfg_path: Path) -> tuple[int, str]:
    reg = _registered(load_raw(cfg_path))
    if reg is None:
        return 1, M.APPS_BAD_SECTION
    lines = []
    for cat in APP_CATEGORIES:
        names = sorted(k for k, v in reg.items() if v == cat)
        if names:
            lines.append(M.APPS_LIST_LINE.format(cat=cat, names=", ".join(names)))
    return 0, "\n".join(lines) if lines else M.APPS_LIST_EMPTY
