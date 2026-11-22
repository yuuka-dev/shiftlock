"""shiftlock: 乗務前の睡眠を守るための見張り役（Claude Code hook + Windows 通知の頭脳）

用語
  出発目安   = start_target - prep            （20:00 出庫なら 18:30）
  最終出発   = start_latest - prep            （22:30 出庫なら 21:00）
  就寝目安(soft) = 出発目安から sleep_target_hours ぶん「寝られる時間」を逆算した就寝リミット
  最低ライン = 最終出発から sleep_min_hours ぶん逆算した就寝リミット（ここを切ると Claude Code をロック）
  ※ no_sleep（訪問看護・ヘルパー等）の時間帯は「寝られる時間」から除外して逆算する

フェーズ（その乗務を「休む」「起きた」にしてない場合）
  lastcall : 就寝目安の lastcall_minutes 前〜就寝目安（表示は「就寝準備」）  長い処理は投げんといて
  soft     : 就寝目安〜最低ライン                警告のみ
  hard     : 最低ライン〜出庫最終               Claude Code の新規プロンプトを拒否（設定で無効化可）
「起きた」なら go（出発目安の15分前〜）/ go_last（最終出発の15分前〜）のリマインドだけ。

解除ワード（Claude Code のプロンプトにそれだけ打つ。Claude には送られへん）
  今日休む / 休む   … 直近の乗務を休みに
  やっぱ出る        … 休みを取り消し
  起きた / おはよう … 寝て起きたことにする（ロック解除、出発リマインドへ）
  状況 / 閉店       … 今日の予定を表示

依存: Python 3.8+（標準ライブラリのみ）。時刻はすべてローカル時刻（WSL の TZ が JST か要確認）。
"""
from __future__ import annotations

import shutil  # noqa: F401  テストが shutil.which を差し替えるのに shiftlock.shutil として参照する

from .config import (WEEKDAYS, DEFAULTS, deep_merge, load_raw, save_raw, calendar_sources, calendar_days, merged_days, apply_lang, load_config, to_local_path)
from .timeutil import (at, fmt, fmt_d, fmt_dur, WD_JA, parse_day)
from .store import (Store, set_mark, departures, TICK_MIN, log_append, log_read, first_log_date)
from .keywords import (KW_YASUMI, KW_UNDO, KW_OKITA, KW_KITAKU, KW_UNDO_FUZZY, KW_NERU, KW_STATUS, KW_SCHEDULE, KW_SHUPPATSU, RE_DAY, PAST_YASUMI, PAST_WORKED, RE_PAST, RE_YASUMI_PAID, RE_PAID, PAID_ON, PAID_OFF, RE_CLOCK, SLEEP_CLEAR)
from .console import (WD_IN, WD_OUT, StdIO)
from .plan import (no_sleep_intervals, latest_bedtime, Plan, is_workday, day_times, make_plan, plans, current, target_plan, phase_messages, last_past_plan)
from .usage import (touch_usage, due_nag, recent_long_gap, pc_gaps, game_idle_limit, pc_active, shift_away)
from .autowake import (slept_before, maybe_auto_okita)
from .intents import (norm_kw, LEARNED_KINDS, to_hira, norm_text, phrase_core, INTENT_PATTERNS, NEGATE, WEAK, REQUEST, AFFIRM, DENY, load_learned, learn, detect_intents, weak_intent, ASK_COOLDOWN, can_ask, set_pending, take_pending, clear_pending, learned_text, unlearn)
from .confirm import (last_asked_unconfirmed, unconfirmed_shifts, mark_confirm_asked, can_ask_confirm, confirm_since, pick_unconfirmed, home_activity_minutes, confirm_text, kakunin_text)
from .enforce import (STOP_FILE, ENFORCE_ACTIONS, CAUTION_ACTIONS, stop_active, enforce_state, set_enforce, enforce_terms, windows_confirm, enforce_declare, enforce_off, enforce_release, enforce_resume, enforce_suffix, enforce_status_text, enforce_due)
from .limit import (LIMIT_ACTIONS, check_steps, limit_stages, read_games, agent_seems_running)
from .notify import (NOTIFY_EVERY, SOUND_PRIORITY, resolve_sound, attach_sounds)
from .days import (days_set, schedule_text)
from .attendance import (absence_stats, absence_note)
from .records import (answer_pending, apply_intents, undo_fuzzy, do_kitaku, do_neru, do_okita, do_shuppatsu, resolve_past_day, paid_flag, late_report, cmd_past, cmd_paid, month_yasumi_count)
from .commands import (cmd_day, cmd_keyword, status_text)
from .phone import (PHONE_MARKS, read_phone_marks, import_phone_marks, mood_str, read_mood)
from .sleeplog import (SLEEP_PAIR_MAX_HOURS, SLEEP_REDO_HOURS, sleep_marks, sleep_sessions, parse_clock, record_sleep)
from .hook import (run_hook)
from .tick import (run_tick)
from .cli import (main)
from .report import (closing_period, build_report, report_name, write_period_reports, cleanup_reports)
from .report_html import (render_report)
from .calendar_sync import (parse_ics, sync_calendar)
from .maintenance import (cleanup, daily_maintenance)
from .settings_menu import (settings_menu, games_menu, calendar_menu, enforce_menu, SOUND_KIND_KEYS, sound_menu, learned_menu, days_menu)
from .apps import (APP_CATEGORIES, normalize_exe, app_add, app_remove, app_list, limit_add, limit_remove, limit_exclude, limit_include, limit_list)
from .install import (MARKER, HOOK_EVENTS, hook_command, merge_hooks, install)

__all__ = [
    "WEEKDAYS",
    "DEFAULTS",
    "deep_merge",
    "load_raw",
    "save_raw",
    "calendar_sources",
    "calendar_days",
    "merged_days",
    "apply_lang",
    "load_config",
    "to_local_path",
    "at",
    "fmt",
    "fmt_d",
    "fmt_dur",
    "WD_JA",
    "parse_day",
    "Store",
    "set_mark",
    "TICK_MIN",
    "log_append",
    "log_read",
    "first_log_date",
    "departures",
    "KW_YASUMI",
    "KW_UNDO",
    "KW_OKITA",
    "KW_KITAKU",
    "KW_UNDO_FUZZY",
    "KW_NERU",
    "KW_STATUS",
    "KW_SCHEDULE",
    "KW_SHUPPATSU",
    "RE_DAY",
    "PAST_YASUMI",
    "PAST_WORKED",
    "RE_PAST",
    "RE_YASUMI_PAID",
    "RE_PAID",
    "PAID_ON",
    "PAID_OFF",
    "RE_CLOCK",
    "SLEEP_CLEAR",
    "WD_IN",
    "WD_OUT",
    "StdIO",
    "no_sleep_intervals",
    "latest_bedtime",
    "Plan",
    "is_workday",
    "day_times",
    "make_plan",
    "plans",
    "current",
    "target_plan",
    "phase_messages",
    "last_past_plan",
    "touch_usage",
    "due_nag",
    "recent_long_gap",
    "pc_gaps",
    "game_idle_limit",
    "pc_active",
    "shift_away",
    "slept_before",
    "maybe_auto_okita",
    "norm_kw",
    "LEARNED_KINDS",
    "to_hira",
    "norm_text",
    "phrase_core",
    "INTENT_PATTERNS",
    "NEGATE",
    "WEAK",
    "REQUEST",
    "AFFIRM",
    "DENY",
    "load_learned",
    "learn",
    "detect_intents",
    "weak_intent",
    "ASK_COOLDOWN",
    "can_ask",
    "set_pending",
    "take_pending",
    "clear_pending",
    "learned_text",
    "unlearn",
    "last_asked_unconfirmed",
    "unconfirmed_shifts",
    "mark_confirm_asked",
    "can_ask_confirm",
    "confirm_since",
    "pick_unconfirmed",
    "home_activity_minutes",
    "confirm_text",
    "kakunin_text",
    "STOP_FILE",
    "ENFORCE_ACTIONS",
    "CAUTION_ACTIONS",
    "stop_active",
    "enforce_state",
    "set_enforce",
    "enforce_terms",
    "windows_confirm",
    "enforce_declare",
    "enforce_off",
    "enforce_release",
    "enforce_resume",
    "enforce_suffix",
    "enforce_status_text",
    "enforce_due",
    "LIMIT_ACTIONS",
    "check_steps",
    "limit_stages",
    "read_games",
    "agent_seems_running",
    "NOTIFY_EVERY",
    "SOUND_PRIORITY",
    "resolve_sound",
    "attach_sounds",
    "days_set",
    "schedule_text",
    "answer_pending",
    "apply_intents",
    "undo_fuzzy",
    "do_kitaku",
    "do_neru",
    "do_okita",
    "do_shuppatsu",
    "resolve_past_day",
    "absence_stats",
    "absence_note",
    "paid_flag",
    "late_report",
    "cmd_past",
    "cmd_paid",
    "month_yasumi_count",
    "cmd_day",
    "cmd_keyword",
    "status_text",
    "PHONE_MARKS",
    "read_phone_marks",
    "import_phone_marks",
    "mood_str",
    "read_mood",
    "SLEEP_PAIR_MAX_HOURS",
    "SLEEP_REDO_HOURS",
    "sleep_marks",
    "sleep_sessions",
    "parse_clock",
    "record_sleep",
    "run_hook",
    "run_tick",
    "main",
    "closing_period",
    "build_report",
    "report_name",
    "write_period_reports",
    "cleanup_reports",
    "render_report",
    "parse_ics",
    "sync_calendar",
    "cleanup",
    "daily_maintenance",
    "settings_menu",
    "games_menu",
    "calendar_menu",
    "enforce_menu",
    "SOUND_KIND_KEYS",
    "sound_menu",
    "learned_menu",
    "days_menu",
    "MARKER",
    "HOOK_EVENTS",
    "hook_command",
    "merge_hooks",
    "install",
    "APP_CATEGORIES",
    "normalize_exe",
    "app_add",
    "app_remove",
    "app_list",
    "limit_add",
    "limit_remove",
    "limit_exclude",
    "limit_include",
    "limit_list",
]
