"""PC の無操作や『ねる』の記録から、寝てたことを控えめに推定して自動で『起きた』にする。"""
from __future__ import annotations

from datetime import datetime, timedelta, date as Date

from . import i18n as M
from .plan import is_workday, make_plan, no_sleep_intervals, Plan, target_plan
from .store import log_read, Store
from .timeutil import at, fmt, fmt_dur, _subtract
from .usage import pc_gaps


def slept_before(cfg: dict, store: Store, p: Plan, now: datetime) -> tuple[datetime, datetime] | None:
    """乗務日 p の出庫前に、最低睡眠ぶんまとまって寝てた区間があれば返す（控えめに数える）。
    - 前日が乗務なら、出発〜帰宅の最遅時刻（home_by_latest）は外出扱いで数えない
    - no_sleep（訪問など）の時間帯は数えない
    - Claude Code にプロンプトを送ってた時刻があれば、そこで区切る
    """
    mode = cfg.get("auto_wake", True)
    mode = "auto" if mode is True else ("off" if mode is False else str(mode))
    if mode == "off":
        return None
    hours = cfg.get("auto_wake_hours") or cfg["sleep_min_hours"]
    d = Date.fromisoformat(p.day)
    day0 = datetime.combine(d, datetime.min.time())
    me = store.shift_status().get(p.day, {})
    gaps = pc_gaps(store, now) or []
    cuts = list(no_sleep_intervals(cfg, d - timedelta(days=1), d))
    prompts = [r["_t"] for r in log_read(store, "claude", day0 - timedelta(hours=6), now + timedelta(minutes=1))
               if r.get("k") == "p"]
    cuts += [(t, t + timedelta(minutes=1)) for t in prompts]

    cands: list[tuple[tuple[datetime, datetime], list]] = []   # (区間, その区間に効かせるカット)
    if me.get("sleep_at"):
        # 『ねる』の記録あり → そこから、PC を触る／Claude を使うまで（どちらもなければ now）
        sa = datetime.fromisoformat(me["sleep_at"])
        end = now
        for gs, ge in gaps:
            if gs <= sa + timedelta(minutes=20) and ge >= sa:
                end = min(end, ge)
        for t in prompts:
            if t > sa + timedelta(minutes=2):
                end = min(end, t)
        cands.append(((sa, end), cuts))          # 『ねる』を打った時点で家におるので外出カットは要らん
    if mode == "auto" and gaps:
        prev = d - timedelta(days=1)
        status = store.shift_status()
        gcuts = list(cuts)
        if is_workday(cfg, prev) and status.get(prev.isoformat(), {}).get("status") != "yasumi":
            pp = make_plan(cfg, prev)
            home = datetime.fromisoformat(me["kitaku"]) if me.get("kitaku") else at(d, cfg.get("home_by_latest", "09:00"))
            gcuts.append((pp.depart_target, home))
        cands += [(g, gcuts) for g in gaps]
    if not cands:
        return None
    best = None
    for g, cc in cands:
        for s_, e_ in _subtract(g, cc):
            s_, e_ = max(s_, day0 - timedelta(hours=3)), min(e_, now, p.depart_latest)
            if e_ - s_ >= timedelta(hours=hours) and (best is None or e_ - s_ > best[1] - best[0]):
                best = (s_, e_)
    return best


def maybe_auto_okita(cfg: dict, store: Store, now: datetime) -> str:
    """直近の乗務で、もう寝てたことが記録からわかれば自動で『起きた』にする。返り値はお知らせ文。"""
    p = target_plan(cfg, now)
    if p is None or now < datetime.combine(Date.fromisoformat(p.day), datetime.min.time()):
        return ""
    if store.shift_status().get(p.day, {}).get("status"):
        return ""
    sl_ = slept_before(cfg, store, p, now)
    if not sl_:
        return ""
    s_, e_ = sl_
    store.set_shift_status(p.day, "okita", now, f"auto {s_:%H:%M}-{e_:%H:%M}")
    return M.AUTOWAKE_OKITA.format(start=fmt(s_), end=fmt(e_), dur=fmt_dur(e_ - s_),
                                   depart=fmt(p.depart_target), latest=fmt(p.depart_latest))
