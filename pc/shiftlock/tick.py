"""Windows の guard.ps1 から 5 分おきに呼ばれる見張り。稼働記録・通知・自動『起きた』・強制モードの判断。"""
from __future__ import annotations

from datetime import datetime, timedelta
from pathlib import Path

from . import i18n as M
from .autowake import maybe_auto_okita
from .config import load_config
from .confirm import confirm_text, mark_confirm_asked, pick_unconfirmed
from .enforce import enforce_due, enforce_suffix
from .maintenance import daily_maintenance
from .notify import attach_sounds, NOTIFY_EVERY, _once
from .phone import import_phone_marks
from .plan import current, phase_messages
from .store import log_append, _read, Store, _write
from .timeutil import fmt_dur
from .usage import due_nag, game_idle_limit, pc_active, shift_away


def run_tick(cfg_path: Path, idle_seconds: float, game: bool, now: datetime,
             uptime_seconds: float | None = None, remote: bool | None = None, dry_run: bool = False) -> dict:
    """Windows guard から5分おきに呼ばれる。通知アクション（と強制モードの実行）を返す。
    uptime_seconds: 起動からの秒数（無ければ強制は発動しない）。remote: リモート操作中（検出できる場合）。"""
    cfg = load_config(cfg_path)
    store = Store(cfg_path)
    us = cfg.get("usage", {})
    reset = timedelta(minutes=us.get("idle_reset_minutes", 20))
    notify = {"phase": "toast", "hard": "popup", "nag": "toast", "speak": False, **cfg.get("notify", {})}
    idle = timedelta(seconds=max(0.0, idle_seconds))
    # 乗務で外出中（『行ってきます』〜『ただいま』）は起動したままのゲームを見ない。ゲーム中でも入力が game_idle_minutes 無ければ離席（issue #24）
    game_on = bool(game) and not shift_away(store, now)
    active = pc_active(idle, game_on, reset, game_idle_limit(cfg))
    log_append(store, "pc", now, {"idle": int(idle.total_seconds()), "game": int(game)})
    daily_maintenance(cfg, store, now)
    enforce_error = ""

    u = _read(store.p("usage-pc.json"), {})
    gaps = u.setdefault("gaps", [])
    if active:
        if u.get("gap_start"):
            gs = datetime.fromisoformat(u["gap_start"])
            ge = now - idle if not game_on else now
            if ge - gs >= timedelta(minutes=30):
                gaps.append([gs.isoformat(timespec="minutes"), ge.isoformat(timespec="minutes")])
            u["gap_start"] = None
            u["start"] = (now - idle).isoformat(timespec="seconds") if not game_on else now.isoformat(timespec="seconds")
            u.pop("last_nag", None)
        elif not u.get("start") or (u.get("last") and
                                    now - datetime.fromisoformat(u["last"]) > reset + timedelta(minutes=10)):
            # 初回 or guard が止まってた（スリープ等）→ 新しいセッション扱い
            u["start"] = now.isoformat(timespec="seconds")
            u.pop("last_nag", None)
        u["last"] = now.isoformat(timespec="seconds")
    elif not u.get("gap_start"):
        u["gap_start"] = (now - idle).isoformat(timespec="seconds")
    u["gaps"] = gaps[-20:]
    _write(store.p("usage-pc.json"), u)   # 自動起床判定がこの直後に読む

    actions = []
    if active:
        try:
            phone_msg = import_phone_marks(cfg, store, now)
        except Exception:
            phone_msg = ""
        auto_msg = phone_msg or maybe_auto_okita(cfg, store, now)
        if auto_msg:
            actions.append({"method": notify["phase"], "kind": "auto_okita", "title": "shiftlock", "text": auto_msg,
                            "speak": bool(notify["speak"])})
        phase, p = current(cfg, store.shift_status(), now)
        if p and _once(store, f"{p.day}:{phase}", now, NOTIFY_EVERY.get(phase)):
            text = phase_messages(phase, p, now, cfg)[0] + enforce_suffix(store, p, phase)
            actions.append({"method": notify["hard"] if phase in ("hard", "go_last") else notify["phase"],
                            "kind": phase, "title": "shiftlock", "text": text, "speak": bool(notify["speak"])})
        pu, others = pick_unconfirmed(cfg, store, now)
        if pu:   # 終わった乗務の「出た？休んだ？」は 1 日 1 件、新しい乗務から
            actions.append({"method": notify["phase"], "kind": "confirm", "title": "shiftlock",
                            "text": confirm_text(cfg, store, pu, now, others)[0], "speak": bool(notify["speak"])})
            mark_confirm_asked(store, pu.day, now)
        cont = due_nag(u, now, cfg)
        if cont:
            what = M.TICK_NAG_GAME if game else M.TICK_NAG_PC
            actions.append({"method": notify["nag"], "kind": "nag", "title": "shiftlock",
                            "text": M.TICK_NAG.format(what=what, dur=fmt_dur(cont)),
                            "speak": bool(notify["speak"])})
        # 強制モード（宣言・同意済みの乗務だけ）。実行は最後に回す。失敗したら強制せず、通知だけ続ける
        try:
            actions += enforce_due(cfg, store, now, phase, p, True, uptime_seconds, bool(remote), notify,
                                   idle_seconds=idle_seconds, dry_run=dry_run)
        except Exception as ex:
            enforce_error = M.TICK_ENFORCE_ERROR.format(err=ex)
        else:
            enforce_error = ""
    _write(store.p("usage-pc.json"), u)
    try:
        warnings = attach_sounds(cfg, store, actions, now)
    except Exception as e:   # 通知音の設定ミスで本線（通知）を止めない
        warnings = [M.TICK_SOUND_CONFIG_ERROR.format(err=e)]
        for a in actions:
            a["sound"] = ""
    try:
        vol, mx = float(notify.get("sound_volume", 0.8)), int(notify.get("sound_max_seconds", 20))
    except (TypeError, ValueError):
        vol, mx = 0.8, 20
        warnings.append(M.TICK_SOUND_NUMBER_ERROR)
    if enforce_error:
        warnings.append(enforce_error)
    return {"actions": actions, "warnings": warnings, "sound": {"volume": vol, "max_seconds": mx}}
