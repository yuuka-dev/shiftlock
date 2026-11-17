"""レポートの睡眠を本人の記録（『ねる』『おきた』）から出す。PC を触っていない時間からの推定はしない。issue #20。
- sleep_marks: events.log と mood.csv から『ねる』『おきた』の時刻を集める（乗務のない日・iPhone・取り消しを含む）
- sleep_sessions: 時刻の列をひと続きの睡眠に組む（片方だけの記録・言い直し・24 時間を超える組は作らない）
- record_sleep: 後から時刻を指定して足す／消す（レポート用の記録だけ。乗務の状態やロックは変えない）
- build_report: 寝た／起きた／睡眠の列と、PC を丸一日触っていない日の「覚醒長」の誤表示"""
from __future__ import annotations

import json
import sys
import tempfile
from datetime import datetime, timedelta
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
import shiftlock as sl
import shiftlock.i18n as M

SRC = ROOT / "shifts.example.json"


def dt(s): return datetime.strptime(s, "%Y-%m-%d %H:%M")
def UPS(prompt): return json.dumps({"hook_event_name": "UserPromptSubmit", "prompt": prompt})


def env(days=None, **over):
    t = Path(tempfile.mkdtemp())
    cfg = json.loads(SRC.read_text(encoding="utf-8")); cfg.update(over)
    cfg.pop("mood_file", None)   # 見本のパスは実在しないので外す（使うテストは _mood で入れる）
    (t / "shifts.json").write_text(json.dumps(cfg, ensure_ascii=False), encoding="utf-8")
    (t / "days.json").write_text(json.dumps(days or {}, ensure_ascii=False), encoding="utf-8")
    return t / "shifts.json", sl.load_config(t / "shifts.json")


def _mood(cp, text):
    mf = cp.parent / "mood.csv"; mf.write_text(text, encoding="utf-8")
    raw = json.loads(cp.read_text(encoding="utf-8")); raw["mood_file"] = str(mf)
    cp.write_text(json.dumps(raw, ensure_ascii=False), encoding="utf-8")
    return sl.load_config(cp)


def _st(cp):
    p = cp.parent / "state.json"
    return json.loads(p.read_text(encoding="utf-8")).get("shifts", {}) if p.exists() else {}


def _events(cp):
    p = cp.parent / "events.log"
    return [ln.split("\t") for ln in p.read_text(encoding="utf-8").splitlines()] if p.exists() else []


def _ticks(store, start, end, active):
    """5 分おきの pc ログ。active(t) が真の時刻は操作中、偽の間は idle が伸び続ける（触っていない）"""
    t, last = dt(start), dt(start)
    while t < dt(end):
        t += timedelta(minutes=5)
        if active(t):
            last = t - timedelta(seconds=30)
        sl.log_append(store, "pc", t, {"idle": int((t - last).total_seconds()), "game": 0})


N, O = "neru", "okita"
OFF = {"off": {"2026-11-03": "連休", "2026-11-04": "連休"}}   # 11/3〜11/4 は乗務なし（24 時間以内に乗務が無い時間帯ができる）


# ---------------------------------------------------------------- 区間に組む（純粋な関数）

def test_sessions_pair_and_singles():
    s = sl.sleep_sessions([(dt("2026-11-06 23:00"), N), (dt("2026-11-07 07:00"), O)])
    assert s == [{"neru": dt("2026-11-06 23:00"), "okita": dt("2026-11-07 07:00")}]
    # 片方だけの記録もそのまま残す（うつで片方しか覚えていない日）
    assert sl.sleep_sessions([(dt("2026-11-07 07:00"), O)]) == [{"neru": None, "okita": dt("2026-11-07 07:00")}]
    assert sl.sleep_sessions([(dt("2026-11-06 23:00"), N)]) == [{"neru": dt("2026-11-06 23:00"), "okita": None}]
    assert sl.sleep_sessions([]) == []


def test_sessions_do_not_pair_over_max_hours():
    """『ねる』から 24 時間を超えた『おきた』は同じ睡眠にしない（何十時間もの睡眠を作らない）"""
    s = sl.sleep_sessions([(dt("2026-11-07 15:00"), N), (dt("2026-11-09 09:00"), O)])
    assert s == [{"neru": dt("2026-11-07 15:00"), "okita": None}, {"neru": None, "okita": dt("2026-11-09 09:00")}]
    s = sl.sleep_sessions([(dt("2026-11-07 15:00"), N), (dt("2026-11-08 15:00"), O)])   # ちょうど 24 時間は組む
    assert s == [{"neru": dt("2026-11-07 15:00"), "okita": dt("2026-11-08 15:00")}]


def test_sessions_redo_and_duplicates():
    # 『ねる』の言い直し（2 時間以内）は後のほうを採る
    s = sl.sleep_sessions([(dt("2026-11-06 23:00"), N), (dt("2026-11-06 23:40"), N), (dt("2026-11-07 07:00"), O)])
    assert s == [{"neru": dt("2026-11-06 23:40"), "okita": dt("2026-11-07 07:00")}]
    # 離れた『ねる』は別の睡眠。前のほうは起きた時刻が分からないまま残す
    s = sl.sleep_sessions([(dt("2026-11-06 01:00"), N), (dt("2026-11-06 23:00"), N), (dt("2026-11-07 07:00"), O)])
    assert s == [{"neru": dt("2026-11-06 01:00"), "okita": None},
                 {"neru": dt("2026-11-06 23:00"), "okita": dt("2026-11-07 07:00")}]
    # 『おきた』『おはよう』の重複（2 時間以内）は最初の 1 回だけ
    s = sl.sleep_sessions([(dt("2026-11-06 23:00"), N), (dt("2026-11-07 07:00"), O), (dt("2026-11-07 07:20"), O)])
    assert s == [{"neru": dt("2026-11-06 23:00"), "okita": dt("2026-11-07 07:00")}]
    # 離れた『おきた』は、寝た時刻の分からない起床として残す
    s = sl.sleep_sessions([(dt("2026-11-07 07:00"), O), (dt("2026-11-08 08:00"), O)])
    assert s == [{"neru": None, "okita": dt("2026-11-07 07:00")}, {"neru": None, "okita": dt("2026-11-08 08:00")}]
    # 順番がばらばらでも時刻順に組む
    s = sl.sleep_sessions([(dt("2026-11-07 07:00"), O), (dt("2026-11-06 23:00"), N)])
    assert s == [{"neru": dt("2026-11-06 23:00"), "okita": dt("2026-11-07 07:00")}]


# ---------------------------------------------------------------- 記録を集める

def test_marks_from_hook_on_shift_day():
    cp, cfg = env()
    sl.run_hook(cp, UPS("ねる"), dt("2026-11-02 08:00"))
    sl.run_hook(cp, UPS("おきた"), dt("2026-11-02 13:00"))
    assert sl.sleep_marks(cfg, sl.Store(cp)) == [(dt("2026-11-02 08:00"), N), (dt("2026-11-02 13:00"), O)]


def test_marks_on_free_day():
    """24 時間以内に乗務が無い日の『ねる』『おきた』も睡眠の記録になる"""
    cp, cfg = env(days=OFF)
    sl.run_hook(cp, UPS("ねる"), dt("2026-11-03 23:00"))
    sl.run_hook(cp, UPS("おきた"), dt("2026-11-04 08:30"))
    assert _st(cp) == {}                                              # 乗務の状態には何も書かない
    assert sl.sleep_marks(cfg, sl.Store(cp)) == [(dt("2026-11-03 23:00"), N), (dt("2026-11-04 08:30"), O)]


def test_marks_from_phone_with_and_without_shift():
    cp, cfg = env(days=OFF)
    cfg = _mood(cp, "2026-11-02 08:10,neru,\n2026-11-02 13:05,okita,\n"      # 乗務日（state にも取り込まれる）
                    "2026-11-03 23:00,ねる,\n2026-11-04 09:00,おきた,\n"       # 乗務のない日（mood.csv だけに残る）
                    "2026-11-04 09:01,-1,だるい\n")
    sl.run_hook(cp, UPS("x"), dt("2026-11-02 13:10"))
    assert _st(cp)["2026-11-02"]["status"] == "okita"
    sl.run_hook(cp, UPS("x"), dt("2026-11-04 10:00"))
    # 取り込み済み（events.log）と mood.csv の両方にあっても 1 回だけ。気分の行は拾わない
    assert sl.sleep_marks(cfg, sl.Store(cp)) == [(dt("2026-11-02 08:10"), N), (dt("2026-11-02 13:05"), O),
                                                 (dt("2026-11-03 23:00"), N), (dt("2026-11-04 09:00"), O)]


def test_marks_undo_removes_fuzzy_record():
    """話し言葉から拾った『寝る』を『今のなし』で取り消したら、睡眠の記録からも消える"""
    cp, cfg = env()
    sl.run_hook(cp, UPS("ねる"), dt("2026-11-02 08:00"))
    sl.run_hook(cp, UPS("おきた"), dt("2026-11-02 13:00"))
    sl.run_hook(cp, UPS("ねむすぎるからもう寝るわー"), dt("2026-11-04 07:05"))
    assert (dt("2026-11-04 07:05"), N) in sl.sleep_marks(cfg, sl.Store(cp))
    sl.run_hook(cp, UPS("今のなし"), dt("2026-11-04 07:06"))
    assert sl.sleep_marks(cfg, sl.Store(cp)) == [(dt("2026-11-02 08:00"), N), (dt("2026-11-02 13:00"), O)]
    cp, cfg = env(days=OFF)                                           # 乗務のない日でも同じ
    sl.run_hook(cp, UPS("ねむすぎるからもう寝るわー"), dt("2026-11-03 23:00"))
    assert sl.sleep_marks(cfg, sl.Store(cp)) == [(dt("2026-11-03 23:00"), N)]
    sl.run_hook(cp, UPS("今のなし"), dt("2026-11-03 23:01"))
    assert sl.sleep_marks(cfg, sl.Store(cp)) == []


def test_marks_undo_keeps_phone_record_at_same_minute():
    """iPhone の記録と同じ分に Claude へ言った『寝る』を『今のなし』で戻しても、iPhone の記録は残る（iPhone の記録は取り消しの対象外）"""
    cp, cfg = env(days=OFF)
    cfg = _mood(cp, "2026-11-03 23:00,neru,\n")
    sl.run_hook(cp, UPS("ねむすぎるからもう寝るわー"), dt("2026-11-03 23:00"))
    assert sl.sleep_marks(cfg, sl.Store(cp)) == [(dt("2026-11-03 23:00"), N)]
    sl.run_hook(cp, UPS("今のなし"), dt("2026-11-03 23:01"))
    assert sl.sleep_marks(cfg, sl.Store(cp)) == [(dt("2026-11-03 23:00"), N)]   # Claude 側だけ戻り、iPhone の分は残る


def test_marks_undo_okita_and_only_recent():
    cp, cfg = env()
    sl.run_hook(cp, UPS("ねる"), dt("2026-11-02 08:00"))
    sl.run_hook(cp, UPS("目が覚めた"), dt("2026-11-02 13:00"))              # 話し言葉から拾う『起きた』（固定キーワードやない）
    assert sl.sleep_marks(cfg, sl.Store(cp)) == [(dt("2026-11-02 08:00"), N), (dt("2026-11-02 13:00"), O)]
    sl.run_hook(cp, UPS("今のなし"), dt("2026-11-02 13:05"))
    assert sl.sleep_marks(cfg, sl.Store(cp)) == [(dt("2026-11-02 08:00"), N)]
    # 記録されなかった発言への『今のなし』（undo 行だけが残る）で、ずっと前の記録を消さない
    cp, cfg = env()
    (cp.parent / "events.log").write_text(
        "2026-11-02T10:00\t2026-11-02\tokita\t\n"
        "2026-11-02T13:00\t2026-11-02\tundo\tokita\n", encoding="utf-8")
    assert sl.sleep_marks(cfg, sl.Store(cp)) == [(dt("2026-11-02 10:00"), O)]
    (cp.parent / "events.log").write_text(
        "2026-11-02T10:00\t2026-11-02\tokita\t\n"
        "2026-11-02T10:30\t2026-11-02\tundo\tokita\n", encoding="utf-8")
    assert sl.sleep_marks(cfg, sl.Store(cp)) == []


def test_marks_skip_auto_okita():
    """PC の無操作から自動で付いた『起きた』は本人の記録ではないので数えない"""
    cp, cfg = env()
    store = sl.Store(cp)
    store.set_shift_status("2026-11-02", "okita", dt("2026-11-02 14:00"), "auto 08:00-14:00")
    assert sl.sleep_marks(cfg, store) == []
    store.set_shift_status("2026-11-03", "okita", dt("2026-11-03 15:00"), "")
    assert sl.sleep_marks(cfg, store) == [(dt("2026-11-03 15:00"), O)]


def test_marks_ignore_broken_lines():
    cp, cfg = env()
    (cp.parent / "events.log").write_text(
        "こわれた行\n2026-11-02T08:00\t2026-11-02\tsleep_at\tclear\nxx\t-\tneru\tfree-day\n"
        "2026-11-02T08:00\t2026-11-02\tsleep_at\tclaude 2026-11-02T07:55\n"
        "2026-11-02T18:00\t2026-11-02\tyasumi\t\n", encoding="utf-8")
    assert sl.sleep_marks(cfg, sl.Store(cp)) == [(dt("2026-11-02 07:55"), N)]   # 時刻はメモの発言時刻


def test_marks_read_old_format_lines():
    """メモに時刻を持たない古い行と、記録・取り込みの時刻で書かれた okita 行。
    - sleep_at claude（時刻なし）は行の時刻。sleep_at phone（時刻なし）は取り込みの時刻なので使わず、mood.csv を正にする
    - okita のメモ『… HH:MM-HH:MM』は終わりの時刻が起きた時刻（行の時刻が後ろにずれていても）"""
    cp, cfg = env()
    cfg = _mood(cp, "2026-11-03 06:10,neru,\n2026-11-03 09:40,okita,\n")
    (cp.parent / "events.log").write_text(
        "2026-11-03T06:13\t2026-11-03\tsleep_at\tphone\n"
        "2026-11-03T09:55\t2026-11-03\tokita\tphone 06:10-09:40\n"
        "2026-11-03T11:15\t2026-11-03\tsleep_at\tclaude\n"
        "2026-11-03T19:30\t2026-11-03\tokita\tneru 11:15-19:30\n"
        "2026-11-04T08:00\t2026-11-04\tsleep_at\tclaude 2026-11-04T07:30\n"
        "2026-11-05T00:10\t2026-11-04\tokita\tneru 07:30-23:55\n"       # 記録が日をまたいだ → 前の日の 23:55
        "2026-11-05T09:00\t2026-11-05\tokita\tneru 99:99-88:77\n", encoding="utf-8")   # 変な時刻は行の時刻で読む
    assert sl.sleep_marks(cfg, sl.Store(cp)) == [
        (dt("2026-11-03 06:10"), N), (dt("2026-11-03 09:40"), O), (dt("2026-11-03 11:15"), N), (dt("2026-11-03 19:30"), O),
        (dt("2026-11-04 07:30"), N), (dt("2026-11-04 23:55"), O), (dt("2026-11-05 09:00"), O)]


# ---------------------------------------------------------------- 後から足す／消す

def test_record_sleep_time_only_and_date():
    cp, cfg = env()
    store = sl.Store(cp)
    now = dt("2026-11-09 12:00")
    sl.record_sleep(cfg, store, now, N, ["23:30"])                    # 今日の 23:30 はまだ来ていない → 昨日
    msg = sl.record_sleep(cfg, store, now, O, ["7:00"])               # 今日の 07:00
    assert "7時間30分" in msg                                          # 23:30〜07:00 の睡眠になると返す
    sl.record_sleep(cfg, store, now, N, ["11/7", "22時半"])
    sl.record_sleep(cfg, store, now, O, ["昨日", "９時"])              # 全角の数字も読む
    assert sl.sleep_marks(cfg, store) == [(dt("2026-11-07 22:30"), N), (dt("2026-11-08 09:00"), O),
                                          (dt("2026-11-08 23:30"), N), (dt("2026-11-09 07:00"), O)]
    assert _st(cp) == {}                                              # 乗務の状態・ロックには触らない
    assert all(e[1] == "-" and e[3] == "late shl" for e in _events(cp))


def test_record_sleep_rejects_bad_input():
    cp, cfg = env()
    store = sl.Store(cp)
    now = dt("2026-11-09 12:00")
    for words in (["11/10", "8:00"],          # 未来
                  ["今日", "12:01"],           # 未来（1 分先）
                  ["11/7"],                    # 時刻が無い
                  ["25:00"], ["7:60"],         # 時刻として読めない
                  ["13/40", "7:00"],           # 日付として読めない
                  ["7:00", "8:00"],            # 時刻が 2 つ
                  ["2025-01-01", "7:00"]):     # 古すぎる（events.log の保存期間より前）
        msg = sl.record_sleep(cfg, store, now, O, words)
        assert msg.startswith("[shiftlock]"), words
    assert sl.sleep_marks(cfg, store) == [] and _events(cp) == []
    sl.record_sleep(cfg, store, now, O, ["今日", "12:00"])            # ちょうど今は記録できる
    assert sl.sleep_marks(cfg, store) == [(dt("2026-11-09 12:00"), O)]


def test_record_sleep_clear():
    """間違えた記録は末尾に『取消』で消せる。iPhone から入れた記録も同じ時刻・同じ種類なら消える"""
    cp, cfg = env()
    cfg = _mood(cp, "2026-11-07 23:00,neru,\n2026-11-08 07:00,okita,\n")
    store = sl.Store(cp)
    now = dt("2026-11-09 12:00")
    sl.record_sleep(cfg, store, now, N, ["11/8", "22:00"])
    sl.record_sleep(cfg, store, now, N, ["11/8", "22:00"])            # 二重に入れても 1 つ
    assert sl.sleep_marks(cfg, store) == [(dt("2026-11-07 23:00"), N), (dt("2026-11-08 07:00"), O), (dt("2026-11-08 22:00"), N)]
    sl.record_sleep(cfg, store, now, N, ["11/8", "22:00", "取消"])
    sl.record_sleep(cfg, store, now, N, ["11/7", "23:00", "とりけし"])  # iPhone の記録
    assert sl.sleep_marks(cfg, store) == [(dt("2026-11-08 07:00"), O)]
    before = len(_events(cp))
    msg = sl.record_sleep(cfg, store, now, O, ["11/6", "07:00", "取消"])   # 無い記録は消せない（行も足さない）
    assert msg.startswith("[shiftlock]") and len(_events(cp)) == before
    sl.record_sleep(cfg, store, now, N, ["11/8", "22:00"])            # 消したあと入れ直せる
    assert (dt("2026-11-08 22:00"), N) in sl.sleep_marks(cfg, store)


def test_record_sleep_clear_survives_phone_import():
    """iPhone の記録を、PC が取り込む前に消しても、あとの取り込みで戻らない。自分で入れ直せば戻る"""
    cp, cfg = env()
    cfg = _mood(cp, "2026-11-02 08:10,neru,\n")
    store = sl.Store(cp)
    sl.record_sleep(cfg, store, dt("2026-11-02 08:20"), N, ["8:10", "取消"])
    assert sl.sleep_marks(cfg, store) == []
    sl.run_hook(cp, UPS("x"), dt("2026-11-02 08:30"))                   # ここで mood.csv が state に取り込まれる
    assert _st(cp)["2026-11-02"]["sleep_at"].endswith("08:10")
    assert any(e[2] == "sleep_at" and e[3].startswith("phone") for e in _events(cp))
    assert sl.sleep_marks(cfg, store) == []
    sl.record_sleep(cfg, store, dt("2026-11-02 08:40"), N, ["8:10"])
    assert sl.sleep_marks(cfg, store) == [(dt("2026-11-02 08:10"), N)]


def test_cli_neru_okita_with_time(capsys):
    cp, cfg = env()
    assert sl.main(["okita", "--config", str(cp), "0:00"]) == 0       # 今日の 0:00 は必ず過去
    out = capsys.readouterr().out
    assert "[shiftlock]" in out
    ev = _events(cp)
    assert len(ev) == 1 and ev[0][1:] == ["-", "okita", "late shl"] and ev[0][0].endswith("T00:00")
    assert sl.main(["neru", "--config", str(cp), "0:00", "取消"]) == 0   # 無い記録の取消でも落ちない
    assert len(_events(cp)) == 1 and _st(cp) == {}
    assert "neru 23:30" in M.CLI_HELP                                  # ヘルプに書き方がある


# ---------------------------------------------------------------- レポート

def _rows(data):
    return {x["date"]: x for x in data["rows"]}


def test_report_sleep_columns_from_records():
    cp, cfg = env(days=OFF)
    cfg = _mood(cp, "2026-11-02 23:30,neru,\n2026-11-03 07:00,okita,\n"        # 夜の睡眠 7:30
                    "2026-11-03 14:00,neru,\n2026-11-03 15:30,okita,\n"        # 昼寝 1:30
                    "2026-11-03 23:00,neru,\n"                                 # 起きた時刻は記録なし
                    "2026-11-05 08:00,okita,\n")                               # 寝た時刻は記録なし（24 時間超）
    store = sl.Store(cp)
    data = sl.build_report(cfg, store, dt("2026-11-05 12:00"), days=4)
    r = _rows(data)
    assert r["2026-11-02"]["neru"] == ["23:30"] and r["2026-11-02"]["okita"] == [] and r["2026-11-02"]["sleep_min"] is None
    assert r["2026-11-03"]["okita"] == ["07:00", "15:30"] and r["2026-11-03"]["neru"] == ["14:00", "23:00"]
    assert r["2026-11-03"]["sleep_min"] == 9 * 60                              # その日に起きた分の合計
    assert r["2026-11-03"]["sleep_span"] == "23:30–07:00 / 14:00–15:30"
    assert r["2026-11-04"]["neru"] == [] and r["2026-11-04"]["okita"] == [] and r["2026-11-04"]["sleep_min"] is None
    assert r["2026-11-05"]["okita"] == ["08:00"] and r["2026-11-05"]["sleep_min"] is None
    assert data["summary"]["sleep_median"] == 9 * 60 and data["summary"]["sleep_paired_days"] == 1   # 中央値の母数（組になった日）
    assert data["summary"]["sleep_days"] == 3                                 # 寝た／起きたのどちらかを記録した日（11/2・11/3・11/5）
    html = sl.render_report(data)
    assert "推定睡眠" not in html and "寝た" in html and "起きた" in html
    assert ">9:00<" in html and "23:30–07:00 / 14:00–15:30" in html


def test_report_no_estimate_when_pc_untouched():
    """PC を何日も触っていない（寝込んでいた）あいだを睡眠として数えない。「覚醒長」も付けない"""
    cp, cfg = env(days={"off": {"2026-11-06": "休", "2026-11-07": "休"}})
    store = sl.Store(cp)

    def active(t):
        return t < dt("2026-11-06 08:00") or t >= dt("2026-11-09 09:00")       # 11/6 08:00 → 11/9 09:00 は触っていない
    _ticks(store, "2026-11-05 00:00", "2026-11-09 12:00", active)
    data = sl.build_report(cfg, store, dt("2026-11-09 12:00"), days=5)
    r = _rows(data)
    assert all(x["sleep_min"] is None and x["neru"] == [] and x["okita"] == [] for x in data["rows"])
    assert "long" in r["2026-11-05"]["flags"]                                   # 一日中触っていた日は従来どおり覚醒長
    for d in ("2026-11-07", "2026-11-08"):                                      # 丸一日触っていない日は覚醒長ではない
        assert "long" not in r[d]["flags"], d
        assert "low" in r[d]["flags"], d                                        # 乗務のない日で操作 1 時間未満 → 低活動
    assert data["summary"]["sleep_median"] is None and data["summary"]["sleep_days"] == 0
    assert data["summary"]["long_days"] == 1
    html = sl.render_report(data)
    assert "推定睡眠" not in html
    # あとから思い出せた分だけ足せる（触っていなかった 73 時間ぶんが睡眠として出ることはない）
    sl.record_sleep(cfg, store, dt("2026-11-09 12:00"), "neru", ["11/8", "23:00"])
    sl.record_sleep(cfg, store, dt("2026-11-09 12:00"), "okita", ["8:30"])
    r = _rows(sl.build_report(cfg, store, dt("2026-11-09 12:00"), days=5))
    assert r["2026-11-09"]["sleep_min"] == 570 and r["2026-11-09"]["okita"] == ["08:30"] and r["2026-11-08"]["neru"] == ["23:00"]


def test_report_long_flag_counts_gap_crossing_midnight():
    """日をまたぐ離席（23:00 → 翌 07:00）は、終わった日に全体を数える（従来どおり）"""
    cp, cfg = env(days={"off": {"2026-11-03": "休", "2026-11-04": "休", "2026-11-05": "休"}})
    store = sl.Store(cp)

    def active(t):
        h = t.hour + t.minute / 60
        if t.date().isoformat() == "2026-11-03":
            return h < 23                                                       # 23 時間触り続けて 23:00 に離れる
        return h >= 7                                                           # 11/4 は 07:00 から、11/5 は終日
    _ticks(store, "2026-11-03 00:00", "2026-11-06 00:00", active)
    r = _rows(sl.build_report(cfg, store, dt("2026-11-05 23:59"), days=3))
    assert "long" in r["2026-11-03"]["flags"]          # その日のうちの離席は 1 時間だけ
    assert "long" not in r["2026-11-04"]["flags"]      # 8 時間の離席が終わった日
    assert "long" not in r["2026-11-05"]["flags"]      # その日のうちに 7 時間の離席（00:00〜07:00）が終わった日


def test_report_long_flag_ignores_time_away_on_shift():
    """乗務で外出していた時間は離席に数えない。家にいるあいだ触り続けていたら、外出が長くても覚醒長"""
    cp, cfg = env()
    store = sl.Store(cp)

    def active(t):                                                             # 11/2（月）18:24 に出て、翌 07:00 に戻る
        return not (dt("2026-11-02 18:24") <= t < dt("2026-11-03 07:00"))
    _ticks(store, "2026-11-02 00:00", "2026-11-03 12:00", active)
    r = _rows(sl.build_report(cfg, store, dt("2026-11-03 12:00"), days=2))
    assert r["2026-11-02"]["shift"] in ("worked", "unconfirmed") and r["2026-11-02"]["away"][20]
    assert "long" in r["2026-11-02"]["flags"]


def test_report_ignores_future_marks_and_shows_tile_note():
    cp, cfg = env(days=OFF)
    cfg = _mood(cp, "2026-11-03 23:00,neru,\n2026-11-04 07:00,okita,\n2026-11-04 23:00,neru,\n")
    store = sl.Store(cp)
    data = sl.build_report(cfg, store, dt("2026-11-04 12:00"), days=3)          # 11/4 23:00 の行はまだ来ていない
    r = _rows(data)
    assert r["2026-11-04"]["neru"] == [] and r["2026-11-04"]["sleep_min"] == 480
    assert data["summary"]["sleep_days"] == 2 and data["summary"]["sleep_paired_days"] == 1   # 記録がある日は 11/3（寝た）と 11/4（起きた）
    html = sl.render_report(data)
    assert "2 / 3 日" in html                                                    # 記録のある日 / 期間の日数（今日まで）
