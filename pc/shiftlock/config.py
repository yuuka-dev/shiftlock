"""設定（shifts.json）の既定値と読み書き、例外日（手動・カレンダー）のマージ、Windows パスの変換。"""
from __future__ import annotations

import json
import os
import sys
from datetime import datetime
from pathlib import Path

from . import i18n as M
from .store import _read


WEEKDAYS = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"]


DEFAULTS: dict = {
    "lang": "ja_kansai",             # 文面の言葉づかい（i18n.LANGS: ja / ja_kansai、ファイルがあれば inm）。設定メニュー 20
    "work_weekdays": ["mon", "tue", "wed", "thu", "fri", "sat"],
    "start_target": "20:00",
    "start_latest": "22:30",
    "prep_minutes": 90,
    "sleep_target_hours": 5,
    "sleep_min_hours": 4,
    "lastcall_minutes": 40,
    "block_claude_on_hard": True,
    "no_sleep": [],
    "usage": {"nag_after_minutes": 120, "nag_every_minutes": 30, "idle_reset_minutes": 20, "game_idle_minutes": 180},
    "notify": {"phase": "toast", "hard": "popup", "nag": "toast", "speak": False,
               "sound": {},                  # 種類ごとの音声ファイル（Windows のパス）。"default" で一括。空なら既定音
               "sound_volume": 0.8, "sound_max_seconds": 20},
    "game_processes": [],
    "mood_file": "",
    "shift_end_earliest": "07:00",
    "home_by_latest": "09:00",
    "auto_wake": True,                # True/"auto": PC無操作でも判定 / "neru": 『ねる』の記録があるときだけ / False: しない
    "auto_wake_hours": None,          # None = sleep_min_hours と同じ
    "steam_libraries": True,          # Steam の steamapps\\common を自動でゲーム扱い
    "game_dirs": [],                  # 追加のゲームフォルダ（この下の exe は奥深くまで全部ゲーム扱い）
    "game_exclude_dirs": [],          # 除外フォルダ
    "game_exclude": ["UnityCrashHandler*", "CrashReport*", "*crashpad*", "unins*", "vc_redist*",
                     "dxsetup*", "*Redist*", "steamerrorreporter*", "*Setup*"],
    "calendar": {"ics_url": "", "sources": [],      # sources: [{"name", "ics_url", "off_keywords"?, "work_keywords"?}]（上が優先）
                 "off_keywords": ["休み", "公休", "有給"],   # ソースに無いときの既定
                 "work_keywords": ["出勤"], "sync_minutes": 60},
    "retention": {"days_keep": 60, "log_keep_days": 180, "events_keep_days": 365},
    "report": {"closing_day": 20, "keep_months": 3},   # 締め日（期間は 締め日の翌日〜次の締め日。0 で月単位）、レポートの保存期間
    # 終わった乗務の確認: 乗務の翌日に 1 回だけ「出た？休んだ？」と聞く（ask / off）。okita_means_worked: 起きた記録があれば出たとみなして聞かない
    "confirm": {"mode": "ask", "lookback_days": 7, "home_activity_minutes": 60, "okita_means_worked": True},
    # 当日欠勤の目安: 直近 window_shifts 乗務のうち、有給の印が無い休みが warn_at 回以上なら注意（warn_at 0 で注意なし）
    "absence": {"window_shifts": 24, "warn_at": 5},
    # 強制モード（寝る前に PC を止める）の既定値。宣言は本人が shl enforce on を対話的に実行して y で同意したときだけ
    "enforce": {"action": "lock", "duration_hours": 2, "grace_minutes": 15, "relock_minutes": 5,
                "max_runs": 6, "boot_grace_minutes": 15, "skip_if_remote": True},
}


def deep_merge(base: dict, over: dict) -> dict:
    out = json.loads(json.dumps(base))
    for k, v in over.items():
        if isinstance(v, dict) and isinstance(out.get(k), dict):
            out[k] = deep_merge(out[k], v)
        else:
            out[k] = v
    return out


def load_raw(path: Path) -> dict:
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def save_raw(path: Path, raw: dict) -> None:
    raw = {k: v for k, v in raw.items() if not k.startswith("_")}
    tmp = path.with_name(path.name + ".tmp")
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:   # Windows の Python でも LF
        json.dump(raw, f, ensure_ascii=False, indent=2)
        f.write("\n")
    os.replace(tmp, path)


def calendar_sources(cal: dict) -> list[dict]:
    """同期するカレンダーの一覧。新形式 sources に、旧形式 ics_url（1 本）を先頭として合流する。
    キーワードはソースに無ければ calendar 直下の既定。同じ URL は 1 つにまとめる。
    ソース名「カレンダー」は calendar.json のキーとして保存される値なので i18n には置かない。"""
    raw = []
    if (cal.get("ics_url") or "").strip():
        raw.append({"name": "カレンダー", "ics_url": cal["ics_url"]})
    raw += [s for s in cal.get("sources", []) if isinstance(s, dict)]
    out: list[dict] = []
    seen: set[str] = set()
    for i, s in enumerate(raw):
        url = (s.get("ics_url") or "").strip()
        if not url or url in seen:
            continue
        seen.add(url)
        name = (s.get("name") or "").strip() or f"カレンダー{i + 1}"
        while name in {o["name"] for o in out}:
            name += "'"
        out.append({"name": name, "ics_url": url,
                    "off_keywords": list(s.get("off_keywords", cal.get("off_keywords", []))),
                    "work_keywords": list(s.get("work_keywords", cal.get("work_keywords", [])))})
    return out


def calendar_days(cal: dict) -> tuple[dict, dict]:
    """calendar.json から例外日を取り出す。新形式は sources の順に先勝ち（同じ日は上のソース）。旧形式は off/work 直下。"""
    if "sources" in cal:
        off: dict = {}
        work: dict = {}
        for src in cal["sources"].values():
            for d, v in src.get("off", {}).items():
                if d not in off and d not in work:
                    off[d] = v
            for d, v in src.get("work", {}).items():
                if d not in off and d not in work:
                    work[d] = v
        return off, work
    return dict(cal.get("off", {})), dict(cal.get("work", {}))


def merged_days(d: Path) -> dict:
    """手動（days.json）とカレンダー（calendar.json）の例外日をまとめる。手動が優先。"""
    man = _read(d / "days.json", {})
    coff, cwork = calendar_days(_read(d / "calendar.json", {}))
    off = {**coff, **man.get("off", {})}
    work = {**cwork, **man.get("work", {})}
    for k in man.get("off", {}):
        work.pop(k, None)
    for k in man.get("work", {}):
        off.pop(k, None)
    return {"off": off, "work": work, "times": man.get("times", {})}


def apply_lang(path: Path) -> str:
    """shifts.json の lang だけ読んで文面の言語を選ぶ（load_config を通らない help / settings 用）。読めなければ既定。"""
    try:
        return M.set_lang(load_raw(path).get("lang"))
    except Exception:
        return M.set_lang(None)


def load_config(path: Path) -> dict:
    cfg = deep_merge(DEFAULTS, load_raw(path))
    cfg["lang"] = M.set_lang(cfg.get("lang"))   # 知らない値は既定に戻す（任意機能の設定ミスで本線を止めない）。以降のエラー文もこの言語
    for k in ("start_target", "start_latest", "shift_end_earliest"):
        datetime.strptime(cfg[k], "%H:%M")   # 形式チェック（壊れてたらここで落ちる）
    bad = [w for w in cfg["work_weekdays"] if w not in WEEKDAYS]
    if bad:
        raise ValueError(M.CONFIG_BAD_WEEKDAY.format(bad=bad))
    for k in ("usage", "notify", "calendar", "retention", "report", "confirm", "enforce", "absence"):
        if not isinstance(cfg.get(k), dict):
            raise ValueError(M.CONFIG_NOT_DICT.format(key=k))
    if not isinstance(cfg["calendar"].get("sources", []), list):
        raise ValueError(M.CONFIG_SOURCES_NOT_LIST)
    if not isinstance(cfg["notify"].get("sound", {}), dict):
        cfg["notify"]["sound"] = {}   # 任意機能の設定ミスで本線を止めない
    cfg["_dir"] = str(Path(path).resolve().parent)
    cfg["_days"] = merged_days(Path(cfg["_dir"]))
    return cfg


def to_local_path(p: str) -> Path:
    """'C:\\Users\\x\\...' を WSL の /mnt/c/... に。POSIX パスはそのまま。
    Windows の Python で動いているとき（guard.ps1 / make-report.ps1 から。issue #22）は C:\\ のまま使う。"""
    if sys.platform == "win32":
        return Path(p)
    if len(p) >= 3 and p[1] == ":" and p[2] in "\\/":
        return Path("/mnt/" + p[0].lower() + "/" + p[3:].replace("\\", "/"))
    return Path(p)
