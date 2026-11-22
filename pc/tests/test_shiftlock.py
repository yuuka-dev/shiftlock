import json, shutil, subprocess, sys, tempfile
from datetime import datetime, timedelta
from pathlib import Path
ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
import shiftlock as sl

SRC = ROOT / "shifts.example.json"
def dt(s): return datetime.strptime(s, "%Y-%m-%d %H:%M")
def UPS(prompt="x"): return json.dumps({"hook_event_name": "UserPromptSubmit", "prompt": prompt})
STOP = json.dumps({"hook_event_name": "Stop"})

def env(days=None, **over):
    t = Path(tempfile.mkdtemp())
    cfg = json.loads(SRC.read_text()); cfg.update(over)
    (t / "shifts.json").write_text(json.dumps(cfg, ensure_ascii=False))
    (t / "days.json").write_text(json.dumps(days if days is not None else
        {"times": {"2026-11-06": {"start_target": "21:00"}}}, ensure_ascii=False))
    return t / "shifts.json", sl.load_config(t / "shifts.json")

def test_plan_numbers():
    _, cfg = env()
    ps = sl.plans(cfg, dt("2026-11-02 00:00").date())
    p = [x for x in ps if x.day == "2026-11-02"][0]
    assert (p.depart_target, p.depart_latest) == (dt("2026-11-02 18:30"), dt("2026-11-02 21:00"))
    assert p.soft_close == dt("2026-11-02 13:30") and p.hard_close == dt("2026-11-02 17:00")
    assert p.lastcall == dt("2026-11-02 12:50")
    p6 = [x for x in ps if x.day == "2026-11-06"][0]
    assert "2026-11-08" not in [x.day for x in ps]          # 日曜は休み
    assert p6.start_target == dt("2026-11-06 21:00") and p6.soft_close == dt("2026-11-06 14:30")

def test_no_sleep_block_tuesday():
    _, cfg = env()
    # 11/03 は火曜（訪問看護 10-11）。8h 必要なら 11:00-18:30 の 7.5h + 09:30-10:00
    assert sl.latest_bedtime(cfg, dt("2026-11-03 18:30"), 8) == dt("2026-11-03 09:30")
    assert sl.latest_bedtime(cfg, dt("2026-11-03 18:30"), 7) == dt("2026-11-03 11:30")
    # 月曜は影響なし
    assert sl.latest_bedtime(cfg, dt("2026-11-02 18:30"), 8) == dt("2026-11-02 10:30")
    # 起床が枠の中（10:30）なら枠の開始から逆算
    assert sl.latest_bedtime(cfg, dt("2026-11-03 10:30"), 2) == dt("2026-11-03 08:00")

def test_phases():
    _, cfg = env()
    ph = lambda s, st={}: sl.current(cfg, st, dt(s))[0]
    assert ph("2026-11-02 12:49") == "free"
    assert ph("2026-11-02 12:50") == "lastcall"
    assert ph("2026-11-02 13:30") == "soft"
    assert ph("2026-11-02 17:00") == "hard"
    assert ph("2026-11-02 22:29") == "hard"
    assert ph("2026-11-02 22:30") == "free"
    y = {"2026-11-02": {"status": "yasumi"}}
    assert ph("2026-11-02 18:00", y) == "free"
    o = {"2026-11-02": {"status": "okita"}}
    assert ph("2026-11-02 15:00", o) == "free"
    assert ph("2026-11-02 18:15", o) == "go"
    assert ph("2026-11-02 20:45", o) == "go_last"

def test_keywords_and_block():
    cp, _ = env()
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-02 17:10"))
    assert c == 2 and "ロック中" in e and "今日休む" in e
    c, o, e = sl.run_hook(cp, UPS("今日休む"), dt("2026-11-02 17:11"))
    assert c == 2 and "休みにした" in e and "1 回目" in e
    assert sl.run_hook(cp, UPS("x"), dt("2026-11-02 17:12"))[0] == 0   # 解除
    c, o, e = sl.run_hook(cp, UPS("やっぱ出る"), dt("2026-11-02 17:13"))
    assert c == 2 and sl.run_hook(cp, UPS("x"), dt("2026-11-02 17:14"))[0] == 2
    c, o, e = sl.run_hook(cp, UPS("起きた"), dt("2026-11-02 17:15"))
    assert c == 2 and "ロック解除" in e and "ほんまに寝た" not in e   # PC記録なし→判定しない
    assert sl.run_hook(cp, UPS("x"), dt("2026-11-02 17:16"))[0] == 0
    c, o, e = sl.run_hook(cp, UPS("状況"), dt("2026-11-02 17:17"))
    assert c == 2 and "就寝目安 11/02 13:30" in e and "最低ライン 11/02 17:00" in e and "就寝準備 12:50" in e
    assert sl.run_hook(cp, UPS("閉店"), dt("2026-11-02 17:17"))[0] == 2   # 旧キーワードも通る
    log = (cp.parent / "events.log").read_text()
    assert log.count("\n") == 3

def test_block_can_be_disabled():
    cp, _ = env(block_claude_on_hard=False)
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-02 17:10"))
    assert c == 0 and "ロック中" in json.loads(o)["systemMessage"]

def test_lastcall_soft_context():
    cp, _ = env()
    c, o, e = sl.run_hook(cp, UPS(), dt("2026-11-02 13:00"))
    d = json.loads(o)
    assert c == 0 and "30分" in d["systemMessage"] and "20分以上" in d["hookSpecificOutput"]["additionalContext"]
    c, o, e = sl.run_hook(cp, UPS(), dt("2026-11-02 14:00"))
    assert "就寝目安 13:30 過ぎてる" in json.loads(o)["systemMessage"]
    assert sl.run_hook(cp, UPS(), dt("2026-11-02 09:00")) == (0, "", "")

def test_claude_nag_and_long_task():
    cp, _ = env()
    t = dt("2026-11-01 09:00"); nags = []
    for i in range(16):  # 10分おきに2.5時間
        c, o, e = sl.run_hook(cp, UPS(), t + timedelta(minutes=10 * i))
        if o: nags.append(i)
    assert nags == [12, 15]   # 120分で1回、30分後にもう1回
    # 30分の長い処理: prompt → Stop(30分後) → prompt で連続扱いのまま
    cp2, _ = env()
    t = dt("2026-11-01 09:00")
    for i in range(4):
        sl.run_hook(cp2, UPS(), t + timedelta(minutes=32 * i))
        sl.run_hook(cp2, STOP, t + timedelta(minutes=32 * i + 30))
    u = json.loads((cp2.parent / "usage-claude.json").read_text())
    assert u["start"].startswith("2026-11-01T09:00")
    # 25分空いたらリセット
    sl.run_hook(cp2, UPS(), t + timedelta(minutes=32 * 3 + 30 + 25))
    u = json.loads((cp2.parent / "usage-claude.json").read_text())
    assert not u["start"].startswith("2026-11-01T09:00")

def test_tick_notify_and_nag():
    cp, _ = env()
    acts = lambda s, idle=10, game=False: sl.run_tick(cp, idle, game, dt(s))["actions"]
    assert acts("2026-11-02 12:00") == []
    a = acts("2026-11-02 12:55"); assert len(a) == 1 and a[0]["method"] == "toast"
    assert acts("2026-11-02 13:00") == []                    # lastcall は1回だけ
    assert len(acts("2026-11-02 13:30")) == 1                # soft 突入
    assert acts("2026-11-02 13:50") == []
    assert len(acts("2026-11-02 14:00")) == 1                # 30分後
    a = acts("2026-11-02 17:00"); assert a[0]["method"] == "popup"
    assert acts("2026-11-02 17:05", idle=3600) == []         # 離席中は通知しない

def test_tick_game_nag_and_gap_then_okita():
    cp, _ = env()
    t = dt("2026-11-01 01:00")
    nag_at = []
    for i in range(0, 26):  # 5分おき、コントローラー操作で idle 大でもゲーム中
        a = sl.run_tick(cp, 9999, True, t + timedelta(minutes=5 * i))["actions"]
        if any("ゲーム連続" in x["text"] for x in a): nag_at.append(i)
    assert nag_at == [24]
    # 離席 6時間（睡眠）→ 復帰
    base = t + timedelta(minutes=130)
    for k in range(0, 75):
        sl.run_tick(cp, 60 * 25 + 300 * k, False, base + timedelta(minutes=5 * k))
    back = base + timedelta(minutes=5 * 75)
    sl.run_tick(cp, 30, False, back)
    gaps = json.loads((cp.parent / "usage-pc.json").read_text())["gaps"]
    assert len(gaps) == 1
    assert sl.recent_long_gap(sl.Store(cp), back + timedelta(minutes=5)) is True
    # 寝てない場合
    cp2, _ = env()
    for k in range(0, 40):
        sl.run_tick(cp2, 60, False, dt("2026-11-02 10:00") + timedelta(minutes=5 * k))
    c, o, e = sl.run_hook(cp2, UPS("起きた"), dt("2026-11-02 13:20"))
    assert "ほんまに寝た" in e
    assert "no-gap" in (cp2.parent / "events.log").read_text()

def test_install_cleans_old_and_registers():
    cp, _ = env(); t = cp.parent
    s = t / "settings.json"
    s.write_text(json.dumps({"model": "opus", "hooks": {
        "PreToolUse": [{"matcher": "*", "hooks": [{"type": "command", "command": "x shiftlock.py hook"}]},
                       {"matcher": "Edit", "hooks": [{"type": "command", "command": "fmt.sh"}]}],
        "UserPromptSubmit": [{"hooks": [{"type": "command", "command": "old shiftlock.py"}]}]}}))
    sl.install(cp, s, t / "hooks"); sl.install(cp, s, t / "hooks")
    d = json.loads(s.read_text())
    assert d["model"] == "opus"
    assert [h["command"] for g in d["hooks"]["PreToolUse"] for h in g["hooks"]] == ["fmt.sh"]
    assert len(d["hooks"]["UserPromptSubmit"]) == 1 and len(d["hooks"]["Stop"]) == 1

def test_cli_shell_roundtrip():
    cp, _ = env(); t = cp.parent
    script = sl.install(cp, t / "settings.json", t / "hooks")
    r = subprocess.run([sys.executable, str(script), "tick", "--config", str(cp), "--idle-seconds", "5"],
                       capture_output=True, text=True)
    assert r.returncode == 0 and r.stdout.isascii() and "actions" in json.loads(r.stdout)
    r = subprocess.run([sys.executable, str(script), "status", "--config", str(cp)], capture_output=True, text=True)
    assert r.returncode == 0 and "フェーズ" in r.stdout

def test_report_away_clip_and_no_sleep_estimate():
    cp, cfg = env()
    store = sl.Store(cp)
    # 11/02 は乗務日。出発 18:30 → 翌朝 07:00 は外出扱い
    t = dt("2026-11-01 08:00")
    end = dt("2026-11-03 20:00")
    def act(x):
        h = x.hour + x.minute / 60
        if x.date().isoformat() == "2026-11-01": return not (h < 9 or 23 <= h)          # 23:00→翌09:00 寝
        if x.date().isoformat() == "2026-11-02": return (9 <= h < 12) or (17.5 <= h < 18.4)  # 12→17:30 昼寝, 18:30 出発
        return (9.5 <= h < 10)                                                         # 11/03 帰宅後少し→寝る
    last = t
    while t < end:
        t += timedelta(minutes=5)
        if act(t): last = t - timedelta(seconds=30)
        sl.log_append(store, "pc", t, {"idle": int((t - last).total_seconds()), "game": 0})
    sl.log_append(store, "claude", dt("2026-11-02 10:10"), {"k": "p"})
    sl.log_append(store, "claude", dt("2026-11-02 10:20"), {"k": "kw"})
    data = sl.build_report(cfg, store, dt("2026-11-03 20:00"), days=3)
    r = {x["date"]: x for x in data["rows"]}
    # PC を触ってない時間があっても、本人の記録（ねる／おきた）が無ければ睡眠は出さへん（推定せえへん）
    assert all(x["sleep_min"] is None and x["sleep_span"] == "" and x["neru"] == [] and x["okita"] == [] for x in data["rows"])
    assert r["2026-11-02"]["shift"] == "unconfirmed" and r["2026-11-02"]["claude_total"] == 1   # 確認が無いので「出?」
    # 乗務明け 11/03: 18:30→07:00 は外出（灰色）。07:00 からは家
    assert r["2026-11-02"]["away"][20] and r["2026-11-03"]["away"][5] and not r["2026-11-03"]["away"][8]
    assert "long" not in r["2026-11-02"]["flags"]             # 23:00→翌09:00 の離席が終わった日は覚醒長やない
    assert data["summary"]["worked"] == 1
    html = sl.render_report(data)
    assert "活動・睡眠レポート" in html and "11/02(月)" in html

def test_report_cli_writes_file():
    cp, _ = env()
    r = subprocess.run([sys.executable, str(ROOT / "shiftlock.py"), "report", "--config", str(cp), "--days", "7"],
                       capture_output=True, text=True)
    assert r.returncode == 0 and (cp.parent / "report.html").exists()

def test_read_mood_and_report_column():
    cp, cfg = env()
    mf = cp.parent / "mood.csv"
    mf.write_text("\ufeff2026-11-02 08:30,-1,だるい\n2026-11-02 08:30,-1,だるい\n"
                  "2026-11-02 21:00,+2,アイデア止まらん, 寝れん\nこわれた行\n2026-11-03 09:00,5,\n"
                  "2026-11-03 10:15,0\n", encoding="utf-8")
    cfg["mood_file"] = str(mf)
    ms = sl.read_mood(cfg)
    assert [(m["v"], m["memo"]) for m in ms] == [(-1, "だるい"), (2, "アイデア止まらん, 寝れん"), (0, "")]
    data = sl.build_report(cfg, sl.Store(cp), dt("2026-11-03 20:00"), days=2)
    r = {x["date"]: x for x in data["rows"]}
    assert len(r["2026-11-02"]["mood"]) == 2 and data["summary"]["mood_down"] == 1 and data["summary"]["mood_up"] == 1
    html = sl.render_report(data)
    assert "▲▲+2" in html and "気分メモ" in html and "寝れん" in html
    cfg.pop("mood_file")
    assert "気分メモ" not in sl.render_report(sl.build_report(cfg, sl.Store(cp), dt("2026-11-03 20:00"), days=2))

ICS = """BEGIN:VCALENDAR\r
BEGIN:VEVENT\r
DTSTART;VALUE=DATE:20261105\r
DTEND;VALUE=DATE:20261107\r
SUMMARY:休み（通院）\r
END:VEVENT\r
BEGIN:VEVENT\r
DTSTART:20261107T010000Z\r
DTEND:20261107T020000Z\r
SUMMARY:有給\r
END:VEVENT\r
BEGIN:VEVENT\r
DTSTART;VALUE=DATE:20261108\r
SUMMARY:臨時出勤\r
END:VEVENT\r
BEGIN:VEVENT\r
DTSTART;VALUE=DATE:20261110\r
SUMMARY:休み\r
STATUS:CANCELLED\r
END:VEVENT\r
BEGIN:VEVENT\r
DTSTART;VALUE=DATE:20261111\r
SUMMARY:歯医者\r
END:VEVENT\r
BEGIN:VEVENT\r
DTSTART;TZID=Asia/Tokyo:20261112T090000\r
RRULE:FREQ=WEEKLY\r
SUMMARY:公休\r
  の予定\r
END:VEVENT\r
END:VCALENDAR\r
"""

def test_parse_ics():
    r = sl.parse_ics(ICS, ["休み", "公休", "有給"], ["出勤"])
    assert set(r["off"]) == {"2026-11-05", "2026-11-06", "2026-11-07", "2026-11-12"}
    assert set(r["work"]) == {"2026-11-08"} and r["recurring_skipped"] == 1
    assert r["off"]["2026-11-12"] == "公休 の予定"   # 折り返し行の連結

def test_calendar_sync_and_precedence():
    cp, cfg = env(days={})
    cfg = sl.load_config(cp)
    cfg["calendar"]["ics_url"] = "https://example.invalid/basic.ics"
    store = sl.Store(cp)
    now = dt("2026-11-01 08:00")
    assert sl.sync_calendar(cfg, store, now, fetch=lambda u: ICS).startswith("同期OK")
    assert sl.sync_calendar(cfg, store, now + timedelta(minutes=10), fetch=lambda u: 1 / 0).startswith("スキップ")
    msg = sl.sync_calendar(cfg, store, now + timedelta(minutes=70), fetch=lambda u: (_ for _ in ()).throw(OSError("down")))
    assert "同期失敗" in msg
    cfg = sl.load_config(cp)
    assert not sl.is_workday(cfg, dt("2026-11-05 00:00").date())     # カレンダーで休み
    assert sl.is_workday(cfg, dt("2026-11-08 00:00").date())         # 日曜だけど出勤
    assert sl.is_workday(cfg, dt("2026-11-04 00:00").date())         # 普通の水曜
    sl.days_set(store, dt("2026-11-05 00:00").date(), "work")         # 手動が優先
    sl.days_set(store, dt("2026-11-04 00:00").date(), "off", "用事")
    cfg = sl.load_config(cp)
    assert sl.is_workday(cfg, dt("2026-11-05 00:00").date())
    assert not sl.is_workday(cfg, dt("2026-11-04 00:00").date())
    txt = sl.schedule_text(cfg, dt("2026-11-02 00:00").date())
    assert "11/04水休（用事）" in txt and "11/08日出（臨時出勤）" in txt

def test_hook_day_keywords():
    cp, _ = env(days={})
    now = dt("2026-11-01 09:00")
    c, o, e = sl.run_hook(cp, UPS("11/5 休み 通院"), now)
    assert c == 2 and "11/05(木) を休みにした" in e
    assert json.loads((cp.parent / "days.json").read_text())["off"] == {"2026-11-05": "通院"}
    c, o, e = sl.run_hook(cp, UPS("11/5 戻す"), now)
    assert json.loads((cp.parent / "days.json").read_text())["off"] == {}
    c, o, e = sl.run_hook(cp, UPS("予定"), now)
    assert c == 2 and "11/01日休" in e and "11/02月出" in e
    # 「今日休む」は当日欠勤として state に入る（days.json には入らない）
    c, o, e = sl.run_hook(cp, UPS("今日休む"), dt("2026-11-02 17:00"))
    assert "休みにした" in e and json.loads((cp.parent / "days.json").read_text())["off"] == {}
    # 普通の文章は素通り
    assert sl.run_hook(cp, UPS("11/5 休みの日にやるタスクを整理して"), now)[0] == 0
    assert json.loads((cp.parent / "days.json").read_text())["off"] == {}
    c, o, e = sl.run_hook(cp, UPS("明日 出勤"), now)
    assert c == 2 and json.loads((cp.parent / "days.json").read_text())["work"] == {"2026-11-02": ""}

def test_parse_day():
    today = dt("2026-12-28 00:00").date()
    assert sl.parse_day("1/3", today).isoformat() == "2027-01-03"
    assert sl.parse_day("12/25", today).isoformat() == "2026-12-25"   # 3日前は今年
    assert sl.parse_day("明日", today).isoformat() == "2026-12-29"

def test_cleanup():
    cp, cfg = env(days={"off": {"2026-08-01": "", "2026-11-05": ""}, "times": {"2026-07-01": {}}})
    store = sl.Store(cp)
    (cp.parent / "state.json").write_text(json.dumps({"shifts": {"2026-08-01": {"status": "yasumi"}, "2026-10-30": {"status": "okita"}}}))
    (cp.parent / "log").mkdir()
    for ym in ("2026-03", "2026-05", "2026-10"):
        (cp.parent / "log" / f"pc-{ym}.jsonl").write_text("{}\n")
    (cp.parent / "events.log").write_text("2025-10-01T10:00\tx\tyasumi\t\n2026-10-01T10:00\ty\tyasumi\t\n")
    r = sl.cleanup(cfg, store, dt("2026-11-01 09:00"))
    assert r == {"days": 2, "state": 1, "logs": 1, "events": 1, "reports": 0}
    assert sorted(f.name for f in (cp.parent / "log").iterdir()) == ["pc-2026-05.jsonl", "pc-2026-10.jsonl"]
    # tick 経由でも 1 日 1 回だけ
    sl.run_tick(cp, 10, False, dt("2026-11-01 09:05"))
    m = json.loads((cp.parent / "maintenance.json").read_text())
    assert m["last_cleanup"] == "2026-11-01"

class FakeIO:
    def __init__(self, answers): self.a = list(answers); self.out = []
    def input(self, p): self.out.append(p); return self.a.pop(0)
    def print(self, *x): self.out.append(" ".join(map(str, x)))

def test_settings_menu():
    cp, _ = env(days={})
    io = FakeIO(["1", "月火水木金", "2", "19:30", "", "4", "x", "", "", "6", "",
                 "9", "p", "d1", "d1", "d1", "a", "EliteDangerous64.exe", "a", "KSP_x64", "", "", "12", "o", "11/5", "通院", "",
                 "s"])
    assert sl.settings_menu(cp, dt("2026-11-01 09:00"), io) is True
    raw = json.loads(cp.read_text())
    assert raw["work_weekdays"] == ["mon", "tue", "wed", "thu", "fri"]
    assert raw["start_target"] == "19:30" and raw["start_latest"] == "22:30"
    assert raw["game_processes"] == ["EliteDangerous64", "KSP_x64"]
    assert not any(k.startswith("_") for k in raw)
    assert json.loads((cp.parent / "days.json").read_text())["off"] == {"2026-11-05": "通院"}
    assert any("✗" in x for x in io.out)   # "x" は数値エラー → もう一度聞かれる
    assert raw["sleep_target_hours"] == 5 and raw["block_claude_on_hard"] is True
    # q は保存しない
    io = FakeIO(["3", "45", "q"])
    assert sl.settings_menu(cp, dt("2026-11-01 09:00"), io) is False
    assert json.loads(cp.read_text())["prep_minutes"] == 90

def test_legacy_shifts_list_still_works():
    cp, _ = env(days={}, work_weekdays=[], shifts=["2026-11-02", {"date": "2026-11-04", "start_target": "21:00"}])
    cfg = sl.load_config(cp)
    ps = sl.plans(cfg, dt("2026-11-02 00:00").date())
    assert [p.day for p in ps] == ["2026-11-02", "2026-11-04"] and ps[1].start_target == dt("2026-11-04 21:00")

def _gap(cp, a, b):
    (cp.parent / "usage-pc.json").write_text(json.dumps({"gaps": [[a, b]], "gap_start": None}))

def test_auto_okita_after_shift_counts_from_home_by():
    # 11/03(火)乗務 → 11/04(水)朝 帰ってすぐ寝る。PC は 11/03 18:20 から 11/04 13:20 まで無操作
    cp, cfg = env(days={})
    _gap(cp, "2026-11-03T18:20", "2026-11-04T13:20")
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-04 13:22"))
    d = json.loads(o)
    assert c == 0 and "自動で『起きた』" in d["systemMessage"] and "09:00〜13:20" in d["systemMessage"]
    st = json.loads((cp.parent / "state.json").read_text())["shifts"]["2026-11-04"]
    assert st["status"] == "okita"
    assert sl.run_hook(cp, UPS("x"), dt("2026-11-04 17:30"))[0] == 0      # ハード時間でも止まらん
    assert "auto 09:00-13:20" in (cp.parent / "events.log").read_text()

def test_auto_okita_respects_visit_and_short_sleep():
    # 11/02(月)乗務 → 11/03(火)。訪問看護 10-11 があるので 9-10 と 11-13:20 に割れて 4h に届かない
    cp, cfg = env(days={})
    _gap(cp, "2026-11-02T18:20", "2026-11-03T13:20")
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-03 13:22"))
    assert "自動" not in (json.loads(o)["systemMessage"] if o else "")
    assert "2026-11-03" not in json.loads((cp.parent / "state.json").read_text() if (cp.parent / "state.json").exists() else "{}").get("shifts", {})
    # 同じ火曜でも 11:00-15:30 寝てたら OK（13:22 に Claude を使った記録が無い別環境で）
    cp, cfg = env(days={})
    _gap(cp, "2026-11-03T11:00", "2026-11-03T15:30")
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-03 15:31"))
    assert "自動で『起きた』" in json.loads(o)["systemMessage"]

def test_auto_okita_split_by_claude_prompt():
    cp, cfg = env(days={})
    _gap(cp, "2026-11-03T18:20", "2026-11-04T13:20")
    sl.log_append(sl.Store(cp), "claude", dt("2026-11-04 11:00"), {"k": "p"})   # 途中で Claude 使ってた
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-04 13:22"))
    assert "自動" not in (json.loads(o)["systemMessage"] if o else "")

def test_auto_okita_not_on_off_day_and_can_disable():
    cp, cfg = env(days={}, auto_wake=False)
    _gap(cp, "2026-11-03T18:20", "2026-11-04T13:20")
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-04 13:22"))
    assert "自動" not in (json.loads(o)["systemMessage"] if o else "")

def test_auto_okita_via_tick():
    cp, cfg = env(days={})
    t = dt("2026-11-04 08:00")
    while t < dt("2026-11-04 13:00"):
        t += timedelta(minutes=5)
        sl.run_tick(cp, (t - dt("2026-11-04 08:30")).total_seconds() if t > dt("2026-11-04 08:30") else 10, False, t)
    a = sl.run_tick(cp, 20, False, dt("2026-11-04 13:05"))["actions"]
    assert any("自動で『起きた』" in x["text"] for x in a)

def test_norm_kw():
    assert sl.norm_kw("ねるわー") == "ねるわ" and sl.norm_kw("おきたぁ！") == "おきた"
    assert sl.norm_kw("おやすみ♡〜") == "おやすみ" and sl.norm_kw("起きたよｗ") == "起きたよ"

def test_neru_okita_keywords_and_auto_from_neru():
    cp, cfg = env(days={})
    c, o, e = sl.run_hook(cp, UPS("ねるわー"), dt("2026-11-04 07:10"))
    assert c == 2 and "おやすみ" in e and "07:10 から" in e
    # PC はずっと無操作 → 11:40 に触った（4.5h）→ 自動で起きた
    _gap(cp, "2026-11-04T07:09", "2026-11-04T11:40")
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-04 11:41"))
    assert "07:10〜11:40" in json.loads(o)["systemMessage"]
    # 手で『おきたぁ！』（別環境）
    cp, cfg = env(days={})
    sl.run_hook(cp, UPS("おやすみ"), dt("2026-11-04 07:10"))
    c, o, e = sl.run_hook(cp, UPS("おきたぁ！"), dt("2026-11-04 10:00"))
    assert c == 2 and "07:10〜10:00" in e and "最低ライン" in e

def test_phone_marks_and_kitaku():
    cp, cfg = env(days={})
    mf = cp.parent / "mood.csv"
    mf.write_text("2026-11-04 06:55,kitaku,\n2026-11-04 07:05,neru,\n2026-11-04 11:30,okita,\n2026-11-04 11:31,0,\n", encoding="utf-8")
    cfg_raw = json.loads(cp.read_text()); cfg_raw["mood_file"] = str(mf); cp.write_text(json.dumps(cfg_raw, ensure_ascii=False))
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-04 12:00"))
    assert "iPhone" in json.loads(o)["systemMessage"] and "07:05〜11:30" in json.loads(o)["systemMessage"]
    st = json.loads((cp.parent / "state.json").read_text())["shifts"]["2026-11-04"]
    assert st["status"] == "okita" and st["kitaku"].endswith("06:55") and st["sleep_at"].endswith("07:05")
    assert [m["v"] for m in sl.read_mood(sl.load_config(cp))] == [0]       # 気分には混ざらへん
    # 帰宅だけ記録 + PC無操作 → 帰宅時刻から数える（09:00 を待たない）
    cp, cfg = env(days={})
    mf = cp.parent / "mood.csv"; mf.write_text("2026-11-04 06:50,ただいま,\n", encoding="utf-8")
    cfg_raw = json.loads(cp.read_text()); cfg_raw["mood_file"] = str(mf); cp.write_text(json.dumps(cfg_raw, ensure_ascii=False))
    _gap(cp, "2026-11-03T18:20", "2026-11-04T11:00")
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-04 11:01"))
    assert "06:50〜11:00" in json.loads(o)["systemMessage"]

def test_auto_wake_neru_only_mode():
    cp, cfg = env(days={}, auto_wake="neru")
    _gap(cp, "2026-11-03T18:20", "2026-11-04T13:20")
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-04 13:22"))
    assert "自動" not in (json.loads(o)["systemMessage"] if o else "")      # ねる無しでは判定しない
    cp, cfg = env(days={}, auto_wake="neru")
    sl.run_hook(cp, UPS("ねる"), dt("2026-11-04 08:00"))
    _gap(cp, "2026-11-04T07:59", "2026-11-04T13:20")
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-04 13:22"))
    assert "08:00〜13:20" in json.loads(o)["systemMessage"]

def test_windows_install_keeps_existing_hooks():
    cp, _ = env(days={}); t = cp.parent
    win = t / "win" / "settings.json"; win.parent.mkdir()
    win.write_text("﻿" + json.dumps({"hooks": {"PreToolUse": [{"matcher": "Bash", "hooks": [{"type": "command", "command": "guard.sh"}]}]}}))
    sl.install(cp, t / "settings.json", t / "hooks", win)
    sl.install(cp, t / "settings.json", t / "hooks", win)
    d = json.loads(win.read_text())
    assert d["hooks"]["PreToolUse"][0]["hooks"][0]["command"] == "guard.sh"
    cmd = d["hooks"]["UserPromptSubmit"][0]["hooks"][0]["command"]
    assert "wsl.exe -e sh -c" in cmd and "MSYS_NO_PATHCONV=1" in cmd and len(d["hooks"]["Stop"]) == 1
    assert (win.parent / "settings.json.bak-shiftlock").exists()

def test_neru_on_free_day_just_logs():
    cp, cfg = env(days={})
    c, o, e = sl.run_hook(cp, UPS("ねるわ"), dt("2026-11-01 09:50"))   # 日曜朝、次は月曜 22:30（24h 超）
    assert c == 2 and "記録だけ" in e and "neru\tfree-day" in (cp.parent / "events.log").read_text()

INTENT_CASES = {
    "おうちかえった！ねむねむだからスヤリンコする！ｗ": ["kitaku", "neru"], "6時に帰庫して今おうち帰ってきた": ["kitaku"],
    "家帰ってきて即寝する": ["kitaku", "neru"], "ねむすぎるからもう寝るわー": ["neru"], "帰宅！ふとん入る": ["kitaku", "neru"],
    "おきたぁぁぁ": ["okita"], "おはよーくろーどっち♡": ["okita"], "起きたけどまた寝る": ["neru"], "寝たけどもう起きた": ["okita"],
    "ただいまー！": ["kitaku"], "スヤァ…": ["neru"], "そろそろねるね": ["neru"], "目が覚めた": ["okita"],
    # 拾ったらあかん
    "このテストが通らなくて寝れない": [], "寝る前にこのバグ直して": [], "眠いけどもうちょい頑張る": [], "たずねるべき？": [],
    "おきたらこれやっといて": [], "Pythonで重ねるやつ書いて": [], "ねむいー": [], "寝られへん…": [], "おきたいものがある": [],
    "rimworld の mod の起動で落ちる、ログ見てほしい": [], "寝たらこれ実行して": [], "夜は寝る前提で組んで": [],
    "このファイル読んで": [], "スクショ撮って": [], "ねるねるねるね食べたい": [], "かさねるとかまねるとか": [],
}

def test_detect_intents_cases():
    bad = {t: sl.detect_intents(t) for t, e in INTENT_CASES.items() if sl.detect_intents(t) != e}
    assert bad == {}

def test_fuzzy_records_without_blocking_and_undo():
    cp, cfg = env(days={})
    now = dt("2026-11-04 07:05")
    c, o, e = sl.run_hook(cp, UPS("おうちかえった！ねむねむだからスヤリンコする！ｗ"), now)
    d = json.loads(o)
    assert c == 0 and "おかえり" in d["systemMessage"] and "おやすみ" in d["systemMessage"] and "今のなし" in d["systemMessage"]
    assert "帰宅・就寝" in d["hookSpecificOutput"]["additionalContext"]
    st = json.loads((cp.parent / "state.json").read_text())["shifts"]["2026-11-04"]
    assert st["kitaku"].endswith("07:05") and st["sleep_at"].endswith("07:05")
    c, o, e = sl.run_hook(cp, UPS("今のなし"), now + timedelta(minutes=1))
    assert c == 2 and "帰宅・就寝" in e and "取り消した" in e
    assert "2026-11-04" not in json.loads((cp.parent / "state.json").read_text()).get("shifts", {})

def test_fuzzy_okita_lifts_hard_lock():
    cp, cfg = env(days={})
    c, o, e = sl.run_hook(cp, UPS("ねるわ"), dt("2026-11-04 12:00"))
    c, o, e = sl.run_hook(cp, UPS("おはよーやっと起きたぁ"), dt("2026-11-04 17:30"))   # ハード時間
    assert c == 0 and "12:00〜17:30" in json.loads(o)["systemMessage"]

def _ctx(o):
    return json.loads(o).get("hookSpecificOutput", {}).get("additionalContext", "") if o else ""

def test_ask_and_learn_yes():
    cp, cfg = env(days={})
    t0 = dt("2026-11-04 07:10")
    c, o, e = sl.run_hook(cp, UPS("おやすーﾑｷﾞｭ"), t0)
    assert c == 0 and "寝るってこと？" in _ctx(o) and "systemMessage" not in json.loads(o)
    c, o, e = sl.run_hook(cp, UPS("うん！"), t0 + timedelta(minutes=3))
    assert c == 2 and "おやすーむぎゅ" in e and "就寝として覚えた" in e and "07:10 から" in e   # 元の発言の時刻
    assert json.loads((cp.parent / "learned.json").read_text())["neru"] == ["おやすーむぎゅ"]
    # 次からは聞かずに拾う（半角/全角/語尾違いでも）
    cp2 = cp
    c, o, e = sl.run_hook(cp2, UPS("オヤスーむぎゅ〜♡"), dt("2026-11-04 08:30"))
    assert "おやすみ" in json.loads(o)["systemMessage"]

def test_ask_deny_with_other_and_plain_deny():
    cp, cfg = env(days={})
    t0 = dt("2026-11-04 12:00")
    c, o, e = sl.run_hook(cp, UPS("むくりんちょ"), t0)
    assert "起きたってこと？" in _ctx(o)
    c, o, e = sl.run_hook(cp, UPS("ちゃう、寝る"), t0 + timedelta(minutes=1))
    assert c == 2 and "就寝として覚えた" in e
    assert json.loads((cp.parent / "learned.json").read_text())["neru"] == ["むくりんちょ"]
    # ただの『ちゃう』→ 何もしない言い方として覚える。30 分クールダウン明けでも同じ文では聞かへん
    cp, cfg = env(days={})
    sl.run_hook(cp, UPS("眠いけどもうちょい頑張る"), t0)
    c, o, e = sl.run_hook(cp, UPS("ちゃう"), t0 + timedelta(minutes=1))
    assert c == 2 and "何もせえへん言い方" in e
    c, o, e = sl.run_hook(cp, UPS("眠いけどもうちょい頑張る！"), t0 + timedelta(hours=1))
    assert "ってこと？" not in _ctx(o)

def test_ask_cooldown_and_non_answer_clears():
    cp, cfg = env(days={})
    t0 = dt("2026-11-04 12:00")
    sl.run_hook(cp, UPS("ねむみ"), t0)
    c, o, e = sl.run_hook(cp, UPS("このログの意味わかる？"), t0 + timedelta(minutes=1))   # 返事やない → 普通に通る
    assert c == 0 and "pending" not in json.loads((cp.parent / "state.json").read_text())
    c, o, e = sl.run_hook(cp, UPS("ねむみ"), t0 + timedelta(minutes=5))                  # クールダウン中は聞かへん
    assert "ってこと？" not in _ctx(o)
    c, o, e = sl.run_hook(cp, UPS("ねむみ"), t0 + timedelta(minutes=40))
    assert "寝るってこと？" in _ctx(o)

def test_weak_hint_false_positives():
    for t in ("バグに気がついた", "おはしどこやったっけ", "かえって遅くなった", "起動でこける", "この関数の戻り値"):
        assert sl.weak_intent(t) is None or t == "起動でこける" and sl.weak_intent(t) is None, t

def test_halfwidth_exact_keyword():
    cp, cfg = env(days={})
    c, o, e = sl.run_hook(cp, UPS("ｵﾔｽﾐ"), dt("2026-11-04 07:10"))
    assert c == 2 and "おやすみ" in e

def test_closing_period():
    D = lambda x: dt(x + " 00:00").date()
    assert sl.closing_period(D("2026-11-01"), 20) == (D("2026-10-21"), D("2026-11-20"))
    assert sl.closing_period(D("2026-11-20"), 20) == (D("2026-10-21"), D("2026-11-20"))
    assert sl.closing_period(D("2026-11-21"), 20) == (D("2026-11-21"), D("2026-12-20"))
    assert sl.closing_period(D("2026-01-05"), 20) == (D("2025-12-21"), D("2026-01-20"))
    assert sl.closing_period(D("2026-12-25"), 20) == (D("2026-12-21"), D("2027-01-20"))
    assert sl.closing_period(D("2026-03-01"), 31) == (D("2026-03-01"), D("2026-03-31"))   # 月末締め

def test_period_reports_auto_and_retention():
    cp, cfg = env(days={})
    store = sl.Store(cp)
    rd = cp.parent / "reports"; rd.mkdir()
    old = rd / "report_2026-06-21_2026-07-20.html"; old.write_text("x")              # 3か月より前 → 消える
    keep = rd / "report_2026-08-21_2026-09-20.pdf"; keep.write_text("x")             # 3か月以内 → 残る
    files = sl.write_period_reports(cfg, store, dt("2026-11-03 09:00"))
    assert [f.name for f in files] == ["report_2026-09-21_2026-10-20.html", "report_2026-10-21_2026-11-20.html"]
    html = files[1].read_text(encoding="utf-8")
    assert "2026-10-21 〜 2026-11-20" in html and "11/20 締めの途中経過" in html   # 途中経過でも締め日までの行
    assert "10/20 締め）" in files[0].read_text(encoding="utf-8")
    assert not old.exists() and keep.exists() and (cp.parent / "report.html").exists()
    r = subprocess.run([sys.executable, str(ROOT / "shiftlock.py"), "report", "--auto", "--config", str(cp)],
                       capture_output=True, text=True)
    assert r.returncode == 0 and r.stdout.count("FILE\t") == 2


def test_report_no_fake_attendance_before_install():
    cp, cfg = env(days={})
    store = sl.Store(cp)
    data = sl.build_report(cfg, store, dt("2026-11-03 09:00"), period=(dt("2026-10-21 00:00").date(), dt("2026-11-20 00:00").date()))
    assert data["summary"]["planned"] == 0 and all(r["shift"] is None for r in data["rows"])   # ログなし → 全部空欄
    sl.log_append(store, "pc", dt("2026-11-01 09:13"), {"idle": 0, "game": 0})
    data = sl.build_report(cfg, store, dt("2026-11-03 09:00"), period=(dt("2026-10-21 00:00").date(), dt("2026-11-20 00:00").date()))
    r = {x["date"]: x for x in data["rows"]}
    assert r["2026-10-31"]["shift"] is None and r["2026-11-02"]["shift"] == "unconfirmed"
    assert data["summary"]["planned"] == 1 and "記録は 11/01 から" in sl.render_report(data)

def test_help_subcommand(capsys):
    cp, _ = env(days={})
    assert sl.main(["help"]) == 0
    out = capsys.readouterr().out
    assert "shl status" in out and "shl neru" in out and "shl settings" in out and "使い方" in out
    assert sl.main(["help", "--config", str(cp)]) == 0      # --config が付いてても動く（shl ラッパーは常に付ける）

def _ics(evs):
    body = "".join(f"BEGIN:VEVENT\nDTSTART;VALUE=DATE:{d}\nDTEND;VALUE=DATE:{d}\nSUMMARY:{s}\nEND:VEVENT\n" for d, s in evs)
    return "BEGIN:VCALENDAR\n" + body + "END:VCALENDAR\n"

def test_calendar_sources_compat():
    cp, cfg = env(days={})
    assert sl.calendar_sources(cfg["calendar"]) == []                        # 未設定
    cfg["calendar"]["ics_url"] = "https://example.invalid/a.ics"
    src = sl.calendar_sources(cfg["calendar"])
    assert len(src) == 1 and src[0]["name"] == "カレンダー" and src[0]["off_keywords"] == ["休み", "公休", "有給"]
    cfg["calendar"]["sources"] = [{"name": "通院", "ics_url": "https://example.invalid/b.ics", "off_keywords": ["通院"]},
                                  {"ics_url": "https://example.invalid/a.ics"}]          # 同じ URL は 1 つ
    src = sl.calendar_sources(cfg["calendar"])
    assert [s["name"] for s in src] == ["カレンダー", "通院"] and src[1]["work_keywords"] == ["出勤"]

def test_calendar_multi_sources_precedence_and_failure():
    cp, cfg = env(days={})
    cfg["calendar"]["sources"] = [{"name": "シフト", "ics_url": "https://s1/", "off_keywords": ["休み"], "work_keywords": ["出勤"]},
                                  {"name": "通院", "ics_url": "https://s2/", "off_keywords": ["通院"], "work_keywords": []}]
    store = sl.Store(cp); now = dt("2026-11-01 08:00")
    ics = {"https://s1/": _ics([("20261105", "出勤")]), "https://s2/": _ics([("20261105", "通院"), ("20261106", "通院")])}
    msg = sl.sync_calendar(cfg, store, now, fetch=lambda u: ics[u])
    assert msg.count("同期OK") == 2
    cj = json.loads((cp.parent / "calendar.json").read_text())
    assert list(cj["sources"]) == ["シフト", "通院"]
    c2 = sl.load_config(cp)
    assert c2["_days"]["work"]["2026-11-05"] == "出勤" and "2026-11-05" not in c2["_days"]["off"]   # 上のソースが勝つ
    assert not sl.is_workday(c2, dt("2026-11-06 00:00").date())
    # 2 回目: 通院が落ちる → 前回の内容＋エラー．シフトは更新される
    ics["https://s1/"] = _ics([("20261107", "休み")])
    def f(u):
        if u == "https://s2/":
            raise OSError("down")
        return ics[u]
    msg = sl.sync_calendar(cfg, store, now + timedelta(minutes=70), fetch=f)
    assert "同期失敗" in msg and "同期OK" in msg
    cj = json.loads((cp.parent / "calendar.json").read_text())
    assert cj["sources"]["通院"]["error"] and cj["sources"]["通院"]["off"]["2026-11-06"] == "通院"
    assert cj["sources"]["シフト"]["off"]["2026-11-07"] == "休み" and "2026-11-05" not in cj["sources"]["シフト"]["work"]
    c3 = sl.load_config(cp)
    assert not sl.is_workday(c3, dt("2026-11-05 00:00").date())      # シフト側の出勤が消えたので通院の休みが効く
    sl.days_set(store, dt("2026-11-06 00:00").date(), "work", "臨時")
    assert sl.is_workday(sl.load_config(cp), dt("2026-11-06 00:00").date())   # 手動が優先
    txt = sl.status_text(cfg, store, now + timedelta(minutes=71))      # sources は cfg（メモリ）側にある
    assert "カレンダー「通院」" in txt and "エラー" in txt and "カレンダー「シフト」" in txt

def test_calendar_legacy_json_read():
    cp, cfg = env(days={})
    (cp.parent / "calendar.json").write_text(json.dumps({"fetched": "2026-11-01T08:00:00", "off": {"2026-11-05": "公休"}, "work": {}}), encoding="utf-8")
    c = sl.load_config(cp)
    assert not sl.is_workday(c, dt("2026-11-05 00:00").date())
    # 旧 ics_url 設定のまま同期に失敗しても，旧ファイルの内容を前回分として引き継ぐ
    c["calendar"]["ics_url"] = "https://x/"
    msg = sl.sync_calendar(c, sl.Store(cp), dt("2026-11-01 10:00"), fetch=lambda u: (_ for _ in ()).throw(OSError("x")))
    assert "同期失敗" in msg and not sl.is_workday(sl.load_config(cp), dt("2026-11-05 00:00").date())
    cj = json.loads((cp.parent / "calendar.json").read_text())
    assert cj["sources"]["カレンダー"]["off"] == {"2026-11-05": "公休"}

def test_settings_menu_calendar_sources():
    cp, _ = env(days={})
    raw = json.loads(cp.read_text()); raw["calendar"]["ics_url"] = "https://old/"; cp.write_text(json.dumps(raw, ensure_ascii=False))
    io = FakeIO(["11", "a", "通院", "https://new/", "通院,受診", "", "", "s"])
    assert sl.settings_menu(cp, dt("2026-11-01 09:00"), io) is True
    cal = json.loads(cp.read_text())["calendar"]
    assert "ics_url" not in cal and [s["name"] for s in cal["sources"]] == ["カレンダー", "通院"]
    assert cal["sources"][0]["ics_url"] == "https://old/" and cal["sources"][1]["off_keywords"] == ["通院", "受診"]
    assert "work_keywords" not in cal["sources"][1]        # Enter は既定のまま（書かない）

def test_sound_resolution_priority_and_single():
    cp, _ = env(days={})
    wav = cp.parent / "a.wav"; wav.write_bytes(b"RIFF")
    mp3 = cp.parent / "b.mp3"; mp3.write_bytes(b"ID3")
    raw = json.loads(cp.read_text()); raw["notify"] = {"sound": {"default": str(wav), "hard": str(mp3)}}
    cp.write_text(json.dumps(raw, ensure_ascii=False))
    t = dt("2026-11-02 15:00")
    for i in range(24):                                   # 15:00〜16:55 ずっと触ってる
        sl.run_tick(cp, 10, False, t + timedelta(minutes=5 * i))
    r = sl.run_tick(cp, 10, False, dt("2026-11-02 17:00"))   # ハード突入 ＋ 連続 2h の声かけ
    kinds = [a["kind"] for a in r["actions"]]
    assert kinds == ["hard", "nag"]
    assert [a["sound"] for a in r["actions"]] == [str(mp3), ""]     # 音は 1 つだけ（強い方）
    assert r["sound"] == {"volume": 0.8, "max_seconds": 20}
    # 乗務のない日曜: 声かけだけ → default にフォールバック（時刻を戻すので別環境で）
    cp, _ = env(days={})
    raw = json.loads(cp.read_text()); raw["notify"] = {"sound": {"default": str(wav), "hard": str(mp3)}}
    cp.write_text(json.dumps(raw, ensure_ascii=False))
    t = dt("2026-11-01 09:00")
    for i in range(24):
        sl.run_tick(cp, 10, False, t + timedelta(minutes=5 * i))
    r = sl.run_tick(cp, 10, False, dt("2026-11-01 11:00"))
    assert [a["kind"] for a in r["actions"]] == ["nag"] and r["actions"][0]["sound"] == str(wav)

def test_sound_missing_file_gives_empty_and_warns():
    cp, _ = env(days={})
    raw = json.loads(cp.read_text()); raw["notify"] = {"sound": {"default": "C:\\nope\\x.wav"}}
    cp.write_text(json.dumps(raw, ensure_ascii=False))
    r = sl.run_tick(cp, 10, False, dt("2026-11-02 12:55"))     # lastcall トースト
    assert r["actions"][0]["sound"] == "" and len(r["warnings"]) == 1 and "x.wav" in r["warnings"][0]
    r = sl.run_tick(cp, 10, False, dt("2026-11-02 13:30"))     # soft
    assert r["actions"] and r["warnings"] == []                 # 同じ日は 2 回言わへん

def test_settings_menu_sound():
    cp, _ = env(days={})
    io = FakeIO(["17", "4", "C:\\s\\hard.mp3", "v", "0.5", "", "s"])
    assert sl.settings_menu(cp, dt("2026-11-01 09:00"), io) is True
    n = json.loads(cp.read_text())["notify"]
    assert n["sound"]["hard"] == "C:\\s\\hard.mp3" and n["sound_volume"] == 0.5

def _pc_log(cp, start, end, idle=10, game=0):
    """pc ログを 5 分おきに書く（記録開始日の判定と在宅ヒント用）"""
    d = cp.parent / "log"; d.mkdir(exist_ok=True)
    t = dt(start); e = dt(end)
    while t <= e:
        with open(d / f"pc-{t:%Y-%m}.jsonl", "a", encoding="utf-8") as f:
            f.write(json.dumps({"t": t.isoformat(timespec="seconds"), "idle": idle, "game": game}) + "\n")
        t += timedelta(minutes=5)

def _st(cp):
    p = cp.parent / "state.json"
    return json.loads(p.read_text()).get("shifts", {}) if p.exists() else {}

def test_today_yasumi_after_start_latest():
    cp, _ = env(days={})
    c, o, e = sl.run_hook(cp, UPS("今日休む"), dt("2026-11-02 23:00"))
    assert c == 2 and "11/02" in e and _st(cp)["2026-11-02"]["status"] == "yasumi" and "2026-11-03" not in _st(cp)
    cp, _ = env(days={})                                              # 出庫最終の前なら従来どおり今日
    sl.run_hook(cp, UPS("今日休む"), dt("2026-11-02 17:00"))
    assert _st(cp)["2026-11-02"]["status"] == "yasumi"
    cp, _ = env(days={})                                              # 乗務のない日曜の夜は翌日が対象
    sl.run_hook(cp, UPS("今日休む"), dt("2026-11-01 23:00"))
    assert _st(cp)["2026-11-02"]["status"] == "yasumi"

def test_past_report_keywords():
    cp, _ = env(days={})
    now = dt("2026-11-03 10:00")
    c, o, e = sl.run_hook(cp, UPS("休んだ"), now)                    # 直近の終わった乗務 = 11/2
    assert c == 2 and "11/02" in e and _st(cp)["2026-11-02"]["status"] == "yasumi"
    assert "late" in (cp.parent / "events.log").read_text()
    c, o, e = sl.run_hook(cp, UPS("今のなし"), now)
    assert "取り消した" in e and "status" not in _st(cp).get("2026-11-02", {})
    c, o, e = sl.run_hook(cp, UPS("昨日 出た"), now)
    assert c == 2 and _st(cp)["2026-11-02"]["status"] == "worked"
    c, o, e = sl.run_hook(cp, UPS("11/1 出た"), now)                  # 日曜（休み）に出た → 出勤の例外 + worked
    assert c == 2 and _st(cp)["2026-11-01"]["status"] == "worked"
    assert "2026-11-01" in json.loads((cp.parent / "days.json").read_text())["work"]
    c, o, e = sl.run_hook(cp, UPS("10/25 休んだ"), now)               # 日曜 = もともと休み（7 日以上前でも去年扱いにならへん）
    assert c == 2 and "もともと休み" in e
    c, o, e = sl.run_hook(cp, UPS("11/3 休んだ"), dt("2026-11-15 10:00"))   # 12 日前の事後報告
    assert c == 2 and "11/03" in e and _st(cp)["2026-11-03"]["status"] == "yasumi"
    sl.run_hook(cp, UPS("今のなし"), dt("2026-11-15 10:00"))
    c, o, e = sl.run_hook(cp, UPS("11/3 休んだ"), now)                # まだ終わってへん
    assert c == 2 and "まだ終わってへん" in e and "status" not in _st(cp).get("2026-11-03", {})
    c, o, e = sl.run_hook(cp, UPS("一昨日休んだ 通院"), dt("2026-11-04 10:00"))
    assert c == 2 and "11/02" in e and _st(cp)["2026-11-02"]["status"] == "yasumi"
    # CLI 相当（shl yasumi 11/2 / shl deru 11/2）
    cfg = sl.load_config(cp)
    assert "出たことにした" in sl.late_report(cfg, sl.Store(cp), dt("2026-11-04 10:00"), "worked", "11/2", via="shl")
    assert _st(cp)["2026-11-02"]["status"] == "worked"

def test_past_report_does_not_eat_normal_prompt():
    cp, _ = env(days={})
    now = dt("2026-11-03 10:00")
    assert sl.run_hook(cp, UPS("出たエラーを直して"), now)[0] == 0
    assert sl.run_hook(cp, UPS("出た ログを見て"), now)[0] == 0          # 『出た』にメモは付けへん（誤爆防止）
    assert sl.run_hook(cp, UPS("休んだ理由を考えて"), now)[0] == 0
    assert sl.run_hook(cp, UPS("出た"), now)[0] == 2

def test_shuppatsu_sets_worked_and_phase_free():
    cp, cfg = env(days={})
    c, o, e = sl.run_hook(cp, UPS("行ってきまーす"), dt("2026-11-02 16:40"))   # 出発目安 18:30 の 2 時間前から
    assert c == 2 and "行ってらっしゃい" in e and _st(cp)["2026-11-02"]["status"] == "worked"
    store = sl.Store(cp)
    assert sl.current(cfg, store.shift_status(), dt("2026-11-02 17:30"))[0] == "free"
    assert sl.run_hook(cp, UPS("x"), dt("2026-11-02 17:30"))[0] == 0        # ハードでも止まらへん
    assert sl.run_tick(cp, 10, False, dt("2026-11-02 17:30"))["actions"] == []
    c, o, e = sl.run_hook(cp, UPS("行ってきます"), dt("2026-11-01 10:00"))   # 日曜: 24h 以内に乗務なし
    assert c == 2 and "乗務の予定はない" in e

def test_unconfirmed_ask_once_tick_and_hook():
    cp, cfg = env(days={})
    _pc_log(cp, "2026-11-02 12:00", "2026-11-03 09:55", idle=3600)        # 記録開始 11/2。乗務中は無操作
    r = sl.run_tick(cp, 10, False, dt("2026-11-03 10:00"))
    conf = [a for a in r["actions"] if a["kind"] == "confirm"]
    assert len(conf) == 1 and "11/02" in conf[0]["text"] and "家におった" not in conf[0]["text"]
    assert sl.run_tick(cp, 10, False, dt("2026-11-03 10:05"))["actions"] == []   # 2 回は聞かへん
    assert _st(cp)["2026-11-02"]["confirm_asked"].startswith("2026-11-03T10:00")
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-03 10:10"))
    assert c == 0 and "出た？" not in (json.loads(o).get("hookSpecificOutput", {}).get("additionalContext", "") if o else "")
    assert "未確認の乗務: 11/02" in sl.status_text(sl.load_config(cp), sl.Store(cp), dt("2026-11-03 10:10"))
    assert "11/02" in sl.kakunin_text(sl.load_config(cp), sl.Store(cp), dt("2026-11-03 10:10"))
    c, o, e = sl.run_hook(cp, UPS("休んだ"), dt("2026-11-03 10:20"))                 # 日付なし → 聞いた乗務
    assert "11/02" in e and _st(cp)["2026-11-02"]["status"] == "yasumi"
    assert "未確認の乗務" not in sl.status_text(sl.load_config(cp), sl.Store(cp), dt("2026-11-03 10:30"))
    # hook が先に聞く場合（別環境）＋ 在宅ヒント
    cp, cfg = env(days={})
    _pc_log(cp, "2026-11-02 12:00", "2026-11-03 09:55", idle=10)                     # 乗務中もずっと触ってた
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-03 10:00"))
    ctx = json.loads(o)["hookSpecificOutput"]["additionalContext"]
    assert "11/02(月) の乗務は出た？休んだ？" in ctx and "家におった" in ctx
    assert sl.run_tick(cp, 10, False, dt("2026-11-03 10:05"))["actions"] == []       # tick 側はもう聞かへん
    # okita の乗務は聞かない（既定）／ okita_means_worked=false なら聞く
    cp, cfg = env(days={})
    _pc_log(cp, "2026-11-02 12:00", "2026-11-03 09:55", idle=3600)
    sl.Store(cp).set_shift_status("2026-11-02", "okita", dt("2026-11-02 15:00"))
    assert sl.run_tick(cp, 10, False, dt("2026-11-03 10:00"))["actions"] == []
    cp, cfg = env(days={}, confirm={"okita_means_worked": False})
    _pc_log(cp, "2026-11-02 12:00", "2026-11-03 09:55", idle=3600)
    sl.Store(cp).set_shift_status("2026-11-02", "okita", dt("2026-11-02 15:00"))
    assert [a["kind"] for a in sl.run_tick(cp, 10, False, dt("2026-11-03 10:00"))["actions"]] == ["confirm"]
    # mode=off なら聞かない
    cp, cfg = env(days={}, confirm={"mode": "off"})
    _pc_log(cp, "2026-11-02 12:00", "2026-11-03 09:55", idle=3600)
    assert sl.run_tick(cp, 10, False, dt("2026-11-03 10:00"))["actions"] == []

def test_report_unconfirmed_and_summary():
    cp, cfg = env(days={})
    _pc_log(cp, "2026-11-01 00:00", "2026-11-04 12:00", idle=3600)
    (cp.parent / "state.json").write_text(json.dumps({"confirm_since": "2026-11-01"}))
    store = sl.Store(cp)
    store.set_shift_status("2026-11-02", "worked", dt("2026-11-02 18:00"), "shuppatsu")
    sl.late_report(cfg, store, dt("2026-11-04 10:00"), "yasumi", "11/3")
    data = sl.build_report(cfg, store, dt("2026-11-04 12:00"), days=4)
    rows = {r["date"]: r["shift"] for r in data["rows"]}
    assert rows["2026-11-02"] == "worked" and rows["2026-11-03"] == "yasumi" and rows["2026-11-04"] == "planned"
    assert data["summary"]["yasumi_late"] == 1 and data["summary"]["unconfirmed"] == 0 and data["summary"]["worked"] == 1
    store.set_shift_status("2026-11-02", None, dt("2026-11-04 12:00"))                # 確認を消す → 未確認
    data = sl.build_report(cfg, store, dt("2026-11-04 12:00"), days=4)
    rows = {r["date"]: r["shift"] for r in data["rows"]}
    assert rows["2026-11-02"] == "unconfirmed" and data["summary"]["unconfirmed"] == 1 and data["summary"]["worked"] == 1
    html = sl.render_report(data)
    assert "出?" in html and "未確認 1" in html

def test_phone_yasumi_mark_idempotent():
    cp, cfg = env(days={})
    mf = cp.parent / "mood.csv"
    mf.write_text("2026-11-03 09:00,yasumi:2026-11-02,\n2026-11-03 09:01,-1,\n", encoding="utf-8")
    raw = json.loads(cp.read_text()); raw["mood_file"] = str(mf); cp.write_text(json.dumps(raw, ensure_ascii=False))
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-03 10:00"))
    assert "11/02" in json.loads(o)["systemMessage"] and _st(cp)["2026-11-02"]["status"] == "yasumi"
    ev = (cp.parent / "events.log").read_text()
    sl.run_hook(cp, UPS("x"), dt("2026-11-03 10:05"))
    assert (cp.parent / "events.log").read_text() == ev                   # 2 回目は何もしない
    assert [m["v"] for m in sl.read_mood(sl.load_config(cp))] == [-1]
    # 日付なしの deta → 記録時点で直近に終わった乗務
    mf.write_text("2026-11-03 09:00,deta,\n", encoding="utf-8")
    cp2, _ = env(days={})
    raw = json.loads(cp2.read_text()); raw["mood_file"] = str(mf); cp2.write_text(json.dumps(raw, ensure_ascii=False))
    sl.run_hook(cp2, UPS("x"), dt("2026-11-03 10:00"))
    assert _st(cp2)["2026-11-02"]["status"] == "worked"

def test_settings_menu_confirm():
    cp, _ = env(days={})
    io = FakeIO(["18", "n", "14", "n", "s"])
    assert sl.settings_menu(cp, dt("2026-11-01 09:00"), io) is True
    assert json.loads(cp.read_text())["confirm"] == {"mode": "off", "lookback_days": 14, "okita_means_worked": False}

def _enf(cp, action="lock", hours=2, when="2026-11-02 12:00", answer="y"):
    cfg = sl.load_config(cp); io = FakeIO([answer])
    return sl.enforce_declare(cfg, sl.Store(cp), dt(when), action, hours, io), io

def _enf_ticks(cp, s, idle=10, **kw):
    return [a for a in sl.run_tick(cp, idle, False, dt(s), **kw)["actions"] if a["kind"] == "enforce"]

def test_enforce_declare_requires_interactive_yes():
    cp, _ = env(days={})
    msg, io = _enf(cp, answer="n")
    assert "やめといた" in msg and "enforce" not in _st(cp).get("2026-11-02", {})
    assert any("ロック" in x and "kaijo" in x for x in io.out)                 # 内容（何を・解除方法）を表示してから聞く
    class EOFIO(FakeIO):
        def input(self, p): raise EOFError
    msg = sl.enforce_declare(sl.load_config(cp), sl.Store(cp), dt("2026-11-02 12:00"), "lock", 2, EOFIO([]))
    assert "やめといた" in msg and "enforce" not in _st(cp).get("2026-11-02", {})   # 非対話（Claude Code の Bash）は同意できへん
    msg = sl.enforce_declare(sl.load_config(cp), sl.Store(cp), dt("2026-11-02 12:00"), "lock", 2, sl.StdIO())
    assert "対話できる端末" in msg and "enforce" not in _st(cp).get("2026-11-02", {})   # pytest の stdin は端末やない（パイプ経由も同じ）
    # 端末から y を打っても、Windows のダイアログで『はい』が無ければ宣言なし（擬似端末では真似できへん）
    import io as _io
    class TTY(_io.StringIO):
        def isatty(self): return True
    old_stdin, old_confirm = sys.stdin, sl.windows_confirm
    try:
        sys.stdin = TTY("y\n"); sl.enforce.windows_confirm = lambda text, timeout=60: False
        msg = sl.enforce_declare(sl.load_config(cp), sl.Store(cp), dt("2026-11-02 12:00"), "lock", 2, sl.StdIO())
        assert "ダイアログ" in msg and "enforce" not in _st(cp).get("2026-11-02", {})
        sys.stdin = TTY("y\n"); sl.enforce.windows_confirm = lambda text, timeout=60: True
        msg = sl.enforce_declare(sl.load_config(cp), sl.Store(cp), dt("2026-11-02 12:00"), "lock", 2, sl.StdIO())
        assert "強制モード" in msg and _st(cp)["2026-11-02"]["enforce"]["consented_at"]
        sl.Store(cp).set_shift_status("2026-11-02", None, dt("2026-11-02 12:01")); sl.set_enforce(sl.Store(cp), "2026-11-02", None, dt("2026-11-02 12:01"), "enforce_off")
    finally:
        sys.stdin, sl.enforce.windows_confirm = old_stdin, old_confirm
    msg, io = _enf(cp)
    e = _st(cp)["2026-11-02"]["enforce"]
    assert "強制モード" in msg and e["consented_at"] and e["action"] == "lock" and e["duration_hours"] == 2 and e["runs"] == []
    ev = (cp.parent / "events.log").read_text()
    assert "enforce_declare" in ev and "enforce_consent" in ev
    assert "ロック" in _enf(cp, action="LOCK", hours="3h")[0] and _st(cp)["2026-11-02"]["enforce"]["duration_hours"] == 3   # 宣言し直し
    assert "lock / sleep / shutdown" in _enf(cp, action="reboot")[0]
    cp, _ = env(days={}); sl.run_hook(cp, UPS("今日休む"), dt("2026-11-02 12:00"))
    assert "休み" in _enf(cp)[0]                                               # 休みの乗務には宣言できへん

def test_enforce_sequence():
    cp, _ = env(days={}); _enf(cp)
    t = lambda s, idle=10: _enf_ticks(cp, s, idle=idle, uptime_seconds=3600)
    assert t("2026-11-02 16:55") == []                                          # ハード前は何もない
    a = t("2026-11-02 17:00"); assert len(a) == 1 and a[0]["method"] == "popup" and "15分後" in a[0]["text"] and a[0]["speak"]
    assert _st(cp)["2026-11-02"]["enforce"]["warned_at"].startswith("2026-11-02T17:00")
    assert t("2026-11-02 17:05") == []
    a = t("2026-11-02 17:10"); assert len(a) == 1 and "あと5分" in a[0]["text"]
    a = t("2026-11-02 17:15"); assert len(a) == 1 and a[0]["method"] == "enforce" and a[0]["action"] == "lock"
    e = _st(cp)["2026-11-02"]["enforce"]; assert len(e["runs"]) == 1 and e["first_run_at"].startswith("2026-11-02T17:15")
    assert "enforce_run" in (cp.parent / "events.log").read_text()
    assert t("2026-11-02 17:20", idle=1500) == []                               # ロック中（離席扱い）は何もしない
    a = t("2026-11-02 17:25"); assert len(a) == 1 and "5分後にまた" in a[0]["text"]   # 戻ってきた → 再予告
    assert t("2026-11-02 17:28") == []
    a = t("2026-11-02 17:30"); assert a[0]["method"] == "enforce"
    assert len(_st(cp)["2026-11-02"]["enforce"]["runs"]) == 2
    assert t("2026-11-02 19:20") == [] and t("2026-11-02 19:25") == []          # 最初の実行から 2h → 以後なにもしない
    # 予告が出た tick の音は enforce 種別
    cp, _ = env(days={}); _enf(cp)
    wav = cp.parent / "e.wav"; wav.write_bytes(b"RIFF")
    raw = json.loads(cp.read_text()); raw["notify"] = {"sound": {"enforce": str(wav)}}; cp.write_text(json.dumps(raw, ensure_ascii=False))
    acts = sl.run_tick(cp, 10, False, dt("2026-11-02 17:00"), uptime_seconds=3600)["actions"]
    assert [a["sound"] for a in acts if a["kind"] == "enforce"] == [str(wav)]

def test_enforce_never_fires_without_consent_or_with_stop_or_boot_or_idle_or_maxruns():
    cp, _ = env(days={})                                                        # 宣言なし
    assert _enf_ticks(cp, "2026-11-02 17:00", uptime_seconds=3600) == []
    cp, _ = env(days={}); _enf(cp); (cp.parent / "STOP.txt").write_text("test")   # STOP.txt → 予告も出ない
    assert _enf_ticks(cp, "2026-11-02 17:00", uptime_seconds=3600) == [] and "warned_at" not in _st(cp)["2026-11-02"]["enforce"]
    cp, _ = env(days={}); _enf(cp)
    assert _enf_ticks(cp, "2026-11-02 17:00", uptime_seconds=300) == []         # 起動直後
    assert _enf_ticks(cp, "2026-11-02 17:05") == []                             # uptime 不明（古い guard）
    assert _enf_ticks(cp, "2026-11-02 17:10", idle=3000, uptime_seconds=3600) == []   # 離席中
    assert _enf_ticks(cp, "2026-11-02 17:15", uptime_seconds=3600, remote=1) == []    # リモート操作中
    assert "warned_at" not in _st(cp)["2026-11-02"]["enforce"]
    cp, _ = env(days={}, enforce={"max_runs": 1}); _enf(cp)                      # 回数上限
    for s in ("2026-11-02 17:00", "2026-11-02 17:10"):
        _enf_ticks(cp, s, uptime_seconds=3600)
    assert _enf_ticks(cp, "2026-11-02 17:15", uptime_seconds=3600)[0]["method"] == "enforce"
    assert _enf_ticks(cp, "2026-11-02 17:25", uptime_seconds=3600) == []
    cp, _ = env(days={}); _enf(cp); sl.run_hook(cp, UPS("今日休む"), dt("2026-11-02 16:00"))   # 休みにしたら止まる
    assert _enf_ticks(cp, "2026-11-02 17:00", uptime_seconds=3600) == []

def test_enforce_off_and_release_and_resume():
    cp, _ = env(days={}); _enf(cp)
    cfg = sl.load_config(cp); store = sl.Store(cp)
    assert "取り消した" in sl.enforce_off(cfg, store, dt("2026-11-02 15:00")) and "enforce" not in _st(cp)["2026-11-02"]
    _enf(cp)
    msg = sl.enforce_off(cfg, store, dt("2026-11-02 17:30"))                     # ハードに入ってからは解除扱い
    assert "解除" in msg and (cp.parent / "STOP.txt").exists() and _st(cp)["2026-11-02"]["enforce"]["released_at"]
    assert "enforce_release" in (cp.parent / "events.log").read_text()
    assert _enf_ticks(cp, "2026-11-02 17:35", uptime_seconds=3600) == []
    assert "再開" in sl.enforce_resume(store, dt("2026-11-02 17:40")) and not (cp.parent / "STOP.txt").exists()
    assert "解除中やない" in sl.enforce_resume(store, dt("2026-11-02 17:41"))
    assert _enf_ticks(cp, "2026-11-02 17:45", uptime_seconds=3600) == []        # 解除した乗務の宣言はやり直し
    txt = sl.enforce_status_text(cfg, store, dt("2026-11-02 17:42"))
    assert "11/02" in txt and "ロック" in txt and "解除" in txt
    assert "強制" in sl.status_text(cfg, store, dt("2026-11-02 17:42"))
    assert "解除中" in sl.enforce_release(cfg, store, dt("2026-11-02 17:50")) or (cp.parent / "STOP.txt").exists()

def test_enforce_okita_gate():
    cp, _ = env(days={}); _enf(cp)
    c, o, e = sl.run_hook(cp, UPS("おきた"), dt("2026-11-02 17:20"))
    assert c == 2 and "寝た記録" in e and "status" not in _st(cp)["2026-11-02"]
    assert sl.run_hook(cp, UPS("x"), dt("2026-11-02 17:21"))[0] == 2             # まだハード
    _gap(cp, "2026-11-02T14:00", "2026-11-02T17:25")                             # 3h25m 離席の記録 → 解除できる
    c, o, e = sl.run_hook(cp, UPS("おきた"), dt("2026-11-02 17:26"))
    assert c == 2 and "ロック解除" in e and _st(cp)["2026-11-02"]["status"] == "okita"
    cp, _ = env(days={}); _enf(cp)                                               # 『ねる』の記録があれば解除できる
    sl.run_hook(cp, UPS("ねる"), dt("2026-11-02 13:00"))
    c, o, e = sl.run_hook(cp, UPS("おきた"), dt("2026-11-02 17:30"))
    assert "13:00〜17:30" in e and _st(cp)["2026-11-02"]["status"] == "okita"
    cp, _ = env(days={}); _enf(cp)                                               # ハード前は普通に解除できる
    c, o, e = sl.run_hook(cp, UPS("おきた"), dt("2026-11-02 15:00"))
    assert _st(cp)["2026-11-02"]["status"] == "okita"

def test_enforce_hook_has_no_keywords_and_no_context():
    cp, _ = env(days={})
    assert sl.run_hook(cp, UPS("強制"), dt("2026-11-02 12:00"))[0] == 0          # ただのプロンプト
    assert "enforce" not in _st(cp).get("2026-11-02", {})
    _enf(cp)
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-02 13:00"))                  # lastcall
    out = json.loads(o)
    assert "強制モード" in out["systemMessage"] and "kaijo" in out["systemMessage"]   # 予約済みの内容は知らせる
    assert "強制" not in out["hookSpecificOutput"]["additionalContext"]          # Claude には出さない
    assert sl.run_hook(cp, UPS("強制なし"), dt("2026-11-02 13:05"))[0] == 0
    assert _st(cp)["2026-11-02"]["enforce"]["consented_at"]                      # Claude Code からは変えられへん
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-02 17:05"))                  # ハードの拒否文にも予約を添える
    assert c == 2 and "強制モード" in e

def test_enforce_status_and_report_summary():
    cp, cfg = env(days={}); _enf(cp)
    _pc_log(cp, "2026-11-01 00:00", "2026-11-03 12:00", idle=3600)
    for s in ("2026-11-02 17:00", "2026-11-02 17:10", "2026-11-02 17:15"):
        sl.run_tick(cp, 10, False, dt(s), uptime_seconds=3600)
    store = sl.Store(cp)
    sl.enforce_release(cfg, store, dt("2026-11-02 17:20"))
    data = sl.build_report(cfg, store, dt("2026-11-03 12:00"), days=3)
    sm = data["summary"]
    assert sm["enforce_run"] == 1 and sm["enforce_declare"] == 1 and sm["enforce_release"] == 1
    assert "強制モード" in sl.render_report(data)

def test_settings_menu_enforce():
    cp, _ = env(days={})
    io = FakeIO(["19", "3", "y", "3", "y", "", "s"])
    assert sl.settings_menu(cp, dt("2026-11-01 09:00"), io) is True
    e = json.loads(cp.read_text())["enforce"]
    assert e["action"] == "shutdown" and e["duration_hours"] == 3
    io = FakeIO(["19", "2", "n", "", "", "s"])                                   # 注意に n → 変わらへん
    sl.settings_menu(cp, dt("2026-11-01 09:00"), io)
    assert json.loads(cp.read_text())["enforce"]["action"] == "shutdown"

def _mood(cp, text):
    mf = cp.parent / "mood.csv"; mf.write_text(text, encoding="utf-8")
    raw = json.loads(cp.read_text()); raw["mood_file"] = str(mf); cp.write_text(json.dumps(raw, ensure_ascii=False))
    return mf

def test_phone_marks_applied_once_and_window():
    cp, cfg = env(days={})
    _mood(cp, "2026-11-03 09:00,yasumi:2026-11-02,\n")
    sl.run_hook(cp, UPS("x"), dt("2026-11-03 10:00"))
    assert _st(cp)["2026-11-02"]["status"] == "yasumi"
    sl.run_hook(cp, UPS("11/2 出た"), dt("2026-11-03 10:05"))                # 訂正
    sl.run_hook(cp, UPS("x"), dt("2026-11-03 10:10"))
    assert _st(cp)["2026-11-02"]["status"] == "worked"                        # 巻き戻らへん（1 件 1 回）
    # 同じ日に yasumi と deta（スマホで訂正）→ それぞれ 1 回ずつ、最後の deta が残る
    cp, cfg = env(days={})
    _mood(cp, "2026-11-03 09:00,yasumi:2026-11-02,\n2026-11-03 09:05,deta:2026-11-02,\n")
    for i in range(3):
        sl.run_hook(cp, UPS("x"), dt("2026-11-03 10:00") + timedelta(minutes=i))
    assert _st(cp)["2026-11-02"]["status"] == "worked"
    assert (cp.parent / "events.log").read_text().count("\tworked\t") == 1
    # 日曜（24h 以内に乗務なし）でも取り込む。日付なしは「記録した時点で直近に終わった乗務」
    cp, cfg = env(days={})
    mf = _mood(cp, "2026-11-08 09:00,yasumi,\n")
    sl.run_hook(cp, UPS("x"), dt("2026-11-08 10:00"))
    assert _st(cp)["2026-11-07"]["status"] == "yasumi"
    mf.write_text("2026-11-08 09:00,yasumi,\n2026-11-08 09:00,deta:2026-11-06,\n", encoding="utf-8")   # 同時刻の混在で落ちへん
    assert len(sl.read_phone_marks(sl.load_config(cp))) == 2

def test_shuppatsu_time_guard_and_undo():
    cp, _ = env(days={})
    c, o, e = sl.run_hook(cp, UPS("行ってきます"), dt("2026-11-02 10:00"))
    assert c == 2 and "まだ早い" in e and "status" not in _st(cp).get("2026-11-02", {})
    c, o, e = sl.run_hook(cp, UPS("行ってきます"), dt("2026-11-02 23:00"))       # 出庫最終のあと → 翌日には付けへん
    assert "まだ早い" in e and "2026-11-03" not in _st(cp)
    sl.run_hook(cp, UPS("行ってきます"), dt("2026-11-02 17:00"))
    assert _st(cp)["2026-11-02"]["status"] == "worked"
    c, o, e = sl.run_hook(cp, UPS("今のなし"), dt("2026-11-02 17:05"))
    assert "取り消した" in e and "status" not in _st(cp).get("2026-11-02", {})
    sl.run_hook(cp, UPS("行ってきます"), dt("2026-11-02 17:10"))
    c, o, e = sl.run_hook(cp, UPS("やっぱ出る"), dt("2026-11-02 17:15"))
    assert "出発済み" in e and "status" not in _st(cp).get("2026-11-02", {})

def test_past_report_memo_and_itta_rules():
    cp, _ = env(days={}); now = dt("2026-11-03 10:00")
    assert sl.run_hook(cp, UPS("休んだ ので明日のタスクを整理して"), now)[0] == 0
    assert sl.run_hook(cp, UPS("行った"), now)[0] == 0
    assert sl.run_hook(cp, UPS("11/2 行った"), now)[0] == 2 and _st(cp)["2026-11-02"]["status"] == "worked"
    c, o, e = sl.run_hook(cp, UPS("11/4 休んだ"), now)                         # 未来は拒否（去年扱いにせえへん）
    assert c == 2 and "未来" in e and not any(k.startswith("2025") for k in _st(cp))
    c, o, e = sl.run_hook(cp, UPS("11/2 出た"), now)                           # 同じ状態なら何もしない
    assert c == 2 and "もう" in e
    c, o, e = sl.run_hook(cp, UPS("11/1 出た"), now)                           # 休みの日 → work 例外、undo で戻る
    assert "2026-11-01" in json.loads((cp.parent / "days.json").read_text())["work"]
    sl.run_hook(cp, UPS("今のなし"), now)
    assert "2026-11-01" not in json.loads((cp.parent / "days.json").read_text())["work"]
    assert "status" not in _st(cp).get("2026-11-01", {})

def test_confirm_ask_once_per_day_and_asked_target():
    cp, cfg = env(days={})
    _pc_log(cp, "2026-10-27 00:00", "2026-11-03 09:55", idle=3600)             # 1 週間ぶん未確認
    (cp.parent / "state.json").write_text(json.dumps({"confirm_since": "2026-10-20"}))
    r = sl.run_tick(cp, 10, False, dt("2026-11-03 10:00"))
    conf = [a for a in r["actions"] if a["kind"] == "confirm"]
    assert len(conf) == 1 and "11/02" in conf[0]["text"] and "ほか 5 件" in conf[0]["text"]   # 新しい順に 1 日 1 件
    assert sl.run_tick(cp, 10, False, dt("2026-11-03 10:05"))["actions"] == []
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-03 10:10"))
    assert "出た？" not in (json.loads(o).get("hookSpecificOutput", {}).get("additionalContext", "") if o else "")
    c, o, e = sl.run_hook(cp, UPS("出た"), dt("2026-11-03 10:20"))              # 日付なしは聞かれた乗務（11/2）
    assert "11/02" in e and _st(cp)["2026-11-02"]["status"] == "worked"
    r = sl.run_tick(cp, 10, False, dt("2026-11-04 10:00"))                      # 翌日はまた 1 件（11/3）
    assert [a["kind"] for a in r["actions"]] == ["confirm"] and "11/03" in r["actions"][0]["text"]
    # 36 時間より前に聞いた未確認は「日付なし」の対象にならない
    cp, cfg = env(days={})
    sl.mark_confirm_asked(sl.Store(cp), "2026-10-20", dt("2026-10-21 10:00"))
    c, o, e = sl.run_hook(cp, UPS("休んだ"), dt("2026-11-03 10:00"))
    assert "11/02" in e

def test_report_yasumi_late_and_okita_time():
    cp, cfg = env(days={})
    _pc_log(cp, "2026-11-01 00:00", "2026-11-04 12:00", idle=3600)
    (cp.parent / "state.json").write_text(json.dumps({"confirm_since": "2026-11-01"}))
    store = sl.Store(cp)
    store.set_shift_status("2026-11-02", "okita", dt("2026-11-02 15:00"))
    sl.run_hook(cp, UPS("行ってきます"), dt("2026-11-02 17:00"))              # okita → worked
    sl.late_report(cfg, store, dt("2026-11-04 10:00"), "yasumi", "11/3")
    sl.late_report(cfg, store, dt("2026-11-04 10:01"), "yasumi", "11/3")      # 二重 → 数えへん
    data = sl.build_report(cfg, store, dt("2026-11-04 12:00"), days=4)
    r = {x["date"]: x for x in data["rows"]}
    assert r["2026-11-02"]["okita"] == ["15:00"] and r["2026-11-02"]["shift"] == "worked"   # 出発で上書きされても起きた時刻は残る
    assert data["summary"]["yasumi_late"] == 1 and data["summary"]["yasumi"] == 1
    data = sl.build_report(cfg, store, dt("2026-11-10 12:00"), period=(dt("2026-11-05 00:00").date(), dt("2026-11-10 00:00").date()))
    assert data["summary"]["yasumi_late"] == 0                                   # 期間外の乗務は数えへん

def test_tick_survives_broken_sound_config():
    cp, _ = env(days={})
    raw = json.loads(cp.read_text()); raw["notify"] = {"sound": "oops", "sound_volume": "loud"}
    cp.write_text(json.dumps(raw, ensure_ascii=False))
    r = sl.run_tick(cp, 10, False, dt("2026-11-02 12:55"))
    assert r["actions"] and r["actions"][0]["sound"] == "" and r["sound"]["volume"] == 0.8
    raw["confirm"] = "off"; cp.write_text(json.dumps(raw, ensure_ascii=False))
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-02 12:56"))
    assert c == 1 and "confirm" in e                                              # 設定の型違いは分かる文で exit 1

def test_calendar_sync_no_rewrite_when_fresh():
    cp, cfg = env(days={})
    cfg["calendar"]["ics_url"] = "https://x/"
    store = sl.Store(cp); now = dt("2026-11-01 08:00")
    sl.sync_calendar(cfg, store, now, fetch=lambda u: ICS)
    txt = (cp.parent / "calendar.json").read_text()
    sl.sync_calendar(cfg, store, now + timedelta(minutes=10), fetch=lambda u: ICS)
    assert (cp.parent / "calendar.json").read_text() == txt                      # 全部スキップなら書き直さへん

def test_confirm_since_migration():
    cp, cfg = env(days={})
    _pc_log(cp, "2026-10-20 00:00", "2026-11-03 09:55", idle=3600)
    r = sl.run_tick(cp, 10, False, dt("2026-11-03 10:00"))                      # 初回 → 導入日は前日
    assert json.loads((cp.parent / "state.json").read_text())["confirm_since"] == "2026-11-02"
    conf = [a for a in r["actions"] if a["kind"] == "confirm"]
    assert len(conf) == 1 and "11/02" in conf[0]["text"] and "ほか" not in conf[0]["text"]
    data = sl.build_report(cfg, sl.Store(cp), dt("2026-11-03 10:00"), days=7)
    rows = {x["date"]: x["shift"] for x in data["rows"]}
    assert rows["2026-10-31"] == "worked" and rows["2026-11-02"] == "unconfirmed"   # 導入前は従来どおり「出」

def test_enforce_rewarn_after_absence_and_input_required():
    cp, _ = env(days={}); _enf(cp)
    t = lambda s, idle=10: _enf_ticks(cp, s, idle=idle, uptime_seconds=3600)
    t("2026-11-02 17:00")                                                        # 予告
    for i in range(1, 24):                                                       # 17:05〜18:55 離席
        t((dt("2026-11-02 17:00") + timedelta(minutes=5 * i)).strftime("%Y-%m-%d %H:%M"), idle=3600)
    a = t("2026-11-02 19:00")                                                    # 戻った → 予告からやり直し（即実行しない）
    assert len(a) == 1 and a[0]["method"] == "popup" and "15分後" in a[0]["text"]
    assert len(t("2026-11-02 19:10")) == 1                                       # 2 周目でも「あと5分」が出る
    a = t("2026-11-02 19:15"); assert a[0]["method"] == "enforce"
    assert t("2026-11-02 19:20", idle=400) == []                                 # ロック直後: 最後の入力が実行より前 → 無人で再予告しない
    a = t("2026-11-02 19:25", idle=30)                                           # 入力あり → 再予告
    assert len(a) == 1 and "5分後にまた" in a[0]["text"]

def test_enforce_untouched_by_undo_and_resume_requires_redeclare():
    cp, cfg = env(days={}); _enf(cp)
    sl.run_hook(cp, UPS("おうちかえった"), dt("2026-11-02 13:00"))                 # 話し言葉 → undo の記録ができる
    store = sl.Store(cp)
    sl.enforce_off(cfg, store, dt("2026-11-02 13:05"))                            # 取り消し
    sl.run_hook(cp, UPS("今のなし"), dt("2026-11-02 13:06"))                      # 帰宅の取り消し → 宣言は復活しない
    assert "enforce" not in _st(cp).get("2026-11-02", {})
    _enf(cp, when="2026-11-02 13:10")
    for s in ("2026-11-02 17:00", "2026-11-02 17:10", "2026-11-02 17:15"):
        _enf_ticks(cp, s, uptime_seconds=3600)
    assert len(_st(cp)["2026-11-02"]["enforce"]["runs"]) == 1
    sl.run_hook(cp, UPS("おうちかえった"), dt("2026-11-02 17:16"))
    sl.run_hook(cp, UPS("今のなし"), dt("2026-11-02 17:17"))
    assert len(_st(cp)["2026-11-02"]["enforce"]["runs"]) == 1                   # runs は戻らへん
    (cp.parent / "STOP.txt").write_text("manual")                                 # 手で置いた STOP.txt
    c, o, e = sl.run_hook(cp, UPS("おきた"), dt("2026-11-02 17:20"))              # 解除中は『おきた』のゲートも外れる
    assert _st(cp)["2026-11-02"]["status"] == "okita"
    assert "やり直し" in sl.enforce_resume(store, dt("2026-11-02 17:25"))
    assert _st(cp)["2026-11-02"]["enforce"]["released_at"]                       # resume で古い同意は復活しない
    assert "解除" in sl.enforce_release(cfg, store, dt("2026-11-02 17:30"))        # 宣言が無くても記録は残る
    assert (cp.parent / "events.log").read_text().count("enforce_release") >= 2

def test_enforce_declaration_freezes_params_dryrun_uptime_and_quiet_when_undeclared():
    cp, cfg = env(days={}); _enf(cp)
    raw = json.loads(cp.read_text()); raw["enforce"] = {"grace_minutes": 1, "relock_minutes": 0, "max_runs": 50}
    cp.write_text(json.dumps(raw, ensure_ascii=False))                            # 同意後に設定を変えても
    e = _st(cp)["2026-11-02"]["enforce"]; assert e["grace_minutes"] == 15 and e["max_runs"] == 6
    _enf_ticks(cp, "2026-11-02 17:00", uptime_seconds=3600)
    assert _enf_ticks(cp, "2026-11-02 17:05", uptime_seconds=3600) == []         # 15 分の猶予のまま
    cp, cfg = env(days={}); _enf(cp)                                              # DryRun は状態を進めない
    a = [x for x in sl.run_tick(cp, 10, False, dt("2026-11-02 17:00"), uptime_seconds=3600, dry_run=True)["actions"] if x["kind"] == "enforce"]
    assert len(a) == 1 and "warned_at" not in _st(cp)["2026-11-02"]["enforce"]
    assert _enf_ticks(cp, "2026-11-02 17:05", uptime_seconds=0) == []            # uptime 0 / NaN は不明扱い
    assert _enf_ticks(cp, "2026-11-02 17:10", uptime_seconds=float("nan")) == []
    assert _enf_ticks(cp, "2026-11-03 17:00", uptime_seconds=3600) == []         # 別日に持ち越さない
    cp, cfg = env(days={})                                                        # 宣言していない乗務の通知に強制の文言は出ない
    acts = sl.run_tick(cp, 10, False, dt("2026-11-02 12:55"))["actions"]
    assert acts and "強制" not in acts[0]["text"] and "kaijo" not in acts[0]["text"]
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-02 13:00"))
    assert "強制" not in o and "kaijo" not in o

def test_enforce_menu_requires_tty_and_windows_confirm_parsing():
    cp, _ = env(days={})
    raw = json.loads(cp.read_text()); c = sl.deep_merge(sl.DEFAULTS, raw)
    before = json.loads(json.dumps(raw))
    sl.enforce_menu(raw, c, sl.StdIO())                                           # pytest の stdin は端末やない → 何も変えない
    assert raw == before
    import subprocess
    class R:
        def __init__(self, out): self.stdout = out
    old_which, old_run = sl.shutil.which, subprocess.run
    try:
        sl.shutil.which = lambda n: "/bin/sh"
        subprocess.run = lambda *a, **k: R("\n6\n"); assert sl.windows_confirm("x") is True
        subprocess.run = lambda *a, **k: R("7\n");   assert sl.windows_confirm("x") is False
        subprocess.run = lambda *a, **k: R("-1\n");  assert sl.windows_confirm("x") is False
        subprocess.run = lambda *a, **k: R("16\n");  assert sl.windows_confirm("x") is False
        def boom(*a, **k): raise OSError("x")
        subprocess.run = boom;                       assert sl.windows_confirm("x") is False
        sl.shutil.which = lambda n: None;            assert sl.windows_confirm("x") is False
    finally:
        sl.shutil.which, subprocess.run = old_which, old_run

def test_phone_okita_after_second_sleep():
    """二度寝のあとの iPhone『おきた』も取り込む（『起きた』済みでも、そのあとに寝た記録があれば再適用）。"""
    cp, cfg = env(days={})
    mf = cp.parent / "mood.csv"
    mf.write_text("2026-11-04 07:05,neru,\n2026-11-04 09:30,okita,\n", encoding="utf-8")
    cfg_raw = json.loads(cp.read_text()); cfg_raw["mood_file"] = str(mf); cp.write_text(json.dumps(cfg_raw, ensure_ascii=False))
    sl.run_hook(cp, UPS("x"), dt("2026-11-04 09:35"))
    st = json.loads((cp.parent / "state.json").read_text())["shifts"]["2026-11-04"]
    assert st["status"] == "okita" and st["sleep_at"].endswith("07:05")
    # 二度寝（iPhone から ねる）→ sleep_at が進む
    mf.write_text("2026-11-04 07:05,neru,\n2026-11-04 09:30,okita,\n2026-11-04 10:30,neru,\n", encoding="utf-8")
    sl.run_hook(cp, UPS("x"), dt("2026-11-04 10:35"))
    st = json.loads((cp.parent / "state.json").read_text())["shifts"]["2026-11-04"]
    assert st["sleep_at"].endswith("10:30")
    # 二度寝のあとの おきた → 取り込まれる
    mf.write_text("2026-11-04 07:05,neru,\n2026-11-04 09:30,okita,\n2026-11-04 10:30,neru,\n2026-11-04 15:00,okita,\n", encoding="utf-8")
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-04 15:05"))
    assert "10:30〜15:00" in json.loads(o)["systemMessage"]
    st = json.loads((cp.parent / "state.json").read_text())["shifts"]["2026-11-04"]
    assert st["status"] == "okita" and st["at"].endswith("15:00")   # at は iPhone の記録時刻（取り込み時刻やない）
    # もう一度呼んでも二重に取り込まへん
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-04 15:06"))
    assert "iPhone" not in (json.loads(o).get("systemMessage", "") if o else "")
    # 出発済み（worked）のあとの おきた は触らへん
    sl.run_hook(cp, UPS("行ってきます"), dt("2026-11-04 17:00"))
    mf.write_text("2026-11-04 07:05,neru,\n2026-11-04 09:30,okita,\n2026-11-04 10:30,neru,\n2026-11-04 15:00,okita,\n2026-11-04 17:30,okita,\n", encoding="utf-8")
    sl.run_hook(cp, UPS("x"), dt("2026-11-04 17:35"))
    st = json.loads((cp.parent / "state.json").read_text())["shifts"]["2026-11-04"]
    assert st["status"] == "worked"

def test_report_away_uses_actual_departure_and_kitaku():
    """レポートの外出区間: 『行ってきます』の時刻 〜 『ただいま』の時刻。記録が無ければ 出発目安 〜 翌朝 shift_end_earliest。"""
    cp, cfg = env(days={})
    _pc_log(cp, "2026-11-01 00:00", "2026-11-05 12:00", idle=3600)
    store = sl.Store(cp)
    # 11/03: 21:59 に行ってきます、翌 10:07 にただいま（ただいまは次の乗務日 11/04 に付く）
    store.set_shift_status("2026-11-03", "worked", dt("2026-11-03 21:59"), "shuppatsu")
    sl.set_mark(store, "2026-11-04", "kitaku", dt("2026-11-04 10:07"), dt("2026-11-04 10:07"), "claude")
    # 11/02: 事後報告の『出た』だけ（出発時刻は分からん）→ 出発目安 18:30 〜 翌 07:00
    sl.late_report(cfg, store, dt("2026-11-04 11:00"), "worked", "11/2")
    data = sl.build_report(cfg, store, dt("2026-11-04 12:00"), days=4)
    r = {x["date"]: x for x in data["rows"]}
    assert r["2026-11-03"]["shift"] == "worked" and r["2026-11-02"]["shift"] == "worked"
    assert not r["2026-11-03"]["away"][18] and not r["2026-11-03"]["away"][20] and r["2026-11-03"]["away"][21]
    assert r["2026-11-04"]["away"][6] and r["2026-11-04"]["away"][9] and r["2026-11-04"]["away"][10] and not r["2026-11-04"]["away"][11]
    assert r["2026-11-02"]["away"][18] and r["2026-11-03"]["away"][6] and not r["2026-11-03"]["away"][7]
    # 『行ってきます』を取り消したら出発目安に戻る
    store.set_shift_status("2026-11-03", None, dt("2026-11-04 12:00"))
    data = sl.build_report(cfg, store, dt("2026-11-04 12:00"), days=4)
    r = {x["date"]: x for x in data["rows"]}
    assert r["2026-11-03"]["shift"] == "unconfirmed" and r["2026-11-03"]["away"][18]


def test_phone_bulk_sync_applies_latest_okita():
    """iPhone の記録をまとめて読んでも（iCloud の遅延・初回同期）二度寝後の『おきた』を取りこぼさへん。at は iPhone の記録時刻。"""
    cp, cfg = env(days={})
    mf = cp.parent / "mood.csv"
    mf.write_text("2026-11-04 07:05,neru,\n2026-11-04 09:30,okita,\n2026-11-04 10:30,neru,\n2026-11-04 15:00,okita,\n", encoding="utf-8")
    cfg_raw = json.loads(cp.read_text()); cfg_raw["mood_file"] = str(mf); cp.write_text(json.dumps(cfg_raw, ensure_ascii=False))
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-04 15:05"))
    assert "10:30〜15:00" in json.loads(o)["systemMessage"]
    st = json.loads((cp.parent / "state.json").read_text())["shifts"]["2026-11-04"]
    assert st["status"] == "okita" and st["at"].endswith("15:00") and st["sleep_at"].endswith("10:30")
    # 再同期しても二重に出えへん
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-04 15:10"))
    assert "iPhone" not in (json.loads(o).get("systemMessage", "") if o else "")
    # 次のまとめ読み（前のバッチの おきた は済み扱い、新しい 寝た→おきた だけ当たる）
    mf.write_text(mf.read_text(encoding="utf-8") + "2026-11-04 16:00,neru,\n2026-11-04 17:30,okita,\n", encoding="utf-8")
    c, o, e = sl.run_hook(cp, UPS("x"), dt("2026-11-04 17:35"))
    assert "16:00〜17:30" in json.loads(o)["systemMessage"]
    st = json.loads((cp.parent / "state.json").read_text())["shifts"]["2026-11-04"]
    assert st["at"].endswith("17:30") and st["sleep_at"].endswith("16:00")


def test_report_departure_survives_undo():
    """『行ってきます』のあとに別の記録を『今のなし』で戻しても、レポートの出発は『行ってきます』の時刻のまま。"""
    cp, cfg = env(days={})
    _pc_log(cp, "2026-11-01 00:00", "2026-11-05 12:00", idle=3600)
    store = sl.Store(cp)
    sl.run_hook(cp, UPS("行ってきます"), dt("2026-11-03 21:59"))
    sl.run_hook(cp, UPS("11/3 休んだ"), dt("2026-11-04 11:00"))      # 間違えた事後報告
    sl.run_hook(cp, UPS("今のなし"), dt("2026-11-04 11:01"))
    assert store.shift_status()["2026-11-03"] == {"status": "worked", "at": "2026-11-03T21:59"}
    data = sl.build_report(cfg, store, dt("2026-11-04 12:00"), days=4)
    r = {x["date"]: x for x in data["rows"]}
    assert r["2026-11-03"]["shift"] == "worked"
    assert not r["2026-11-03"]["away"][18] and not r["2026-11-03"]["away"][20] and r["2026-11-03"]["away"][21]


def test_report_home_is_first_kitaku_after_departure():
    """同じ乗務日に『ただいま』を 2 回言っても（帰宅→買い物→帰宅）、前の乗務の帰宅は最初の『ただいま』のまま。"""
    cp, cfg = env(days={})
    _pc_log(cp, "2026-11-01 00:00", "2026-11-05 12:00", idle=3600)
    store = sl.Store(cp)
    store.set_shift_status("2026-11-03", "worked", dt("2026-11-03 21:59"), "shuppatsu")
    sl.run_hook(cp, UPS("ただいま"), dt("2026-11-04 10:07"))
    sl.run_hook(cp, UPS("ただいま"), dt("2026-11-04 15:00"))
    assert store.shift_status()["2026-11-04"]["kitaku"].endswith("15:00")   # state は最後の帰宅（自動起きた用）
    data = sl.build_report(cfg, store, dt("2026-11-04 16:00"), days=4)
    r = {x["date"]: x for x in data["rows"]}
    assert r["2026-11-04"]["away"][9] and not r["2026-11-04"]["away"][11] and not r["2026-11-04"]["away"][14]
    # iPhone 経由の『ただいま』（取り込みが遅れても記録時刻で判定する）
    store2 = sl.Store(cp)
    sl.set_mark(store2, "2026-11-05", "kitaku", dt("2026-11-05 09:40"), dt("2026-11-05 12:00"), "phone")
    store2.set_shift_status("2026-11-04", "worked", dt("2026-11-04 21:30"), "shuppatsu")
    data = sl.build_report(cfg, store2, dt("2026-11-05 13:00"), days=4)
    r = {x["date"]: x for x in data["rows"]}
    assert r["2026-11-05"]["away"][9] and not r["2026-11-05"]["away"][10] and not r["2026-11-05"]["away"][11]


# --- claude-busy.json（Claude Code が処理中の印） ---
def _ups(prompt="x", sid="s1"): return json.dumps({"hook_event_name": "UserPromptSubmit", "prompt": prompt, "session_id": sid})
def _stop(sid="s1"): return json.dumps({"hook_event_name": "Stop", "session_id": sid})
def _busy(cp):
    p = cp.parent / "claude-busy.json"
    return json.loads(p.read_text()) if p.exists() else None
BUSY_NOW = dt("2026-12-07 10:00")

def test_busy_mark_set_on_prompt_and_cleared_on_stop():
    cp, _ = env()
    assert sl.run_hook(cp, _ups(), BUSY_NOW)[0] == 0
    assert _busy(cp) == {"s1": "2026-12-07T10:00:00"}
    assert sl.run_hook(cp, _stop(), BUSY_NOW + timedelta(minutes=1)) == (0, "", "")
    assert _busy(cp) == {}

def test_busy_mark_two_sessions():
    cp, _ = env()
    sl.run_hook(cp, _ups(sid="s1"), BUSY_NOW)
    sl.run_hook(cp, _ups(sid="s2"), BUSY_NOW + timedelta(minutes=1))
    sl.run_hook(cp, _stop("s1"), BUSY_NOW + timedelta(minutes=2))
    assert _busy(cp) == {"s2": "2026-12-07T10:01:00"}

def test_busy_mark_not_set_on_keyword():
    cp, _ = env()
    assert sl.run_hook(cp, _ups("ねる"), BUSY_NOW)[0] == 2
    assert not _busy(cp)

def test_busy_mark_not_set_on_hard_block():
    cp, _ = env()
    assert sl.run_hook(cp, _ups("x"), dt("2026-11-02 17:10"))[0] == 2
    assert not _busy(cp)

def test_busy_mark_not_set_on_config_error():
    cp, _ = env()
    cp.write_text("{")
    assert sl.run_hook(cp, _ups(), BUSY_NOW)[0] == 1
    assert not _busy(cp)

def test_busy_mark_without_or_bad_session_id():
    cp, _ = env()
    base = sl.run_hook(cp, UPS("x"), BUSY_NOW)
    assert base[0] == 0 and not _busy(cp)
    for sid in (None, 5, ["a"], ""):
        txt = json.dumps({"hook_event_name": "UserPromptSubmit", "prompt": "x", "session_id": sid})
        assert sl.run_hook(cp, txt, BUSY_NOW) == base
    assert not _busy(cp)

def test_busy_mark_write_failure_does_not_change_exit(monkeypatch):
    import importlib
    hk = importlib.import_module("shiftlock.hook")
    def boom(*a, **k): raise OSError("locked")
    cp, _ = env()
    want = sl.run_hook(cp, _ups(), BUSY_NOW)
    monkeypatch.setattr(hk, "_save_busy", boom)
    assert sl.run_hook(cp, _ups(), BUSY_NOW) == want
    assert sl.run_hook(cp, _stop(), BUSY_NOW) == (0, "", "")

def test_busy_mark_broken_file_is_replaced():
    cp, _ = env()
    (cp.parent / "claude-busy.json").write_text("[")
    assert sl.run_hook(cp, _ups(), BUSY_NOW)[0] == 0
    assert _busy(cp) == {"s1": "2026-12-07T10:00:00"}
    (cp.parent / "claude-busy.json").write_text('["a"]')   # 形が違うだけでも置き換わる
    assert sl.run_hook(cp, _ups(), BUSY_NOW)[0] == 0
    assert _busy(cp) == {"s1": "2026-12-07T10:00:00"}

def test_busy_mark_drops_entries_older_than_a_day():
    cp, _ = env()
    (cp.parent / "claude-busy.json").write_text(json.dumps(
        {"old": "2026-12-06T09:59:59", "keep": "2026-12-06T10:00:00", "bad": "x", "num": 3}))
    sl.run_hook(cp, _ups(), BUSY_NOW)
    assert _busy(cp) == {"keep": "2026-12-06T10:00:00", "s1": "2026-12-07T10:00:00"}
