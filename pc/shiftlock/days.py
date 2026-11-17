"""手動の例外日（days.json）と 2 週間の出／休の表。"""
from __future__ import annotations

from datetime import timedelta, date as Date

from . import i18n as M
from .plan import is_workday
from .store import _read, Store, _write
from .timeutil import WD_JA


def days_set(store: Store, d: Date, kind: str | None, note: str = "") -> None:
    """kind: 'off' | 'work' | None(手動の例外を消す)"""
    path = store.p("days.json")
    data = _read(path, {})
    off, work = data.setdefault("off", {}), data.setdefault("work", {})
    ds = d.isoformat()
    off.pop(ds, None)
    work.pop(ds, None)
    if kind == "off":
        off[ds] = note
    elif kind == "work":
        work[ds] = note
    _write(path, data)


def schedule_text(cfg: dict, today: Date, n: int = 14) -> str:
    dd = cfg.get("_days", {})
    cells = []
    for i in range(n):
        d = today + timedelta(days=i)
        ds = d.isoformat()
        work = is_workday(cfg, d)
        mark = M.DAYS_WORK if work else M.DAYS_OFF
        why = dd.get("work", {}).get(ds) if work else dd.get("off", {}).get(ds)
        extra = M.DAYS_NOTE.format(why=why) if why else (M.DAYS_EXCEPTION_MARK if ds in dd.get("off", {}) or ds in dd.get("work", {}) else "")
        cells.append(M.DAYS_CELL.format(md=f"{d:%m/%d}", wd=WD_JA[d.weekday()], mark=mark, extra=extra))
    lines = []
    for i in range(0, len(cells), 7):
        lines.append("  " + "  ".join(cells[i:i + 7]))
    return "\n".join(lines)
