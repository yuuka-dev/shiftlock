"""Google カレンダー（iCal）の解析と同期（複数ソース、失敗時は前回分で続行）。"""
from __future__ import annotations

from datetime import datetime, timedelta, date as Date

from . import i18n as M
from .config import calendar_sources
from .store import _read, Store, _write


def _ics_unfold(text: str) -> list[str]:
    out: list[str] = []
    for line in text.replace("\r\n", "\n").split("\n"):
        if line[:1] in (" ", "\t") and out:
            out[-1] += line[1:]
        else:
            out.append(line)
    return out


def _ics_dt(value: str, params: str) -> tuple[Date, bool]:
    """returns (ローカル日付, 終日かどうか)"""
    v = value.strip()
    if "VALUE=DATE" in params or (len(v) == 8 and v.isdigit()):
        return datetime.strptime(v[:8], "%Y%m%d").date(), True
    if v.endswith("Z"):
        from datetime import timezone
        dt = datetime.strptime(v, "%Y%m%dT%H%M%SZ").replace(tzinfo=timezone.utc).astimezone()
        return dt.date(), False
    return datetime.strptime(v[:15], "%Y%m%dT%H%M%S").date(), False


def parse_ics(text: str, off_kw: list[str], work_kw: list[str]) -> dict:
    """SUMMARY にキーワードを含む予定の日付を拾う。繰り返し予定（RRULE）は初回だけ。"""
    res: dict = {"off": {}, "work": {}, "recurring_skipped": 0}
    ev: dict | None = None
    for line in _ics_unfold(text):
        if line == "BEGIN:VEVENT":
            ev = {}
            continue
        if line == "END:VEVENT" and ev is not None:
            summ = ev.get("SUMMARY", "").replace("\\,", ",").replace("\\;", ";").replace("\\n", " ")
            kind = "off" if any(k in summ for k in off_kw) else "work" if any(k in summ for k in work_kw) else None
            if kind and "DTSTART" in ev and ev.get("STATUS") != "CANCELLED":
                s, allday = _ics_dt(*ev["DTSTART"])
                e = s
                if "DTEND" in ev:
                    e2, allday_e = _ics_dt(*ev["DTEND"])
                    e = e2 - timedelta(days=1) if allday_e else e2   # 終日の DTEND は翌日（排他的）
                e = max(s, e)
                if "RRULE" in ev:
                    res["recurring_skipped"] += 1
                d = s
                while d <= e and (d - s).days < 62:
                    res[kind][d.isoformat()] = summ
                    d += timedelta(days=1)
            ev = None
            continue
        if ev is not None and ":" in line:
            head, val = line.split(":", 1)
            name, _, params = head.partition(";")
            if name in ("DTSTART", "DTEND"):
                ev[name] = (val, params)
            elif name in ("SUMMARY", "STATUS", "RRULE"):
                ev[name] = val
    return res


def _http_get(url: str) -> str:
    import urllib.request
    with urllib.request.urlopen(url, timeout=10) as r:
        return r.read().decode("utf-8", "replace")


def sync_calendar(cfg: dict, store: Store, now: datetime, force: bool = False, fetch=None) -> str:
    """ソースごとに同期して calendar.json（新形式）に書く。1 本失敗しても他は更新し、失敗した分は前回の内容で続ける。
    戻り値はソースごとに 1 行。"""
    cal = cfg.get("calendar", {})
    sources = calendar_sources(cal)
    if not sources:
        return M.CAL_UNSET
    path = store.p("calendar.json")
    cur = _read(path, {})
    prev = cur.get("sources", {})
    if "sources" not in cur and cur.get("fetched"):   # 旧形式 → 先頭ソースの前回分として引き継ぐ
        prev = {sources[0]["name"]: {k: cur[k] for k in ("fetched", "error", "off", "work", "recurring_skipped") if k in cur}}
    keep_from = (now.date() - timedelta(days=cfg["retention"]["days_keep"])).isoformat()
    keep_to = (now.date() + timedelta(days=180)).isoformat()
    every = timedelta(minutes=cal.get("sync_minutes", 60))
    out: dict = {}
    lines = []
    for s in sources:
        name = s["name"]
        old = dict(prev.get(name, {}))
        if not force and old.get("fetched") and now - datetime.fromisoformat(old["fetched"]) < every:
            out[name] = old
            lines.append(M.CAL_SKIP.format(name=name))
            continue
        try:
            text = fetch(s["ics_url"]) if fetch is not None else _http_get(s["ics_url"])
            res = parse_ics(text, s["off_keywords"], s["work_keywords"])
        except Exception as e:  # ネットが落ちてても前回分で動く
            old["error"] = f"{now:%m/%d %H:%M} {type(e).__name__}: {e}"[:300]   # calendar.json に保存する値（i18n の対象外）
            old.setdefault("fetched", None)
            out[name] = old
            lines.append(M.CAL_FAIL.format(name=name, err=e))
            continue
        out[name] = {"fetched": now.isoformat(timespec="seconds"), "error": None,
                     "off": {k: v for k, v in res["off"].items() if keep_from <= k <= keep_to},
                     "work": {k: v for k, v in res["work"].items() if keep_from <= k <= keep_to},
                     "recurring_skipped": res["recurring_skipped"]}
        lines.append(M.CAL_OK.format(name=name, off=len(out[name]["off"]), work=len(out[name]["work"])))
    if out != prev or "sources" not in cur:   # 全部スキップなら書き直さへん
        _write(path, {"fetched": now.isoformat(timespec="seconds"), "sources": out})
    return "\n".join(lines)
