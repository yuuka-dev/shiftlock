"""レポートの集計: log/app-YYYY-MM.jsonl（Agent の稼働計測）をカテゴリ別の分にする．"""
import json
import sys
from datetime import datetime, timedelta
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
import shiftlock as sl
from shiftlock import Store, build_report

VECTOR = Path(__file__).resolve().parents[2] / "docs" / "test-vectors" / "app-log.jsonl"
NOW = datetime(2026, 12, 8, 12, 0)


def _line(t, apps, span=300):
    return json.dumps({"t": t, "span": span, "apps": apps, "other": 0})


def _app(cat, act):
    return {"cat": cat, "act": act, "cpu": 1.0}


def _setup(tmp_path, app=None, pc=None, weekdays=()):
    (tmp_path / "shifts.json").write_text(
        json.dumps({"lang": "ja", "work_weekdays": list(weekdays)}), encoding="utf-8")
    (tmp_path / "log").mkdir(exist_ok=True)
    if app is not None:
        (tmp_path / "log" / "app-2026-12.jsonl").write_text(app, encoding="utf-8")
    if pc is not None:
        (tmp_path / "log" / "pc-2026-12.jsonl").write_text(pc, encoding="utf-8")
    return tmp_path


def _report(tmp_path, app=None, pc=None, weekdays=()):
    p = _setup(tmp_path, app, pc, weekdays)
    cfg = sl.load_config(p / "shifts.json")
    return build_report(cfg, Store(p / "shifts.json"), NOW, days=2)


def _pc(*times, game=1):
    return "".join(json.dumps({"t": t, "idle": 10, "game": game}) + "\n" for t in times)


def test_shared_vector_totals(tmp_path):
    r = _report(tmp_path, app=VECTOR.read_text(encoding="utf-8"))["rows"][0]
    assert (r["dev"][21], r["game"][21], r["adobe"][21]) == (5, 5, 4)
    assert (r["dev_total"], r["game_total"], r["adobe_total"]) == (5, 5, 4)
    assert sum(r["dev"]) == 5 and sum(r["adobe"]) == 4      # ほかの時は 0


def test_seconds_are_summed_not_lines(tmp_path):
    # 21:05 と 21:10 の 2 行，code.exe の act が 100 と 110 → 210 秒 → 4 分（3.5 の四捨五入）
    app = (_line("2026-12-07T21:05:00", {"code.exe": _app("dev", 100)}) + "\n"
           + _line("2026-12-07T21:10:00", {"code.exe": _app("dev", 110)}) + "\n")
    r = _report(tmp_path, app=app)["rows"][0]
    assert r["dev"][21] == 4 and r["dev_total"] == 4


def test_bucket_is_mid_of_span(tmp_path):
    # 22:00 の行（span 300 → 中央 21:57:30）は 21 時のマスに 5 分．22 時は 0
    app = _line("2026-12-07T22:00:00", {"code.exe": _app("dev", 300)}) + "\n"
    r = _report(tmp_path, app=app)["rows"][0]
    assert r["dev"][21] == 5 and r["dev"][22] == 0


def test_hour_is_capped_at_60(tmp_path):
    app = _line("2026-12-07T21:30:00", {"code.exe": _app("dev", 4000)}, span=1800) + "\n"
    r = _report(tmp_path, app=app)["rows"][0]
    assert r["dev"][21] == 60


def test_cells_without_meter_keep_pc_game(tmp_path):
    # pc: 20 時台に 6 行，21 時台にも 4 行（game 1）．app: 共有の見本
    pc = _pc(*[f"2026-12-07T20:{m:02d}:00" for m in range(5, 35, 5)],
             *[f"2026-12-07T21:{m:02d}:00" for m in range(5, 25, 5)])
    r = _report(tmp_path, app=VECTOR.read_text(encoding="utf-8"), pc=pc)["rows"][0]
    assert r["game"][20] == 30      # 計測なしのマスは pc から
    assert r["game"][21] == 5       # 計測ありのマスは計測から（pc の 20 分は足さない）
    assert r["game_total"] == 35


def test_no_app_log_is_unchanged(tmp_path):
    pc = _pc(*[f"2026-12-07T20:{m:02d}:00" for m in range(5, 35, 5)])
    r = _report(tmp_path, pc=pc)["rows"][0]
    assert r["game"][20] == 30 and r["game_total"] == 30
    assert r["dev"] == [0] * 24 and r["adobe"] == [0] * 24
    assert r["dev_total"] == 0 and r["adobe_total"] == 0


def test_away_lines_are_not_counted(tmp_path):
    # 月曜（12/07）を 22:00 に「行ってきます」済みにする．外出中（22:30）の行は秒を足さない
    pc = _pc("2026-12-07T09:00:00", game=0)
    app = _line("2026-12-07T22:30:00", {"code.exe": _app("dev", 300)}) + "\n"
    p = _setup(tmp_path, app=app, pc=pc, weekdays=["mon"])
    cfg = sl.load_config(p / "shifts.json")
    store = Store(p / "shifts.json")
    store.set_shift_status("2026-12-07", "worked", datetime(2026, 12, 7, 22, 0), "shuppatsu")
    r = build_report(cfg, store, NOW, days=2)["rows"][0]
    assert r["away"][22] and r["shift"] == "worked"   # 前提: 外出中になっている
    assert r["dev"][22] == 0 and r["dev_total"] == 0


def test_broken_lines_are_skipped(tmp_path):
    good = _line("2026-12-07T21:05:00", {"code.exe": _app("dev", 300)})
    lines = [
        json.dumps({"t": "2026-12-07T21:05:00", "apps": {"code.exe": _app("dev", 300)}}),          # span 無し
        json.dumps({"t": "2026-12-07T21:05:00", "span": "x", "apps": {"code.exe": _app("dev", 300)}}),
        json.dumps({"t": "2026-12-07T21:05:00", "span": 300, "apps": []}),
        _line("2026-12-07T21:05:00", {"code.exe": {"cat": "dev", "act": "9"}}),
        _line("2026-12-07T21:05:00", {"x.exe": _app("video", 300)}),
        "{not json",
        good,
    ]
    r = _report(tmp_path, app="\n".join(lines) + "\n")["rows"][0]
    assert r["dev"][21] == 5
    assert sum(r["game"]) == 0 and sum(r["adobe"]) == 0


def test_unhashable_and_non_finite_values_do_not_crash(tmp_path):
    # cat が list／dict，span が Infinity・1e30・NaN，act が Infinity・NaN の行（json.loads は Infinity・NaN を通す）
    t = "2026-12-07T21:05:00"
    raw = [
        '{"t": "%s", "span": 300, "apps": {"a.exe": {"cat": ["dev"], "act": 1}}}' % t,
        '{"t": "%s", "span": 300, "apps": {"a.exe": {"cat": {"x": 1}, "act": 1}}}' % t,
        '{"t": "%s", "span": Infinity, "apps": {"a.exe": {"cat": "dev", "act": 300}}}' % t,
        '{"t": "%s", "span": 1e30, "apps": {"a.exe": {"cat": "dev", "act": 300}}}' % t,
        '{"t": "%s", "span": NaN, "apps": {"a.exe": {"cat": "dev", "act": 300}}}' % t,
        '{"t": "%s", "span": 300, "apps": {"a.exe": {"cat": "dev", "act": Infinity}}}' % t,
        '{"t": "%s", "span": 300, "apps": {"a.exe": {"cat": "dev", "act": NaN}}}' % t,
        _line(t, {"code.exe": _app("dev", 300)}),
    ]
    r = _report(tmp_path, app="\n".join(raw) + "\n")["rows"][0]
    assert r["dev"][21] == 5 and sum(r["game"]) == 0 and sum(r["adobe"]) == 0


# --- レポートの表示（render_report） ---

import re

from shiftlock.report_html import render_report


def _html(tmp_path, app=None, mutate=None):
    data = _report(tmp_path, app=app)
    if mutate:
        mutate(data)
    return render_report(data)


def _row(html, label):
    """その日の <tr>…</tr> を取り出す"""
    for m in re.finditer(r"<tr[^>]*>.*?</tr>", html, re.S):
        if f">{label}</th>" in m.group(0):
            return m.group(0)
    raise AssertionError(label)


def _over15_app():
    # 21 時台: game 20 分・dev 16 分・adobe 14 分
    apps = {"eldenring.exe": _app("game", 1200), "code.exe": _app("dev", 960),
            "photoshop.exe": _app("adobe", 840)}
    return _line("2026-12-07T21:30:00", apps, span=1800) + "\n"


def _row_label(tmp_path):
    (tmp_path / "x").mkdir(exist_ok=True)
    return _report(tmp_path / "x")["rows"][0]["label"]


def test_bands_for_categories_over_15_min(tmp_path):
    (tmp_path / "a").mkdir()
    html = _html(tmp_path / "a", app=_over15_app())
    row = _row(html, _row_label(tmp_path))
    assert '<i class="bd"><i class="g"></i><i class="dv"></i></i>' in row
    assert 'class="ad"' not in row


def test_no_band_container_when_nothing_over_15(tmp_path):
    html = _html(tmp_path, app=VECTOR.read_text(encoding="utf-8"))
    row = _row(html, _row_label(tmp_path))
    assert 'class="bd"' not in row


def test_tip_has_category_minutes(tmp_path):
    # 21 時台に pc の記録（cov あり）が要る．無いと tip は「記録なし」になる
    pc = _pc("2026-12-07T21:05:00", "2026-12-07T21:10:00", game=0)
    data = _report(tmp_path, app=_over15_app(), pc=pc)
    row = _row(render_report(data), data["rows"][0]["label"])
    assert " / 開発 16分" in row and " / Adobe 14分" in row and " / ゲーム 20分" in row
    # 0 分のカテゴリは出ない（開発だけのログ）
    (tmp_path / "b").mkdir()
    only_dev = _line("2026-12-07T21:30:00", {"code.exe": _app("dev", 960)}, span=1800) + "\n"
    data = _report(tmp_path / "b", app=only_dev, pc=pc)
    row2 = _row(render_report(data), data["rows"][0]["label"])
    assert " / 開発 16分" in row2 and "Adobe" not in row2 and "ゲーム" not in row2


def test_day_columns(tmp_path):
    html = _html(tmp_path, app=_over15_app())
    head = re.search(r"<thead>.*?</thead>", html, re.S).group(0)
    i_game, i_dev, i_adobe = (head.index(s) for s in ("ゲーム(h)", "開発(h)", "Adobe(h)"))
    assert i_game < i_dev < i_adobe
    row = _row(html, _row_label(tmp_path))
    assert row.count("<td") == len(re.findall(r"<th[ >]", head)) - 1


def test_legend_has_two_more(tmp_path):
    html = _html(tmp_path)
    assert "開発15分以上" in html and "Adobe15分以上" in html
    assert '<i class="bd"><i class="dv"></i></i>' in html and '<i class="bd"><i class="ad"></i></i>' in html


def test_app_names_never_appear(tmp_path):
    html = _html(tmp_path, app=VECTOR.read_text(encoding="utf-8") + _over15_app())
    for name in ("code.exe", "eldenring", "photoshop"):
        assert name not in html


def test_old_rows_without_dev_render(tmp_path):
    def strip(data):
        for r in data["rows"]:
            for k in ("dev", "adobe", "dev_total", "adobe_total"):
                del r[k]
    html = _html(tmp_path, mutate=strip)
    assert "開発(h)" in html


def test_css_has_both_themes(tmp_path):
    html = _html(tmp_path)
    assert html.count("--dev:") == 2 and html.count("--adobe:") == 2
