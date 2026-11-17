"""テストベクタ（docs/test-vectors/*.json）を Python 版から作る。Python 版が「正」。

ネイティブ版（native/shiftlock.Core）はこのファイルを読んで、同じ入力から同じ結果になることを確かめる
（docs/native-app-plan.md §4.3）。実データは使わない。日付は固定なので、何回作っても同じ内容になる。

使い方（WSL、リポジトリ直下の .venv）:
    cd pc && ../.venv/bin/python tools/make_test_vectors.py          # 書き出す
    cd pc && ../.venv/bin/python tools/make_test_vectors.py --check  # 今のファイルと同じか確かめるだけ（違えば exit 1）
pc/tests/test_vectors.py が --check と同じことをするので、ロジックを変えたら作り直してコミットする。

plan-scenarios.json の形:
    scenarios[] = {
      name, about,
      config   shifts.json に書く内容（書いていないキーは既定値。既定値も移植の対象）
      days     例外日をまとめたもの（config.merged_days の結果 = cfg["_days"]）
      plans[]  = { date, workday, plan }           plan は乗務日でなければ null
      moments[] = { now, current, target, last_past }
          current    その日の状態（status）ごとの [フェーズ, 乗務日]。"" は記録なし。前後 3 日の全部に同じ状態を入れて判定する
          target     target_plan の乗務日（無ければ null）
          last_past  last_past_plan(lookback_days=7) の乗務日（無ければ null）
    }
日時は全部ローカル時刻（タイムゾーンなし）の ISO 形式。
"""
from __future__ import annotations

import json
import sys
from dataclasses import asdict
from datetime import datetime, timedelta, date as Date
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from shiftlock.config import DEFAULTS, deep_merge  # noqa: E402
from shiftlock import plan as P  # noqa: E402

OUT = ROOT.parent / "docs" / "test-vectors"
STATUSES = ["", "okita", "yasumi", "worked"]
LOOKBACK = 7
EVERY_DAY = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"]

# (名前, 説明, shifts.json の内容, 例外日, 基準日, 何日ぶん)
SCENARIOS: list[tuple[str, str, dict, dict, str, int]] = [
    ("defaults", "設定なし（全部既定値）", {}, {}, "2026-11-16", 4),
    ("no_sleep_inside", "寝られない時間帯（曜日）が就寝〜出発の間に入る",
     {"start_target": "14:00", "start_latest": "15:30",
      "no_sleep": [{"weekday": "tue", "from": "10:00", "to": "11:00", "label": "a"},
                   {"weekday": "Thursday", "from": "07:15", "to": "07:45"}]},
     {}, "2026-11-16", 4),
    ("no_sleep_midnight", "日をまたぐ時間帯・日付指定・出発にかぶる時間帯・複数",
     {"start_target": "06:00", "start_latest": "07:00", "prep_minutes": 60,
      "no_sleep": [{"weekday": "mon", "from": "23:00", "to": "01:00"},
                   {"date": "2026-11-18", "from": "03:30", "to": "05:30"},
                   {"date": "2026-11-18", "from": "00:10", "to": "00:40"},
                   {"weekday": "wed", "date": "2026-11-19", "from": "21:00", "to": "22:00"},
                   {"weekday": "fri", "from": "00:00", "to": "06:30"}]},
     {}, "2026-11-16", 5),
    ("exceptions", "休みの例外・出勤の例外・その日だけの時刻・旧形式の shifts",
     {"work_weekdays": ["mon", "wed", "fri"],
      "shifts": ["2026-11-17", {"date": "2026-11-19", "start_target": "18:00", "sleep_min_hours": 3},
                 {"date": "2026-11-21", "start_target": "10:00", "start_latest": "11:00"},
                 {"date": "2026-11-21", "start_target": "12:00", "sleep_target_hours": 6}]},
     {"off": {"2026-11-18": "有給", "2026-11-17": ""}, "work": {"2026-11-22": "出勤"},
      "times": {"2026-11-20": {"start_target": "21:15", "start_latest": "23:45", "sleep_target_hours": 6.5},
                "2026-11-21": {"start_latest": "13:30"}}},
     "2026-11-16", 7),
    ("clamps", "最終出庫が目標より早い（目標に合わせる）・最低睡眠が目標睡眠より長い（就寝目安に合わせる）",
     {"work_weekdays": EVERY_DAY, "start_target": "20:00", "start_latest": "19:00",
      "sleep_target_hours": 4, "sleep_min_hours": 6, "lastcall_minutes": 0},
     {}, "2026-11-16", 3),
    ("year_end", "年またぎ・小数の時間・毎日乗務",
     {"work_weekdays": EVERY_DAY, "start_target": "19:30", "start_latest": "21:00", "prep_minutes": 75,
      "sleep_target_hours": 5.5, "sleep_min_hours": 3.25, "lastcall_minutes": 25,
      "no_sleep": [{"date": "2026-12-31", "from": "12:00", "to": "12:20"}]},
     {"off": {"2027-01-01": "正月"}}, "2026-12-30", 5),
    ("overlap", "乗務の窓が前後の日と重なる（朝早い出庫・最終が遅い・睡眠が長い）",
     {"work_weekdays": EVERY_DAY, "start_target": "05:00", "start_latest": "23:30", "prep_minutes": 120,
      "sleep_target_hours": 9, "sleep_min_hours": 8, "lastcall_minutes": 90},
     {}, "2026-11-16", 3),
    ("edges", "境目: 始まりと終わりが同じ時間帯（丸 1 日）・空きが必要な睡眠とちょうど同じ",
     {"work_weekdays": ["tue", "wed", "fri"],
      "no_sleep": [{"date": "2026-11-17", "from": "15:00", "to": "15:00"},
                   {"date": "2026-11-18", "from": "12:00", "to": "13:30"},
                   {"date": "2026-11-20", "from": "13:00", "to": "14:30"},
                   {"date": "2026-11-20", "from": "08:00", "to": "09:00"}]},
     {}, "2026-11-16", 5),
]


def _cfg(raw: dict, days: dict) -> dict:
    cfg = deep_merge(DEFAULTS, raw)
    cfg["_days"] = {"off": days.get("off", {}), "work": days.get("work", {}), "times": days.get("times", {})}
    return cfg


def _plan(p: P.Plan | None):
    if p is None:
        return None
    return {k: (v.isoformat() if isinstance(v, datetime) else v) for k, v in asdict(p).items()}


def _moments(cfg: dict, base: Date, n: int) -> list[datetime]:
    """1 時間おき（毎時 07 分）と、各乗務の境目（ちょうど・1 分前）。"""
    out: set[datetime] = set()
    t = datetime(base.year, base.month, base.day, 0, 7)
    for i in range(24 * n):
        out.add(t + timedelta(hours=i))
    q = timedelta(minutes=15)
    for p in P.plans(cfg, base, back=1, fwd=n):
        for edge in (p.lastcall, p.soft_close, p.hard_close, p.depart_target - q, p.depart_latest - q,
                     p.start_latest, p.start_latest - timedelta(hours=24)):
            out.add(edge)
            out.add(edge - timedelta(minutes=1))
    return sorted(out)


def _scenario(name: str, about: str, raw: dict, days: dict, base_s: str, n: int) -> dict:
    cfg = _cfg(raw, days)
    base = Date.fromisoformat(base_s)
    plans = []
    for i in range(-2, n + 2):
        d = base + timedelta(days=i)
        work = P.is_workday(cfg, d)
        plans.append({"date": d.isoformat(), "workday": work, "plan": _plan(P.make_plan(cfg, d)) if work else None})
    moments = []
    for now in _moments(cfg, base, n):
        cur = {}
        for s in STATUSES:
            status = {(now.date() + timedelta(days=i)).isoformat(): {"status": s} for i in range(-3, 4)} if s else {}
            phase, p = P.current(cfg, status, now)
            cur[s] = [phase, p.day if p else None]
        tp = P.target_plan(cfg, now)
        lp = P.last_past_plan(cfg, now, LOOKBACK)
        moments.append({"now": now.isoformat(), "current": cur,
                        "target": tp.day if tp else None, "last_past": lp.day if lp else None})
    return {"name": name, "about": about, "config": raw, "days": days, "plans": plans, "moments": moments}


def build_plan() -> dict:
    return {"format": 1,
            "about": "乗務日・時刻の逆算・フェーズ判定のテストベクタ。pc/tools/make_test_vectors.py が作る（手で直さない）",
            "lookback_days": LOOKBACK,
            "scenarios": [_scenario(*s) for s in SCENARIOS]}


def _dump(doc: dict) -> str:
    """差分が読めるように、plans / moments は 1 件 1 行。"""
    head = {k: v for k, v in doc.items() if k != "scenarios"}
    lines = ["{"]
    for k, v in head.items():
        lines.append(f"  {json.dumps(k)}: {json.dumps(v, ensure_ascii=False)},")
    lines.append('  "scenarios": [')
    for si, sc in enumerate(doc["scenarios"]):
        lines.append("    {")
        for k in ("name", "about", "config", "days"):
            lines.append(f"      {json.dumps(k)}: {json.dumps(sc[k], ensure_ascii=False)},")
        for k in ("plans", "moments"):
            lines.append(f"      {json.dumps(k)}: [")
            rows = sc[k]
            for i, r in enumerate(rows):
                lines.append("        " + json.dumps(r, ensure_ascii=False) + ("," if i < len(rows) - 1 else ""))
            lines.append("      ]" + ("," if k == "plans" else ""))
        lines.append("    }" + ("," if si < len(doc["scenarios"]) - 1 else ""))
    lines += ["  ]", "}"]
    return "\n".join(lines) + "\n"


FILES = {"plan-scenarios.json": build_plan}


def render() -> dict[str, str]:
    return {name: _dump(fn()) for name, fn in FILES.items()}


def main(argv: list[str]) -> int:
    check = "--check" in argv
    bad = 0
    for name, text in render().items():
        path = OUT / name
        if check:
            if not path.exists() or path.read_text(encoding="utf-8") != text:
                print(f"stale: {path}")
                bad = 1
            continue
        OUT.mkdir(parents=True, exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        print(f"wrote {path}")
    return bad


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
