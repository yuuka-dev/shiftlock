"""時刻の整形と日付の解釈、区間の引き算。cfg や Store に依存しない純粋な関数だけ。
日付の表記（%m/%d や WD_JA）はここで決める（i18n には置かない）。「2時間30分」のような時間の言い方だけ i18n。"""
from __future__ import annotations

from datetime import datetime, timedelta, date as Date

from . import i18n as M


def at(d: Date, hm: str) -> datetime:
    h, m = map(int, hm.split(":"))
    return datetime(d.year, d.month, d.day, h, m)


def fmt(dt: datetime) -> str:
    return dt.strftime("%H:%M")


def fmt_d(dt: datetime) -> str:
    return dt.strftime("%m/%d %H:%M")


def fmt_dur(td: timedelta) -> str:
    m = int(td.total_seconds() // 60)
    return M.TIME_DUR_HM.format(h=m // 60, m=m % 60) if m >= 60 else M.TIME_DUR_M.format(m=m)


WD_JA = "月火水木金土日"


def _subtract(iv: tuple[datetime, datetime], cuts: list[tuple[datetime, datetime]]):
    pieces = [iv]
    for cs, ce in cuts:
        nxt = []
        for s, e in pieces:
            if ce <= s or cs >= e:
                nxt.append((s, e))
                continue
            if s < cs:
                nxt.append((s, cs))
            if ce < e:
                nxt.append((ce, e))
        pieces = nxt
    return pieces


def _clamp_day(y: int, m: int, d: int) -> Date:
    import calendar
    return Date(y, m, min(d, calendar.monthrange(y, m)[1]))


def parse_day(text: str, today: Date, past: bool = False) -> Date:
    """'11/5' '2026-11-05' '明日' '今日' '昨日' を日付に。月日だけなら、7日以上前になる場合は来年扱い。
    past=True（事後報告）のときは逆に、未来になる場合は去年扱い。"""
    t = text.strip().replace("／", "/")
    if t in ("今日", "きょう"):
        return today
    if t in ("明日", "あした"):
        return today + timedelta(days=1)
    if t in ("昨日", "きのう"):
        return today - timedelta(days=1)
    if t in ("一昨日", "おととい"):
        return today - timedelta(days=2)
    if "-" in t:
        return Date.fromisoformat(t)
    m, d = map(int, t.split("/"))
    cand = Date(today.year, m, d)
    if past:
        return Date(today.year - 1, m, d) if (cand > today and today.month <= 2 and m >= 11) else cand
    if cand < today - timedelta(days=7):
        cand = Date(today.year + 1, m, d)
    return cand
