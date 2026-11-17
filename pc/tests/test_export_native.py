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
    # 画面に要らない文（設定メニューや CLI の説明）は書き出さない
    assert not any(k.startswith(("CLI_", "SETTINGS_", "ENFORCE_")) for k in ja)
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
