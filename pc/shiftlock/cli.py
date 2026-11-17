"""サブコマンドの配線（main）とヘルプ。"""
from __future__ import annotations

import argparse
import json
import re
import sys
from datetime import datetime, timedelta
from pathlib import Path

from . import i18n as M
from .calendar_sync import sync_calendar
from .commands import cmd_keyword, status_text
from .config import apply_lang, load_config
from .confirm import kakunin_text
from .days import days_set, schedule_text
from .enforce import (enforce_declare, ENFORCE_ACTIONS, enforce_off, enforce_release, enforce_resume, enforce_status_text)
from .hook import run_hook
from .install import install
from .intents import learn, learned_text, unlearn
from .maintenance import cleanup
from .records import late_report, paid_flag
from .report import build_report, closing_period, write_period_reports
from .report_html import render_report
from .settings_menu import settings_menu
from .sleeplog import record_sleep
from .store import Store
from .tick import run_tick
from .timeutil import parse_day


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="shiftlock")
    sub = ap.add_subparsers(dest="cmd", required=True)
    for name in ("status", "yasumi", "deru", "okita", "neru", "yotei", "hook", "tick", "install-hook", "report",
                 "settings", "day", "sync", "cleanup", "learned", "learn", "unlearn", "help", "itte", "kakunin", "enforce"):
        sp = sub.add_parser(name)
        sp.add_argument("--config", required=(name != "help"), type=Path)   # help だけ設定が無くても動く
        if name == "enforce":
            sp.add_argument("action", nargs="?", choices=["on", "off", "resume", "release", "status"])
            sp.add_argument("opts", nargs="*", help=M.CLI_ARG_ENFORCE_OPTS)
        if name in ("okita", "neru"):
            sp.add_argument("words", nargs="*", help=M.CLI_ARG_SLEEP_WHEN)
        if name in ("yasumi", "deru"):
            sp.add_argument("date", nargs="?", help=M.CLI_ARG_PAST_DATE)
            sp.add_argument("note", nargs="*", help=M.CLI_ARG_PAID)
        if name == "tick":
            sp.add_argument("--idle-seconds", type=float, required=True)
            sp.add_argument("--game", type=int, default=0)
            sp.add_argument("--uptime-seconds", type=float, default=None, help=M.CLI_ARG_UPTIME)
            sp.add_argument("--remote", type=int, default=None, help=M.CLI_ARG_REMOTE)
            sp.add_argument("--dry-run", action="store_true", help=M.CLI_ARG_DRY_RUN)
        if name == "day":
            sp.add_argument("action", choices=["off", "work", "rm", "list"])
            sp.add_argument("date", nargs="?")
            sp.add_argument("note", nargs="*")
        if name == "learn":
            sp.add_argument("kind", choices=["kitaku", "neru", "okita", "none"])
            sp.add_argument("phrase", nargs="+")
        if name == "unlearn":
            sp.add_argument("phrase", nargs="+")
        if name == "report":
            sp.add_argument("--days", type=int, default=28)
            sp.add_argument("--out", type=Path)
            sp.add_argument("--period", choices=["current", "prev"], help=M.CLI_ARG_PERIOD)
            sp.add_argument("--auto", action="store_true", help=M.CLI_ARG_AUTO)
        if name == "install-hook":
            sp.add_argument("--settings", type=Path, default=Path.home() / ".claude" / "settings.json")
            sp.add_argument("--hooks-dir", type=Path, default=Path.home() / ".claude" / "hooks")
            sp.add_argument("--windows-settings", type=Path, help=M.CLI_ARG_WIN_SETTINGS)
    a = ap.parse_args(argv)
    now = datetime.now()

    if a.cmd == "help":
        if a.config:
            apply_lang(a.config)   # 設定が無くても動くが、あれば言葉づかいは合わせる
        print(M.CLI_HELP)
        return 0
    if a.cmd == "hook":
        code, out, err = run_hook(a.config, sys.stdin.read(), now)
        sys.stdout.write(out)
        sys.stderr.write(err)
        return code
    if a.cmd == "tick":
        # PowerShell 5.1 側の文字コード事故を避けるため ASCII で返す
        print(json.dumps(run_tick(a.config, a.idle_seconds, bool(a.game), now, a.uptime_seconds,
                                  None if a.remote is None else bool(a.remote), a.dry_run), ensure_ascii=True))
        return 0

    if a.cmd == "settings":
        settings_menu(a.config, now)
        return 0
    cfg = load_config(a.config)
    store = Store(a.config)
    if a.cmd == "day":
        if a.action == "list":
            print(schedule_text(cfg, now.date(), 28))
            return 0
        if not a.date:
            print(M.CLI_DAY_NEED_DATE)
            return 1
        d = parse_day(a.date, now.date())
        days_set(store, d, {"off": "off", "work": "work", "rm": None}[a.action], " ".join(a.note))
        print(schedule_text(load_config(a.config), now.date()))
        return 0
    if a.cmd == "learned":
        print(learned_text(store))
        return 0
    if a.cmd == "enforce":
        if a.action == "on":
            act = next((o for o in a.opts if o.lower() in ENFORCE_ACTIONS), None)
            hrs = next((o for o in a.opts if re.match(r"^\d+(\.\d+)?h?$", o.lower())), None)
            print(enforce_declare(cfg, store, now, act, hrs))      # 対話で y をもらう（非対話なら無効のまま）
        elif a.action == "off":
            print(enforce_off(cfg, store, now))
        elif a.action == "release":
            print(enforce_release(cfg, store, now, "kaijo"))
        elif a.action == "resume":
            print(enforce_resume(store, now))
        else:
            print(enforce_status_text(cfg, store, now))
        return 0
    if a.cmd == "learn":
        core = learn(store, " ".join(a.phrase), a.kind)
        print(M.CLI_LEARNED.format(core=core, label=M.INTENT_LABEL.get(a.kind, M.CLI_LEARNED_NONE)) if core else M.CLI_LEARNED_SHORT)
        return 0
    if a.cmd == "unlearn":
        print(unlearn(store, " ".join(a.phrase)))
        return 0
    if a.cmd == "sync":
        print(sync_calendar(cfg, store, now, force=True))
        return 0
    if a.cmd == "cleanup":
        print(cleanup(cfg, store, now))
        return 0
    if a.cmd == "status":
        print(status_text(cfg, store, now))
    elif a.cmd == "yotei":
        print(schedule_text(cfg, now.date(), 14))
    elif a.cmd in ("yasumi", "deru"):
        # shl yasumi [11/3] [有給|有給なし]: 日付があれば事後報告、無ければ『今日休む』。『有給』は休みに有給の印（deru には無関係）
        words = [w for w in [a.date, *a.note] if w]
        paid = next((paid_flag(w) for w in words if paid_flag(w) is not None), None)
        date = next((w for w in words if paid_flag(w) is None), None)
        kind = "yasumi" if a.cmd == "yasumi" else "worked"
        if date:
            print(late_report(cfg, store, now, kind, date, via="shl", paid=paid if kind == "yasumi" else None))
        else:
            kw = {"yasumi": "今日休む", "deru": "やっぱ出る"}[a.cmd]   # hook と同じキーワード（入力の語彙）
            print(cmd_keyword(kw, cfg, store, now, paid=bool(paid) and kind == "yasumi", via="shl"))
    elif a.cmd in ("okita", "neru") and a.words:
        # shl neru 23:30 / shl okita 11/7 7:00 [取消]: 後から足す睡眠の記録（レポート用。乗務の状態・ロックは変えない）
        print(record_sleep(cfg, store, now, a.cmd, a.words))
    elif a.cmd in ("okita", "neru", "itte"):
        # サブコマンド → hook と同じキーワード（入力の語彙。cmd_keyword が KW_* と照合する）
        kw = {"okita": "起きた", "neru": "ねる", "itte": "行ってきます"}[a.cmd]
        print(cmd_keyword(kw, cfg, store, now, via="shl"))
    elif a.cmd == "kakunin":
        print(kakunin_text(cfg, store, now))
    elif a.cmd == "report":
        if a.auto:
            for f in write_period_reports(cfg, store, now):
                print(f"FILE\t{f}")   # make-report.ps1 が読む行（機械向け。i18n の対象外）
            return 0
        if a.period:
            closing = cfg["report"]["closing_day"]   # 0 / None は月単位（closing_period が丸める）
            per = closing_period(now.date(), closing)
            if a.period == "prev":
                per = closing_period(per[0] - timedelta(days=1), closing)
            data = build_report(cfg, store, now, period=per)
        else:
            data = build_report(cfg, store, now, a.days)
        out = a.out or store.p("report.html")
        out.write_text(render_report(data), encoding="utf-8")
        print(f"FILE\t{out}")
    elif a.cmd == "install-hook":
        cfg_abs = a.config.resolve()
        win = a.windows_settings
        if win is None:
            # 設定が /mnt/c/Users/<名前>/shift-lock にあるなら、Windows 版 Claude Code の設定も自動で一緒に更新する
            # （WSL と Windows で settings.json が別ファイルのとき、片方だけ古いコマンドが残る事故を防ぐ）
            guess = cfg_abs.parent.parent / ".claude" / "settings.json"
            if str(cfg_abs).startswith("/mnt/") and guess.parent.is_dir():
                win = guess
        dest = install(cfg_abs, a.settings, a.hooks_dir, win)
        print(M.CLI_INSTALL_DONE.format(dest=dest, settings=a.settings))
        if win:
            print(M.CLI_INSTALL_WIN.format(win=win))
        print(M.CLI_INSTALL_RESTART)
    return 0
