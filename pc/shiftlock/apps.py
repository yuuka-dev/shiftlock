"""時間を測るアプリ（開発・Adobe）の登録。shifts.json の apps.registered だけを書き換える（ネイティブの Agent が読む）。
ゲームは自動で判定するので登録しない。"""
from __future__ import annotations

from datetime import datetime
from pathlib import Path

from . import i18n as M
from .config import DEFAULTS, load_raw, save_raw
from .limit import read_games

APP_CATEGORIES = ("dev", "adobe")


def normalize_exe(name: str) -> str | None:
    """C# の MeterSettings.NormalizeExe と同じ決まり。前後の空白を取り小文字にし，.exe で終わらせる。
    空・フォルダ区切り・コロンを含むものは None。"""
    n = (name or "").strip().lower()
    if not n or any(c in n for c in ("\\", "/", ":")):
        return None
    return n if n.endswith(".exe") else n + ".exe"


def _registered(raw) -> dict | None:
    """raw["apps"]["registered"] を返す（無ければ空の辞書）。形がおかしければ None（shifts.json の最上位が辞書でないときも）。"""
    if not isinstance(raw, dict):
        return None
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
    same = [k for k in reg if normalize_exe(k) == exe]   # 手で大文字・.exe 無しで書いたキーも同じアプリとして置き換える
    old = reg[exe] if exe in reg else (reg[same[0]] if same else None)
    for k in same:
        del reg[k]
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


# ---- shl app limit（減速・一時停止の対象を足す・外す）----
def _limit_section(raw) -> dict | None:
    """raw["limit"] を返す（無ければ空の辞書）。形がおかしければ None（limit が辞書でない・apps／exclude がリストでない）。"""
    if not isinstance(raw, dict):
        return None
    lim = raw.get("limit", {})
    if not isinstance(lim, dict):
        return None
    if any(not isinstance(lim.get(k, []), list) for k in ("apps", "exclude")):
        return None
    return lim


def _norm_set(items) -> set[str]:
    return {x for x in (normalize_exe(i) for i in items if isinstance(i, str)) if x}


def _never(lim: dict) -> set[str]:
    """手を加えない名前。shifts.json に limit.never がリストであればそれ，無ければ既定値。"""
    raw = lim.get("never")
    return _norm_set(raw if isinstance(raw, list) else DEFAULTS["limit"]["never"])


def _limit_load(cfg_path: Path, name: str):
    """(exe, raw, lim, エラー)。エラーがあれば最後が (1, 文)。"""
    exe = normalize_exe(name)
    if exe is None:
        return None, None, None, (1, M.APPS_BAD_NAME)
    raw = load_raw(cfg_path)
    lim = _limit_section(raw)
    if lim is None:
        return exe, raw, None, (1, M.APPS_BAD_SECTION)
    return exe, raw, lim, None


def _limit_put(cfg_path: Path, raw: dict, lim: dict, key: str, values: list) -> None:
    """lim[key] だけを書き換えて保存する（無かった他のキーは作らない）。"""
    lim[key] = values
    raw["limit"] = lim
    save_raw(cfg_path, raw)


def limit_add(cfg_path: Path, name: str, interactive: bool) -> tuple[int, str]:
    """登録済みのアプリを減速の対象に足す。対象を広げる側なので，対話できる端末からだけ。"""
    if normalize_exe(name) is None:
        return 1, M.APPS_BAD_NAME
    if not interactive:
        return 1, M.APPS_LIMIT_NEED_TTY
    exe, raw, lim, err = _limit_load(cfg_path, name)
    if err:
        return err
    reg = _registered(raw)
    if reg is None:
        return 1, M.APPS_BAD_SECTION
    if exe in _never(lim):
        return 1, M.APPS_LIMIT_NEVER.format(name=exe)
    if exe not in {normalize_exe(k) for k in reg}:
        return 1, M.APPS_LIMIT_NOT_REGISTERED.format(name=exe)
    cur = lim.get("apps", [])
    if exe not in _norm_set(cur):
        _limit_put(cfg_path, raw, lim, "apps", [*cur, exe])
    return 0, "\n".join((M.APPS_LIMIT_ADDED.format(name=exe), M.APPS_LIMIT_ANTICHEAT_WARN, M.APPS_LIMIT_PROTECT_NOTE))


def limit_remove(cfg_path: Path, name: str) -> tuple[int, str]:
    exe, raw, lim, err = _limit_load(cfg_path, name)
    if err:
        return err
    cur = lim.get("apps", [])
    keep = [x for x in cur if not (isinstance(x, str) and normalize_exe(x) == exe)]
    if len(keep) == len(cur):
        return 1, M.APPS_LIMIT_NOT_FOUND.format(name=exe)
    _limit_put(cfg_path, raw, lim, "apps", keep)
    return 0, M.APPS_LIMIT_REMOVED.format(name=exe)


def limit_exclude(cfg_path: Path, name: str) -> tuple[int, str]:
    """ゲームを減速の対象から外す。見たことの無い名前でも外せる（先回りして外せるように）。"""
    exe, raw, lim, err = _limit_load(cfg_path, name)
    if err:
        return err
    cur = lim.get("exclude", [])
    if exe not in _norm_set(cur):
        _limit_put(cfg_path, raw, lim, "exclude", [*cur, exe])
    return 0, M.APPS_LIMIT_EXCLUDED.format(name=exe)


def limit_include(cfg_path: Path, name: str, interactive: bool) -> tuple[int, str]:
    """外したゲームを戻す。対象を広げる側なので，対話できる端末からだけ。"""
    if normalize_exe(name) is None:
        return 1, M.APPS_BAD_NAME
    if not interactive:
        return 1, M.APPS_LIMIT_NEED_TTY
    exe, raw, lim, err = _limit_load(cfg_path, name)
    if err:
        return err
    cur = lim.get("exclude", [])
    keep = [x for x in cur if not (isinstance(x, str) and normalize_exe(x) == exe)]
    if len(keep) == len(cur):
        return 1, M.APPS_LIMIT_NOT_EXCLUDED.format(name=exe)
    _limit_put(cfg_path, raw, lim, "exclude", keep)
    return 0, M.APPS_LIMIT_INCLUDED.format(name=exe)


def limit_list(cfg_path: Path, store, now: datetime) -> tuple[int, str]:
    """足したアプリ・外したゲーム・最近見たゲームの判定。"""
    lim = _limit_section(load_raw(cfg_path))
    if lim is None:
        return 1, M.APPS_BAD_SECTION
    apps, excluded = sorted(_norm_set(lim.get("apps", []))), sorted(_norm_set(lim.get("exclude", [])))
    lines = [M.APPS_LIMIT_LIST_APPS.format(names=", ".join(apps)) if apps else M.APPS_LIMIT_LIST_APPS_NONE,
             M.APPS_LIMIT_LIST_EXCLUDED.format(names=", ".join(excluded)) if excluded else M.APPS_LIMIT_LIST_EXCLUDED_NONE]
    seen = read_games(store, now)
    label = {"target": M.APPS_LIMIT_VERDICT_TARGET, "anticheat": M.APPS_LIMIT_VERDICT_ANTICHEAT, "unknown": M.APPS_LIMIT_VERDICT_UNKNOWN}
    if seen:
        lines.append(M.APPS_LIMIT_LIST_GAMES)
        lines += [M.APPS_LIMIT_LIST_GAME_LINE.format(name=g, verdict=label[v],
                                                      excluded=M.APPS_LIMIT_LIST_GAME_EXCLUDED if g in excluded else "")
                  for g, v in sorted(seen.items())]
    else:
        lines.append(M.APPS_LIMIT_LIST_GAMES_NONE)
    return 0, "\n".join(lines)
