"""自己申告の記録（帰宅・就寝・起床・出発・事後報告）と『今のなし』の取り消し。"""
from __future__ import annotations

import json
import re
from datetime import datetime, timedelta, date as Date

from . import i18n as M
from .confirm import last_asked_unconfirmed
from .days import days_set
from .enforce import stop_active
from .intents import AFFIRM, clear_pending, DENY, detect_intents, learn, phrase_core, take_pending
from .attendance import absence_note
from .keywords import PAID_OFF, PAID_ON, PAST_YASUMI
from .plan import is_workday, last_past_plan, make_plan, Plan, target_plan
from .store import _read, set_mark, Store, _write
from .timeutil import fmt, fmt_dur, parse_day, WD_JA
from .usage import recent_long_gap


def answer_pending(prompt: str, cfg: dict, store: Store, now: datetime) -> str | None:
    """聞いてた質問への返事なら処理して表示文を返す。返事やなかったら None（質問は取り下げ）。"""
    pd = take_pending(store, now)
    if not pd:
        return None
    a = phrase_core(prompt)
    when = datetime.fromisoformat(pd["at"])
    if re.match(AFFIRM, a):
        core = learn(store, pd["phrase"], pd["kind"])
        clear_pending(store)
        msg = apply_intents([pd["kind"]], cfg, store, now, when)
        return M.RECORDS_LEARNED_YES.format(core=core, label=M.INTENT_LABEL[pd["kind"]], msg=msg)
    m = re.match(DENY, a)
    if m:
        rest = a[m.end():]
        other = [k for k in detect_intents(rest) if k != pd["kind"]] if rest else []
        clear_pending(store)
        if other:
            core = learn(store, pd["phrase"], other[-1])
            msg = apply_intents([other[-1]], cfg, store, now, when)
            return M.RECORDS_LEARNED_OTHER.format(core=core, label=M.INTENT_LABEL[other[-1]], msg=msg)
        core = learn(store, pd["phrase"], "none")
        return M.RECORDS_LEARNED_NONE.format(core=core)
    clear_pending(store)
    return None


def apply_intents(intents: list[str], cfg: dict, store: Store, now: datetime, when: datetime | None = None) -> str:
    """拾った意図を記録。取り消し用に直前の状態を残す。when = 発言の時刻（聞き返した場合は元の発言の時刻）。"""
    when = when or now
    p = target_plan(cfg, when)
    st = _read(store.p("state.json"), {})
    day = p.day if p else "-"
    st["undo"] = {"day": day, "prev": json.loads(json.dumps(st.get("shifts", {}).get(day, {}))),
                  "at": now.isoformat(timespec="minutes"), "what": intents}
    _write(store.p("state.json"), st)
    msgs = []
    for k in intents:
        if k == "kitaku":
            msgs.append(do_kitaku(cfg, store, now, p, when))
        elif k == "neru":
            msgs.append(do_neru(cfg, store, now, p, when))
        elif k == "okita":
            msgs.append(do_okita(cfg, store, now, p, when))
    return " ".join(m.replace(M.PREFIX, "") for m in msgs if m)


def undo_fuzzy(store: Store, now: datetime) -> str:
    st = _read(store.p("state.json"), {})
    u = st.get("undo")
    if not u or now - datetime.fromisoformat(u["at"]) > timedelta(minutes=60):
        return M.RECORDS_UNDO_NOTHING
    if u["day"] != "-":
        prev = dict(u["prev"])
        prev.pop("enforce", None)   # 強制モードの宣言・実行・解除は『今のなし』で戻さない（hook から強制を操作しない）
        cur_enf = st.get("shifts", {}).get(u["day"], {}).get("enforce")
        if cur_enf is not None:
            prev["enforce"] = cur_enf
        if prev:
            st.setdefault("shifts", {})[u["day"]] = prev
        else:
            st.get("shifts", {}).pop(u["day"], None)
    st.pop("undo", None)
    _write(store.p("state.json"), st)
    if u.get("days_work_added"):
        days_set(store, Date.fromisoformat(u["day"]), None)   # 『11/1 出た』で足した出勤の例外も戻す
    with open(store.p("events.log"), "a", encoding="utf-8", newline="\n") as f:
        f.write(f"{now.isoformat(timespec='minutes')}\t{u['day']}\tundo\t{','.join(u['what'])}\n")
    return M.RECORDS_UNDO_DONE.format(what=M.INTENT_LABEL_SEP.join(M.INTENT_LABEL[w] for w in u["what"]))


def _log_free(store: Store, when: datetime, kind: str) -> None:
    with open(store.p("events.log"), "a", encoding="utf-8", newline="\n") as f:
        f.write(f"{when.isoformat(timespec='minutes')}\t-\t{kind}\tfree-day\n")


def do_kitaku(cfg: dict, store: Store, now: datetime, p: Plan | None, when: datetime | None = None) -> str:
    when = when or now
    if p is None:
        _log_free(store, when, "kitaku")
        return M.RECORDS_KITAKU_FREE
    set_mark(store, p.day, "kitaku", when, now, "claude")
    return M.RECORDS_KITAKU.format(when=fmt(when))


def do_neru(cfg: dict, store: Store, now: datetime, p: Plan | None, when: datetime | None = None) -> str:
    when = when or now
    if p is None:
        _log_free(store, when, "neru")
        return M.RECORDS_NERU_FREE
    set_mark(store, p.day, "sleep_at", when, now, "claude")
    aw = cfg.get("auto_wake_hours") or cfg["sleep_min_hours"]
    return M.RECORDS_NERU.format(when=fmt(when), hours=aw, depart=fmt(p.depart_target), latest=fmt(p.depart_latest))


def do_okita(cfg: dict, store: Store, now: datetime, p: Plan | None, when: datetime | None = None) -> str:
    when = when or now
    if p is None:
        _log_free(store, when, "okita")
        return M.RECORDS_OKITA_FREE
    me = store.shift_status().get(p.day, {})
    e = me.get("enforce") or {}
    if e.get("consented_at") and not e.get("released_at") and not stop_active(store) and now >= p.hard_close \
            and not me.get("sleep_at") and recent_long_gap(store, now) is not True:
        # 強制中は『おきた』一言では解除せえへん（寝た記録が要る）。本当の緊急は kaijo.cmd / STOP.txt
        return M.RECORDS_OKITA_ENFORCE_GATE
    if me.get("status") in ("yasumi", "worked"):
        # 休み／出発済みの乗務は status を触らへん（okita にするとレポートで出勤扱いになる。issue #29）。
        # 睡眠の記録（events.log の okita 行）だけ残してレポートの寝た／起きたに出す
        day, what = f"{p.start_latest:%m/%d}", M.INTENT_LABEL[me["status"]]
        if me.get("sleep_at"):
            sa = datetime.fromisoformat(me["sleep_at"])
            store.log_event(p.day, "okita", now, f"neru {sa:%H:%M}-{when:%H:%M}")
            return M.RECORDS_OKITA_KEEP_SLEPT.format(sleep_at=fmt(sa), when=fmt(when), dur=fmt_dur(when - sa), day=day, what=what)
        store.log_event(p.day, "okita", now)
        return M.RECORDS_OKITA_KEEP.format(day=day, what=what)
    if me.get("sleep_at"):
        sa = datetime.fromisoformat(me["sleep_at"])
        store.set_shift_status(p.day, "okita", now, f"neru {sa:%H:%M}-{when:%H:%M}")
        msg = M.RECORDS_OKITA_SLEPT.format(sleep_at=fmt(sa), when=fmt(when), dur=fmt_dur(when - sa),
                                           depart=fmt(p.depart_target), latest=fmt(p.depart_latest))
        if when - sa < timedelta(hours=cfg["sleep_min_hours"]):
            msg += M.RECORDS_OKITA_SHORT.format(min_h=cfg["sleep_min_hours"])
        return msg
    slept = recent_long_gap(store, now)
    store.set_shift_status(p.day, "okita", now, "" if slept is not False else "no-gap")
    msg = M.RECORDS_OKITA.format(depart=fmt(p.depart_target), latest=fmt(p.depart_latest))
    if slept is False:
        msg += M.RECORDS_OKITA_NO_GAP
    return msg


def do_shuppatsu(cfg: dict, store: Store, now: datetime, p: Plan | None) -> str:
    """『行ってきます』: 今日の乗務を出発済み（worked）に。以後その乗務の通知・ロック・拒否は止まる。"""
    if p is None:
        return M.RECORDS_SHUPPATSU_NO_PLAN
    if now < p.depart_target - timedelta(hours=2):
        # 昼間の「行ってきます」（買い物など）で夜の乗務の保護が外れんように、出発の 2 時間前から
        return M.RECORDS_SHUPPATSU_EARLY.format(day=f"{p.start_latest:%m/%d}", depart=fmt(p.depart_target))
    st = _read(store.p("state.json"), {})
    st["undo"] = {"day": p.day, "prev": json.loads(json.dumps(st.get("shifts", {}).get(p.day, {}))),
                  "at": now.isoformat(timespec="minutes"), "what": ["worked"]}
    _write(store.p("state.json"), st)
    store.set_shift_status(p.day, "worked", now, "shuppatsu")
    return M.RECORDS_SHUPPATSU.format(day=f"{p.start_latest:%m/%d}")


def resolve_past_day(cfg: dict, store: Store, when: datetime, day_text: str | None):
    """事後報告の対象日。日付あり → その日。無し → 直近に聞いた未確認の乗務 → 直近の終わった乗務。
    見つからなければ None。日付が読めなければ ValueError。"""
    if day_text:
        return parse_day(day_text, when.date(), past=True)
    asked = last_asked_unconfirmed(store, when, cfg["confirm"]["lookback_days"])
    if asked:
        return Date.fromisoformat(asked)
    p = last_past_plan(cfg, when, cfg["confirm"]["lookback_days"])
    return Date.fromisoformat(p.day) if p else None


def paid_flag(text: str | None):
    """メモ・引数の『有給』『有休』→ True、『有給なし』→ False。どちらでもなければ None。"""
    t = (text or "").replace(" ", "").replace("　", "").strip()
    return True if t in PAID_ON else False if t in PAID_OFF else None


def _late_report(cfg: dict, store: Store, now: datetime, kind: str, day_text: str | None = None,
                 when: datetime | None = None, via: str = "claude", paid: bool | None = None) -> tuple[bool, str]:
    """終わった乗務の事後報告。kind: 'yasumi'（休んだ）/ 'worked'（出た）。(記録したか, 表示文)。取り消し用に直前の状態を残す。
    paid: 休んだに添えた『有給』（True）/『有給なし』（False）。既に休みになっている日なら印の付け外しだけする。"""
    when = when or now
    lb = int(cfg["confirm"]["lookback_days"])
    try:
        d = resolve_past_day(cfg, store, when, day_text)
    except ValueError as e:
        return False, M.RECORDS_LATE_BAD_DATE.format(err=e)
    if d is None:
        return False, M.RECORDS_LATE_NONE.format(days=lb)
    label = f"{d:%m/%d}({WD_JA[d.weekday()]})"
    if d > when.date():
        return False, M.RECORDS_LATE_FUTURE.format(label=label)
    if d < when.date() - timedelta(days=int(cfg["retention"]["days_keep"])):
        return False, M.RECORDS_LATE_TOO_OLD.format(label=label, days=cfg["retention"]["days_keep"])
    ds = d.isoformat()
    if store.shift_status().get(ds, {}).get("status") == kind:
        if kind == "yasumi" and paid is not None:
            store.set_paid(ds, paid, now, via)
            msg = M.RECORDS_PAID_SET.format(label=label) if paid else M.RECORDS_PAID_UNSET.format(label=label)
            return True, msg + absence_note(cfg, store, now)
        return False, M.RECORDS_LATE_ALREADY.format(label=label, what=M.RECORDS_LATE_KIND_YASUMI if kind == "yasumi" else M.RECORDS_LATE_KIND_WORKED)
    work_added = False
    if is_workday(cfg, d):
        if make_plan(cfg, d).start_latest > when:
            return False, M.RECORDS_LATE_NOT_YET.format(label=label)
    elif kind == "yasumi":
        return False, M.RECORDS_LATE_OFF_DAY.format(label=label)
    else:
        days_set(store, d, "work", "事後報告")   # 休みの日に出た → 出勤の例外日にもする（undo で戻す）
        work_added = True
    st = _read(store.p("state.json"), {})
    st["undo"] = {"day": ds, "prev": json.loads(json.dumps(st.get("shifts", {}).get(ds, {}))),
                  "at": now.isoformat(timespec="minutes"), "what": [kind], "days_work_added": work_added}
    _write(store.p("state.json"), st)
    store.set_shift_status(ds, kind, now, f"late {via}")
    if kind == "yasumi":
        if paid:
            store.set_paid(ds, True, now, via)
        return True, (M.RECORDS_LATE_YASUMI.format(label=label, n=month_yasumi_count(store, now))
                      + (M.RECORDS_YASUMI_PAID if paid else "") + absence_note(cfg, store, now))
    return True, M.RECORDS_LATE_WORKED.format(label=label)


def late_report(cfg: dict, store: Store, now: datetime, kind: str, day_text: str | None = None,
                when: datetime | None = None, via: str = "claude", paid: bool | None = None) -> str:
    return _late_report(cfg, store, now, kind, day_text, when, via, paid)[1]


def cmd_past(m, cfg: dict, store: Store, now: datetime) -> str:
    kind = "yasumi" if m.group(2) in PAST_YASUMI else "worked"
    return late_report(cfg, store, now, kind, m.group(1), paid=paid_flag(m.group(3)) if kind == "yasumi" else None)


def cmd_paid(m, cfg: dict, store: Store, now: datetime, via: str = "claude") -> str:
    """『11/3 有給』『11/3 有給なし』（RE_PAID）: 休みの記録に有給の印を付ける／外す。休みの記録が無い日には付けへん。"""
    try:
        d = resolve_past_day(cfg, store, now, m.group(1))
    except ValueError as e:
        return M.RECORDS_PAID_BAD_DATE.format(err=e)
    label = f"{d:%m/%d}({WD_JA[d.weekday()]})"
    if store.shift_status().get(d.isoformat(), {}).get("status") != "yasumi":
        return M.RECORDS_PAID_NO_YASUMI.format(label=label)
    on = not m.group(3)
    store.set_paid(d.isoformat(), on, now, via)
    msg = M.RECORDS_PAID_SET.format(label=label) if on else M.RECORDS_PAID_UNSET.format(label=label)
    return msg + absence_note(cfg, store, now)


def month_yasumi_count(store: Store, now: datetime) -> int:
    ym = now.strftime("%Y-%m")
    return sum(1 for d, v in store.shift_status().items()
               if v.get("status") == "yasumi" and d.startswith(ym))
