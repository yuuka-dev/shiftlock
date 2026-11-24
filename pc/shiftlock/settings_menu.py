"""設定メニュー（settings.cmd / shl settings）。JSON を直接いじらんでええように。
番号・記号（1〜21、a/d/y/s/q など）の対応表は「入力」なのでここに残す。表示の言葉は i18n。"""
from __future__ import annotations

import re
import sys
from datetime import datetime
from pathlib import Path

from . import i18n as M
from .calendar_sync import sync_calendar
from .config import calendar_sources, deep_merge, DEFAULTS, load_config, load_raw, save_raw
from .console import _ask, _hm_conv, _list_edit, _num_conv, StdIO, _wd_conv, WD_IN, WD_OUT, _wd_str
from .days import days_set, schedule_text
from .enforce import CAUTION_ACTIONS
from .intents import learn, learned_text, load_learned, unlearn
from .store import Store
from .timeutil import parse_day


def settings_menu(cfg_path: Path, now: datetime, io=None) -> bool:
    """対話式で設定を変える。保存したら True。"""
    io = io or StdIO()
    raw = load_raw(cfg_path)
    M.set_lang(raw.get("lang"))   # メニューの文面も設定の言葉づかいで（20 で変えた分は保存後の次回から）
    store = Store(cfg_path)

    def view() -> dict:
        return deep_merge(DEFAULTS, raw)

    def cur_lang(c: dict) -> str:
        """表示用の言語名。lang が [] / {} / 知らない値でも落ちへんように、set_lang と同じく既定に寄せる"""
        return c["lang"] if isinstance(c["lang"], str) and c["lang"] in M.LANGS else M.DEFAULT_LANG

    def setk(path: str, v):
        if v is None:
            return
        node = raw
        keys = path.split(".")
        for k in keys[:-1]:
            node = node.setdefault(k, {})
        node[keys[-1]] = v

    while True:
        c = view()
        ns = M.MENU_NO_SLEEP_SEP.join(
            M.MENU_NO_SLEEP_ITEM.format(wd=WD_OUT.get(r.get("weekday", "")[:3], r.get("date", "?")), frm=r["from"], to=r["to"],
                                        label=r.get("label", ""))
            for r in c["no_sleep"]) or M.MENU_NONE
        ncal = len(calendar_sources(c["calendar"]))
        cal = M.MENU_CAL_COUNT.format(n=ncal) if ncal else M.MENU_UNSET
        io.print(M.MENU_HEAD)
        io.print(M.MENU_1.format(wd=_wd_str(c["work_weekdays"]) or M.MENU_NONE))
        io.print(M.MENU_2.format(target=c["start_target"], latest=c["start_latest"]))
        io.print(M.MENU_3.format(prep=c["prep_minutes"]))
        io.print(M.MENU_4.format(target_h=c["sleep_target_hours"], min_h=c["sleep_min_hours"]))
        io.print(M.MENU_5.format(lastcall=c["lastcall_minutes"]))
        io.print(M.MENU_6.format(block=M.MENU_6_BLOCK if c["block_claude_on_hard"] else M.MENU_6_WARN))
        io.print(M.MENU_7.format(ns=ns))
        io.print(M.MENU_8.format(after=c["usage"]["nag_after_minutes"], every=c["usage"]["nag_every_minutes"]))
        io.print(M.MENU_9.format(steam=M.MENU_9_AUTO if c["steam_libraries"] else M.MENU_9_OFF, dirs=len(c["game_dirs"]),
                                 xdirs=len(c["game_exclude_dirs"]), xnames=len(c["game_exclude"]), procs=len(c["game_processes"])))
        io.print(M.MENU_10.format(path=c["mood_file"] or M.MENU_UNSET))
        io.print(M.MENU_11.format(cal=cal, off="/".join(c["calendar"]["off_keywords"]), work="/".join(c["calendar"]["work_keywords"])))
        io.print(M.MENU_12)
        io.print(M.MENU_13.format(days=c["retention"]["days_keep"], logs=c["retention"]["log_keep_days"]))
        aw = c.get("auto_wake_hours") or c["sleep_min_hours"]
        mode = {True: "auto", False: "off"}.get(c["auto_wake"], c["auto_wake"])
        mlabel = M.MENU_14_MODES.get(mode, mode)
        io.print(M.MENU_14.format(mode=mlabel, hours=aw, home=c["home_by_latest"]))
        nl = sum(len(v) for v in load_learned(store).values())
        io.print(M.MENU_15.format(n=nl))
        closing = c["report"]["closing_day"]
        io.print(M.MENU_16.format(closing=M.MENU_16_CLOSING.format(closing=closing) if closing else M.MENU_16_MONTHLY,
                                  keep=c["report"]["keep_months"]))
        nsnd = sum(1 for v in c["notify"].get("sound", {}).values() if v)
        io.print(M.MENU_17.format(sound=M.MENU_17_SET.format(n=nsnd) if nsnd else M.MENU_17_UNSET))
        io.print(M.MENU_19.format(label=M.ENFORCE_LABEL[c["enforce"]["action"]], hours=f"{float(c['enforce']['duration_hours']):g}",
                                  grace=c["enforce"]["grace_minutes"]))
        io.print(M.MENU_18.format(ask=M.MENU_18_ASK if c["confirm"]["mode"] == "ask" else M.MENU_18_NO,
                                  days=c["confirm"]["lookback_days"],
                                  omw=M.MENU_YES if c["confirm"]["okita_means_worked"] else M.MENU_NO))
        io.print(M.MENU_20.format(lang=M.LANG_LABEL.get(cur_lang(c), cur_lang(c))))
        io.print(M.MENU_21.format(shifts=c["absence"]["window_shifts"], limit=c["absence"]["warn_at"]))
        io.print(M.MENU_FOOT)
        ch = io.input(M.MENU_PROMPT).strip().lower()
        if ch == "s":
            save_raw(cfg_path, raw)
            io.print(M.MENU_SAVED)
            return True
        if ch == "q":
            io.print(M.MENU_NOT_SAVED)
            return False
        io.print(M.MENU_ENTER_KEEPS)
        if ch == "1":
            setk("work_weekdays", _ask(io, M.MENU_ASK_1, _wd_str(c["work_weekdays"]), _wd_conv))
        elif ch == "2":
            setk("start_target", _ask(io, M.MENU_ASK_2A, c["start_target"], _hm_conv))
            setk("start_latest", _ask(io, M.MENU_ASK_2B, c["start_latest"], _hm_conv))
        elif ch == "3":
            setk("prep_minutes", _ask(io, M.MENU_ASK_3, c["prep_minutes"], _num_conv(0, 300)))
        elif ch == "4":
            setk("sleep_target_hours", _ask(io, M.MENU_ASK_4A, c["sleep_target_hours"], _num_conv(1, 12)))
            setk("sleep_min_hours", _ask(io, M.MENU_ASK_4B, c["sleep_min_hours"], _num_conv(1, 12)))
        elif ch == "5":
            setk("lastcall_minutes", _ask(io, M.MENU_ASK_5, c["lastcall_minutes"], _num_conv(0, 240)))
        elif ch == "6":
            setk("block_claude_on_hard", _ask(io, M.MENU_ASK_6, "y" if c["block_claude_on_hard"] else "n",
                                               lambda s: s.lower().startswith("y")))
        elif ch == "7":
            io.print(M.MENU_7_CURRENT.format(ns=ns))
            a = io.input(M.MENU_7_PROMPT).strip().lower()
            if a == "a":
                wd = _ask(io, M.MENU_7_WD, "", lambda s: WD_IN[s])
                fr = _ask(io, M.MENU_7_FROM, "", _hm_conv)
                to = _ask(io, M.MENU_7_TO, "", _hm_conv)
                lb = io.input(M.MENU_7_LABEL).strip()
                if wd and fr and to:
                    raw.setdefault("no_sleep", list(c["no_sleep"])).append(
                        {"weekday": wd, "from": fr, "to": to, "label": lb})
            elif a == "d":
                raw["no_sleep"] = []
        elif ch == "8":
            setk("usage.nag_after_minutes", _ask(io, M.MENU_ASK_8A, c["usage"]["nag_after_minutes"], _num_conv(10, 600)))
            setk("usage.nag_every_minutes", _ask(io, M.MENU_ASK_8B, c["usage"]["nag_every_minutes"], _num_conv(5, 240)))
        elif ch == "9":
            games_menu(raw, c, io)
        elif ch == "10":
            setk("mood_file", _ask(io, M.MENU_ASK_10, c["mood_file"] or "", lambda s: s.strip().strip('"')))
        elif ch == "11":
            calendar_menu(cfg_path, raw, c, store, now, io)
        elif ch == "12":
            days_menu(cfg_path, store, now, io)
        elif ch == "13":
            setk("retention.days_keep", _ask(io, M.MENU_ASK_13A, c["retention"]["days_keep"], _num_conv(30, 3650)))
            setk("retention.log_keep_days", _ask(io, M.MENU_ASK_13B, c["retention"]["log_keep_days"], _num_conv(30, 3650)))
        elif ch == "16":
            setk("report.closing_day", _ask(io, M.MENU_ASK_16A, c["report"]["closing_day"], _num_conv(0, 31)))
            setk("report.keep_months", _ask(io, M.MENU_ASK_16B, c["report"]["keep_months"], _num_conv(1, 36)))
            io.print(M.MENU_16_NOTE)
        elif ch == "15":
            learned_menu(store, io)
        elif ch == "17":
            sound_menu(raw, c, io)
        elif ch == "19":
            enforce_menu(raw, c, io)
        elif ch == "20":
            def _lang(x: str) -> str:
                i = int(x) if x.isdigit() else 0
                if not 1 <= i <= len(M.LANGS):
                    raise ValueError(M.MENU_20_BAD)
                return M.LANGS[i - 1]
            for i, name in enumerate(M.LANGS, 1):
                io.print(M.MENU_20_OPT.format(i=i, label=M.LANG_LABEL.get(name, name)))
            cur = M.LANGS.index(cur_lang(c)) + 1
            setk("lang", _ask(io, M.MENU_ASK_20, cur, _lang))
            io.print(M.MENU_20_NOTE)
        elif ch == "21":
            setk("absence.window_shifts", _ask(io, M.MENU_ASK_21A, c["absence"]["window_shifts"], _num_conv(1, 365)))
            setk("absence.warn_at", _ask(io, M.MENU_ASK_21B, c["absence"]["warn_at"], _num_conv(0, 100)))
        elif ch == "18":
            setk("confirm.mode", _ask(io, M.MENU_ASK_18A, "y" if c["confirm"]["mode"] == "ask" else "n",
                                      lambda s: "ask" if s.lower().startswith("y") else "off"))
            setk("confirm.lookback_days", _ask(io, M.MENU_ASK_18B, c["confirm"]["lookback_days"], _num_conv(1, 60)))
            setk("confirm.okita_means_worked", _ask(io, M.MENU_ASK_18C, "y" if c["confirm"]["okita_means_worked"] else "n",
                                                    lambda s: s.lower().startswith("y")))
        elif ch == "14":
            def _mode(x: str):
                m = {"1": "auto", "2": "neru", "3": False, "a": "auto", "n": "neru", "o": False}.get(x.lower())
                if m is None:
                    raise ValueError(M.MENU_14_BAD)
                return True if m == "auto" else m
            io.print(M.MENU_14_OPT1)
            io.print(M.MENU_14_OPT2)
            io.print(M.MENU_14_OPT3)
            setk("auto_wake", _ask(io, M.MENU_ASK_14A, {"auto": "1", "neru": "2", "off": "3"}.get(mode, "1"), _mode))
            setk("home_by_latest", _ask(io, M.MENU_ASK_14B, c["home_by_latest"], _hm_conv))
            setk("auto_wake_hours", _ask(io, M.MENU_ASK_14C, aw, _num_conv(1, 12)))
        else:
            io.print(M.MENU_UNKNOWN)


def games_menu(raw: dict, c: dict, io) -> None:
    io.print(M.MENU_GAMES_INTRO1)
    io.print(M.MENU_GAMES_INTRO2)
    while True:
        io.print(M.MENU_GAMES_S.format(on=M.MENU_GAMES_ON if raw.get("steam_libraries", c["steam_libraries"]) else M.MENU_GAMES_OFF))
        io.print(M.MENU_GAMES_G.format(n=len(raw.get("game_dirs", c["game_dirs"]))))
        io.print(M.MENU_GAMES_X.format(n=len(raw.get("game_exclude_dirs", c["game_exclude_dirs"]))))
        io.print(M.MENU_GAMES_N.format(n=len(raw.get("game_exclude", c["game_exclude"]))))
        io.print(M.MENU_GAMES_P.format(n=len(raw.get("game_processes", c["game_processes"]))))
        a = io.input(M.MENU_GAMES_PROMPT).strip().lower()
        if a == "":
            return
        if a == "s":
            raw["steam_libraries"] = not raw.get("steam_libraries", c["steam_libraries"])
        elif a == "g":
            raw["game_dirs"] = _list_edit(io, M.MENU_GAMES_G_TITLE, raw.get("game_dirs", c["game_dirs"]))
        elif a == "x":
            raw["game_exclude_dirs"] = _list_edit(io, M.MENU_GAMES_X_TITLE, raw.get("game_exclude_dirs", c["game_exclude_dirs"]))
        elif a == "n":
            raw["game_exclude"] = _list_edit(io, M.MENU_GAMES_N_TITLE, raw.get("game_exclude", c["game_exclude"]))
        elif a == "p":
            raw["game_processes"] = _list_edit(io, M.MENU_GAMES_P_TITLE, raw.get("game_processes", c["game_processes"]),
                                               lambda v: re.sub(r"\.exe$", "", v, flags=re.I))


def calendar_menu(cfg_path: Path, raw: dict, c: dict, store: Store, now: datetime, io) -> None:
    """11) Google カレンダー: ソースの一覧・追加・編集・削除・同期。raw を直接更新する（保存は s）。
    ソース名「カレンダー」は shifts.json / calendar.json に保存される値なので i18n には置かない。"""
    cal = raw.setdefault("calendar", {})
    if (cal.get("ics_url") or "").strip():   # 旧形式 → 新形式（保存時に ics_url は消える）
        cal.setdefault("sources", []).insert(0, {"name": "カレンダー", "ics_url": cal["ics_url"]})
        cal.pop("ics_url", None)
    cal.pop("ics_url", None)
    kw = lambda s: [x.strip() for x in s.split(",") if x.strip()]
    while True:
        srcs = cal.setdefault("sources", [])
        io.print(M.MENU_CAL_HEAD)
        for i, s in enumerate(srcs, 1):
            io.print(M.MENU_CAL_ITEM.format(i=i, name=s.get("name") or "?",
                                            off="/".join(s.get("off_keywords", c["calendar"]["off_keywords"])),
                                            work="/".join(s.get("work_keywords", c["calendar"]["work_keywords"])),
                                            url=M.MENU_CAL_URL_YES if (s.get("ics_url") or "").strip() else M.MENU_CAL_URL_NO))
        if not srcs:
            io.print(M.CONSOLE_LIST_EMPTY)
        a = io.input(M.MENU_CAL_PROMPT).strip().lower()
        if a == "":
            return
        idx = int(a[1:]) - 1 if len(a) >= 2 and a[0] in "ed" and a[1:].isdigit() else None
        if a == "a" or (a[0] == "e" and idx is not None and 0 <= idx < len(srcs)):
            s = {} if a == "a" else srcs[idx]
            name = io.input(M.MENU_CAL_NAME.format(name=s.get("name", ""))).strip() or s.get("name") or f"カレンダー{len(srcs) + 1}"
            url = io.input(M.MENU_CAL_URL).strip() or s.get("ics_url", "")
            offk = _ask(io, M.MENU_CAL_OFF_KW, ",".join(s.get("off_keywords", c["calendar"]["off_keywords"])), kw)
            workk = _ask(io, M.MENU_CAL_WORK_KW, ",".join(s.get("work_keywords", c["calendar"]["work_keywords"])), kw)
            s.update({"name": name, "ics_url": url})
            if offk is not None:
                s["off_keywords"] = offk
            if workk is not None:
                s["work_keywords"] = workk
            if a == "a":
                srcs.append(s)
        elif a[0] == "d" and idx is not None and 0 <= idx < len(srcs):
            srcs.pop(idx)
        elif a == "y":
            save_raw(cfg_path, raw)
            io.print("  " + sync_calendar(load_config(cfg_path), store, now, force=True).replace("\n", "\n  "))


def enforce_menu(raw: dict, c: dict, io) -> None:
    """19) 強制モードの既定値。ここでは宣言できない（宣言は shl enforce on の対話だけ）。"""
    if isinstance(io, StdIO) and not sys.stdin.isatty():
        io.print(M.MENU_ENFORCE_NEED_TTY)
        return
    io.print(M.MENU_ENFORCE_INTRO1)
    io.print(M.MENU_ENFORCE_INTRO2)
    ec = raw.setdefault("enforce", {})
    a = _ask(io, M.MENU_ENFORCE_ACTION, {"lock": "1", "sleep": "2", "shutdown": "3"}.get(c["enforce"]["action"], c["enforce"]["action"]),
             lambda s: {"1": "lock", "2": "sleep", "3": "shutdown"}[s.strip()])
    if a in CAUTION_ACTIONS:
        io.print(M.MENU_ENFORCE_CAUTION.format(caution=M.ENFORCE_CAUTION[a]))
        if io.input(M.MENU_ENFORCE_STILL_OK).strip().lower() != "y":
            a = None
    if a:
        ec["action"] = a
    h = _ask(io, M.MENU_ENFORCE_HOURS, c["enforce"]["duration_hours"], _num_conv(0.5, 12))
    if h is not None:
        io.print(M.MENU_ENFORCE_HOURS_NOTE.format(hours=f"{h:g}", relock=c["enforce"]["relock_minutes"], max_runs=c["enforce"]["max_runs"]))
        if io.input(M.COMMON_ASK_OK).strip().lower() == "y":
            ec["duration_hours"] = h
    g = _ask(io, M.MENU_ENFORCE_GRACE, c["enforce"]["grace_minutes"], _num_conv(5, 60))
    if g is not None:
        ec["grace_minutes"] = g


SOUND_KIND_KEYS = ("default", "lastcall", "soft", "hard", "go", "go_last", "nag", "auto_okita", "confirm", "enforce")   # notify.sound のキー。この順がメニューの番号


def sound_menu(raw: dict, c: dict, io) -> None:
    """17) 通知音: 種類ごとに音声ファイル（WAV / MP3 など）のパス。空なら既定音。"""
    snd = raw.setdefault("notify", {}).setdefault("sound", {})
    while True:
        io.print(M.MENU_SOUND_HEAD)
        for i, k in enumerate(SOUND_KIND_KEYS, 1):
            io.print(M.MENU_SOUND_ITEM.format(i=f"{i:2d}", label=f"{M.MENU_SOUND_LABELS[k]:<10}", path=snd.get(k) or M.MENU_SOUND_DEFAULT))
        io.print(M.MENU_SOUND_VOL.format(volume=raw["notify"].get("sound_volume", c["notify"]["sound_volume"]),
                                         max_seconds=raw["notify"].get("sound_max_seconds", c["notify"]["sound_max_seconds"])))
        a = io.input(M.MENU_SOUND_PROMPT).strip().lower()
        if a == "":
            return
        if a.isdigit() and 1 <= int(a) <= len(SOUND_KIND_KEYS):
            k = SOUND_KIND_KEYS[int(a) - 1]
            v = io.input(M.MENU_SOUND_FILE.format(kind=k, cur=snd.get(k, ""))).strip().strip('"')
            if v == "-":
                snd.pop(k, None)
            elif v:
                snd[k] = v
        elif a == "v":
            v = _ask(io, M.MENU_SOUND_VOLUME, c["notify"]["sound_volume"], _num_conv(0, 1))
            if v is not None:
                raw["notify"]["sound_volume"] = v
        elif a == "m":
            v = _ask(io, M.MENU_SOUND_MAX, c["notify"]["sound_max_seconds"], _num_conv(1, 60))
            if v is not None:
                raw["notify"]["sound_max_seconds"] = v


def learned_menu(store: Store, io) -> None:
    while True:
        io.print(learned_text(store))
        a = io.input(M.MENU_LEARNED_PROMPT).strip().lower()
        if a == "a":
            k = io.input(M.MENU_LEARNED_KIND).strip()
            kind = {"1": "kitaku", "2": "neru", "3": "okita", "4": "none"}.get(k)
            ph = io.input(M.MENU_LEARNED_PHRASE).strip()
            if kind and ph:
                core = learn(store, ph, kind)
                io.print(M.MENU_LEARNED_OK.format(core=core) if core else M.MENU_LEARNED_SHORT)
        elif a == "r":
            ph = io.input(M.MENU_LEARNED_FORGET).strip()
            if ph:
                io.print("  " + unlearn(store, ph))
        else:
            return


def days_menu(cfg_path: Path, store: Store, now: datetime, io) -> None:
    today = now.date()
    while True:
        cfg = load_config(cfg_path)
        io.print(M.MENU_DAYS_HEAD)
        io.print(schedule_text(cfg, today))
        a = io.input(M.MENU_DAYS_PROMPT).strip().lower()
        if a not in ("o", "w", "r"):
            return
        d = _ask(io, M.MENU_DAYS_DATE, "", lambda s: parse_day(s, today))
        if d is None:
            continue
        note = io.input(M.MENU_DAYS_NOTE).strip() if a != "r" else ""
        kind = {"o": "off", "w": "work", "r": None}[a]   # 入力の文字 → days.json の種別
        days_set(store, d, kind, note)
        io.print(M.MENU_DAYS_SET.format(md=f"{d:%m/%d}", kind=M.COMMON_DAY_KIND_LABEL[kind]))
