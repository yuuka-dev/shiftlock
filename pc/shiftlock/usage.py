"""連続稼働（Claude Code / PC）の計測と声かけの判定、PC の離席区間。"""
from __future__ import annotations

import math
from datetime import datetime, timedelta

from .store import _read, departures, Store

_GAME_IDLE_MINUTES = 180   # ゲーム起動中でも、入力がこれだけ無ければ離席とみなす（既定。usage.game_idle_minutes）


def touch_usage(u: dict, now: datetime, reset: timedelta, can_reset: bool = True) -> dict:
    """can_reset=False（Stop 時）: 長い処理の終わりで連続カウントを切らない。"""
    last = datetime.fromisoformat(u["last"]) if u.get("last") else None
    if not u.get("start") or (can_reset and (last is None or now - last > reset)):
        u["start"] = now.isoformat(timespec="seconds")
        u.pop("last_nag", None)
    u["last"] = now.isoformat(timespec="seconds")
    return u


def due_nag(u: dict, now: datetime, cfg: dict) -> timedelta | None:
    us = cfg.get("usage", {})
    after = timedelta(minutes=us.get("nag_after_minutes", 120))
    every = timedelta(minutes=us.get("nag_every_minutes", 30))
    if not u.get("start"):
        return None
    cont = now - datetime.fromisoformat(u["start"])
    if cont < after:
        return None
    ln = u.get("last_nag")
    if ln and now - datetime.fromisoformat(ln) < every:
        return None
    u["last_nag"] = now.isoformat(timespec="seconds")
    return cont


def recent_long_gap(store: Store, now: datetime, min_hours: float = 2.5, within_hours: float = 4) -> bool | None:
    """PC の記録上、最近まとまって離席（≒睡眠）してたか。記録なしなら None。"""
    u = _read(store.p("usage-pc.json"), {})
    gaps = u.get("gaps")
    if gaps is None:
        return None
    for s, e in gaps:
        s, e = datetime.fromisoformat(s), datetime.fromisoformat(e)
        if e - s >= timedelta(hours=min_hours) and now - e <= timedelta(hours=within_hours):
            return True
    return False


def pc_gaps(store: Store, now: datetime) -> list[tuple[datetime, datetime]] | None:
    """PC を触ってない区間。いま離席中なら『離席開始〜now』も含む。記録なしなら None。"""
    u = _read(store.p("usage-pc.json"), {})
    if "gaps" not in u:
        return None
    out = [(datetime.fromisoformat(a), datetime.fromisoformat(b)) for a, b in u.get("gaps", [])]
    if u.get("gap_start"):
        out.append((datetime.fromisoformat(u["gap_start"]), now))
    return out


def game_idle_limit(cfg: dict) -> timedelta | None:
    """usage.game_idle_minutes。0 以下なら None（無制限＝ゲーム中は常に触っている扱い）。壊れた値（文字列・inf・nan）は既定。"""
    v = cfg.get("usage", {}).get("game_idle_minutes", _GAME_IDLE_MINUTES)
    try:
        n = float(v)
    except (TypeError, ValueError):
        n = _GAME_IDLE_MINUTES
    if math.isnan(n) or math.isinf(n):
        n = _GAME_IDLE_MINUTES
    return None if n <= 0 else timedelta(minutes=n)


def pc_active(idle: timedelta, game: bool, reset: timedelta, game_idle: timedelta | None) -> bool:
    """PC を触っている扱いか。最後の入力から reset 未満なら触っている。
    ゲーム起動中はゲームパッド入力が idle に乗らないので、game_idle（None なら無制限）までは触っている扱い（issue #24）。"""
    if idle < reset:
        return True
    return bool(game) and (game_idle is None or idle < game_idle)


def _dt(s) -> datetime | None:
    try:
        return datetime.fromisoformat(s)
    except (TypeError, ValueError):
        return None


def shift_away(store: Store, now: datetime, horizon: timedelta = timedelta(hours=24)) -> bool:
    """乗務で外出中か。『行ってきます』（state の worked の at が events.log の shuppatsu 行と一致）から horizon 以内で、
    その後の『ただいま』（乗務日に紐づかない free-day の記録も含む）がまだ無ければ True。
    事後報告の『出た』は出発時刻が分からないので対象外。壊れた時刻は無視する（tick の本線を止めない）。"""
    shuppatsu, kitaku = departures(store)
    status = store.shift_status()
    homes = [h for h in (_dt(k) for k in kitaku | {v["kitaku"] for v in status.values() if v.get("kitaku")}) if h]
    for day, v in status.items():
        if v.get("status") != "worked" or (day, v.get("at")) not in shuppatsu:
            continue
        dep = _dt(v["at"])
        if dep is None or not (dep <= now < dep + horizon):
            continue
        if any(dep < h <= now for h in homes):
            continue
        return True
    return False
