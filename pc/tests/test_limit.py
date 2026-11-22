"""強制モードの動作 throttle／suspend（段階の計算・設定・見張りが何も出さないこと）。"""
import json
from datetime import datetime

import pytest

import shiftlock as sl
from shiftlock import i18n as M
from shiftlock.limit import LIMIT_ACTIONS, check_steps, limit_stages
from test_shiftlock import env, dt, _st

S, H = datetime(2026, 12, 7, 13, 30), datetime(2026, 12, 7, 17, 0)


def test_limit_actions():
    assert LIMIT_ACTIONS == ("throttle", "suspend")
    assert set(LIMIT_ACTIONS) <= set(sl.ENFORCE_ACTIONS)


def test_stages_throttle():
    st = limit_stages(S, H, [75, 50, 25, 10], "throttle")
    assert [(x["step"], x["at"].strftime("%H:%M:%S"), x["percent"]) for x in st] == [
        (1, "13:30:00", 75), (2, "14:22:30", 50), (3, "15:15:00", 25), (4, "16:07:30", 10), (5, "17:00:00", 10)]


def test_stages_suspend_last_is_none():
    assert limit_stages(S, H, [75, 50, 25, 10], "suspend")[-1] == {"step": 5, "at": H, "percent": None}


def test_stages_hard_not_after_soft():
    assert [x["step"] for x in limit_stages(H, H, [75, 50, 25, 10], "suspend")] == [5]
    assert [x["step"] for x in limit_stages(H, S, [75, 50, 25, 10], "throttle")] == [5]


def test_stages_truncate_below_second():
    st = limit_stages(datetime(2026, 12, 7, 16, 50), datetime(2026, 12, 7, 16, 57, 1), [75, 50, 25, 10], "throttle")
    assert [x["at"].strftime("%H:%M:%S") for x in st] == ["16:50:00", "16:51:45", "16:53:30", "16:55:15", "16:57:01"]


def test_check_steps_accepts():
    assert check_steps([75, 50, 25, 10]) == [75, 50, 25, 10]
    assert check_steps([99, 3, 2, 1]) == [99, 3, 2, 1]


@pytest.mark.parametrize("bad", [None, "75", [75, 50, 25], [75, 50, 25, 10, 5], [10, 25, 50, 75], [75, 75, 25, 10],
                                 [75, 50, 25, 0], [100, 50, 25, 10], [75, 50, 25, True], [75.5, 50, 25, 10], [75.0, 50, 25, 10],
                                 ["75", 50, 25, 10], [75, 50, 25, None], {"a": 1}])
def test_check_steps_rejects(bad):
    assert check_steps(bad) is None


def test_config_defaults_and_rejects_limit_not_dict(tmp_path):
    from shiftlock.config import DEFAULTS
    assert DEFAULTS["limit"]["steps"] == [75, 50, 25, 10] and DEFAULTS["limit"]["baseline_minutes"] == 30
    assert "claude.exe" in DEFAULTS["limit"]["protect"] and "EasyAntiCheat*" in DEFAULTS["limit"]["anticheat_markers"]
    cp, _ = env(days={})
    raw = json.loads(cp.read_text()); raw["limit"] = [75, 50]; cp.write_text(json.dumps(raw))
    with pytest.raises(ValueError, match="limit"):
        sl.load_config(cp)


def _declare(cp, action, day="2026-11-02"):
    st = cp.parent / "state.json"
    e = {"action": action, "declared_at": "2026-11-02T12:00", "consented_at": "2026-11-02T12:00", "via": "shl",
         "steps": [75, 50, 25, 10], "runs": []}
    st.write_text(json.dumps({"shifts": {day: {"enforce": e}}}))


@pytest.mark.parametrize("action", LIMIT_ACTIONS)
def test_enforce_due_returns_nothing_for_limit_actions(action):
    cp, _ = env(days={}); _declare(cp, action)
    before = _st(cp)["2026-11-02"]["enforce"]
    for s in ("2026-11-02 17:00", "2026-11-02 17:10", "2026-11-02 17:15", "2026-11-02 17:30"):   # hard フェーズ・active・uptime 十分
        acts = sl.run_tick(cp, 10, False, dt(s), uptime_seconds=3600)["actions"]
        assert [a for a in acts if a["kind"] == "enforce"] == []
    e = _st(cp)["2026-11-02"]["enforce"]
    assert "seen_at" not in e and "warned_at" not in e and e["runs"] == []
    assert e == before   # 宣言は 1 バイトも変わらない


def test_enforce_suffix_for_limit_action():
    cp, cfg = env(days={}); _declare(cp, "suspend")
    p = sl.target_plan(cfg, dt("2026-11-02 12:00"))
    got = sl.enforce_suffix(sl.Store(cp), p, "hard")
    assert got == M.ENFORCE_SUFFIX_LIMIT.format(label=M.ENFORCE_LABEL["suspend"], stop="")
    assert "時間" not in got and "{" not in got
