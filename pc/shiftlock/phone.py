"""iPhone のショートカットが書く mood.csv（気分と ねる／おきた／ただいま／休んだ／出た）の読み取りと取り込み。"""
from __future__ import annotations

from datetime import datetime, timedelta, date as Date

from . import i18n as M
from .config import to_local_path
from .plan import last_past_plan, target_plan
from .records import _late_report
from .store import _read, set_mark, Store, _write
from .timeutil import fmt, fmt_dur


PHONE_MARKS = {"neru": "neru", "ねる": "neru", "寝る": "neru", "okita": "okita", "おきた": "okita", "起きた": "okita",
               "kitaku": "kitaku", "ただいま": "kitaku", "帰宅": "kitaku",
               "yasumi": "yasumi", "休んだ": "yasumi", "deta": "worked", "出た": "worked"}   # yasumi:2026-11-02 のように日付付きも可


def read_phone_marks(cfg: dict) -> list[tuple[datetime, str]]:
    """mood.csv に iPhone から書かれた 『日時,neru』『日時,okita』『日時,kitaku』 を拾う。"""
    path = cfg.get("mood_file")
    if not path:
        return []
    try:
        text = to_local_path(path).read_text(encoding="utf-8-sig")
    except OSError:
        return []
    out = []
    for line in text.splitlines():
        parts = [x.strip() for x in line.split(",")]
        if len(parts) < 2:
            continue
        key, _, extra = parts[1].partition(":")
        if key not in PHONE_MARKS:
            continue
        try:
            t = datetime.strptime(parts[0].replace("T", " ")[:16], "%Y-%m-%d %H:%M")
        except ValueError:
            continue
        out.append((t, PHONE_MARKS[key], extra.strip() or None))   # extra = 乗務日（yasumi / deta のとき）
    return sorted(set(out), key=lambda x: (x[0], x[1], x[2] or ""))


def _phone_okita_pending(me: dict) -> bool:
    """iPhone の『おきた』をまだ当ててへん状態か。
    status 無し → まだ。『起きた』済みでも、そのあとに寝た記録（二度寝）があれば再び当てる。
    at は iPhone の記録時刻（取り込み時刻やない）なので、まとめて取り込んでも二度寝の前後を取り違えへん。
    休み／出発済み（yasumi / worked）は触らへん。"""
    st = me.get("status")
    if not st:
        return True
    if st != "okita":
        return False
    return datetime.fromisoformat(me["at"]) < datetime.fromisoformat(me["sleep_at"])


def import_phone_marks(cfg: dict, store: Store, now: datetime) -> str:
    """iPhone の記録を state に取り込む（何回呼んでも同じ結果）。
    yasumi / deta（事後報告）は 1 件につき 1 回だけ適用し、適用済みを state.json の phone_applied に残す（訂正が巻き戻らへん）。
    neru / okita / kitaku は直近の乗務（target_plan）に対して従来どおり。"""
    all_marks = read_phone_marks(cfg)
    if not all_marks:
        return ""
    msg = ""
    lb = int(cfg["confirm"]["lookback_days"])
    applied = list(_read(store.p("state.json"), {}).get("phone_applied", []))
    added = []
    for t, k, extra in all_marks:
        if k not in ("yasumi", "worked") or t > now or t < now - timedelta(days=lb):
            continue
        key = f"{t.isoformat(timespec='minutes')}|{k}|{extra or ''}"
        if key in applied:
            continue
        added.append(key)
        if extra:
            day_text = extra
        else:
            p0 = last_past_plan(cfg, t, lb)       # 記録した時点で直近に終わった乗務
            if p0 is None:
                continue
            day_text = p0.day
        ok, r = _late_report(cfg, store, now, k, day_text, when=t, via="phone")
        if ok:
            msg = M.PHONE_LATE_PREFIX + r.replace(M.PREFIX, "")
    if added:
        st = _read(store.p("state.json"), {})
        st["phone_applied"] = (applied + added)[-300:]
        _write(store.p("state.json"), st)
    p = target_plan(cfg, now)
    if p is None:
        return msg
    lo = datetime.combine(Date.fromisoformat(p.day), datetime.min.time()) - timedelta(hours=12)
    marks = [m for m in all_marks if lo <= m[0] <= now and m[1] in ("kitaku", "neru", "okita")]
    me = store.shift_status().get(p.day, {})
    for t, k, extra in marks:
        if k == "kitaku" and me.get("kitaku") != t.isoformat(timespec="minutes"):
            set_mark(store, p.day, "kitaku", t, now, "phone")
        elif k == "neru" and (not me.get("sleep_at") or datetime.fromisoformat(me["sleep_at"]) < t):
            set_mark(store, p.day, "sleep_at", t, now, "phone")
            me = store.shift_status().get(p.day, {})
        elif k == "okita" and me.get("sleep_at") and t > datetime.fromisoformat(me["sleep_at"]) \
                and _phone_okita_pending(me):
            sa = datetime.fromisoformat(me["sleep_at"])
            # at と events.log の時刻は iPhone の記録時刻 t（取り込みが遅れても起床時刻がずれへん）
            store.set_shift_status(p.day, "okita", t, f"phone {sa:%H:%M}-{t:%H:%M}")
            msg = M.PHONE_OKITA.format(sleep_at=fmt(sa), when=fmt(t), dur=fmt_dur(t - sa), depart=fmt(p.depart_target))
        me = store.shift_status().get(p.day, {})
    return msg



def mood_str(v: int) -> str:
    return M.PHONE_MOOD_GLYPH[v] + (f"{v:+d}" if v else "0")


def read_mood(cfg: dict) -> list[dict]:
    """iPhone ショートカットが追記する CSV: 'YYYY-MM-DD HH:MM,<-2..2>,<メモ>'。壊れた行は無視。"""
    path = cfg.get("mood_file")
    if not path:
        return []
    f = to_local_path(path)
    try:
        text = f.read_text(encoding="utf-8-sig")
    except (FileNotFoundError, OSError):
        return []
    out, seen = [], set()
    for line in text.splitlines():
        line = line.strip()
        if not line or line in seen:      # iCloud の重複対策
            continue
        seen.add(line)
        parts = [x.strip() for x in line.split(",", 2)]
        if len(parts) < 2:
            continue
        try:
            t = datetime.strptime(parts[0].replace("T", " ")[:16], "%Y-%m-%d %H:%M")
            v = int(parts[1].replace("＋", "+").replace("−", "-").replace("ー", "-"))
        except ValueError:
            continue
        if -2 <= v <= 2:
            out.append({"t": t, "v": v, "memo": parts[2] if len(parts) > 2 else ""})
    return sorted(out, key=lambda r: r["t"])
