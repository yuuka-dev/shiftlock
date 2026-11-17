"""キーワードへの応答（cmd_keyword / cmd_day）と『状況』の表示。"""
from __future__ import annotations

from datetime import datetime, timedelta, date as Date
from pathlib import Path

from . import i18n as M
from .attendance import absence_note, absence_stats
from .config import calendar_sources, load_config
from .confirm import unconfirmed_shifts
from .days import days_set, schedule_text
from .enforce import _declared, stop_active
from .keywords import (KW_KITAKU, KW_NERU, KW_OKITA, KW_SCHEDULE, KW_SHUPPATSU, KW_STATUS, KW_UNDO, KW_UNDO_FUZZY, KW_YASUMI)
from .plan import current, is_workday, make_plan, plans, target_plan
from .records import do_kitaku, do_neru, do_okita, do_shuppatsu, late_report, month_yasumi_count, undo_fuzzy
from .store import _read, Store
from .timeutil import fmt, fmt_d, parse_day, WD_JA


def cmd_day(m, cfg_path: Path, store: Store, now: datetime) -> str:
    try:
        d = parse_day(m.group(1), now.date())
    except ValueError as e:
        return M.CMD_BAD_DATE.format(err=e)
    kind = {"休み": "off", "休む": "off", "出勤": "work", "出る": "work", "戻す": None}[m.group(2)]   # 入力の語彙（RE_DAY の 2 つ目）
    days_set(store, d, kind, (m.group(3) or "").strip())
    cfg = load_config(cfg_path)
    return M.CMD_DAY_SET.format(label=f"{d:%m/%d}({WD_JA[d.weekday()]})", kind=M.CMD_DAY_KIND_LABEL[kind],
                                schedule=schedule_text(cfg, now.date()))


def cmd_keyword(kw: str, cfg: dict, store: Store, now: datetime, paid: bool = False, via: str = "claude") -> str:
    """固定キーワードへの応答。paid: 『今日休む 有給』（当日欠勤を有給の印つきで記録）。via は events.log の出所。"""
    if kw in KW_STATUS:
        return status_text(cfg, store, now)
    if kw in KW_SCHEDULE:
        return M.CMD_SCHEDULE.format(schedule=schedule_text(cfg, now.date()))
    if kw in KW_UNDO_FUZZY:
        return undo_fuzzy(store, now)
    p = target_plan(cfg, now)
    if kw in KW_NERU:
        return do_neru(cfg, store, now, p)
    if kw in KW_OKITA:
        return do_okita(cfg, store, now, p)
    if kw in KW_KITAKU:
        return do_kitaku(cfg, store, now, p)
    if kw in KW_SHUPPATSU:
        return do_shuppatsu(cfg, store, now, p)
    # 出庫最終のあとの「今日休む」は、翌日やなくて今日の乗務（事後報告）
    if kw == "今日休む" and is_workday(cfg, now.date()) and make_plan(cfg, now.date()).start_latest <= now:
        return late_report(cfg, store, now, "yasumi", "今日", via=via, paid=True if paid else None)
    if p is None:
        return M.CMD_NO_PLAN
    d = p.start_latest.strftime("%m/%d")
    if kw in KW_YASUMI:
        store.set_shift_status(p.day, "yasumi", now)
        if paid:
            store.set_paid(p.day, True, now, via)
        n = month_yasumi_count(store, now)
        return (M.CMD_YASUMI.format(day=d, n=n) + (M.CMD_YASUMI_PAID if paid else "")
                + absence_note(cfg, store, now, Date.fromisoformat(p.day)))
    if kw in KW_UNDO:
        prev = store.shift_status().get(p.day, {}).get("status")
        store.set_shift_status(p.day, None, now)
        return M.CMD_UNDO.format(day=d, what=M.CMD_UNDO_WORKED if prev == "worked" else M.CMD_UNDO_YASUMI)
    return ""


def status_text(cfg: dict, store: Store, now: datetime) -> str:
    phase, cur = current(cfg, store.shift_status(), now)
    p = cur or target_plan(cfg, now) or next((x for x in plans(cfg, now.date(), back=0) if x.start_latest > now), None)
    lines = [M.STATUS_HEAD.format(now=fmt_d(now), phase=phase)]
    if p:
        st = store.shift_status().get(p.day, {}).get("status", "-")
        lines.append(M.STATUS_SHIFT.format(day=p.start_latest.strftime("%m/%d"), status=st,
                                           start_target=fmt(p.start_target), start_latest=fmt(p.start_latest),
                                           depart_target=fmt(p.depart_target), depart_latest=fmt(p.depart_latest)))
        lines.append(M.STATUS_TIMES.format(soft=fmt_d(p.soft_close), target_h=cfg["sleep_target_hours"],
                                           hard=fmt_d(p.hard_close), min_h=cfg["sleep_min_hours"], lastcall=fmt(p.lastcall)))
    else:
        lines.append(M.STATUS_NO_SHIFT)
    lines.append(M.STATUS_YASUMI_COUNT.format(n=month_yasumi_count(store, now)))
    ab = absence_stats(cfg, store, now)
    if ab["limit"] > 0:
        lines.append(M.STATUS_ABSENCE.format(shifts=ab["shifts"], n=ab["absent"], paid=ab["paid"], limit=ab["limit"],
                                             over=M.STATUS_ABSENCE_OVER if ab["over"] else ""))
    calst = _read(store.p("calendar.json"), {})
    for s in calendar_sources(cfg.get("calendar", {})):
        cs = (calst.get("sources", {}).get(s["name"]) if "sources" in calst else calst) or {}
        lines.append(M.STATUS_CAL.format(name=s["name"], fetched=(cs.get("fetched") or M.STATUS_CAL_NEVER)[:16].replace("T", " "),
                                         error=M.STATUS_CAL_ERROR.format(error=cs["error"]) if cs.get("error") else ""))
    unc = unconfirmed_shifts(cfg, store, now, include_asked=True)
    if unc:
        lines.append(M.STATUS_UNCONFIRMED.format(days=M.STATUS_UNCONFIRMED_SEP.join(f"{Date.fromisoformat(x.day):%m/%d}" for x in unc)))
    recent = [(d, e) for d, e in _declared(store, include_released=True)
              if Date.fromisoformat(d) >= now.date() - timedelta(days=1)]
    if stop_active(store) or recent:
        parts = []
        for d, e in recent:
            state = M.STATUS_ENFORCE_RELEASED if e.get("released_at") else M.STATUS_ENFORCE_RUNS.format(n=len(e.get("runs", [])))
            parts.append(M.STATUS_ENFORCE_ITEM.format(day=f"{Date.fromisoformat(d):%m/%d}",
                                                      label=M.ENFORCE_LABEL.get(e.get("action"), e.get("action")),
                                                      hours=f"{float(e.get('duration_hours', 2)):g}", state=state))
        lines.append(M.STATUS_ENFORCE.format(stop=M.STATUS_ENFORCE_STOP if stop_active(store) else "", items=M.STATUS_ENFORCE_SEP.join(parts)))
    lines.append(M.STATUS_SCHEDULE_HEAD)
    lines.append(schedule_text(cfg, now.date()))
    return "\n".join(lines)
