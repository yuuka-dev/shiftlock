import json

import pytest

from shiftlock import i18n as M
from shiftlock.apps import normalize_exe
from shiftlock.cli import main
from shiftlock.config import DEFAULTS, load_config


def test_defaults_have_apps():
    assert DEFAULTS["apps"] == {"registered": {}, "active_input_minutes": 5, "active_cpu_percent": 5}


def test_load_config_merges_apps(tmp_path):
    p = tmp_path / "shifts.json"
    p.write_text('{"apps": {"registered": {"code.exe": "dev"}}}', encoding="utf-8")
    cfg = load_config(p)
    assert cfg["apps"] == {"registered": {"code.exe": "dev"}, "active_input_minutes": 5, "active_cpu_percent": 5}


def _cfg(tmp_path, body='{"lang": "ja", "start_target": "20:00"}'):
    p = tmp_path / "shifts.json"
    p.write_text(body, encoding="utf-8")
    return p


def _read(p):
    return json.loads(p.read_text(encoding="utf-8"))


def test_add_normalizes_and_keeps_other_keys(tmp_path, capsys):
    p = _cfg(tmp_path)
    assert main(["app", "add", "Code", "dev", "--config", str(p)]) == 0
    assert _read(p) == {"lang": "ja", "start_target": "20:00", "apps": {"registered": {"code.exe": "dev"}}}
    assert "code.exe" in capsys.readouterr().out


def test_add_again_changes_category(tmp_path, capsys):
    p = _cfg(tmp_path)
    assert main(["app", "add", "code.exe", "dev", "--config", str(p)]) == 0
    capsys.readouterr()
    assert main(["app", "add", "CODE", "adobe", "--config", str(p)]) == 0
    assert _read(p)["apps"]["registered"] == {"code.exe": "adobe"}
    assert capsys.readouterr().out.strip() == "code.exe を dev から adobe に付け替えたよ"


def test_add_same_category_is_added(tmp_path, capsys):
    p = _cfg(tmp_path)
    main(["app", "add", "code.exe", "dev", "--config", str(p)])
    capsys.readouterr()
    assert main(["app", "add", "code.exe", "dev", "--config", str(p)]) == 0
    assert "登録したよ" in capsys.readouterr().out


def _refused(tmp_path, args, body='{"lang": "ja"}'):
    p = _cfg(tmp_path, body)
    before = p.read_bytes()
    assert main(["app", *args, "--config", str(p)]) == 1
    assert p.read_bytes() == before
    return p


def test_add_game_is_refused(tmp_path, capsys):
    _refused(tmp_path, ["add", "eldenring.exe", "game"])
    assert "ゲームは登録しなくていいよ" in capsys.readouterr().out


def test_add_bad_category_is_refused(tmp_path):
    _refused(tmp_path, ["add", "code.exe", "video"])


def test_add_path_is_refused(tmp_path):
    _refused(tmp_path, ["add", r"C:\x\code.exe", "dev"])


@pytest.mark.parametrize("body", ['{"apps": 5}', '{"apps": {"registered": []}}'])
def test_add_bad_section_is_refused(tmp_path, body):
    _refused(tmp_path, ["add", "code.exe", "dev"], body)


@pytest.mark.parametrize("body", ["[]", "null", "5"])
@pytest.mark.parametrize("args", [["add", "code.exe", "dev"], ["remove", "code.exe"], ["list"]])
def test_top_level_not_a_dict_is_refused(tmp_path, capsys, body, args):
    # shifts.json の最上位が辞書でなくても落ちず，形がおかしいと言って書き換えない
    _refused(tmp_path, args, body)
    assert "apps の形がおかしい" in capsys.readouterr().out


def test_add_replaces_hand_written_keys(tmp_path, capsys):
    # 手で大文字・.exe 無しで書いたキーが残って二重にならない（付け替えとして扱う）
    p = _cfg(tmp_path, '{"lang": "ja", "apps": {"registered": {"Code.EXE": "dev", "CODE": "dev", "photoshop.exe": "adobe"}}}')
    assert main(["app", "add", "code", "adobe", "--config", str(p)]) == 0
    assert _read(p)["apps"]["registered"] == {"photoshop.exe": "adobe", "code.exe": "adobe"}
    assert capsys.readouterr().out.strip() == "code.exe を dev から adobe に付け替えたよ"


def test_remove(tmp_path, capsys):
    p = _cfg(tmp_path, '{"apps": {"registered": {"Code.EXE": "dev", "photoshop.exe": "adobe"}}}')
    assert main(["app", "remove", "code", "--config", str(p)]) == 0
    assert _read(p) == {"apps": {"registered": {"photoshop.exe": "adobe"}}}
    assert "code.exe" in capsys.readouterr().out


def test_remove_missing_returns_1(tmp_path):
    _refused(tmp_path, ["remove", "node.exe"], '{"apps": {"registered": {"code.exe": "dev"}}}')


def test_list_groups_by_category(tmp_path, capsys):
    p = _cfg(tmp_path, '{"apps": {"registered": {"node.exe": "dev", "photoshop.exe": "adobe", "code.exe": "dev"}}}')
    assert main(["app", "list", "--config", str(p)]) == 0
    assert capsys.readouterr().out == "dev: code.exe, node.exe\nadobe: photoshop.exe\n"


def test_list_empty(tmp_path, capsys):
    p = _cfg(tmp_path)
    assert main(["app", "list", "--config", str(p)]) == 0
    assert "登録は無いよ" in capsys.readouterr().out


@pytest.mark.parametrize("args", [[], ["add", "code"], ["remove"]])
def test_missing_args_show_usage(tmp_path, capsys, args):
    p = _cfg(tmp_path)
    assert main(["app", *args, "--config", str(p)]) == 1
    assert "shl app add" in capsys.readouterr().out


@pytest.mark.parametrize("lang", ["ja", "ja_kansai"])
def test_help_mentions_app(lang):
    M.set_lang(lang)
    try:
        assert "shl app" in M.CLI_HELP
    finally:
        M.set_lang(None)


@pytest.mark.parametrize("raw,want", [("Code", "code.exe"), (" Code.EXE ", "code.exe"), ("", None), ("a/b", None), ("C:x", None)])
def test_normalize_exe(raw, want):
    assert normalize_exe(raw) == want
