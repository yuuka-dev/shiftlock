"""ネイティブ版（native/）向けの書き出し（pc/tools/export_native.py）。
文面の JSON がコミット済みの生成物と同じか、言語どうしでキーとプレースホルダが揃っているかを見張る。"""
from __future__ import annotations

import json
import re
import string
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "tools"))
import export_native as ex
import shiftlock.i18n as M
import shiftlock.i18n.ja as JA


def _placeholders(template: str) -> set:
    return {f for _, f, _, _ in string.Formatter().parse(template) if f is not None}


def test_messages_have_app_keys_in_both_langs():
    ja, kansai = ex.build_messages("ja"), ex.build_messages("ja_kansai")
    assert set(ja) == set(kansai)
    assert ja["APP_BTN_KITAKU"] == "帰ってきた"
    assert set(ja["APP_PHASE"]) == {"free", "lastcall", "soft", "hard", "go", "go_last"}
    assert ja["RECORDS_KITAKU"].startswith("[shiftlock] ")
    assert list(ja) == sorted(ja)
    # 要らない文（設定メニュー）は書き出さない。CLI_ は shl.exe が使う
    assert not any(k.startswith(("MENU_", "SETTINGS_")) for k in ja)
    assert ja["CLI_HELP"].startswith("shiftlock の使い方") and "{cmd}" in ja["SHL_UNKNOWN"] and "{cmd}" in ja["SHL_NOT_YET"]
    # 見るだけのコマンド（shl status / yotei / day list / kakunin）の文。Claude 向けの文（_CTX）は持っていかない
    assert {"STATUS_HEAD", "STATUS_ENFORCE_ITEM_LIMIT", "DAYS_CELL", "DAYS_WORK", "CONFIRM_KAKUNIN_HEAD", "CONFIRM_USER"} <= set(ja)
    assert not any(k.endswith("_CTX") for k in ja)
    assert "INTENT_LABEL_SEP" in ja and "INTENT_LABEL" in ja
    assert all(k in ("INTENT_LABEL", "INTENT_LABEL_SEP") for k in ja if k.startswith("INTENT_"))


def test_placeholders_match_between_langs():
    """言語が違ってもプレースホルダの名前の集合は同じ（辞書の値も 1 つずつ比べる）"""
    ja, kansai = ex.build_messages("ja"), ex.build_messages("ja_kansai")
    for key, a in ja.items():
        b = kansai[key]
        if isinstance(a, dict):
            assert isinstance(b, dict) and set(a) == set(b), key
            for sub in a:
                assert _placeholders(a[sub]) == _placeholders(b[sub]), f"{key}.{sub}"
        else:
            assert isinstance(b, str), key
            assert _placeholders(a) == _placeholders(b), key


def test_escaped_braces_are_kept():
    """{{...}} は Python の書式のエスケープ。書き出しでは直さずそのまま残す（C# 側が解釈する）"""
    assert "{{...}}" in ex.build_messages("ja")["CONFIG_NOT_DICT"]


@pytest.mark.parametrize("bad", ["{x:02d}", "{x!r}", "{}", "{0}"])
def test_format_spec_is_rejected(monkeypatch, bad):
    """書式指定・変換・位置引数は C# 側で解釈できないので、書き出す前に断る"""
    before = M.LANG
    monkeypatch.setattr(JA, "APP_NEXT_SHIFT", "次の乗務 " + bad)
    with pytest.raises(ValueError):
        ex.build_messages("ja")
    assert M.LANG == before


def test_non_string_value_is_rejected(monkeypatch):
    monkeypatch.setattr(JA, "APP_SCHEDULE", ("予定",))
    with pytest.raises(ValueError):
        ex.build_messages("ja")


def test_unknown_lang_is_rejected():
    """知らない言語は既定に落とさず断る（別の言語の文を別名で書き出さない）"""
    with pytest.raises(ValueError):
        ex.build_messages("xx")


def test_render_is_lf_indent1_unescaped():
    assert ex.render({"a": "あ", "b": {"c": "d"}}) == '{\n "a": "あ",\n "b": {\n  "c": "d"\n }\n}\n'


def test_outputs_match_committed():
    for path, text in ex.outputs().items():
        assert path.is_file(), f"{path} が無い。python3 pc/tools/export_native.py を実行する"
        assert path.read_bytes().decode("utf-8") == text, f"{path} が古い。python3 pc/tools/export_native.py を実行する"


def test_lang_is_restored():
    """outputs() を呼んだあと i18n.LANG と文面が呼ぶ前と同じ（ほかのテストの言語を変えない）"""
    try:
        M.set_lang("ja")
        ex.outputs()
        assert M.LANG == "ja" and M.APP_NO_SHIFT == JA.APP_NO_SHIFT
        M.set_lang("ja_kansai")
        ex.outputs()
        assert M.LANG == "ja_kansai" and M.APP_NO_SHIFT != JA.APP_NO_SHIFT
    finally:
        M.set_lang(M.DEFAULT_LANG)


def test_check_reports_stale(tmp_path, monkeypatch):
    """--check は生成物が違えば 1、同じなら 0。ファイルは書き換えない"""
    assert ex.main(["--check"]) == 0
    stale = tmp_path / "messages.ja.json"
    stale.write_text("{}\n", encoding="utf-8")
    monkeypatch.setattr(ex, "outputs", lambda: {stale: "{\n}\n"})
    assert ex.main(["--check"]) == 1
    assert stale.read_text(encoding="utf-8") == "{}\n"
    monkeypatch.setattr(ex, "outputs", lambda: {tmp_path / "none.json": "{}\n"})
    assert ex.main(["--check"]) == 1


def test_all_app_keys_are_exported():
    """ja.py の APP_ は全部書き出しに入る（test_package の『使われているか』から外してある分をここで見張る）"""
    ja = ex.build_messages("ja")
    names = {n for n in dir(JA) if n.isupper() and n.startswith("APP_")}
    assert names and names <= set(ja)


def test_vectors_use_only_synthetic_dates():
    """テストベクタの日付は合成（2026-12-01 以降）だけ。実データの日付を入れない"""
    text = json.dumps(ex.build_vectors(), ensure_ascii=False)
    dates = set(re.findall(r"20\d\d-\d\d-\d\d", text))
    assert dates and all(d >= "2026-12-01" for d in dates), sorted(d for d in dates if d < "2026-12-01")


def test_vectors_shape():
    """どのケースも name / files / input / expect を持ち、節の中で名前が重ならない"""
    for fname, body in ex.build_vectors().items():
        for section, cases in body.items():
            assert cases, f"{fname} {section}"
            names = [c["name"] for c in cases]
            assert len(names) == len(set(names)), f"{fname} {section}"
            assert all(set(c) == {"name", "files", "input", "expect"} for c in cases), f"{fname} {section}"


def test_vectors_base_plan():
    """土台の設定の 2026-12-01 の時刻（計画書に書いてある値）"""
    case = ex.build_vectors()["plan.json"]["make_plan"][0]
    assert case["expect"] == {"day": "2026-12-01", "start_target": "2026-12-01T17:00", "start_latest": "2026-12-01T17:30",
                              "depart_target": "2026-12-01T16:00", "depart_latest": "2026-12-01T16:30",
                              "soft_close": "2026-12-01T08:00", "hard_close": "2026-12-01T10:30", "lastcall": "2026-12-01T07:20"}


def test_vectors_do_not_change_lang():
    before = M.LANG
    ex.build_vectors()
    assert M.LANG == before


def test_record_vectors_use_app_as_source():
    """記録のベクタは出所 app で書く（Python 版の claude / phone が混ざらない）。断ったステップは events.log を増やさない"""
    cases = ex.build_vectors()["records.json"]["steps"]
    assert len(cases) >= 21
    for c in cases:
        assert not any("claude" in line or "phone" in line for line in c["expect"]["events"]), c["name"]
        assert len(c["expect"]["results"]) == len(c["input"]["steps"]), c["name"]
        assert sum(r["recorded"] for r in c["expect"]["results"]) == len(c["expect"]["events"]), c["name"]
        assert not any(r["message"].startswith(M.PREFIX) for r in c["expect"]["results"]), c["name"]


def test_do_kitaku_and_neru_default_source_is_claude(tmp_path):
    """via を足しても、既定（hook・CLI）の出所は claude のまま"""
    import shiftlock as sl
    from datetime import datetime
    (tmp_path / "shifts.json").write_text(json.dumps(ex.BASE), encoding="utf-8")
    before = M.LANG
    try:
        cfg, store = sl.load_config(tmp_path / "shifts.json"), sl.Store(tmp_path / "shifts.json")
        now = datetime(2026, 12, 1, 8, 10)
        p = sl.target_plan(cfg, now)
        sl.do_neru(cfg, store, now, p)
        sl.do_kitaku(cfg, store, now, p)
        lines = (tmp_path / "events.log").read_text(encoding="utf-8").splitlines()
        assert [ln.split("\t")[3] for ln in lines] == ["claude 2026-12-01T08:10", "claude 2026-12-01T08:10"]
    finally:
        M.set_lang(before)


def test_snapshot_vectors_cover_all_phases_and_countdowns():
    """画面の値のベクタが、フェーズ 6 つと残り時間の相手 6 つ（APP_PHASE / APP_COUNTDOWN のキー）を全部通る"""
    cases = ex.build_vectors()["snapshot.json"]["build"]
    ja = ex.build_messages("ja")
    assert {c["expect"]["phase"] for c in cases} == set(ja["APP_PHASE"])
    assert {c["expect"]["countdown_key"] for c in cases} - {None} == set(ja["APP_COUNTDOWN"])
    assert all(len(c["expect"]["schedule"]) == 14 for c in cases)


def test_tick_vectors_shape():
    """tick.json: 各回の結果は actions / warnings / sound を持ち、回数は steps と同じ。watch したファイルは全部 expect.files にある。
    一時フォルダのパスが残っていない"""
    body = ex.build_vectors()["tick.json"]
    for section, cases in body.items():
        if section == "release":   # 緊急解除は tick を通らない（形は test_release_vectors で見る）
            continue
        for c in cases:
            name = f"{section} {c['name']}"
            assert len(c["expect"]["results"]) == len(c["input"]["steps"]), name
            assert all(set(r) == {"actions", "warnings", "sound"} for r in c["expect"]["results"]), name
            assert set(c["expect"]["files"]) == set(c["input"]["watch"]), name
            assert all(set(s) <= {"now", "idle", "game", "uptime", "dry_run"} for s in c["input"]["steps"]), name
    text = json.dumps(body, ensure_ascii=False)
    assert "/tmp" not in text and "Temp" not in text and "tmp" not in text.lower().replace("{dir}", "")


def test_tick_usage_vectors_have_no_actions():
    """節 usage は稼働・離席の記録だけを見る（通知が混ざらない時刻を選んである）"""
    cases = ex.build_vectors()["tick.json"]["usage"]
    assert len(cases) >= 8
    for c in cases:
        assert all(r["actions"] == [] and r["warnings"] == [] for r in c["expect"]["results"]), c["name"]
        assert c["expect"]["files"]["usage-pc.json"] is not None, c["name"]


def _tick_kinds(section: str) -> list:
    return [[a["kind"] for r in c["expect"]["results"] for a in r["actions"]] for c in ex.build_vectors()["tick.json"][section]]


def test_tick_notify_vectors_cover_phases_and_nag():
    """節 notify はフェーズ通知 5 種と声かけだけ（自動起きた・確認・強制が混ざらない）。間引きの回数も見る"""
    kinds = _tick_kinds("notify")
    assert {k for ks in kinds for k in ks} == {"lastcall", "soft", "hard", "go", "go_last", "nag"}
    by_name = {c["name"]: ks for c, ks in zip(ex.build_vectors()["tick.json"]["notify"], kinds)}
    assert by_name["lastcall は 1 回だけ"] == ["lastcall"]
    assert by_name["soft は 30 分おき"] == ["lastcall", "soft", "soft"]
    assert by_name["hard は 10 分おき"] == ["hard", "hard", "hard"]
    assert by_name["go と go_last は 1 回ずつ"] == ["go", "go_last"]
    assert by_name["声かけは連続 120 分で 1 回、その 30 分後にもう 1 回"] == ["nag", "nag"]
    assert by_name["離席から戻ると声かけは数え直し"] == []
    assert by_name["同じ回にフェーズ通知と声かけ"] == ["soft", "nag"]


def test_tick_sound_vectors():
    """節 sound: 鳴らすのは 1 件、無いファイルの警告は 1 日 1 回、数字でない音量は既定と警告"""
    cases = {c["name"]: c["expect"]["results"] for c in ex.build_vectors()["tick.json"]["sound"]}
    assert [a["sound"] for a in cases["種類ごとの音"][1]["actions"]] == ["{DIR}/hard.wav"]
    assert [a["sound"] for a in cases["同じ回に 2 件あれば優先の高い 1 件だけ"][0]["actions"]] == ["{DIR}/s.wav", ""]
    assert [a["sound"] for a in cases["優先の高いほうのファイルが無ければ次の 1 件"][0]["actions"]] == ["", "{DIR}/n.wav"]
    assert [len(r["warnings"]) for r in cases["無いファイルの警告は 1 日 1 回"]] == [1, 0]
    assert cases["sound_volume が数字でなければ 0.8 と警告"][0]["sound"] == {"volume": 0.8, "max_seconds": 20}
    assert cases["sound_volume が数字でなければ 0.8 と警告"][0]["warnings"] == [JA.TICK_SOUND_NUMBER_ERROR]
    assert cases["音量と最長秒数"][0]["sound"] == {"volume": 0.5, "max_seconds": 7}


def test_tick_autowake_vectors():
    """節 autowake: 起きたにするケースと，しないケースが名前どおりに分かれている。events.log のメモは auto HH:MM-HH:MM"""
    cases = ex.build_vectors()["tick.json"]["autowake"]
    fired = {c["name"]: any(a["kind"] == "auto_okita" for r in c["expect"]["results"] for a in r["actions"]) for c in cases}
    yes = {"離席だけ 6 時間（auto_wake の既定）", "「ねる」あり＋離席", "「ねる」だけ（PC は切っていた）",
           "auto_wake が neru でも「ねる」があれば起きたにする", "auto_wake_hours 3 なら 3 時間で成立",
           "プロンプト以外の行（k が p でない）は区切らない", "出発の最終までで足りる（そのまま最終出発の通知も出る）",
           "前日が乗務: home_by_latest の後から数える", "前日が乗務: 帰宅の記録があればそこから数える",
           "前日が乗務: home_by_latest を変える", "前日が乗務でも休みにしていれば数える", "関西弁"}
    assert {n for n, f in fired.items() if f} == yes
    assert len(cases) - len(yes) >= 9
    by_name = {c["name"]: c["expect"] for c in cases}
    assert by_name["離席だけ 6 時間（auto_wake の既定）"]["files"]["events.log"] == [
        "2026-12-07T14:30\t2026-12-07\tokita\tauto 08:10-14:29"]
    assert by_name["前日が乗務: home_by_latest の後から数える"]["files"]["events.log"][0].endswith("auto 09:00-15:29")
    assert by_name["前日が乗務: 帰宅の記録があればそこから数える"]["files"]["events.log"][0].endswith("auto 07:05-13:29")
    kinds = {a["kind"] for c in cases for r in c["expect"]["results"] for a in r["actions"]}
    assert kinds <= {"auto_okita", "lastcall", "soft", "hard", "go", "go_last"}


def test_tick_phone_vectors():
    """節 phone: iPhone の印が state.json・events.log・days.json に名前どおりに入る"""
    by_name = {c["name"]: c["expect"] for c in ex.build_vectors()["tick.json"]["phone"]}

    def texts(name):
        return [a["text"] for r in by_name[name]["results"] for a in r["actions"] if a["kind"] == "auto_okita"]

    e = by_name["neru の後の okita → 起きた（時刻は iPhone の記録）"]
    assert e["files"]["state.json"]["shifts"]["2026-12-07"] == {"sleep_at": "2026-12-07T08:10", "status": "okita",
                                                               "at": "2026-12-07T14:20"}
    assert e["files"]["events.log"][-1] == "2026-12-07T14:20\t2026-12-07\tokita\tphone 08:10-14:20"
    assert len(texts("neru の後の okita → 起きた（時刻は iPhone の記録）")) == 1
    for name in ("二度寝をまとめて取り込んでも最後の okita が効く", "二度寝を途中で 1 回取り込んでいても最後の okita が効く"):
        assert by_name[name]["files"]["state.json"]["shifts"]["2026-12-07"]["at"] == "2026-12-07T14:20", name
    assert len(by_name["kitaku → 帰宅の時刻。2 回目は書かない"]["files"]["events.log"]) == 1
    e = by_name["日付つきの yasumi → その日の事後報告。2 回目の見張りでは適用しない"]
    assert e["files"]["events.log"] == ["2026-12-03T10:00\t2026-12-02\tyasumi\tlate phone"]
    assert e["files"]["state.json"]["phone_applied"] == ["2026-12-03T09:50|yasumi|2026-12-02"]
    assert [sum(a["kind"] == "auto_okita" for a in r["actions"]) for r in e["results"]] == [1, 0]
    assert texts("日付つきの yasumi → その日の事後報告。2 回目の見張りでは適用しない")[0].startswith(JA.PHONE_LATE_PREFIX)
    e = by_name["休みの日（土曜）に deta → 出勤の例外日に足す"]
    assert e["files"]["days.json"]["work"] == {"2026-12-05": "事後報告"}
    assert e["files"]["state.json"]["undo"]["days_work_added"] is True
    for name in ("休みの日に yasumi は記録しない（適用済みには残る）", "まだ終わっていない乗務の yasumi は記録しない"):
        assert by_name[name]["files"]["events.log"] is None and len(by_name[name]["files"]["state.json"]["phone_applied"]) == 1
    for name in ("未来の時刻と lookback_days より前の印は無視", "気分の行と知らない印は無視",
                 "mood_file のファイルが無ければ何も起きない", "neru の無い okita は何もしない"):
        assert by_name[name]["files"] == {"state.json": None, "events.log": None, "days.json": None}, name
    assert "当日欠勤" in texts("当日欠勤の目安に達したら一文が付く")[0]


def test_tick_confirm_vectors():
    """節 confirm: 聞くのは 1 日 1 件。聞いた乗務に confirm_asked が付く"""
    by_name = {c["name"]: c["expect"] for c in ex.build_vectors()["tick.json"]["confirm"]}

    def asked(name):
        return [sum(a["kind"] == "confirm" for a in r["actions"]) for r in by_name[name]["results"]]

    assert asked("同じ日の 2 回目は聞かない。翌日は次の 1 件") == [1, 0, 1]
    assert asked("翌朝 07:00（shift_end_earliest）より前は聞かない") == [0, 1]
    assert asked("mode が off なら聞かない") == asked("離席中は聞かない") == asked("休みにした乗務は聞かない") == [0]
    hint = [a["text"] for a in by_name["乗務の時間帯に PC を 60 分触っていたら一言付く"]["results"][0]["actions"] if a["kind"] == "confirm"]
    assert JA.CONFIRM_HOME_HINT in hint[0]
    plain = [a["text"] for a in by_name["55 分なら付かない"]["results"][0]["actions"] if a["kind"] == "confirm"]
    assert JA.CONFIRM_HOME_HINT not in plain[0]
    assert by_name["confirm_since が無ければ前日を書き、それより前は聞かない"]["files"]["state.json"]["confirm_since"] == "2026-12-09"


def _enforce_actions(name: str) -> list:
    """節 enforce のケース name の各回について、強制の予告・実行を (method, action) の並びで。"""
    case = next(c for c in ex.build_vectors()["tick.json"]["enforce"] if c["name"] == name)
    return [[(a["method"], a.get("action")) for a in r["actions"] if a["kind"] == "enforce"] for r in case["expect"]["results"]]


def test_tick_enforce_vectors():
    """節 enforce: 予告 → あと 5 分 → 実行 → 再予告 → 再実行の順と、止まる条件が名前どおり"""
    warn, run = [("popup", None)], [("enforce", "lock")]
    assert _enforce_actions("予告 → あと 5 分 → 実行 → 再予告 → 再実行") == [warn, [], warn, run, warn, run]
    assert _enforce_actions("実行のあと誰も触っていなければ（入力が実行より前）何もしない") == [warn, [], warn, run, []]
    assert _enforce_actions("回数の上限（max_runs 2）で止まる") == [warn, [], warn, run, warn, run, [], []]
    assert _enforce_actions("時間切れ（duration_hours 0.5）で止まる") == [warn, [], warn, run, warn, run, warn, run, warn, [], []]
    assert _enforce_actions("12 分以上空いたら予告からやり直し") == [warn, warn, [], run]
    assert _enforce_actions("実行のあと 12 分以上空いたら再予告からやり直し") == [warn, [], warn, run, warn, warn, run]
    assert _enforce_actions("dry_run の予告は状態を進めない") == [warn, warn]
    assert _enforce_actions("dry_run の実行は記録しない") == [run]
    assert _enforce_actions("action が shutdown")[-1] == [("enforce", "shutdown")]
    for name in ("STOP.txt があれば何もしない（通知の添え字に書く）", "解除済みの宣言は何もしない（添え字も出さない）",
                 "起動直後（uptime 600 秒）は発動しない", "uptime が 0 なら発動しない", "uptime が無ければ発動しない",
                 "離席中は何もしない", "起きた記録のある乗務は発動しない", "宣言が別の乗務日のものなら何もしない"):
        assert not any(_enforce_actions(name)), name
    # 宣言の無いケースで強制の予告・実行が出ることは無い（ほかの節には宣言を置いていない）
    for section, cases in ex.build_vectors()["tick.json"].items():
        if section in ("enforce", "release"):
            continue
        assert not any(a["kind"] == "enforce" for c in cases for r in c["expect"]["results"] for a in r["actions"]), section
    case = next(c for c in ex.build_vectors()["tick.json"]["enforce"] if c["name"] == "dry_run の実行は記録しない")
    assert case["expect"]["files"]["events.log"] is None
    assert case["expect"]["files"]["state.json"]["shifts"]["2026-12-07"]["enforce"]["runs"] == []


def test_release_vectors():
    """節 release: STOP.txt に 1 行、同意済みで未解除の宣言に released_at、events.log に enforce_release（理由 tray）"""
    cases = {c["name"]: c for c in ex.build_vectors()["tick.json"]["release"]}
    assert all(set(c["expect"]) == {"message", "state", "events", "stop"} for c in cases.values())
    assert all(c["expect"]["stop"][-1] == "2026-12-07 11:00 tray" for c in cases.values())
    e = cases["宣言が 2 件（別の乗務日）"]["expect"]
    assert [v["enforce"]["released_at"] for v in e["state"]["shifts"].values()] == ["2026-12-07T11:00"] * 2
    assert e["events"] == ["2026-12-07T11:00	2026-12-07	enforce_release	tray", "2026-12-07T11:00	2026-12-08	enforce_release	tray"]
    assert cases["宣言なし（乗務日は -）"]["expect"]["events"] == ["2026-12-07T11:00	-	enforce_release	tray"]
    assert cases["STOP.txt が既にあって宣言なし"]["expect"]["message"] == JA.ENFORCE_RELEASE_ALREADY
    e = cases["解除済みの宣言と同意の無い宣言には触らない"]["expect"]
    assert e["state"] == cases["解除済みの宣言と同意の無い宣言には触らない"]["files"]["state.json"]


def test_tick_calendar_vectors():
    """節 calendar: 同期の結果は次の見張りから効く。失敗は前回分を残して error を入れる。取りに行かないケースは書き直さない"""
    by_name = {c["name"]: c for c in ex.build_vectors()["tick.json"]["calendar"]}
    kinds = {name: [[a["kind"] for a in r["actions"]] for r in c["expect"]["results"]] for name, c in by_name.items()}
    assert kinds["カレンダー未設定なら何もしない"] == [[], ["lastcall"]]
    assert kinds["終日の「公休」で乗務日が休みになる"] == [[], []]
    assert kinds["「出勤」で休みの日が乗務日になる"] == [[], ["lastcall"]]
    assert kinds["日付の読めない DTSTART は失敗として前回分を残す"] == [[], []]

    def src(name, source="カレンダー"):
        return by_name[name]["expect"]["files"]["calendar.json"]["sources"][source]

    assert by_name["カレンダー未設定なら何もしない"]["expect"]["files"]["calendar.json"] is None
    assert src("RRULE は初回だけ（recurring_skipped 1）")["recurring_skipped"] == 1
    assert len(src("62 日を超える予定は 62 日ぶん")["off"]) == 62
    failed = src("取得に失敗したら前回分が残り error が入る")
    assert failed["error"] == "12/07 07:20 OSError: fetch failed" and failed["off"] == {"2026-12-07": "公休"}
    assert src("2 本のうち 1 本だけ失敗", "会社")["error"] is None and src("2 本のうち 1 本だけ失敗", "私用")["fetched"] is None
    for name in ("sync_minutes 内は取りに行かない（取りに行けば失敗が書かれる）", "前回の fetched が読めなければ同期しない（見張りは続く）"):
        assert by_name[name]["expect"]["files"]["calendar.json"] == by_name[name]["files"]["calendar.json"], name
    assert list(by_name["名前の重複に ' が付く・名前が無ければ番号"]["expect"]["files"]["calendar.json"]["sources"]) == \
        ["会社", "会社'", "カレンダー3"]
    assert src("旧形式の calendar.json は先頭のソースに引き継ぐ", "会社")["recurring_skipped"] == 2
    # Z つきの時刻は、書き出すマシンのタイムゾーンで日付が変わらない時刻だけ使う（UTC-3 〜 UTC+20 で同じ日）
    assert src("Z つきの時刻はローカルの日付")["off"] == {"2026-12-07": "休み"}


def test_tick_maintenance_vectors():
    """節 maintenance: 1 日 1 回だけ古い記録を消す。消えたファイルは null"""
    by_name = {c["name"]: c["expect"]["files"] for c in ex.build_vectors()["tick.json"]["maintenance"]}
    f = by_name["古い記録が消える"]
    assert f["maintenance.json"] == {"result": {"days": 3, "state": 1, "logs": 3, "events": 2, "reports": 3},
                                     "last_cleanup": "2027-12-04"}
    gone = {k for k, v in f.items() if v is None}
    assert gone == {"log/pc-2026-12.jsonl", "log/pc-2027-05.jsonl", "log/phone-2027-05.jsonl",
                    "reports/report_2027-07-21_2027-08-20.html", "reports/report_2027-07-04_2027-09-03.pdf",
                    "reports/report-2027-08-01.html"}
    assert f["events.log"] == ["2026-12-04T09:00\t2026-12-04\tyasumi\tapp"]
    assert list(f["state.json"]["shifts"]) == ["2027-10-05"] and f["days.json"]["memo"] == "残る"
    same = by_name["同じ日の 2 回目は整理しない"]
    assert same["maintenance.json"] == {"last_cleanup": "2027-12-04", "result": {"days": 9}}
    assert not any(v is None for k, v in same.items() if k != "log/pc-2027-12.jsonl")
    assert by_name["翌日は整理する"]["maintenance.json"]["memo"] == "残る"
    assert by_name["何も無いフォルダ（days.json は空で作られる）"]["days.json"] == {"off": {}, "work": {}, "times": {}}
    assert by_name["events.log が全部古ければ空になる"]["events.log"] == []


AGENT_KEYS = ("AGENT_MENU_OPEN", "AGENT_MENU_RELEASE", "AGENT_MENU_EXIT", "AGENT_RELEASE_TITLE", "AGENT_RELEASE_ASK",
              "AGENT_RELEASE_PARTIAL", "AGENT_RELEASE_FAILED", "AGENT_TIP_GUARD_TASK", "AGENT_TIP_FAILED", "AGENT_OPEN_FAILED",
              "AGENT_TEST_TOAST", "AGENT_TEST_POPUP",
              "AGENT_SETUP_AUTOSTART_ON", "AGENT_SETUP_AUTOSTART_OFF", "AGENT_SETUP_TASK_DISABLED", "AGENT_SETUP_TASK_ENABLED",
              "AGENT_SETUP_TASK_MISSING", "AGENT_SETUP_TASK_FAILED", "AGENT_SETUP_ROLLED_BACK", "AGENT_SETUP_ROLLBACK_FAILED",
              "AGENT_SETUP_STARTED",
              "AGENT_SETUP_STOPPED", "AGENT_SETUP_FAILED",
              # アプリの減速・一時停止（Limiter）。アプリの名前は入れない
              "AGENT_LIMIT_NOTICE", "AGENT_LIMIT_NOTICE_SUSPEND", "AGENT_LIMIT_SUSPENDED", "AGENT_LIMIT_RELEASED",
              "AGENT_LIMIT_SKIPPED", "AGENT_LIMIT_FAILED", "AGENT_LIMIT_BAD_RECORD", "AGENT_LIMIT_STALE_CAP")


def test_agent_keys_are_exported_in_both_langs():
    """常駐（Agent）の文は両方の言語にあり、ja.py の AGENT_ は全部書き出しに入る。
    強制を促す文・宣言や設定の入口になる文は無い（メニューは 開く・緊急解除・終了 の 3 つだけ）"""
    for lang in ex.LANGS:
        msgs = ex.build_messages(lang)
        assert not [k for k in AGENT_KEYS if k not in msgs], lang
        assert {k for k in msgs if k.startswith("AGENT_")} == set(AGENT_KEYS), lang
        assert {k for k in msgs if k.startswith("AGENT_MENU_")} == {"AGENT_MENU_OPEN", "AGENT_MENU_RELEASE", "AGENT_MENU_EXIT"}, lang
    assert {n for n in dir(JA) if n.isupper() and n.startswith("AGENT_")} == set(AGENT_KEYS)


TICK_KEYS = (
    "PHASE_LASTCALL_USER", "PHASE_SOFT_USER", "PHASE_HARD_USER", "PHASE_GO_USER", "PHASE_GO_LAST_USER",
    "AUTOWAKE_OKITA", "PHONE_LATE_PREFIX", "PHONE_OKITA",
    "RECORDS_LATE_BAD_DATE", "RECORDS_LATE_YASUMI", "RECORDS_LATE_WORKED", "RECORDS_PAID_SET", "RECORDS_YASUMI_PAID",
    "ABSENCE_OVER", "CONFIRM_HOME_HINT", "CONFIRM_MORE", "CONFIRM_USER",
    "ENFORCE_LABEL", "ENFORCE_SUFFIX", "ENFORCE_SUFFIX_STOP", "ENFORCE_SUFFIX_LIMIT", "ENFORCE_WARN", "ENFORCE_WARN_SOON", "ENFORCE_REWARN",
    "ENFORCE_RUN", "ENFORCE_RELEASED", "ENFORCE_RELEASE_ALREADY",
    "NOTIFY_SOUND_MISSING", "TICK_NAG", "TICK_NAG_GAME", "TICK_NAG_PC", "TICK_ENFORCE_ERROR",
    "TICK_SOUND_CONFIG_ERROR", "TICK_SOUND_NUMBER_ERROR", "CAL_UNSET", "CAL_SKIP", "CAL_FAIL", "CAL_OK",
)

ENFORCE_EXPORTED = {"ENFORCE_LABEL", "ENFORCE_SUFFIX", "ENFORCE_SUFFIX_STOP", "ENFORCE_SUFFIX_LIMIT", "ENFORCE_WARN", "ENFORCE_WARN_SOON",
                    "ENFORCE_REWARN", "ENFORCE_RUN", "ENFORCE_RELEASED", "ENFORCE_RELEASE_ALREADY"}


def test_tick_keys_are_exported_in_both_langs():
    """見張り（Agent）が使う文は両方の言語に書き出す"""
    for lang in ex.LANGS:
        msgs = ex.build_messages(lang)
        missing = [k for k in TICK_KEYS if k not in msgs]
        assert not missing, f"{lang}: {missing}"


def test_enforce_declare_and_claude_context_are_not_exported():
    """強制の宣言・状態表示の文と、Claude 向けの文（*_CTX）は書き出さない（C# に宣言の入口を作らない）"""
    ja = ex.build_messages("ja")
    assert not any(k.endswith("_CTX") for k in ja)
    for k in ("ENFORCE_TERMS", "ENFORCE_DECLARED", "PHASE_SOFT_CTX"):
        assert k not in ja, k
    assert {k for k in ja if k.startswith("ENFORCE_")} == ENFORCE_EXPORTED


def test_record_command_vectors():
    """shl の記録のコマンドのベクタ: 出所は yasumi・deru が shl、okita・neru は claude（cmd_keyword が渡さない）。app は混ざらない"""
    body = ex.build_vectors()["commands.json"]
    kw, late, days = body["cmd_keyword"], body["late_report"], body["days_set"]
    assert len(kw) >= 35 and len(late) >= 25 and len(days) >= 15
    for c in kw + late:
        assert c["input"]["cmd"] in ("okita", "neru", "itte", "yasumi", "deru"), c["name"]
        assert c["expect"]["text"].startswith(M.PREFIX), c["name"]
        for line in c["expect"]["events"]:
            memo = line.split("\t")[3]
            assert "app" not in memo and "phone" not in memo, c["name"]
            if line.split("\t")[2] == "sleep_at":
                assert memo.startswith("claude "), c["name"]
            if memo.startswith(("late", "on ", "off ")):
                assert memo.endswith(" shl"), c["name"]
    # 日付つきは事後報告（記録したら undo を残す）。日付なしの okita・neru は undo を書かない
    assert all(not c["input"]["words"] or c["input"]["cmd"] in ("yasumi", "deru") for c in kw)
    assert any((c["expect"]["state"] or {}).get("undo", {}).get("days_work_added") for c in late)
    for c in kw:
        if c["input"]["cmd"] in ("okita", "neru") and "undo" not in c["files"].get("state.json", {}):
            assert "undo" not in (c["expect"]["state"] or {}), c["name"]
    # 断るケース（何も書かない）と書くケースの両方がある
    assert any(not c["expect"]["events"] for c in kw) and any(not c["expect"]["events"] for c in late)
    assert {c["expect"]["code"] for c in days} == {0, 1}
    assert all((c["expect"]["code"] == 0) == bool(c["input"]["date"]) for c in days)


def test_record_cmd_matches_main(tmp_path, capsys):
    """cli.main の yasumi・day は record_cmd・day_cmd を通る（ベクタが main と同じ道を見ている）"""
    import shiftlock as sl
    from datetime import datetime
    before = M.LANG
    try:
        for name in ("a", "b"):
            (tmp_path / name).mkdir()
            (tmp_path / name / "shifts.json").write_text(json.dumps(ex.BASE), encoding="utf-8")
        a, b = tmp_path / "a" / "shifts.json", tmp_path / "b" / "shifts.json"
        assert sl.main(["day", "off", "明日", "用事", "--config", str(a)]) == 0
        out = capsys.readouterr().out
        code, text = sl.day_cmd("off", "明日", ["用事"], b, sl.Store(b), datetime.now())
        assert (code, text + "\n") == (0, out)
        assert (tmp_path / "a" / "days.json").read_text(encoding="utf-8") == (tmp_path / "b" / "days.json").read_text(encoding="utf-8")
        assert sl.main(["day", "rm", "--config", str(a)]) == 1
        assert capsys.readouterr().out == M.CLI_DAY_NEED_DATE + "\n"
        assert sl.main(["yasumi", "明日", "--config", str(a)]) == 0   # 先の日付は断る（何も書かない）
        out = capsys.readouterr().out
        assert out == sl.record_cmd("yasumi", [None, "明日"], sl.load_config(b), sl.Store(b), datetime.now()) + "\n"
        assert not (tmp_path / "a" / "state.json").exists()
    finally:
        M.set_lang(before)
