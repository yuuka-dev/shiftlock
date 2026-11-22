"""強制モードの動作 throttle／suspend（段階の計算・設定・見張りが何も出さないこと）。"""
import json
import sys
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


# ---- 宣言と同意の表示（Task 3） ----

from shiftlock.limit import read_games, agent_seems_running

NOW = dt("2026-11-02 12:00")


class _IO:
    def __init__(self, answers):
        self.a = list(answers); self.out = []

    def input(self, p):
        self.out.append(p)
        if not self.a:
            raise EOFError
        return self.a.pop(0)

    def print(self, *x):
        self.out.append(" ".join(map(str, x)))


def _games(cp, games, seen="2026-11-01T20:00:00"):
    (cp.parent / "limit-games.json").write_text(json.dumps({k: {"verdict": v, "seen": seen} for k, v in games.items()}))


def _applog(cp, t):
    d = cp.parent / "log"; d.mkdir(exist_ok=True)
    (d / "app-2026-11.jsonl").write_text(json.dumps({"t": t, "span": 300, "apps": {}, "other": 0}) + "\n")


def _decl(cp, action="suspend", hours=None, answers=("y",), now=NOW):
    cfg = sl.load_config(cp); io = _IO(answers)
    return sl.enforce_declare(cfg, sl.Store(cp), now, action, hours, io), io


def test_declare_suspend_fixes_steps_and_apps():
    cp, _ = env(days={}, limit={"apps": ["Photoshop"]})
    msg, io = _decl(cp, "suspend")
    e = _st(cp)["2026-11-02"]["enforce"]
    assert e == {"action": "suspend", "via": "shl", "runs": [], "steps": [75, 50, 25, 10], "limit_apps": ["photoshop.exe"],
                 "limit_exclude": [], "declared_at": "2026-11-02T12:00", "consented_at": "2026-11-02T12:00"}
    assert "duration_hours" not in e and "{" not in msg and "時間" not in msg
    ev = (cp.parent / "events.log").read_text()
    assert "enforce_declare\tsuspend\n" in ev and "enforce_consent\tsuspend\n" in ev


def test_declare_limit_apps_sorted_and_deduped():
    cp, _ = env(days={}, limit={"apps": ["notepad.exe", "Code", "code.exe", ""]})
    _decl(cp, "throttle")
    assert _st(cp)["2026-11-02"]["enforce"]["limit_apps"] == ["code.exe", "notepad.exe"]


def test_declare_limit_exclude_is_fixed_sorted_and_deduped():
    cp, _ = env(days={}, limit={"exclude": ["Apex.exe", "eldenring", "apex.exe", ""]})
    _decl(cp, "suspend")
    assert _st(cp)["2026-11-02"]["enforce"]["limit_exclude"] == ["apex.exe", "eldenring.exe"]


def test_declare_limit_exclude_not_a_list_is_empty():
    cp, _ = env(days={}, limit={"exclude": "apex.exe"})
    _decl(cp, "suspend")
    assert _st(cp)["2026-11-02"]["enforce"]["limit_exclude"] == []


def test_status_text_default_action_limit_has_no_hours():
    cp, _ = env(days={}, enforce={"action": "throttle"})
    out = sl.enforce_status_text(sl.load_config(cp), sl.Store(cp), dt("2026-11-02 12:00"))
    line = [l for l in out.splitlines() if "既定" in l][0]
    assert M.ENFORCE_LABEL["throttle"] in line and "2h" not in line and "h /" not in line and "{" not in line


def test_enforce_menu_with_limit_default_action_does_not_crash():
    cp, _ = env(days={}, enforce={"action": "suspend"})
    raw = json.loads(cp.read_text()); c = sl.deep_merge(sl.DEFAULTS, raw)
    io = _IO(["", "", ""])   # 全部そのまま（空 Enter）
    sl.enforce_menu(raw, c, io)
    assert any("suspend" in line for line in io.out)   # いまの値をそのまま見せる
    assert raw == json.loads(cp.read_text())


def test_declare_limit_has_no_lock_only_fields():
    cp, _ = env(days={})   # enforce.duration_hours などの既定があっても，減速には写さない
    _decl(cp, "throttle")
    e = _st(cp)["2026-11-02"]["enforce"]
    assert not {"duration_hours", "grace_minutes", "relock_minutes", "max_runs", "boot_grace_minutes"} & set(e)


@pytest.mark.parametrize("action", LIMIT_ACTIONS)
def test_declare_limit_rejects_hours(action):
    cp, _ = env(days={})
    for h in ("3h", 3, "0.1", "99"):
        msg, io = _decl(cp, action, h)
        assert msg == M.ENFORCE_LIMIT_NO_HOURS and io.out == []
    assert "enforce" not in _st(cp).get("2026-11-02", {})


@pytest.mark.parametrize("bad", [[10, 25, 50, 75], [75, 75, 25, 10], [75, 50, 25], "75", None])
def test_declare_limit_rejects_bad_steps(bad):
    cp, _ = env(days={})
    cfg = sl.load_config(cp)
    cfg["limit"]["steps"] = bad
    io = _IO(["y"])
    assert sl.enforce_declare(cfg, sl.Store(cp), NOW, "throttle", None, io) == M.ENFORCE_LIMIT_BAD_STEPS
    assert io.out == [] and "enforce" not in _st(cp).get("2026-11-02", {})


@pytest.mark.parametrize("ans", [["n"], [""], []])
def test_declare_limit_needs_yes(ans):
    cp, _ = env(days={})
    msg, io = _decl(cp, "throttle", answers=ans)   # [] は EOF（非対話）
    assert msg == M.ENFORCE_DECLINED and "enforce" not in _st(cp).get("2026-11-02", {})


def test_declare_limit_stdio_still_needs_tty():
    cp, _ = env(days={})
    msg = sl.enforce_declare(sl.load_config(cp), sl.Store(cp), NOW, "suspend", None, sl.StdIO())
    assert msg == M.ENFORCE_NEED_TTY and "enforce" not in _st(cp).get("2026-11-02", {})


def test_declare_limit_dialog_text_has_no_hours():
    import io as _io
    cp, _ = env(days={}); seen = {}

    class TTY(_io.StringIO):
        def isatty(self): return True

    def confirm(text, timeout=60):
        seen["t"] = text
        return True
    old_stdin, old_confirm = sys.stdin, sl.enforce.windows_confirm
    try:
        sys.stdin = TTY("y\n")
        sl.enforce.windows_confirm = confirm
        sl.enforce_declare(sl.load_config(cp), sl.Store(cp), NOW, "suspend", None, sl.StdIO())
    finally:
        sys.stdin, sl.enforce.windows_confirm = old_stdin, old_confirm
    assert "時間" not in seen["t"] and "h）" not in seen["t"] and "{" not in seen["t"]
    assert _st(cp)["2026-11-02"]["enforce"]["action"] == "suspend"


def test_lock_declare_unchanged():
    cp, _ = env(days={})
    _decl(cp, "lock", 2)
    e = _st(cp)["2026-11-02"]["enforce"]
    assert e["duration_hours"] == 2 and "steps" not in e and "limit_apps" not in e


def _terms(cp, action, now=NOW):
    cfg = sl.load_config(cp); p = sl.target_plan(cfg, now)
    return sl.enforce_terms(cfg, p, action, None, sl.Store(cp), now)


def test_terms_limit_lists_stages_and_games():
    cp, _ = env(days={}, limit={"apps": ["photoshop.exe"]})
    _games(cp, {"eldenring.exe": "target", "apex.exe": "anticheat"}); _applog(cp, "2026-11-02T11:55:00")
    t = _terms(cp, "throttle")
    for x in ("13:30", "14:22", "15:15", "16:07", "17:00", "75", "50", "25", "10", "eldenring.exe", "apex.exe", "photoshop.exe",
              M.ENFORCE_TERMS_LIMIT_ANTICHEAT_NOTE, "STOP.txt", "kaijo"):
        assert x in t, x
    p = sl.target_plan(sl.load_config(cp), NOW)
    assert f"{p.start_latest:%H:%M}" in t   # 戻る時刻
    assert "{" not in t and "時間" not in t
    tgt = [l for l in t.splitlines() if "eldenring.exe" in l]   # 外したゲームは「対象」の行に出ない
    assert len(tgt) == 1 and "apex.exe" not in tgt[0]


def test_terms_limit_suspend_last_stage_is_stop():
    cp, _ = env(days={}); _applog(cp, "2026-11-02T11:55:00")
    t = _terms(cp, "suspend")
    last = [l for l in t.splitlines() if "段階 5" in l][0]
    assert "17:00" in last and M.ENFORCE_TERMS_LIMIT_STOPPED in last and "%" not in last
    last_t = [l for l in _terms(cp, "throttle").splitlines() if "段階 5" in l][0]
    assert "17:00" in last_t and "10%" in last_t and M.ENFORCE_TERMS_LIMIT_STOPPED not in last_t


def test_terms_limit_no_games_seen():
    cp, _ = env(days={}); _applog(cp, "2026-11-02T11:55:00")
    t = _terms(cp, "throttle")
    assert M.ENFORCE_TERMS_LIMIT_GAMES_NONE in t and M.ENFORCE_TERMS_LIMIT_APPS_NONE in t and "{" not in t


def test_terms_limit_manual_exclude_not_listed_as_target():
    cp, _ = env(days={}, limit={"exclude": ["eldenring.exe"]}); _games(cp, {"eldenring.exe": "target"})
    assert "eldenring.exe" not in _terms(cp, "throttle")


def test_terms_suspend_has_unsaved_warning_throttle_does_not():
    cp, _ = env(days={}); _applog(cp, "2026-11-02T11:55:00")
    s, t = _terms(cp, "suspend"), _terms(cp, "throttle")
    assert M.ENFORCE_TERMS_LIMIT_SUSPEND_NOTE in s and M.ENFORCE_TERMS_LIMIT_SUSPEND_NOTE not in t
    assert "保存" in s and "保存" not in t
    assert M.ENFORCE_CAUTION["throttle"] in t and M.ENFORCE_CAUTION["suspend"] in s   # CAUTION_ACTIONS と同じ扱い


def test_terms_warns_when_agent_not_running():
    cp, _ = env(days={})
    assert M.ENFORCE_LIMIT_NO_AGENT in _terms(cp, "throttle")                    # app ログが無い
    _applog(cp, "2026-11-02T11:55:00")
    assert M.ENFORCE_LIMIT_NO_AGENT not in _terms(cp, "throttle")                # 5 分前
    _applog(cp, "2026-11-02T11:30:00")
    assert M.ENFORCE_LIMIT_NO_AGENT in _terms(cp, "throttle")                    # 30 分前
    cfg = sl.load_config(cp); p = sl.target_plan(cfg, NOW)
    assert M.ENFORCE_LIMIT_NO_AGENT not in sl.enforce_terms(cfg, p, "lock", 2, sl.Store(cp), NOW)


def test_terms_lock_unchanged_without_store():
    cp, _ = env(days={})
    cfg = sl.load_config(cp); p = sl.target_plan(cfg, NOW)
    t = sl.enforce_terms(cfg, p, "lock", 2)
    assert "2 時間" in t and "STOP.txt" in t


def test_status_text_limit_has_no_hours():
    cp, _ = env(days={}); _declare(cp, "suspend")
    cfg = sl.load_config(cp)
    out = sl.enforce_status_text(cfg, sl.Store(cp), dt("2026-11-02 12:00"))
    line = [l for l in out.splitlines() if "11/02" in l][0]
    assert M.ENFORCE_LABEL["suspend"] in line and "2h" not in line and "時間" not in line and "{" not in line


def test_off_before_first_run_cancels_after_run_releases():
    cp, _ = env(days={}); _declare(cp, "throttle")
    cfg = sl.load_config(cp)
    assert sl.enforce_off(cfg, sl.Store(cp), dt("2026-11-02 12:00")) == M.ENFORCE_OFF_DONE.format(day="11/02")
    assert "enforce" not in _st(cp).get("2026-11-02", {})
    _declare(cp, "throttle")
    e = _st(cp)["2026-11-02"]["enforce"]; e["runs"] = [{"at": "2026-11-02T13:30:00", "action": "throttle", "step": 1}]
    (cp.parent / "state.json").write_text(json.dumps({"shifts": {"2026-11-02": {"enforce": e}}}))
    sl.enforce_off(cfg, sl.Store(cp), dt("2026-11-02 14:00"))
    assert _st(cp)["2026-11-02"]["enforce"]["released_at"] and (cp.parent / "STOP.txt").exists()


def test_read_games_ignores_old_and_broken():
    cp, _ = env(days={}); st = sl.Store(cp)
    assert read_games(st, NOW) == {}   # ファイルなし
    (cp.parent / "limit-games.json").write_text(json.dumps({
        "eldenring.exe": {"verdict": "target", "seen": "2026-11-01T20:00:00"},
        "apex.exe": {"verdict": "anticheat", "seen": "2026-10-04T12:00:00"},     # 29 日前 → 残す
        "old.exe": {"verdict": "target", "seen": "2026-10-03T11:59:00"},         # 30 日より古い → 捨てる
        "unk.exe": {"verdict": "unknown", "seen": "2026-11-02T11:00:00"},
        "bad1.exe": {"verdict": "weird", "seen": "2026-11-01T20:00:00"},
        "bad2.exe": {"verdict": "target"},
        "bad3.exe": {"verdict": "target", "seen": "yesterday"},
        "bad4.exe": "target",
        "bad5.exe": {"verdict": ["target"], "seen": "2026-11-01T20:00:00"},
        "bad6.exe": {"verdict": "target", "seen": 5}}))
    assert read_games(st, NOW) == {"eldenring.exe": "target", "apex.exe": "anticheat", "unk.exe": "unknown"}
    for broken in ("{", "[]", '"x"', "null", ""):
        (cp.parent / "limit-games.json").write_text(broken)
        assert read_games(st, NOW) == {}


def test_agent_seems_running_edges():
    cp, _ = env(days={}); st = sl.Store(cp)
    assert agent_seems_running(st, NOW) is False
    _applog(cp, "2026-11-02T11:45:00"); assert agent_seems_running(st, NOW) is True     # ちょうど 15 分
    _applog(cp, "2026-11-02T11:44:59"); assert agent_seems_running(st, NOW) is False
    d = cp.parent / "log"
    (d / "app-2026-11.jsonl").write_text(json.dumps({"t": "2026-11-02T11:00:00"}) + "\n" + json.dumps({"t": "2026-11-02T11:58:00"}) + "\n")
    assert agent_seems_running(st, NOW) is True      # 最後の行を見る
    (d / "app-2026-11.jsonl").write_text(json.dumps({"t": "2026-11-02T11:58:00"}) + "\n{broken")
    assert agent_seems_running(st, NOW) is False     # 最後の行が壊れている
    (d / "app-2026-11.jsonl").write_text("")
    assert agent_seems_running(st, NOW) is False


def test_agent_seems_running_looks_at_previous_month_file():
    cp, _ = env(days={}); st = sl.Store(cp)
    d = cp.parent / "log"; d.mkdir(exist_ok=True)
    (d / "app-2026-10.jsonl").write_text(json.dumps({"t": "2026-10-31T23:55:00"}) + "\n")
    assert agent_seems_running(st, dt("2026-11-01 00:03")) is True


@pytest.mark.parametrize("bad", [None, "a.exe", 5])
def test_terms_limit_exclude_not_a_list_is_empty(bad):
    # limit.exclude が壊れていても同意の表示は途中で落ちない（空として扱う）
    cp, _ = env(days={}, limit={"exclude": bad}); _games(cp, {"eldenring.exe": "target"}); _applog(cp, "2026-11-02T11:55:00")
    assert "eldenring.exe" in _terms(cp, "throttle")
