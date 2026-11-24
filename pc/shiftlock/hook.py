"""Claude Code の hook（UserPromptSubmit / Stop）。処理の順番は docs/pc-spec.md §5.3。失敗しても exit 2 にしない。"""
from __future__ import annotations

import json
from datetime import datetime, timedelta
from pathlib import Path

from . import i18n as M
from .autowake import maybe_auto_okita
from .commands import cmd_day, cmd_keyword
from .config import load_config
from .confirm import confirm_text, mark_confirm_asked, pick_unconfirmed
from .enforce import enforce_suffix
from .intents import (can_ask, clear_pending, detect_intents, load_learned, norm_kw, norm_text, phrase_core, set_pending, weak_intent)
from .keywords import (KW_KITAKU, KW_NERU, KW_OKITA, KW_SCHEDULE, KW_SHUPPATSU, KW_STATUS, KW_UNDO, KW_UNDO_FUZZY, KW_YASUMI, PAST_WORKED, RE_DAY, RE_PAID, RE_PAST, RE_YASUMI_PAID)
from .phone import import_phone_marks
from .plan import current, phase_messages, plans
from .records import answer_pending, apply_intents, cmd_paid, cmd_past, paid_flag
from .store import log_append, _read, Store, _write
from .timeutil import fmt_dur
from .usage import due_nag, touch_usage


_BUSY_FILE = "claude-busy.json"
_BUSY_KEEP = timedelta(days=1)   # Stop が来なかった印がたまらんように，1 日より古いのは捨てる


def _save_busy(path: Path, marks: dict) -> None:
    _write(path, marks)


def _update_busy(cfg_path: Path, payload: dict, event: str, code: int, now: datetime) -> None:
    """Claude Code が処理中の印を残す／消す（Agent が通知を待たせるのに読む）。失敗しても何も起こさへん。"""
    try:
        sid = payload.get("session_id")
        if not isinstance(sid, str) or not sid:
            return
        if event == "UserPromptSubmit":
            if code != 0:
                return   # exit 2 の道は Claude が動かへんので Stop も来ない．設定エラー（1）の道も足さない
        elif event != "Stop":
            return
        path = Store(cfg_path).p(_BUSY_FILE)
        raw = _read(path, {})
        marks = {}
        for k, v in (raw.items() if isinstance(raw, dict) else []):
            try:
                if isinstance(k, str) and now - datetime.fromisoformat(v) <= _BUSY_KEEP:
                    marks[k] = v
            except (TypeError, ValueError):
                pass   # 読めへん印は捨てる
        if event == "Stop":
            marks.pop(sid, None)
        else:
            marks[sid] = now.replace(microsecond=0).isoformat()
        _save_busy(path, marks)
    except Exception:
        pass


def run_hook(cfg_path: Path, stdin_text: str, now: datetime) -> tuple[int, str, str]:
    """returns (exit_code, stdout, stderr)"""
    try:
        payload = json.loads(stdin_text) if stdin_text.strip() else {}
        if not isinstance(payload, dict):
            payload = {}
    except json.JSONDecodeError:
        payload = {}
    result = _run_hook(cfg_path, stdin_text, now)
    _update_busy(cfg_path, payload, payload.get("hook_event_name", ""), result[0], now)
    return result


def _run_hook(cfg_path: Path, stdin_text: str, now: datetime) -> tuple[int, str, str]:
    try:
        payload = json.loads(stdin_text) if stdin_text.strip() else {}
    except json.JSONDecodeError:
        payload = {}
    event = payload.get("hook_event_name", "")
    try:
        cfg = load_config(cfg_path)
        plans(cfg, now.date(), back=0, fwd=1)
    except Exception as e:  # 設定が壊れてても作業は止めない
        return 1, "", M.HOOK_CONFIG_ERROR.format(err=e)
    store = Store(cfg_path)
    reset = timedelta(minutes=cfg.get("usage", {}).get("idle_reset_minutes", 20))
    upath = store.p("usage-claude.json")
    usage = touch_usage(_read(upath, {}), now, reset, can_reset=(event == "UserPromptSubmit"))

    if event != "UserPromptSubmit":   # Stop 等: 稼働記録だけ
        _write(upath, usage)
        return 0, "", ""

    prompt = str(payload.get("prompt", "")).strip()
    fixed_kw = KW_YASUMI | KW_UNDO | KW_OKITA | KW_NERU | KW_KITAKU | KW_UNDO_FUZZY | KW_STATUS | KW_SCHEDULE | KW_SHUPPATSU
    nk = norm_kw(norm_text(prompt)) if len(prompt) <= 20 else ""
    # 「今日休む 有給」= 有給で当日欠勤（目安に数えへん）。「11/3 有給」「11/3 有給なし」= 休みの記録に印を付ける／外す
    mpaidy = RE_YASUMI_PAID.match(prompt) if len(prompt) <= 40 and nk not in fixed_kw else None
    mpaid = RE_PAID.match(prompt) if len(prompt) <= 40 and nk not in fixed_kw and not mpaidy else None
    # 「今日休む」は当日欠勤（記録に残す）として扱う。日付付きは予定の休み
    mday = RE_DAY.match(prompt) if len(prompt) <= 40 and nk not in fixed_kw and not (mpaidy or mpaid) else None
    # 「休んだ」「11/2 出た」= 終わった乗務の事後報告。『出た』にメモは付けへん（普通の文を食わんように）
    mpast = RE_PAST.match(norm_kw(prompt)) if len(prompt) <= 40 and nk not in fixed_kw and not (mday or mpaidy or mpaid) else None
    if mpast and mpast.group(3) and (mpast.group(2) in PAST_WORKED or (not mpast.group(1) and paid_flag(mpast.group(3)) is None)):
        mpast = None   # メモが付くのは日付付きの『休んだ』と『休んだ 有給』だけ（「休んだ ので…」「出た ログを…」を食わない）
    if mpast and mpast.group(2) == "行った" and not mpast.group(1):
        mpast = None   # 『行った』だけやと返事と区別がつかへん
    is_kw = bool(mday) or bool(mpast) or bool(mpaidy) or bool(mpaid) or nk in fixed_kw
    try:
        phone_msg = import_phone_marks(cfg, store, now)
    except Exception:
        phone_msg = ""
    # 聞いてた『寝るってこと？』への返事（うん／ちゃう／ちゃう起きた）
    ans = None if is_kw else answer_pending(prompt, cfg, store, now)
    log_append(store, "claude", now, {"k": "kw" if (is_kw or ans) else "p"})  # プロンプト本文は記録しない
    if ans:
        _write(upath, usage)
        return 2, "", ans + "\n"
    if is_kw:
        clear_pending(store)
        _write(upath, usage)
        if mpaidy:
            return 2, "", cmd_keyword(mpaidy.group(1), cfg, store, now, paid=True) + "\n"   # 『今日休む』『休む』は素の語と同じ対象
        if mpaid:
            return 2, "", cmd_paid(mpaid, cfg, store, now) + "\n"
        if mday:
            return 2, "", cmd_day(mday, cfg_path, store, now) + "\n"
        if mpast:
            return 2, "", cmd_past(mpast, cfg, store, now) + "\n"
        return 2, "", cmd_keyword(nk, cfg, store, now) + "\n"

    learned = load_learned(store)
    intents = detect_intents(prompt, learned)
    fuzzy_msg = apply_intents(intents, cfg, store, now) if intents else ""
    ask = None if intents or not can_ask(store, now) else weak_intent(prompt, learned)
    if ask:
        set_pending(store, prompt, ask, now)
    auto_msg = maybe_auto_okita(cfg, store, now)
    phase, p = current(cfg, store.shift_status(), now)
    if phase == "hard" and cfg.get("block_claude_on_hard", True):
        _write(upath, usage)
        head = M.HOOK_HARD_FUZZY_HEAD.format(msg=fuzzy_msg) if fuzzy_msg else ""
        if ask:
            head += M.HOOK_HARD_ASK_HEAD.format(core=phrase_core(prompt), ask_label=M.INTENT_ASK_LABEL[ask])
        return 2, "", head + M.PREFIX + phase_messages(phase, p, now, cfg)[0] + enforce_suffix(store, p, phase) + "\n"

    user, ctx = [m for m in (phone_msg, fuzzy_msg and M.HOOK_FUZZY_UNDO_HINT.format(msg=fuzzy_msg), auto_msg) if m], []
    pu, others = pick_unconfirmed(cfg, store, now)
    if pu:   # 終わった乗務の「出た？休んだ？」を 1 日 1 件だけ Claude に聞かせる（記録は shiftlock がやる）
        ctx.append(confirm_text(cfg, store, pu, now, others)[1])
        mark_confirm_asked(store, pu.day, now)
    if fuzzy_msg:
        ctx.append(M.HOOK_FUZZY_CTX.format(what=M.INTENT_LABEL_SEP.join(M.INTENT_LABEL[w] for w in intents)))
    if ask:
        ctx.append(M.HOOK_ASK_CTX.format(label=M.INTENT_LABEL[ask], ask_label=M.INTENT_ASK_LABEL[ask]))
    if p:
        u, c = phase_messages(phase, p, now, cfg)
        user.append(u + enforce_suffix(store, p, phase))   # 予約済みの強制はユーザーにだけ知らせる（Claude の文脈には入れない）
        if c:
            ctx.append(c)
    cont = due_nag(usage, now, cfg)
    if cont:
        user.append(M.HOOK_NAG_USER.format(dur=fmt_dur(cont)))
        ctx.append(M.HOOK_NAG_CTX.format(dur=fmt_dur(cont)))
    _write(upath, usage)

    if not user and not ctx:
        return 0, "", ""
    out = {"systemMessage": M.PREFIX + M.HOOK_USER_SEP.join(user)} if user else {}
    if ctx:
        out["hookSpecificOutput"] = {"hookEventName": "UserPromptSubmit",
                                     "additionalContext": "\n".join(ctx)}
    return 0, json.dumps(out, ensure_ascii=False) + "\n", ""
