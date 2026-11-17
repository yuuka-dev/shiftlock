"""終わった乗務の『出た？休んだ？』（未確認の乗務の検出と、1 日 1 件の聞き方）。"""
from __future__ import annotations

from datetime import datetime, timedelta, date as Date

from . import i18n as M
from .plan import Plan, plans
from .store import first_log_date, log_read, _read, Store, TICK_MIN, _write
from .timeutil import at, WD_JA


def last_asked_unconfirmed(store: Store, now: datetime, lookback_days: int) -> str | None:
    """直近 36 時間に『出た？休んだ？』と聞いて、まだ返事の無い乗務日（lookback 内）。"""
    best = None
    for d, v in store.shift_status().items():
        ca = v.get("confirm_asked")
        if not ca or v.get("status"):
            continue
        try:
            asked = datetime.fromisoformat(ca)
        except ValueError:
            continue
        if now - asked > timedelta(hours=36) or Date.fromisoformat(d) < now.date() - timedelta(days=int(lookback_days)):
            continue
        if best is None or ca > best[1]:
            best = (d, ca)
    return best[0] if best else None


def unconfirmed_shifts(cfg: dict, store: Store, now: datetime, include_asked: bool = False) -> list[Plan]:
    """終わった乗務で『出た／休んだ』が無いもの（記録開始日以降、直近 lookback_days）。古い順。"""
    cf = cfg["confirm"]
    since = first_log_date(store)
    if since is None:
        return []
    since = max(since, confirm_since(store, now))
    status = store.shift_status()
    out = []
    for p in plans(cfg, now.date(), back=int(cf["lookback_days"]), fwd=0):
        d = Date.fromisoformat(p.day)
        if d < since or at(d + timedelta(days=1), cfg.get("shift_end_earliest", "07:00")) > now:
            continue
        me = status.get(p.day, {})
        st_ = me.get("status")
        if st_ in ("yasumi", "worked") or (st_ == "okita" and cf["okita_means_worked"]):
            continue
        if me.get("confirm_asked") and not include_asked:
            continue
        out.append(p)
    return out


def mark_confirm_asked(store: Store, day: str, now: datetime) -> None:
    st = _read(store.p("state.json"), {})
    st.setdefault("shifts", {}).setdefault(day, {})["confirm_asked"] = now.isoformat(timespec="minutes")
    st["confirm_last_ask"] = now.date().isoformat()   # 聞くのは 1 日 1 件
    _write(store.p("state.json"), st)


def can_ask_confirm(store: Store, now: datetime) -> bool:
    return _read(store.p("state.json"), {}).get("confirm_last_ask") != now.date().isoformat()


def confirm_since(store: Store, now: datetime) -> Date:
    """未確認の扱いを始めた日（v11 の導入日の前日）。それより前の乗務は従来どおり「出た」扱い（導入前の日が全部「出?」にならんように）。"""
    st = _read(store.p("state.json"), {})
    if not st.get("confirm_since"):
        st["confirm_since"] = (now.date() - timedelta(days=1)).isoformat()
        _write(store.p("state.json"), st)
    return Date.fromisoformat(st["confirm_since"])


def pick_unconfirmed(cfg: dict, store: Store, now: datetime) -> tuple[Plan | None, int]:
    """今日まだ聞いてなければ、いちばん新しい未確認の乗務と残りの件数。"""
    if cfg["confirm"]["mode"] != "ask" or not can_ask_confirm(store, now):
        return None, 0
    unc = unconfirmed_shifts(cfg, store, now)
    return (unc[-1], len(unc) - 1) if unc else (None, 0)


def home_activity_minutes(cfg: dict, store: Store, p: Plan) -> int:
    """乗務の時間帯（出発目安〜翌朝 shift_end_earliest）に PC を触ってた分数。Parsec 経由も混ざるので目安だけ。"""
    end = at(Date.fromisoformat(p.day) + timedelta(days=1), cfg.get("shift_end_earliest", "07:00"))
    return sum(TICK_MIN for r in log_read(store, "pc", p.depart_target, end)
               if r.get("game") or int(r.get("idle", 0)) < TICK_MIN * 60)


def confirm_text(cfg: dict, store: Store, p: Plan, now: datetime, others: int = 0) -> tuple[str, str]:
    """(ユーザー向けの通知文, Claude 向けの文脈)"""
    d = Date.fromisoformat(p.day)
    label = f"{d:%m/%d}({WD_JA[d.weekday()]})"
    hint = ""
    if home_activity_minutes(cfg, store, p) >= int(cfg["confirm"]["home_activity_minutes"]):
        hint = M.CONFIRM_HOME_HINT
    more = M.CONFIRM_MORE.format(n=others) if others > 0 else ""
    return (M.CONFIRM_USER.format(label=label, hint=hint, more=more),
            M.CONFIRM_CTX.format(label=label, hint=hint, more=more))


def kakunin_text(cfg: dict, store: Store, now: datetime) -> str:
    unc = unconfirmed_shifts(cfg, store, now, include_asked=True)
    if not unc:
        return M.CONFIRM_KAKUNIN_NONE
    return (M.CONFIRM_KAKUNIN_HEAD + "\n"
            + "\n".join(f"  {Date.fromisoformat(p.day):%m/%d}({WD_JA[Date.fromisoformat(p.day).weekday()]})" for p in unc))
