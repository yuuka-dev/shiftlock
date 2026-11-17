"""乗務日と時刻（出発目安・就寝目安・最低ライン）の逆算、いまのフェーズの判定、フェーズごとの文面。"""
from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timedelta, date as Date

from . import i18n as M
from .config import DEFAULTS, WEEKDAYS
from .timeutil import at, fmt, fmt_dur


def no_sleep_intervals(cfg: dict, d0: Date, d1: Date) -> list[tuple[datetime, datetime]]:
    out = []
    d = d0
    while d <= d1:
        for r in cfg.get("no_sleep", []):
            hit = ("date" in r and r["date"] == d.isoformat()) or \
                  ("weekday" in r and r["weekday"].lower()[:3] == WEEKDAYS[d.weekday()])
            if hit:
                s, e = at(d, r["from"]), at(d, r["to"])
                if e <= s:
                    e += timedelta(days=1)
                out.append((s, e))
        d += timedelta(days=1)
    return out


def latest_bedtime(cfg: dict, wake: datetime, hours: float) -> datetime:
    """wake までに、no_sleep を除いて hours 時間寝られる最も遅い就寝時刻。"""
    need = timedelta(hours=hours)
    blocks = no_sleep_intervals(cfg, (wake - timedelta(days=2)).date(), wake.date())
    blocks.sort(key=lambda b: b[1], reverse=True)
    t = wake
    for s, e in blocks:
        if s >= t:
            continue
        if e < t:
            free = t - e
            if free >= need:
                return t - need
            need -= free
        t = min(t, s)
    return t - need


@dataclass(frozen=True)
class Plan:
    day: str                 # 乗務日 YYYY-MM-DD（出庫する日）
    start_target: datetime
    start_latest: datetime
    depart_target: datetime
    depart_latest: datetime
    soft_close: datetime
    hard_close: datetime
    lastcall: datetime


def is_workday(cfg: dict, d: Date) -> bool:
    ds = d.isoformat()
    dd = cfg.get("_days", {})
    if ds in dd.get("off", {}):
        return False
    if ds in dd.get("work", {}):
        return True
    legacy = {x if isinstance(x, str) else x.get("date") for x in cfg.get("shifts", [])}
    if ds in legacy:
        return True
    return WEEKDAYS[d.weekday()] in cfg.get("work_weekdays", DEFAULTS["work_weekdays"])


def day_times(cfg: dict, ds: str) -> dict:
    t = dict(cfg.get("_days", {}).get("times", {}).get(ds, {}))
    for x in cfg.get("shifts", []):   # 旧形式 { "date":..., "start_target":... } も読む
        if isinstance(x, dict) and x.get("date") == ds:
            t = {**x, **t}
    return t


def make_plan(cfg: dict, d: Date) -> Plan:
    s = day_times(cfg, d.isoformat())
    prep = timedelta(minutes=float(cfg["prep_minutes"]))
    lastcall = timedelta(minutes=float(cfg.get("lastcall_minutes", 40)))
    st = at(d, s.get("start_target", cfg["start_target"]))
    sl = at(d, s.get("start_latest", cfg["start_latest"]))
    if sl < st:
        sl = st
    dt_, dl = st - prep, sl - prep
    soft = latest_bedtime(cfg, dt_, float(s.get("sleep_target_hours", cfg["sleep_target_hours"])))
    hard = max(soft, latest_bedtime(cfg, dl, float(s.get("sleep_min_hours", cfg["sleep_min_hours"]))))
    return Plan(d.isoformat(), st, sl, dt_, dl, soft, hard, soft - lastcall)


def plans(cfg: dict, around: Date | None = None, back: int = 40, fwd: int = 14) -> list[Plan]:
    """around を中心に back 日前〜 fwd 日後の乗務日（曜日ルール＋例外）を展開する。"""
    around = around or datetime.now().date()
    out = []
    for i in range(-back, fwd + 1):
        d = around + timedelta(days=i)
        if is_workday(cfg, d):
            out.append(make_plan(cfg, d))
    return out


def current(cfg: dict, status: dict, now: datetime) -> tuple[str, Plan | None]:
    for p in plans(cfg, now.date(), back=2, fwd=2):
        if not (p.lastcall <= now < p.start_latest):
            continue
        s = status.get(p.day, {}).get("status")
        if s in ("yasumi", "worked"):   # 休み / 出発済み（出た）→ その乗務についてはもう何も言わへん
            continue
        if s == "okita":
            if now >= p.depart_latest - timedelta(minutes=15):
                return "go_last", p
            if now >= p.depart_target - timedelta(minutes=15):
                return "go", p
            continue
        if now < p.soft_close:
            return "lastcall", p
        if now < p.hard_close:
            return "soft", p
        return "hard", p
    return "free", None


def target_plan(cfg: dict, now: datetime) -> Plan | None:
    """休む/起きた の対象 = 最終出庫がまだ来てない直近の乗務（24時間以内）。"""
    for p in plans(cfg, now.date(), back=1, fwd=2):
        if now < p.start_latest <= now + timedelta(hours=24):
            return p
    return None


def phase_messages(phase: str, p: Plan, now: datetime, cfg: dict) -> tuple[str, str]:
    """(ユーザー向け, Claude 向け)。文面は i18n。"""
    if phase == "lastcall":
        remain = fmt_dur(p.soft_close - now)
        return (M.PHASE_LASTCALL_USER.format(soft=fmt(p.soft_close), remain=remain),
                M.PHASE_LASTCALL_CTX.format(remain=remain))
    if phase == "soft":
        return (M.PHASE_SOFT_USER.format(soft=fmt(p.soft_close), target_h=cfg["sleep_target_hours"], hard=fmt(p.hard_close)),
                M.PHASE_SOFT_CTX)
    if phase == "hard":
        return M.PHASE_HARD_USER.format(min_h=cfg["sleep_min_hours"], latest=fmt(p.start_latest)), ""
    if phase == "go":
        return M.PHASE_GO_USER.format(depart=fmt(p.depart_target)), M.PHASE_GO_CTX
    if phase == "go_last":
        return M.PHASE_GO_LAST_USER.format(depart=fmt(p.depart_latest), latest=fmt(p.start_latest)), M.PHASE_GO_LAST_CTX
    return "", ""


def last_past_plan(cfg: dict, now: datetime, lookback_days: int) -> Plan | None:
    """直近 lookback_days 日で、出庫最終がもう過ぎた最後の乗務。"""
    ps = [p for p in plans(cfg, now.date(), back=int(lookback_days), fwd=0) if p.start_latest <= now]
    return ps[-1] if ps else None
