"""言語ファイルの切り替え（shifts.json の lang）と、言語ファイル同士の整合。
ja.py 単体の決まり（名前付きプレースホルダ・全定数が使われる）は test_package.py。"""
from __future__ import annotations

import ast
import importlib
import json
import string
import sys
import tempfile
import types
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
import shiftlock as sl
import shiftlock.i18n as M
import shiftlock.i18n.ja as JA

I18N = ROOT / "shiftlock" / "i18n"
LANG_FILES = sorted(p.stem for p in I18N.glob("*.py") if p.stem != "__init__")
MODULES = sorted(p.stem for p in (ROOT / "shiftlock").glob("*.py") if p.stem != "__init__")


def _consts(mod) -> dict:
    return {k: v for k, v in vars(mod).items() if k.isupper() and not k.startswith("_")}


def _fields(v) -> set:
    """文字列なら {name} の集合、dict ならキーと値の {name}、list/tuple なら要素の {name}"""
    if isinstance(v, str):
        return {f for _, f, _, _ in string.Formatter().parse(v) if f}
    if isinstance(v, dict):
        return {("key", k) for k in v} | set().union(*(_fields(x) for x in v.values())) if v else set()
    if isinstance(v, (list, tuple)):
        return set().union(*(_fields(x) for x in v)) if v else set()
    return set()


def _env(**over):
    t = Path(tempfile.mkdtemp())
    cfg = json.loads((ROOT / "shifts.example.json").read_text(encoding="utf-8"))
    cfg.update(over)
    (t / "shifts.json").write_text(json.dumps(cfg, ensure_ascii=False), encoding="utf-8")
    (t / "days.json").write_text("{}", encoding="utf-8")
    return t / "shifts.json"


class FakeIO:
    def __init__(self, answers): self.a = list(answers); self.out = []
    def input(self, p): self.out.append(p); return self.a.pop(0)
    def print(self, *x): self.out.append(" ".join(map(str, x)))


# ---------------------------------------------------------------- 言語ファイル同士の整合

def test_lang_files_are_registered():
    """i18n/ の言語ファイル = LANGS。既定は LANGS の中。表示名（ja.LANG_LABEL）も全部ある"""
    assert set(LANG_FILES) == set(M.LANGS), (LANG_FILES, M.LANGS)
    assert M.DEFAULT_LANG in M.LANGS
    # 表示名は任意の言語の分も持っておく（ファイルが無い環境では LANGS に入らないだけ）
    assert set(JA.LANG_LABEL) == set(M.BUILTIN_LANGS) | set(M.OPTIONAL_LANGS)
    assert set(M.LANGS) <= set(JA.LANG_LABEL)


def test_optional_lang_needs_its_file():
    """任意の言語（配布物に入れない inm.py）は、ファイルが手元にある環境でだけ選べる（#33）"""
    assert M.DEFAULT_LANG in M.BUILTIN_LANGS and "ja" in M.BUILTIN_LANGS
    assert not set(M.BUILTIN_LANGS) & set(M.OPTIONAL_LANGS)
    for name in M.BUILTIN_LANGS:                       # いつも配る言語はファイルが必ずある
        assert (I18N / f"{name}.py").exists(), name
    for name in M.OPTIONAL_LANGS:
        assert (name in M.LANGS) == (I18N / f"{name}.py").exists(), name
    # ファイルが無い任意の言語は入らない。並びは「いつも配る言語 → 任意の言語」（設定メニュー 20 の番号が変わらない）
    assert M.available_langs(optional=("zz_missing",)) == M.BUILTIN_LANGS
    assert M.LANGS[:len(M.BUILTIN_LANGS)] == M.BUILTIN_LANGS


def test_missing_optional_lang_falls_back(monkeypatch):
    """ファイルが無い環境で lang に任意の言語が書かれていても落ちず、既定に戻す。設定メニューにも出さない（#33）"""
    monkeypatch.setattr(M, "LANGS", M.BUILTIN_LANGS)
    try:
        for name in M.OPTIONAL_LANGS:
            assert M.set_lang(name) == M.DEFAULT_LANG
            cfg = sl.load_config(_env(lang=name))
            assert cfg["lang"] == M.DEFAULT_LANG and M.LANG == M.DEFAULT_LANG
            cp = _env(lang=name)
            io = FakeIO(["20", "", "s"])
            sl.settings_menu(cp, datetime(2026, 11, 1, 9, 0), io)
            assert not any(JA.LANG_LABEL[name] in x for x in io.out), name
            assert any("20)" in x and JA.LANG_LABEL[M.DEFAULT_LANG] in x for x in io.out), name
    finally:
        M.set_lang(M.DEFAULT_LANG)


def test_lang_files_match_base():
    """ja.py が土台。他の言語ファイルは ja.py に無いキーを持たず、あるキーは同じ型・同じプレースホルダ（足りないのは可）"""
    base = _consts(JA)
    for name in LANG_FILES:
        if name == "ja":
            continue
        other = _consts(importlib.import_module(f"shiftlock.i18n.{name}"))
        extra = set(other) - set(base)
        assert not extra, f"{name}.py に ja.py に無いキー: {sorted(extra)}"
        for k, v in other.items():
            assert type(v) is type(base[k]), f"{name}.{k} の型が ja.py と違う"
            assert _fields(v) == _fields(base[k]), f"{name}.{k} のプレースホルダが ja.py と違う: {_fields(v) ^ _fields(base[k])}"


def test_no_import_time_binding_of_messages():
    """モジュール直下で X = M.Y と束縛しない（import 時の言語で固定されて lang が効かなくなる）"""
    for m in MODULES:
        tree = ast.parse((ROOT / "shiftlock" / f"{m}.py").read_text(encoding="utf-8"))
        for node in tree.body:
            if isinstance(node, ast.Assign):
                for sub in ast.walk(node.value):
                    if isinstance(sub, ast.Attribute) and isinstance(sub.value, ast.Name) and sub.value.id == "M":
                        raise AssertionError(f"{m}.py:{node.lineno} モジュール直下で M.{sub.attr} を束縛している")


# ---------------------------------------------------------------- 切り替え

def test_set_lang_switches_and_falls_back(monkeypatch):
    try:
        assert M.set_lang("ja") == "ja" and M.LANG == "ja"
        assert M.COMMON_NO_PLAN == JA.COMMON_NO_PLAN
        assert M.set_lang("ja_kansai") == "ja_kansai"
        assert M.COMMON_NO_PLAN != JA.COMMON_NO_PLAN and "ないで" in M.COMMON_NO_PLAN
        # 知らない名前・None は既定
        assert M.set_lang("xx") == M.DEFAULT_LANG and M.LANG == M.DEFAULT_LANG
        assert M.set_lang(None) == M.DEFAULT_LANG
        # 言語ファイルに無いキーは ja.py の文（キーが 1 つしか無い言語をでっち上げる）
        fake = types.ModuleType("shiftlock.i18n.zz")
        fake.COMMON_NONE = "(zz)"
        monkeypatch.setitem(sys.modules, "shiftlock.i18n.zz", fake)
        monkeypatch.setattr(M, "LANGS", M.LANGS + ("zz",))
        assert M.set_lang("zz") == "zz"
        assert M.COMMON_NONE == "(zz)" and M.COMMON_NO_PLAN == JA.COMMON_NO_PLAN
        # 戻すと上書きが残らない
        M.set_lang("ja_kansai")
        assert M.COMMON_NONE == JA.COMMON_NONE
    finally:
        M.set_lang(M.DEFAULT_LANG)


def test_load_config_applies_lang():
    try:
        cfg = sl.load_config(_env(lang="ja"))
        assert cfg["lang"] == "ja" and M.LANG == "ja" and M.MENU_SAVED == JA.MENU_SAVED
        for name in M.OPTIONAL_LANGS:                  # 任意の言語は、ファイルがある環境でだけ選べる
            if name in M.LANGS:
                cfg = sl.load_config(_env(lang=name))
                assert cfg["lang"] == name and M.LANG == name
        # 設定ミスは既定に戻す（本線を止めない）
        cfg = sl.load_config(_env(lang="klingon"))
        assert cfg["lang"] == M.DEFAULT_LANG and M.LANG == M.DEFAULT_LANG
        # lang が無い shifts.json は既定
        cp = _env()
        raw = json.loads(cp.read_text(encoding="utf-8")); raw.pop("lang", None)
        cp.write_text(json.dumps(raw), encoding="utf-8")
        assert sl.load_config(cp)["lang"] == M.DEFAULT_LANG
    finally:
        M.set_lang(M.DEFAULT_LANG)


def test_messages_follow_lang_at_use_time():
    """import 時ではなく使うときの言語で出る（束縛していた LABEL / ENFORCE_LABEL / MOOD_LABEL / HELP_TEXT の経路）"""
    now = datetime(2026, 11, 1, 9, 0)
    try:
        cp = _env(lang="ja")
        cfg = sl.load_config(cp)
        assert "やっぱり出る" in M.CLI_HELP
        assert "ロック" in sl.enforce_status_text(cfg, sl.Store(cp), now)
        assert sl.mood_str(2) == "▲▲+2"
        cp = _env(lang="ja_kansai")
        sl.load_config(cp)
        assert "やっぱ出る" in M.CLI_HELP and "やっぱり出る" not in M.CLI_HELP
    finally:
        M.set_lang(M.DEFAULT_LANG)


def test_settings_menu_lang(capsys):
    try:
        # メニューと help は load_config を通らないが、lang を読んで文面を合わせる
        cp = _env(lang="ja")
        io = FakeIO(["s"])
        sl.settings_menu(cp, datetime(2026, 11, 1, 9, 0), io)
        assert JA.MENU_SAVED in io.out and M.LANG == "ja"
        assert sl.main(["help", "--config", str(cp)]) == 0 and "やっぱり出る" in capsys.readouterr().out
        cp = _env()
        io = FakeIO(["20", "x", "9", "1", "s"])
        assert sl.settings_menu(cp, datetime(2026, 11, 1, 9, 0), io) is True
        raw = json.loads(cp.read_text(encoding="utf-8"))
        assert raw["lang"] == "ja"
        assert any("20)" in x and "関西弁" in x for x in io.out)        # メニューに今の言語が出る
        assert any("標準語" in x and "1)" in x for x in io.out)         # 選択肢
        assert sum("✗" in x for x in io.out) == 2                        # x と 9 は弾く
        assert sl.load_config(cp)["lang"] == "ja" and M.LANG == "ja"
        # lang が [] / {} / 知らない値でもメニューは既定（関西弁）として出て、20 で直せる（Copilot の指摘）
        for bad in ([], {}, "xx", 5):
            cp = _env(lang=bad)
            io = FakeIO(["20", "1", "s"])
            assert sl.settings_menu(cp, datetime(2026, 11, 1, 9, 0), io) is True, bad
            assert any("20)" in x and "関西弁" in x for x in io.out), bad
            assert json.loads(cp.read_text(encoding="utf-8"))["lang"] == "ja", bad
        # Enter は今のまま
        cp = _env(lang="ja")
        io = FakeIO(["20", "", "s"])
        sl.settings_menu(cp, datetime(2026, 11, 1, 9, 0), io)
        assert json.loads(cp.read_text(encoding="utf-8"))["lang"] == "ja"
    finally:
        M.set_lang(M.DEFAULT_LANG)
