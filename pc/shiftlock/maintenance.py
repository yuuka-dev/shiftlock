"""日次の整理（古い記録の削除）とカレンダー同期の呼び出し。"""
from __future__ import annotations

from datetime import datetime, timedelta, date as Date

from .calendar_sync import sync_calendar
from .report import cleanup_reports
from .store import _read, Store, _write


def cleanup(cfg: dict, store: Store, now: datetime) -> dict:
    ret = cfg["retention"]
    today = now.date()
    cut_days = (today - timedelta(days=ret["days_keep"])).isoformat()
    removed = {"days": 0, "state": 0, "logs": 0, "events": 0}

    path = store.p("days.json")
    data = _read(path, {})
    for k in ("off", "work", "times"):
        before = len(data.get(k, {}))
        data[k] = {d: v for d, v in data.get(k, {}).items() if d >= cut_days}
        removed["days"] += before - len(data[k])
    if data:
        _write(path, data)

    st = _read(store.p("state.json"), {})
    if "shifts" in st:
        before = len(st["shifts"])
        st["shifts"] = {d: v for d, v in st["shifts"].items() if d >= cut_days}
        removed["state"] = before - len(st["shifts"])
        _write(store.p("state.json"), st)

    logdir = store.p("log")
    if logdir.is_dir():
        cut_log = today - timedelta(days=ret["log_keep_days"])
        for f in logdir.glob("*-????-??.jsonl"):
            ym = f.stem[-7:]
            try:
                y, m = map(int, ym.split("-"))
            except ValueError:
                continue
            month_end = Date(y + (m == 12), m % 12 + 1, 1) - timedelta(days=1)
            if month_end < cut_log:
                f.unlink()
                removed["logs"] += 1

    removed["reports"] = cleanup_reports(cfg, store, now)

    ev = store.p("events.log")
    if ev.exists():
        cut_ev = (today - timedelta(days=ret["events_keep_days"])).isoformat()
        lines = ev.read_text(encoding="utf-8").splitlines()
        keep = [ln for ln in lines if ln[:10] >= cut_ev]
        if len(keep) != len(lines):
            ev.write_text("\n".join(keep) + ("\n" if keep else ""), encoding="utf-8")
            removed["events"] = len(lines) - len(keep)
    return removed


def daily_maintenance(cfg: dict, store: Store, now: datetime) -> None:
    """tick から呼ぶ。カレンダー同期（間隔ごと）と整理（1日1回）。失敗しても tick は止めない。"""
    try:
        sync_calendar(cfg, store, now)
    except Exception:
        pass
    mpath = store.p("maintenance.json")
    m = _read(mpath, {})
    if m.get("last_cleanup") != now.date().isoformat():
        try:
            m["result"] = cleanup(cfg, store, now)
        except Exception as e:
            m["result"] = {"error": str(e)}
        m["last_cleanup"] = now.date().isoformat()
        _write(mpath, m)
