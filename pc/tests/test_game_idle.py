"""ゲームを起動したまま離席・出発したときの扱い（issue #24）。
tick: ゲーム中でも入力が usage.game_idle_minutes（既定 180 分）無ければ離席。『行ってきます』〜『ただいま』はゲームを無視して idle だけで見る。
レポート: 離席区間（_gaps_from_ticks）と時間帯ごとの加算も同じ判定。"""
from __future__ import annotations

import json
import sys
import tempfile
from datetime import datetime, timedelta
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
import shiftlock as sl
from shiftlock.report import _gaps_from_ticks

SRC = ROOT / "shifts.example.json"


def dt(s): return datetime.strptime(s, "%Y-%m-%d %H:%M")


def env(**over):
    t = Path(tempfile.mkdtemp())
    cfg = json.loads(SRC.read_text(encoding="utf-8")); cfg.update(over)
    (t / "shifts.json").write_text(json.dumps(cfg, ensure_ascii=False), encoding="utf-8")
    (t / "days.json").write_text("{}", encoding="utf-8")
    return t / "shifts.json", sl.load_config(t / "shifts.json")


def _usage(cp):
    return json.loads((cp.parent / "usage-pc.json").read_text(encoding="utf-8"))


# ---- tick -----------------------------------------------------------------

def test_tick_game_left_running_becomes_gap_after_limit():
    """ゲームを起動したまま離席: 入力が 180 分無い tick で離席（gap_start は最後の入力時刻）。
    復帰で gaps に載り，連続稼働の起点は戻った時刻になる"""
    cp, _ = env()
    t0 = dt("2026-11-01 01:00")
    sl.run_tick(cp, 10, True, t0)                                    # ゲーム開始（入力あり）
    for k in range(1, 40):                                           # 5 分おきに idle が伸びる（放置）
        sl.run_tick(cp, 300 * k, True, t0 + timedelta(minutes=5 * k))
    u = _usage(cp)
    assert u["start"] == "2026-11-01T01:00:00"
    assert u["gap_start"] == "2026-11-01T01:00:00"
    assert u["gaps"] == []
    back = t0 + timedelta(minutes=200)
    sl.run_tick(cp, 10, True, back)                                  # マウスを触った（ゲームはまだ起動中）
    u = _usage(cp)
    assert u["gap_start"] is None
    assert u["gaps"] == [["2026-11-01T01:00", "2026-11-01T04:20"]]
    assert u["start"] == "2026-11-01T04:20:00"


def _game_idle_inactive(over: dict, idle_seconds: int) -> bool:
    cp, _ = env(**over)
    t0 = dt("2026-11-01 01:00")
    sl.run_tick(cp, 10, True, t0)
    sl.run_tick(cp, idle_seconds, True, t0 + timedelta(seconds=idle_seconds))
    return _usage(cp).get("gap_start") is not None   # 一度も離席になっていなければキー自体が無い


def test_tick_game_idle_limit_setting_and_bad_values():
    """既定 180 分。設定で変えられる。壊れた値は既定，0 以下は無制限（従来どおりゲーム中は触っている扱い）"""
    assert _game_idle_inactive({}, 180 * 60)
    assert not _game_idle_inactive({}, 180 * 60 - 1)
    assert _game_idle_inactive({"usage": {"game_idle_minutes": 60}}, 3600)
    assert not _game_idle_inactive({"usage": {"game_idle_minutes": 60}}, 3599)
    for bad in ("abc", float("inf"), float("nan"), None, [180]):
        assert _game_idle_inactive({"usage": {"game_idle_minutes": bad}}, 180 * 60), bad
        assert not _game_idle_inactive({"usage": {"game_idle_minutes": bad}}, 180 * 60 - 1), bad
    for off in (0, -5):
        assert not _game_idle_inactive({"usage": {"game_idle_minutes": off}}, 99999), off


def test_tick_ignores_game_while_on_shift():
    """『行ってきます』のあと『ただいま』までは，ゲームが起動していても idle だけで見る"""
    cp, _ = env()
    store = sl.Store(cp)
    sl.run_tick(cp, 10, True, dt("2026-11-02 18:00"))                # 出発前はゲーム中＝触っている
    store.set_shift_status("2026-11-02", "worked", dt("2026-11-02 18:30"), "shuppatsu")
    sl.run_tick(cp, 1500, True, dt("2026-11-02 19:00"))              # 25 分無入力 → 離席
    u = _usage(cp)
    assert u["gap_start"] == "2026-11-02T18:35:00"
    sl.set_mark(store, "2026-11-02", "kitaku", dt("2026-11-03 09:00"), dt("2026-11-03 09:00"), "claude")
    sl.run_tick(cp, 1500, True, dt("2026-11-03 09:05"))              # 帰宅後はまたゲーム中＝触っている
    u = _usage(cp)
    assert u["gap_start"] is None
    assert u["gaps"] == [["2026-11-02T18:35", "2026-11-03T09:05"]]
    assert u["start"] == "2026-11-03T09:05:00"


def test_shift_away():
    """乗務で外出中 = 『行ってきます』から 24 時間以内で，その後の『ただいま』がまだ無い"""
    cp, _ = env()
    store = sl.Store(cp)
    away = lambda s: sl.shift_away(store, dt(s))
    assert not away("2026-11-02 18:00")                              # 記録なし
    store.set_shift_status("2026-11-02", "worked", dt("2026-11-02 18:30"), "shuppatsu")
    assert not away("2026-11-02 18:29")
    assert away("2026-11-02 18:30") and away("2026-11-03 06:00")
    assert not away("2026-11-03 18:30")                              # 24 時間過ぎたら外出扱いにしない（記録忘れ）
    with open(cp.parent / "events.log", "a", encoding="utf-8") as f:   # 乗務日に紐づかない『ただいま』
        f.write("2026-11-03T08:00\t-\tkitaku\tfree-day\n")
    assert away("2026-11-03 07:59") and not away("2026-11-03 08:00")
    # 取り消したら外出ではない
    store.set_shift_status("2026-11-02", None, dt("2026-11-03 10:00"), "undo")
    assert not away("2026-11-02 20:00")
    # 事後報告の『出た』は出発時刻が分からないので外出扱いにしない
    store.set_shift_status("2026-11-03", "worked", dt("2026-11-04 10:00"), "late shl")
    assert not away("2026-11-04 10:30")


# ---- レポート -------------------------------------------------------------

def test_report_gaps_game_left_running():
    """ゲームを起動したまま離席: 入力が 180 分無い tick から，最後の入力まで遡って離席。設定 0（None）なら従来どおり"""
    t0 = dt("2026-11-01 01:00")
    ticks = [{"_t": t0, "idle": 10, "game": 1}]
    ticks += [{"_t": t0 + timedelta(minutes=5 * k), "idle": 300 * k, "game": 1} for k in range(1, 60)]
    ticks.append({"_t": t0 + timedelta(minutes=300), "idle": 10, "game": 1})   # 06:00 復帰
    assert _gaps_from_ticks(ticks, game_idle=timedelta(hours=3)) == [(dt("2026-11-01 01:00"), dt("2026-11-01 06:00"))]
    assert _gaps_from_ticks(ticks, game_idle=None) == []


def test_report_gaps_ignore_game_while_away():
    """外出中（『行ってきます』〜『ただいま』）はゲームが起動していても idle で見る"""
    t0 = dt("2026-11-02 18:00")
    ticks = [{"_t": t0 + timedelta(minutes=5 * k), "idle": 300 * k if k else 10, "game": 1} for k in range(13)]
    away = [(dt("2026-11-02 18:30"), dt("2026-11-03 09:00"))]
    assert _gaps_from_ticks(ticks, away=away, game_idle=timedelta(hours=3)) == [(dt("2026-11-02 18:00"), dt("2026-11-02 19:00"))]
    assert _gaps_from_ticks(ticks, game_idle=timedelta(hours=3)) == []


def test_report_hours_ignore_game_while_away():
    """時間帯の PC／ゲームの加算も，外出中はゲームを無視する（出発したままのゲームは塗らない）"""
    cp, cfg = env()
    store = sl.Store(cp)
    store.set_shift_status("2026-11-02", "worked", dt("2026-11-02 18:30"), "shuppatsu")
    sl.set_mark(store, "2026-11-02", "kitaku", dt("2026-11-03 09:00"), dt("2026-11-03 09:00"), "claude")
    last_input = dt("2026-11-02 18:28")
    t = dt("2026-11-02 17:00")
    while t <= dt("2026-11-03 10:00"):
        idle = 10 if t <= last_input or t >= dt("2026-11-03 09:00") else int((t - last_input).total_seconds())
        sl.log_append(store, "pc", t, {"idle": idle, "game": 1})
        t += timedelta(minutes=5)
    data = sl.build_report(cfg, store, dt("2026-11-03 12:00"), days=3)
    r = {x["date"]: x for x in data["rows"]}
    assert r["2026-11-02"]["pc"][18] == 30 and r["2026-11-02"]["game"][18] == 25   # 18:30 までゲーム，出発後は idle だけ
    assert r["2026-11-02"]["pc"][20] == 0 and r["2026-11-02"]["game"][20] == 0     # 乗務中にゲームが起動していても塗らない
    assert r["2026-11-02"]["away"][20] and r["2026-11-03"]["away"][8] and not r["2026-11-03"]["away"][9]
    assert r["2026-11-03"]["pc"][9] == 60 and r["2026-11-03"]["game"][9] == 60     # 帰宅後はゲーム中＝触っている


def test_report_hours_game_left_running_stops_after_limit():
    """時間帯のゲーム加算も game_idle_minutes で止める（放置の最初の 180 分は tick の時点ではゲーム中なので残る）"""
    cp, cfg = env()
    store = sl.Store(cp)
    last_input, back = dt("2026-11-01 12:00"), dt("2026-11-01 21:00")
    t = dt("2026-11-01 10:00")
    while t <= dt("2026-11-01 23:00"):
        idle = 10 if t <= last_input or t >= back else int((t - last_input).total_seconds())
        sl.log_append(store, "pc", t, {"idle": idle, "game": 1})
        t += timedelta(minutes=5)
    r = {x["date"]: x for x in sl.build_report(cfg, store, dt("2026-11-01 23:30"), days=1)["rows"]}["2026-11-01"]
    assert not any(r["away"][12:])                                      # 日曜なので外出は無い（0 の理由が away ではない）
    assert r["pc"][13] == 60 and r["game"][13] == 60                    # 放置の最初の 180 分はゲーム中のまま
    assert r["pc"][16] == 0 and r["game"][16] == 0                      # 180 分を超えたら PC にもゲームにも足さない
    assert r["pc"][22] == 60 and r["game"][22] == 60                    # 復帰後はゲーム中＝触っている
