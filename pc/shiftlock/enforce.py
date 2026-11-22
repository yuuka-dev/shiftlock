"""強制モード（本人が shl enforce on で同意した乗務だけ PC をロック／スリープ／シャットダウン）。
宣言・同意・発動の判断・解除。hook からは触らない（CLAUDE.md）。"""
from __future__ import annotations

import shutil
import sys
from datetime import datetime, timedelta, date as Date
from pathlib import Path

from . import i18n as M
from .console import StdIO
from .apps import normalize_exe
from .limit import LIMIT_ACTIONS, agent_seems_running, check_steps, limit_stages, read_games
from .notify import _once
from .plan import Plan, target_plan
from .store import _read, Store, _write
from .timeutil import fmt, fmt_d


STOP_FILE = "STOP.txt"   # 実行フォルダにあれば強制は一切しない（緊急解除。エクスプローラーで置くだけでええ）

ENFORCE_ACTIONS = ("lock", "sleep", "shutdown") + LIMIT_ACTIONS   # action の妥当性はこれで見る（表示の言葉は i18n）
CAUTION_ACTIONS = ("sleep", "shutdown") + LIMIT_ACTIONS   # 注意を出して y をもらう action（遠隔から戻せへん・アプリが止まる）


def stop_active(store: Store) -> bool:
    return store.p(STOP_FILE).exists()


def enforce_state(store: Store, day: str) -> dict:
    return dict(store.shift_status().get(day, {}).get("enforce") or {})


def set_enforce(store: Store, day: str, e: dict | None, now: datetime, kind: str, note: str = "") -> None:
    st = _read(store.p("state.json"), {})
    cur = st.setdefault("shifts", {}).setdefault(day, {})
    if e is None:
        cur.pop("enforce", None)
    else:
        cur["enforce"] = e
    _write(store.p("state.json"), st)
    if kind != "enforce_state":
        with open(store.p("events.log"), "a", encoding="utf-8", newline="\n") as f:
            f.write(f"{now.isoformat(timespec='minutes')}\t{day}\t{kind}\t{note}\n")


def _names(exes) -> str:
    return " / ".join(sorted(exes))


def _limit_apps(cfg: dict) -> list[str]:
    """宣言に写す，足したアプリの exe 名（normalize_exe でそろえて重複を除き，並べ替える）。"""
    raw = cfg["limit"].get("apps", [])
    return sorted({x for x in (normalize_exe(a) for a in raw if isinstance(a, str)) if x}) if isinstance(raw, list) else []


def _limit_terms(cfg: dict, p: Plan, action: str, store: Store | None, now: datetime | None) -> str:
    """throttle／suspend の同意の表示。段階の時刻と割合・対象・戻る時刻・解除の方法・注意。勧める文は入れない。"""
    lc = cfg["limit"]
    stages = "\n".join(
        M.ENFORCE_TERMS_LIMIT_STAGE.format(
            step=st["step"], at=fmt(st["at"]),
            what=M.ENFORCE_TERMS_LIMIT_STOPPED if st["percent"] is None else M.ENFORCE_TERMS_LIMIT_PERCENT.format(percent=st["percent"]))
        for st in limit_stages(p.soft_close, p.hard_close, check_steps(lc.get("steps")) or [0, 0, 0, 0], action))
    seen = read_games(store, now) if store is not None and now is not None else {}
    excluded = {x for x in (normalize_exe(str(e)) for e in lc.get("exclude", [])) if x}
    targets = [g for g, v in seen.items() if v == "target" and g not in excluded]
    anticheat = [g for g, v in seen.items() if v == "anticheat"]
    unknown = [g for g, v in seen.items() if v == "unknown"]
    games = [M.ENFORCE_TERMS_LIMIT_GAMES.format(names=_names(targets)) if targets else M.ENFORCE_TERMS_LIMIT_GAMES_NONE]
    if anticheat:
        games.append(M.ENFORCE_TERMS_LIMIT_EXCLUDED.format(names=_names(anticheat)))
    if unknown:
        games.append(M.ENFORCE_TERMS_LIMIT_UNKNOWN.format(names=_names(unknown)))
    apps = _limit_apps(cfg)
    text = M.ENFORCE_TERMS_LIMIT.format(
        day=f"{p.start_latest:%m/%d}", what=M.ENFORCE_TERMS_WHAT[action], hard=fmt(p.hard_close), stages=stages,
        games="\n".join(games), back=fmt(p.start_latest),
        apps=M.ENFORCE_TERMS_LIMIT_APPS.format(names=_names(apps)) if apps else M.ENFORCE_TERMS_LIMIT_APPS_NONE)
    text += "\n" + M.ENFORCE_TERMS_LIMIT_ANTICHEAT_NOTE
    if action == "suspend":
        text += "\n" + M.ENFORCE_TERMS_LIMIT_SUSPEND_NOTE
    text += "\n" + M.ENFORCE_TERMS_CAUTION_LINE.format(caution=M.ENFORCE_CAUTION[action])
    if store is not None and now is not None and not agent_seems_running(store, now):
        text += "\n" + M.ENFORCE_LIMIT_NO_AGENT
    return text


def enforce_terms(cfg: dict, p: Plan, action: str, hours: float | None, store: Store | None = None,
                  now: datetime | None = None) -> str:
    """同意の前に見せる内容（何を・いつから・どれだけ・解除方法・注意）。
    throttle／suspend は段階・対象の表示で，store・now があればゲームの判定と Agent の動作確認も出す。"""
    if action in LIMIT_ACTIONS:
        return _limit_terms(cfg, p, action, store, now)
    ec = cfg["enforce"]
    end = p.hard_close + timedelta(minutes=ec["grace_minutes"]) + timedelta(hours=hours)
    text = M.ENFORCE_TERMS.format(day=f"{p.start_latest:%m/%d}", what=M.ENFORCE_TERMS_WHAT[action], hard=fmt(p.hard_close),
                                  grace=ec["grace_minutes"], hours=f"{hours:g}", end=fmt_d(end),
                                  relock=ec["relock_minutes"], max_runs=ec["max_runs"])
    if action in CAUTION_ACTIONS:
        text += "\n" + M.ENFORCE_TERMS_CAUTION_LINE.format(caution=M.ENFORCE_CAUTION[action])
    return text


def windows_confirm(text: str, timeout: int = 60) -> bool:
    """Windows のダイアログ（はい／いいえ）で本人に押してもらう。WSL から powershell.exe を呼ぶ。
    押されない・タイムアウト・powershell.exe が無い → False（安全側）。"""
    import base64
    import subprocess
    exe = shutil.which("powershell.exe") or "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
    if not Path(exe).exists():
        return False
    esc = text.replace("'", "''")
    title = M.ENFORCE_DIALOG_TITLE.replace("'", "''")   # 文面に ' が入っても PowerShell の単一引用符を壊さへん
    cmd = f"(New-Object -ComObject WScript.Shell).Popup('{esc}', {int(timeout)}, '{title}', 4 + 32 + 256 + 4096)"   # はい/いいえ、?、既定は「いいえ」、最前面
    enc = base64.b64encode(cmd.encode("utf-16-le")).decode("ascii")
    try:
        r = subprocess.run([exe, "-NoProfile", "-EncodedCommand", enc], capture_output=True, text=True, timeout=timeout + 15)
        lines = [x.strip() for x in r.stdout.splitlines() if x.strip()]
        return bool(lines) and lines[-1] == "6"   # 6 = はい、7 = いいえ、-1 = タイムアウト
    except Exception:
        return False


def enforce_declare(cfg: dict, store: Store, now: datetime, action: str | None = None, hours=None, io=None) -> str:
    """shl enforce on。内容を表示して対話で y をもらったときだけ有効化する。非対話（EOF）は無効のまま。"""
    io = io or StdIO()
    ec = cfg["enforce"]
    action = (action or ec["action"]).lower()
    if action not in ENFORCE_ACTIONS:
        return M.ENFORCE_BAD_ACTION
    limit = action in LIMIT_ACTIONS
    if limit:   # 減速・一時停止は時間を持たない（最低ラインから出庫の時刻まで）．時間の指定は断る
        if hours is not None:
            return M.ENFORCE_LIMIT_NO_HOURS
        steps = check_steps(cfg["limit"].get("steps"))
        if steps is None:
            return M.ENFORCE_LIMIT_BAD_STEPS
    else:
        try:
            hours = float(str(hours).lower().rstrip("h")) if hours is not None else float(ec["duration_hours"])
        except ValueError:
            return M.ENFORCE_BAD_HOURS
        if not 0.5 <= hours <= 12:
            return M.ENFORCE_HOURS_RANGE
    p = target_plan(cfg, now)
    if p is None:
        return M.ENFORCE_NO_PLAN
    me = store.shift_status().get(p.day, {})
    if me.get("status") in ("yasumi", "worked"):
        return M.ENFORCE_SHIFT_OFF.format(day=f"{p.start_latest:%m/%d}")
    if (me.get("enforce") or {}).get("runs"):
        return M.ENFORCE_ALREADY_RUNNING
    if isinstance(io, StdIO) and not sys.stdin.isatty():
        # パイプ（echo y |）や Claude Code の Bash からは同意できへん。対話できる端末だけ
        return M.ENFORCE_NEED_TTY
    io.print(enforce_terms(cfg, p, action, hours, store, now))
    try:
        ans = io.input(M.ENFORCE_ASK_OK).strip().lower()
    except EOFError:
        ans = ""
    if ans != "y":
        return M.ENFORCE_DECLINED
    dialog = (M.ENFORCE_DIALOG_TEXT_LIMIT.format(day=f"{p.start_latest:%m/%d}", label=M.ENFORCE_LABEL[action]) if limit else
              M.ENFORCE_DIALOG_TEXT.format(day=f"{p.start_latest:%m/%d}", label=M.ENFORCE_LABEL[action], hours=f"{hours:g}"))
    if isinstance(io, StdIO) and not windows_confirm(dialog):
        # 端末の y だけやと擬似端末で真似できるので、Windows のダイアログで本人に押してもらう（画面の前におる人しか押せへん）
        return M.ENFORCE_DIALOG_DENIED
    e = {"action": action, "declared_at": now.isoformat(timespec="minutes"),
         "consented_at": now.isoformat(timespec="minutes"), "via": "shl", "runs": []}
    if limit:
        # 同意した割合と足したアプリを固定する（あとで設定を変えても、この乗務には効かない）
        e.update({"steps": steps, "limit_apps": _limit_apps(cfg)})
        note = action
    else:
        e.update({"duration_hours": hours,
                  # 同意した内容を固定する（あとで設定を変えても、この乗務には効かない）
                  "grace_minutes": int(ec["grace_minutes"]), "relock_minutes": int(ec["relock_minutes"]),
                  "max_runs": int(ec["max_runs"]), "boot_grace_minutes": int(ec["boot_grace_minutes"])})
        note = f"{action} {hours:g}h"
    set_enforce(store, p.day, e, now, "enforce_declare", note)
    with open(store.p("events.log"), "a", encoding="utf-8", newline="\n") as f:
        f.write(f"{now.isoformat(timespec='minutes')}\t{p.day}\tenforce_consent\t{note}\n")
    extra = M.ENFORCE_STOP_NOTE if stop_active(store) else ""
    if limit:
        return M.ENFORCE_DECLARED_LIMIT.format(day=f"{p.start_latest:%m/%d}", label=M.ENFORCE_LABEL[action], extra=extra)
    return M.ENFORCE_DECLARED.format(day=f"{p.start_latest:%m/%d}", label=M.ENFORCE_LABEL[action], hours=f"{hours:g}", extra=extra)


def _declared(store: Store, include_released: bool = False) -> list[tuple[str, dict]]:
    """同意済みの宣言（乗務日, enforce）。既定では解除済みを除く。"""
    return [(d, v["enforce"]) for d, v in sorted(store.shift_status().items())
            if (v.get("enforce") or {}).get("consented_at") and (include_released or not v["enforce"].get("released_at"))]


def enforce_off(cfg: dict, store: Store, now: datetime) -> str:
    """shl enforce off。最低ライン前（実行前）なら取り消し。最低ラインに入ってからは解除（記録に残る）扱い。"""
    p = target_plan(cfg, now)
    e = enforce_state(store, p.day) if p else {}
    if not p or not e:
        return M.ENFORCE_OFF_NONE
    if now < p.hard_close and not e.get("runs"):
        set_enforce(store, p.day, None, now, "enforce_off")
        return M.ENFORCE_OFF_DONE.format(day=f"{p.start_latest:%m/%d}")
    return enforce_release(cfg, store, now, "off")


def enforce_release(cfg: dict, store: Store, now: datetime, reason: str = "kaijo") -> str:
    """緊急解除。STOP.txt を置き、同意済みの宣言に released_at を付ける（記録に残る）。"""
    stop = store.p(STOP_FILE)
    already = stop.exists()
    with open(stop, "a", encoding="utf-8", newline="\n") as f:
        f.write(f"{now:%Y-%m-%d %H:%M} {reason}\n")
    decl = _declared(store)
    for d, e in decl:
        e["released_at"] = now.isoformat(timespec="minutes")
        set_enforce(store, d, e, now, "enforce_release", reason)
    if not decl:   # 宣言が無くても解除の操作は記録に残す
        with open(store.p("events.log"), "a", encoding="utf-8", newline="\n") as f:
            f.write(f"{now.isoformat(timespec='minutes')}\t-\tenforce_release\t{reason}\n")
    if already and not decl:
        return M.ENFORCE_RELEASE_ALREADY
    return M.ENFORCE_RELEASED


def enforce_resume(store: Store, now: datetime) -> str:
    stop = store.p(STOP_FILE)
    if not stop.exists():
        return M.ENFORCE_RESUME_NONE
    stop.unlink()
    for d, e in _declared(store):   # 手で置いた STOP.txt でも、解除中の宣言は終わりにする（再開で古い同意が復活せんように）
        e["released_at"] = now.isoformat(timespec="minutes")
        set_enforce(store, d, e, now, "enforce_release", "resume")
    with open(store.p("events.log"), "a", encoding="utf-8", newline="\n") as f:
        f.write(f"{now.isoformat(timespec='minutes')}\t-\tenforce_resume\t\n")
    return M.ENFORCE_RESUMED


def enforce_suffix(store: Store, p: Plan, phase: str = "hard") -> str:
    """宣言済みの乗務の通知に添える一言（予約の内容を知らせるだけ。宣言してない乗務には何も出さない）。"""
    if phase not in ("lastcall", "soft", "hard"):
        return ""
    e = enforce_state(store, p.day)
    if not e.get("consented_at") or e.get("released_at"):
        return ""
    stop = M.ENFORCE_SUFFIX_STOP if stop_active(store) else ""
    if e.get("action") in LIMIT_ACTIONS:   # 減速・一時停止には「N 時間」が無い
        return M.ENFORCE_SUFFIX_LIMIT.format(label=M.ENFORCE_LABEL[e["action"]], stop=stop)
    return M.ENFORCE_SUFFIX.format(label=M.ENFORCE_LABEL.get(e.get("action"), e.get("action")),
                                   hours=f"{float(e.get('duration_hours', 2)):g}", stop=stop)


def enforce_status_text(cfg: dict, store: Store, now: datetime) -> str:
    lines = [M.ENFORCE_STATUS_HEAD]
    ec = cfg["enforce"]
    lines.append(M.ENFORCE_STATUS_DEFAULT.format(label=M.ENFORCE_LABEL[ec["action"]], hours=f"{float(ec['duration_hours']):g}",
                                                 grace=ec["grace_minutes"]))
    lines.append(M.ENFORCE_STATUS_STOP.format(stop=M.ENFORCE_STATUS_STOP_ON if stop_active(store) else M.ENFORCE_STATUS_STOP_OFF))
    found = False
    for d, v in sorted(store.shift_status().items()):
        e = v.get("enforce")
        if not e or Date.fromisoformat(d) < now.date() - timedelta(days=3):
            continue
        found = True
        runs = e.get("runs", [])
        state = (M.ENFORCE_STATE_RELEASED.format(at=e["released_at"][11:16]) if e.get("released_at") else
                 M.ENFORCE_STATE_RUNNING if runs else M.ENFORCE_STATE_WARNED if e.get("warned_at") else
                 M.ENFORCE_STATE_CONSENTED if e.get("consented_at") else M.ENFORCE_STATE_PENDING)
        label = M.ENFORCE_LABEL.get(e.get("action"), e.get("action"))
        last = M.ENFORCE_STATUS_LAST.format(at=runs[-1]["at"][11:16]) if runs else ""
        if e.get("action") in LIMIT_ACTIONS:   # 減速・一時停止には「N 時間」が無い
            lines.append(M.ENFORCE_STATUS_LINE_LIMIT.format(day=f"{Date.fromisoformat(d):%m/%d}", label=label, state=state,
                                                            runs=len(runs), last=last))
        else:
            lines.append(M.ENFORCE_STATUS_LINE.format(day=f"{Date.fromisoformat(d):%m/%d}", label=label,
                                                      hours=f"{float(e.get('duration_hours', 2)):g}", state=state, runs=len(runs), last=last))
    if not found:
        lines.append(M.ENFORCE_STATUS_NONE)
    return "\n".join(lines)


def enforce_due(cfg: dict, store: Store, now: datetime, phase: str, p: Plan | None, active: bool,
                uptime_seconds: float | None, remote: bool, notify: dict,
                idle_seconds: float = 0.0, dry_run: bool = False) -> list[dict]:
    """強制モードの判断。戻り値は actions に足す要素（予告の通知か、{"method": "enforce"} の実行）。
    猶予は hard_close からではなく、PC を触っている状態で出した最初の予告（warned_at）から数える。
    離席をまたいだら（前回見た tick から 12 分以上）予告からやり直す。dry_run なら状態を進めない。"""
    if phase != "hard" or p is None or not active:
        return []
    e = enforce_state(store, p.day)
    if not e.get("consented_at") or e.get("released_at") or stop_active(store):
        return []
    if e.get("action") in LIMIT_ACTIONS:
        return []   # 減速・一時停止は Agent の段階の処理（予告・実行）が持つ．ここは何も返さず，seen_at・warned_at も書かない
    ec = cfg["enforce"]
    if remote and ec.get("skip_if_remote", True):
        return []
    boot_grace = float(e.get("boot_grace_minutes", ec["boot_grace_minutes"]))
    if uptime_seconds is None or not (uptime_seconds > 0) or uptime_seconds < boot_grace * 60:
        return []   # 起動直後・uptime が不明（古い guard、0、NaN）は発動しない
    action = e.get("action", ec["action"])
    label = M.ENFORCE_LABEL.get(action, action)
    grace = float(e.get("grace_minutes", ec["grace_minutes"]))
    relock = float(e.get("relock_minutes", ec["relock_minutes"]))
    max_runs = int(e.get("max_runs", ec["max_runs"]))
    runs = list(e.get("runs", []))
    last_input = now - timedelta(seconds=max(0.0, float(idle_seconds)))

    def notice(text: str) -> dict:
        return {"method": notify["hard"], "kind": "enforce", "title": "shiftlock", "text": text, "speak": True}

    def save() -> None:
        if not dry_run:
            set_enforce(store, p.day, e, now, "enforce_state", "")

    seen = e.get("seen_at")
    if seen and now - datetime.fromisoformat(seen) > timedelta(minutes=12):
        e.pop("warned_at", None)   # 離席をまたいだ → 戻ってきたら必ず予告からやり直す
        e.pop("rewarned_at", None)
    e["seen_at"] = now.isoformat(timespec="seconds")
    save()

    if runs:
        first = datetime.fromisoformat(e.get("first_run_at") or runs[0]["at"])
        if now >= first + timedelta(hours=float(e.get("duration_hours", ec["duration_hours"]))):
            return []   # 時間切れ。最低ラインを過ぎたままでももう何もしない
        if len(runs) >= max_runs:
            return []
        last = datetime.fromisoformat(runs[-1]["at"])
        if last_input <= last:
            return []   # ロック直後で誰も触ってない（最後の入力が実行より前）→ 無人の画面に再予告しない
        rew = e.get("rewarned_at")
        if not rew or datetime.fromisoformat(rew) < last:
            e["rewarned_at"] = now.isoformat(timespec="seconds")
            save()
            return [notice(M.ENFORCE_REWARN.format(relock=f"{relock:g}", label=label))]
        if now < datetime.fromisoformat(rew) + timedelta(minutes=relock):
            return []
    else:
        warned = e.get("warned_at")
        if not warned:
            e["warned_at"] = now.isoformat(timespec="seconds")
            save()
            return [notice(M.ENFORCE_WARN.format(grace=f"{grace:g}", label=label))]
        remaining = datetime.fromisoformat(warned) + timedelta(minutes=grace) - now
        if remaining > timedelta(0):
            mins = int(-(-remaining.total_seconds() // 60))
            if remaining <= timedelta(minutes=5) and (dry_run or _once(store, f"{p.day}:enforce:warn5:{warned}", now)):
                return [notice(M.ENFORCE_WARN_SOON.format(mins=mins, label=label))]
            return []
    runs.append({"at": now.isoformat(timespec="seconds"), "action": action})
    e["runs"] = runs
    e.setdefault("first_run_at", runs[0]["at"])
    if not dry_run:
        set_enforce(store, p.day, e, now, "enforce_run", action)
    return [{"method": "enforce", "action": action, "kind": "enforce", "title": "shiftlock",
             "text": M.ENFORCE_RUN.format(label=label), "speak": True}]
