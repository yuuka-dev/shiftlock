"""レポートの期間（締め日までの行・締め日オフ）と、当日欠勤の目安（有給の印・直近 N 乗務で K 回）。issue #18。"""
from __future__ import annotations

import json
import sys
import tempfile
from datetime import datetime, date as Date
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
import shiftlock as sl

SRC = ROOT / "shifts.example.json"


def dt(s): return datetime.strptime(s, "%Y-%m-%d %H:%M")
def D(s): return Date.fromisoformat(s)
def UPS(prompt): return json.dumps({"hook_event_name": "UserPromptSubmit", "prompt": prompt})


def env(days=None, **over):
    t = Path(tempfile.mkdtemp())
    cfg = json.loads(SRC.read_text(encoding="utf-8")); cfg.update(over)
    (t / "shifts.json").write_text(json.dumps(cfg, ensure_ascii=False), encoding="utf-8")
    (t / "days.json").write_text(json.dumps(days or {}, ensure_ascii=False), encoding="utf-8")
    return t / "shifts.json", sl.load_config(t / "shifts.json")


def _st(cp):
    p = cp.parent / "state.json"
    return json.loads(p.read_text(encoding="utf-8")).get("shifts", {}) if p.exists() else {}


def _events(cp):
    p = cp.parent / "events.log"
    return [ln.split("\t") for ln in p.read_text(encoding="utf-8").splitlines()] if p.exists() else []


def _yasumi(store, days, paid=()):
    """乗務日を休み（当日欠勤）にする。paid の日は有給の印も付ける"""
    for d in days:
        store.set_shift_status(d, "yasumi", dt(d + " 18:00"))
        if d in paid:
            store.set_paid(d, True, dt(d + " 18:00"), "test")


class FakeIO:
    def __init__(self, answers): self.a = list(answers); self.out = []
    def input(self, p): self.out.append(p); return self.a.pop(0)
    def print(self, *x): self.out.append(" ".join(map(str, x)))


# ---------------------------------------------------------------- 締め期間

def test_closing_period_off_is_calendar_month():
    assert sl.closing_period(D("2026-02-10"), 0) == (D("2026-02-01"), D("2026-02-28"))
    assert sl.closing_period(D("2026-11-30"), 0) == (D("2026-11-01"), D("2026-11-30"))
    assert sl.closing_period(D("2026-11-01"), None) == (D("2026-11-01"), D("2026-11-30"))
    assert sl.closing_period(D("2026-11-01"), 20) == (D("2026-10-21"), D("2026-11-20"))   # 従来どおり


def test_report_period_rows_run_to_closing_day():
    cp, cfg = env()
    store = sl.Store(cp)
    now = dt("2026-11-03 09:00")
    data = sl.build_report(cfg, store, now, period=(D("2026-10-21"), D("2026-11-20")))
    assert data["start"] == "2026-10-21" and data["end"] == "2026-11-20" and len(data["rows"]) == 31
    assert data["rows"][-1]["date"] == "2026-11-20" and data["closed"] is False and data["today"] == "2026-11-03"
    future = [r for r in data["rows"] if r["date"] > "2026-11-03"]
    assert future and all(not any(r["cov"]) and r["flags"] == [] for r in future)
    # 未来の乗務日は「予定」。乗務回数の母数（planned）には入らない
    assert all(r["shift"] in ("planned", None) for r in future)
    assert data["summary"]["planned"] == sum(1 for r in data["rows"] if r["shift"] in ("worked", "unconfirmed", "yasumi"))
    html = sl.render_report(data)
    assert "2026-10-21 〜 2026-11-20" in html and "11/20 締めの途中経過" in html and 'class="fu"' in html
    # 締め日を過ぎたら確定
    data = sl.build_report(cfg, store, dt("2026-11-21 09:00"), period=(D("2026-10-21"), D("2026-11-20")))
    assert data["closed"] is True and "11/20 締め）" in sl.render_report(data)


def test_period_reports_with_closing_off():
    cp, cfg = env(report={"closing_day": 0, "keep_months": 3})
    files = sl.write_period_reports(cfg, sl.Store(cp), dt("2026-11-03 09:00"))
    assert [f.name for f in files] == ["report_2026-10-01_2026-10-31.html", "report_2026-11-01_2026-11-30.html"]
    assert "2026-11-01 〜 2026-11-30" in files[1].read_text(encoding="utf-8")


def test_menu_16_renders_in_every_language():
    """MENU_16 の {closing} に「20日締め」「月単位」の文を入れるので、どの言語ファイルでも「日締め」が二重にならない"""
    import shiftlock.i18n as M
    try:
        for lang in M.LANGS:
            for closing in (20, 0):
                cp, _ = env(lang=lang, report={"closing_day": closing, "keep_months": 3})
                io = FakeIO(["q"])
                sl.settings_menu(cp, dt("2026-11-01 09:00"), io)
                line = next(x for x in io.out if x.startswith("16)"))
                assert "日締め日締め" not in line and ("20" in line if closing else "月" in line), (lang, line)
    finally:
        M.set_lang(M.DEFAULT_LANG)


def test_report_mood_tile_counts_only_past_days():
    cp, cfg = env()
    data = sl.build_report(cfg, sl.Store(cp), dt("2026-11-03 09:00"), period=(D("2026-10-21"), D("2026-11-20")))
    assert data["mood_enabled"]
    html = sl.render_report(data)
    assert "/ 14 日" in html and "/ 31 日" not in html   # 10/21〜11/03 の 14 日が分母（未来の 17 日は入れない）


def test_absence_config_tolerates_bad_values():
    cp, cfg = env(absence={"window_shifts": "abc", "warn_at": None})
    s = sl.absence_stats(cfg, sl.Store(cp), dt("2026-11-03 10:00"))
    assert s["window"] == 24 and s["limit"] == 5


def test_absence_config_out_of_range_falls_back():
    """設定メニューと同じ範囲（window_shifts 1..365 / warn_at 0..100）の外や inf・nan は既定に戻す（落ちない・黙って無効化しない）"""
    cp, cfg = env(absence={"window_shifts": 0, "warn_at": -1})
    now = dt("2026-11-03 10:00")
    s = sl.absence_stats(cfg, sl.Store(cp), now)
    assert s["window"] == 24 and s["limit"] == 5
    cfg["absence"] = {"window_shifts": 1000, "warn_at": 101}
    s = sl.absence_stats(cfg, sl.Store(cp), now)
    assert s["window"] == 24 and s["limit"] == 5
    cfg["absence"] = {"window_shifts": float("inf"), "warn_at": float("nan")}
    s = sl.absence_stats(cfg, sl.Store(cp), now)
    assert s["window"] == 24 and s["limit"] == 5
    cfg["absence"] = {"window_shifts": 365, "warn_at": 0}                     # 境界はそのまま有効
    s = sl.absence_stats(cfg, sl.Store(cp), now)
    assert s["window"] == 365 and s["limit"] == 0


def test_paid_keyword_targets_same_shift_as_plain_word():
    """『休む 有給』『今日休む 有給』は素の『休む』『今日休む』と同じ乗務が対象（出庫最終後の『今日休む』は今日の乗務）"""
    cp, _ = env()
    c, o, e = sl.run_hook(cp, UPS("今日休む 有給"), dt("2026-11-02 23:00"))     # 出庫最終（22:30）後 → 今日（11/2）の事後報告
    assert c == 2 and _st(cp)["2026-11-02"]["status"] == "yasumi" and _st(cp)["2026-11-02"]["paid"] is True
    assert "2026-11-03" not in _st(cp)
    cp, _ = env()
    c, o, e = sl.run_hook(cp, UPS("休む 有給"), dt("2026-11-02 23:00"))         # 素の『休む』と同じく次の乗務（11/3）
    assert c == 2 and _st(cp)["2026-11-03"]["status"] == "yasumi" and _st(cp)["2026-11-03"]["paid"] is True
    # 日付なしの『休んだ 有給』は直近に終わった乗務
    cp, _ = env()
    c, o, e = sl.run_hook(cp, UPS("休んだ 有給"), dt("2026-11-03 10:00"))
    assert c == 2 and _st(cp)["2026-11-02"]["status"] == "yasumi" and _st(cp)["2026-11-02"]["paid"] is True
    assert sl.run_hook(cp, UPS("休んだ ので明日の予定を立てて"), dt("2026-11-03 10:05"))[0] == 0   # メモ付きは今までどおり素通り


def test_settings_menu_closing_off_and_absence():
    cp, _ = env()
    io = FakeIO(["16", "0", "", "21", "30", "6", "s"])
    assert sl.settings_menu(cp, dt("2026-11-01 09:00"), io) is True
    raw = json.loads(cp.read_text(encoding="utf-8"))
    assert raw["report"]["closing_day"] == 0
    assert raw["absence"] == {"window_shifts": 30, "warn_at": 6}
    assert any("16)" in x and "月単位" in x for x in io.out)   # 保存後の表示（q の前にもう一周しない）か、表示文に月単位がある
    assert any("21)" in x and "24" in x and "5" in x for x in io.out)


# ---------------------------------------------------------------- 有給の印

def test_yasumi_with_paid_keyword():
    cp, _ = env()
    c, o, e = sl.run_hook(cp, UPS("今日休む 有給"), dt("2026-11-02 17:00"))
    st = _st(cp)["2026-11-02"]
    assert c == 2 and st["status"] == "yasumi" and st["paid"] is True and "有給" in e
    assert any(ev[2] == "paid" and ev[1] == "2026-11-02" and ev[3].startswith("on") for ev in _events(cp))
    # 『やっぱ出る』で休みを消すと有給の印も消える
    sl.run_hook(cp, UPS("やっぱ出る"), dt("2026-11-02 17:10"))
    assert "paid" not in _st(cp).get("2026-11-02", {})
    # 括弧つきでも
    sl.run_hook(cp, UPS("休む（有休）"), dt("2026-11-02 17:20"))
    assert _st(cp)["2026-11-02"].get("paid") is True


def test_late_yasumi_with_paid_and_mark_after():
    cp, _ = env()
    now = dt("2026-11-03 10:00")
    c, o, e = sl.run_hook(cp, UPS("11/2 休んだ 有給"), now)
    assert c == 2 and _st(cp)["2026-11-02"]["status"] == "yasumi" and _st(cp)["2026-11-02"]["paid"] is True
    # 後から外す・付ける
    c, o, e = sl.run_hook(cp, UPS("11/2 有給なし"), now)
    assert c == 2 and "paid" not in _st(cp)["2026-11-02"] and "11/02" in e
    assert any(ev[2] == "paid" and ev[3].startswith("off") for ev in _events(cp))
    c, o, e = sl.run_hook(cp, UPS("11/2 有給"), now)
    assert c == 2 and _st(cp)["2026-11-02"]["paid"] is True
    # 休みの記録が無い日には付かない
    c, o, e = sl.run_hook(cp, UPS("10/31 有給"), now)
    assert c == 2 and "休みの記録" in e and "paid" not in _st(cp).get("2026-10-31", {})
    # 日付なしの「有給」は普通のプロンプト
    assert sl.run_hook(cp, UPS("有給"), now)[0] == 0
    assert sl.run_hook(cp, UPS("有給の残りを計算して"), now)[0] == 0


def test_cli_yasumi_paid(monkeypatch):
    class FakeDT(datetime):
        @classmethod
        def now(cls, tz=None): return dt("2026-11-03 10:00")
    monkeypatch.setattr(sl.cli, "datetime", FakeDT)   # cli.main は datetime.now() を使う
    cp, _ = env()
    assert sl.main(["yasumi", "有給", "--config", str(cp)]) == 0           # 直近の乗務（11/3）を有給で休み
    assert _st(cp)["2026-11-03"]["status"] == "yasumi" and _st(cp)["2026-11-03"]["paid"] is True
    cp, _ = env()
    assert sl.main(["yasumi", "11/2", "有給", "--config", str(cp)]) == 0
    assert _st(cp)["2026-11-02"]["status"] == "yasumi" and _st(cp)["2026-11-02"]["paid"] is True
    assert sl.main(["yasumi", "11/2", "有給なし", "--config", str(cp)]) == 0
    assert "paid" not in _st(cp)["2026-11-02"]


# ---------------------------------------------------------------- 当日欠勤の目安

def test_absence_stats_window_and_paid():
    cp, cfg = env()
    store = sl.Store(cp)
    now = dt("2026-11-03 10:00")
    # 10/5〜11/3 の月〜土 = 26 乗務日。古い順に 5 日休む（うち 1 日は有給）
    _yasumi(store, ["2026-10-05", "2026-10-06", "2026-10-07", "2026-10-08", "2026-10-09"], paid=["2026-10-09"])
    s = sl.absence_stats(cfg, store, now, D("2026-11-03"))
    assert s["window"] == 24 and s["limit"] == 5 and s["shifts"] == 24
    assert s["from"] == "2026-10-07" and s["to"] == "2026-11-03"          # 直近 24 乗務なので 10/5・10/6 は窓の外
    assert s["absent"] == 2 and s["paid"] == 1 and s["over"] is False
    _yasumi(store, ["2026-10-12", "2026-10-13", "2026-10-14"])
    s = sl.absence_stats(cfg, store, now, D("2026-11-03"))
    assert s["absent"] == 5 and s["over"] is True
    store.set_paid("2026-10-12", True, now, "test")
    assert sl.absence_stats(cfg, store, now, D("2026-11-03"))["over"] is False
    # 目安 0 = 警告なし
    cfg["absence"]["warn_at"] = 0
    assert sl.absence_stats(cfg, store, now, D("2026-11-03"))["over"] is False


def test_yasumi_warns_when_over_limit():
    cp, cfg = env()
    store = sl.Store(cp)
    _yasumi(store, ["2026-10-26", "2026-10-27", "2026-10-28", "2026-10-29"])
    c, o, e = sl.run_hook(cp, UPS("今日休む"), dt("2026-11-02 17:00"))     # 5 回目
    assert c == 2 and "目安" in e and "5 回" in e
    cp, cfg = env()
    store = sl.Store(cp)
    _yasumi(store, ["2026-10-26", "2026-10-27", "2026-10-28"])
    c, o, e = sl.run_hook(cp, UPS("今日休む"), dt("2026-11-02 17:00"))     # 4 回目 → 警告なし
    assert c == 2 and "目安" not in e
    c, o, e = sl.run_hook(cp, UPS("10/31 休んだ"), dt("2026-11-02 17:10"))   # 事後報告で 5 回目
    assert c == 2 and "目安" in e
    assert "当日欠勤" in sl.status_text(sl.load_config(cp), store, dt("2026-11-02 17:20"))


def test_report_summary_absence():
    cp, cfg = env()
    store = sl.Store(cp)
    sl.log_append(store, "pc", dt("2026-10-01 00:00"), {"idle": 0, "game": 0})   # 記録開始日（これより前の乗務は数えない）
    _yasumi(store, ["2026-10-26", "2026-10-27", "2026-10-28", "2026-10-29", "2026-10-30"], paid=["2026-10-30"])
    now = dt("2026-11-03 09:00")
    data = sl.build_report(cfg, store, now, period=(D("2026-10-21"), D("2026-11-20")))
    sm = data["summary"]
    assert sm["yasumi"] == 5 and sm["absent"] == 4 and sm["absent_paid"] == 1
    assert sm["absence"]["absent"] == 4 and sm["absence"]["over"] is False
    html = sl.render_report(data)
    assert "当日欠勤" in html and "warn" not in html.split("<table")[0].split('class="tiles"')[1]
    store.set_paid("2026-10-30", False, now, "test")
    data = sl.build_report(cfg, store, now, period=(D("2026-10-21"), D("2026-11-20")))
    assert data["summary"]["absence"]["over"] is True
    assert 'tile warn' in sl.render_report(data)
