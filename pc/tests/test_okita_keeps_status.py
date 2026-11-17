"""休み／出発済みにした乗務は『起きた』で status を上書きしない（issue #29）。
2026-11-12 に「今日休む」→「ねる」→「起きた」で status が okita になり、レポートで出勤扱いになった。
- do_okita: status が yasumi / worked なら status を変えず、睡眠の記録（events.log の okita 行）だけ残す
- 『今のなし』で睡眠の記録だけ戻る
- 事後報告の休み（late）は、そのあとの okita 行で yasumi_late の数えから外れない"""
from __future__ import annotations

import json
import sys
import tempfile
from datetime import datetime, timedelta
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
import shiftlock as sl

SRC = ROOT / "shifts.example.json"


def dt(s): return datetime.strptime(s, "%Y-%m-%d %H:%M")
def UPS(prompt): return json.dumps({"hook_event_name": "UserPromptSubmit", "prompt": prompt})


def _pc_log(cp, start, end, idle=3600):
    """pc ログを 5 分おきに書く（レポートは記録開始日より前の日を乗務扱いにしない）"""
    d = cp.parent / "log"; d.mkdir(exist_ok=True)
    t, e = dt(start), dt(end)
    while t <= e:
        with open(d / f"pc-{t:%Y-%m}.jsonl", "a", encoding="utf-8") as f:
            f.write(json.dumps({"t": t.isoformat(timespec="seconds"), "idle": idle, "game": 0}) + "\n")
        t += timedelta(minutes=5)


def env():
    t = Path(tempfile.mkdtemp())
    (t / "shifts.json").write_text(SRC.read_text(encoding="utf-8"), encoding="utf-8")
    (t / "days.json").write_text("{}")
    (t / "state.json").write_text(json.dumps({"confirm_since": "2026-11-01"}))
    _pc_log(t / "shifts.json", "2026-11-01 00:00", "2026-11-03 12:00")
    return t / "shifts.json", sl.load_config(t / "shifts.json")


def _status(cp, day):
    return json.loads((cp.parent / "state.json").read_text(encoding="utf-8"))["shifts"][day]


def test_okita_keeps_yasumi_and_logs_sleep():
    cp, cfg = env()
    sl.run_hook(cp, UPS("今日休む"), dt("2026-11-02 10:00"))
    sl.run_hook(cp, UPS("ねる"), dt("2026-11-02 11:00"))
    c, o, e = sl.run_hook(cp, UPS("起きた"), dt("2026-11-02 15:00"))
    assert c == 2 and "11:00〜15:00" in e and "休みのまま" in e and "ロック解除" not in e
    st = _status(cp, "2026-11-02")
    assert st["status"] == "yasumi" and st["sleep_at"] == "2026-11-02T11:00"
    assert "2026-11-02T15:00\t2026-11-02\tokita\tneru 11:00-15:00" in (cp.parent / "events.log").read_text(encoding="utf-8")
    assert sl.run_hook(cp, UPS("x"), dt("2026-11-02 18:20"))[0] == 0        # 休みのままなので出発リマインドも出ない
    data = sl.build_report(cfg, sl.Store(cp), dt("2026-11-03 12:00"), days=3)
    r = {x["date"]: x for x in data["rows"]}["2026-11-02"]
    assert r["shift"] == "yasumi" and r["neru"] == ["11:00"] and r["okita"] == ["15:00"] and r["sleep_min"] == 240
    assert data["summary"]["yasumi"] == 1 and data["summary"]["worked"] == 0


def test_okita_keeps_worked():
    cp, cfg = env()
    sl.run_hook(cp, UPS("ねる"), dt("2026-11-02 11:00"))
    sl.run_hook(cp, UPS("行ってきます"), dt("2026-11-02 17:00"))
    c, o, e = sl.run_hook(cp, UPS("起きた"), dt("2026-11-02 17:30"))
    assert c == 2 and "出勤のまま" in e
    assert _status(cp, "2026-11-02")["status"] == "worked"


def test_undo_after_kept_okita_removes_only_sleep_mark():
    cp, cfg = env()
    sl.run_hook(cp, UPS("今日休む"), dt("2026-11-02 10:00"))
    sl.run_hook(cp, UPS("ねる"), dt("2026-11-02 11:00"))
    sl.run_hook(cp, UPS("おはよーやっと起きたぁ"), dt("2026-11-02 15:00"))   # 話し言葉（apply_intents）→『今のなし』が効く
    c, o, e = sl.run_hook(cp, UPS("今のなし"), dt("2026-11-02 15:05"))
    assert c == 2 and "起床" in e
    assert _status(cp, "2026-11-02")["status"] == "yasumi"
    data = sl.build_report(cfg, sl.Store(cp), dt("2026-11-03 12:00"), days=3)
    r = {x["date"]: x for x in data["rows"]}["2026-11-02"]
    assert r["shift"] == "yasumi" and r["neru"] == ["11:00"] and r["okita"] == []


def test_late_yasumi_stays_late_after_backdated_okita():
    cp, cfg = env()
    store = sl.Store(cp)
    sl.late_report(cfg, store, dt("2026-11-03 10:00"), "yasumi", "11/2")
    sl.apply_intents(["neru"], cfg, store, dt("2026-11-03 10:01"), when=dt("2026-11-02 11:00"))
    msg = sl.apply_intents(["okita"], cfg, store, dt("2026-11-03 10:02"), when=dt("2026-11-02 15:00"))
    assert "11:00〜15:00" in msg and "休みのまま" in msg
    assert _status(cp, "2026-11-02")["status"] == "yasumi"
    data = sl.build_report(cfg, store, dt("2026-11-03 12:00"), days=3)
    r = {x["date"]: x for x in data["rows"]}["2026-11-02"]
    assert r["shift"] == "yasumi" and r["okita"] == ["15:00"]
    assert data["summary"]["yasumi_late"] == 1 and data["summary"]["yasumi"] == 1
