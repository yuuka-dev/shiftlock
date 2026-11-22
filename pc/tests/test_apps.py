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


# ---- shl app limit（減速の対象を足す・外す）----
from datetime import datetime  # noqa: E402

from shiftlock import apps as A  # noqa: E402
from shiftlock.store import Store  # noqa: E402

LNOW = datetime(2026, 12, 7, 12, 0)


def _lcfg(tmp_path, body):
    return _cfg(tmp_path, json.dumps(body))


def test_limit_add_requires_registered(tmp_path):
    p = _lcfg(tmp_path, {"lang": "ja"})
    before = p.read_bytes()
    assert A.limit_add(p, "photoshop", True) == (1, M.APPS_LIMIT_NOT_REGISTERED.format(name="photoshop.exe"))
    assert p.read_bytes() == before


def test_limit_add_rejects_never_names(tmp_path):
    p = _lcfg(tmp_path, {"apps": {"registered": {"node.exe": "dev"}}})
    before = p.read_bytes()
    assert A.limit_add(p, "node.exe", True) == (1, M.APPS_LIMIT_NEVER.format(name="node.exe"))
    assert p.read_bytes() == before


def test_limit_add_never_uses_hand_written_list(tmp_path):
    # shifts.json に limit.never があればそれを使う（既定値ではなく）
    p = _lcfg(tmp_path, {"apps": {"registered": {"photoshop.exe": "adobe", "code.exe": "dev"}}, "limit": {"never": ["Photoshop"]}})
    assert A.limit_add(p, "photoshop.exe", True)[0] == 1
    assert A.limit_add(p, "code.exe", True)[0] == 0


def test_limit_add_needs_interactive(tmp_path):
    p = _lcfg(tmp_path, {"apps": {"registered": {"photoshop.exe": "adobe"}}})
    before = p.read_bytes()
    assert A.limit_add(p, "photoshop.exe", False) == (1, M.APPS_LIMIT_NEED_TTY)
    assert p.read_bytes() == before


def test_limit_add_writes_and_warns(tmp_path):
    p = _lcfg(tmp_path, {"apps": {"registered": {"photoshop.exe": "adobe"}}})
    code, out = A.limit_add(p, "Photoshop", True)
    assert code == 0 and _read(p)["limit"]["apps"] == ["photoshop.exe"]
    assert M.APPS_LIMIT_ANTICHEAT_WARN in out and M.APPS_LIMIT_PROTECT_NOTE in out and "{" not in out
    assert A.limit_add(p, "photoshop.exe", True)[0] == 0
    assert _read(p)["limit"]["apps"] == ["photoshop.exe"]


def test_limit_add_does_not_write_omitted_keys(tmp_path):
    p = _lcfg(tmp_path, {"apps": {"registered": {"photoshop.exe": "adobe"}}})
    A.limit_add(p, "photoshop.exe", True)
    assert list(_read(p)["limit"]) == ["apps"]


def test_limit_remove(tmp_path):
    p = _lcfg(tmp_path, {"limit": {"apps": ["Photoshop.EXE", "code.exe"]}})
    assert A.limit_remove(p, "photoshop")[0] == 0 and _read(p)["limit"]["apps"] == ["code.exe"]
    before = p.read_bytes()
    assert A.limit_remove(p, "photoshop") == (1, M.APPS_LIMIT_NOT_FOUND.format(name="photoshop.exe"))
    assert p.read_bytes() == before
    p2 = _lcfg(tmp_path, {"lang": "ja"})
    assert A.limit_remove(p2, "code")[0] == 1 and "limit" not in _read(p2)


def test_limit_exclude_include(tmp_path):
    p = _lcfg(tmp_path, {"lang": "ja"})
    assert A.limit_exclude(p, "EldenRing")[0] == 0
    assert A.limit_exclude(p, "eldenring.exe")[0] == 0
    assert _read(p)["limit"]["exclude"] == ["eldenring.exe"]
    before = p.read_bytes()
    assert A.limit_include(p, "eldenring.exe", False) == (1, M.APPS_LIMIT_NEED_TTY)
    assert p.read_bytes() == before
    assert A.limit_include(p, "apex.exe", True) == (1, M.APPS_LIMIT_NOT_EXCLUDED.format(name="apex.exe"))
    assert A.limit_include(p, "eldenring", True)[0] == 0 and _read(p)["limit"]["exclude"] == []


def test_limit_exclude_accepts_unknown_game(tmp_path):
    p = _lcfg(tmp_path, {"lang": "ja"})
    assert A.limit_exclude(p, "never-seen.exe")[0] == 0 and _read(p)["limit"]["exclude"] == ["never-seen.exe"]


def test_limit_list_shows_three_groups(tmp_path):
    p = _lcfg(tmp_path, {"limit": {"apps": ["photoshop.exe"], "exclude": ["apex.exe"]}})
    (tmp_path / "limit-games.json").write_text(json.dumps({
        "eldenring.exe": {"verdict": "target", "seen": "2026-12-06T20:00:00"},
        "apex.exe": {"verdict": "anticheat", "seen": "2026-12-06T20:00:00"},
        "odd.exe": {"verdict": "unknown", "seen": "2026-12-06T20:00:00"}}))
    code, out = A.limit_list(p, Store(p), LNOW)
    assert code == 0
    for x in ("photoshop.exe", "apex.exe", "eldenring.exe", "odd.exe", M.APPS_LIMIT_VERDICT_TARGET,
              M.APPS_LIMIT_VERDICT_ANTICHEAT, M.APPS_LIMIT_VERDICT_UNKNOWN):
        assert x in out, x
    assert "{" not in out


def test_limit_list_empty(tmp_path):
    p = _lcfg(tmp_path, {"lang": "ja"})
    code, out = A.limit_list(p, Store(p), LNOW)
    assert code == 0 and M.APPS_LIMIT_LIST_APPS_NONE in out and M.APPS_LIMIT_LIST_GAMES_NONE in out


@pytest.mark.parametrize("body", ['{"limit": []}', '{"limit": 5}', '{"limit": {"apps": null}}', '{"limit": {"exclude": "a"}}'])
def test_limit_bad_section(tmp_path, body):
    p = _cfg(tmp_path, body)
    before = p.read_bytes()
    assert A.limit_remove(p, "a.exe") == (1, M.APPS_BAD_SECTION)
    assert A.limit_exclude(p, "a.exe") == (1, M.APPS_BAD_SECTION)
    assert A.limit_list(p, Store(p), LNOW) == (1, M.APPS_BAD_SECTION)
    assert p.read_bytes() == before


@pytest.mark.parametrize("body", ["[]", "null"])
def test_limit_top_level_not_a_dict(tmp_path, body):
    p = _cfg(tmp_path, body)
    assert A.limit_add(p, "a.exe", True) == (1, M.APPS_BAD_SECTION)
    assert A.limit_exclude(p, "a.exe") == (1, M.APPS_BAD_SECTION)


def test_limit_bad_name(tmp_path):
    p = _lcfg(tmp_path, {})
    assert A.limit_exclude(p, r"C:\x.exe") == (1, M.APPS_BAD_NAME)
    assert A.limit_add(p, "", True) == (1, M.APPS_BAD_NAME)


def test_cli_limit_exclude_and_usage(tmp_path, capsys):
    p = _lcfg(tmp_path, {"lang": "ja"})
    assert main(["app", "limit", "exclude", "eldenring", "--config", str(p)]) == 0
    assert _read(p)["limit"]["exclude"] == ["eldenring.exe"]
    capsys.readouterr()
    assert main(["app", "limit", "list", "--config", str(p)]) == 0
    assert "eldenring.exe" in capsys.readouterr().out
    for args in (["limit"], ["limit", "exclude"], ["limit", "bogus", "x"]):
        assert main(["app", *args, "--config", str(p)]) == 1
        assert "shl app limit" in capsys.readouterr().out


def test_cli_limit_add_is_refused_without_tty(tmp_path, capsys, monkeypatch):
    # 非対話（stdin が端末でない）では，足す側は断る。ここでは直接 main を呼ぶが，端末でないので書かれない
    import io
    monkeypatch.setattr("sys.stdin", io.StringIO(""))
    p = _lcfg(tmp_path, {"apps": {"registered": {"photoshop.exe": "adobe"}}})
    before = p.read_bytes()
    assert main(["app", "limit", "add", "photoshop", "--config", str(p)]) == 1
    assert main(["app", "limit", "include", "photoshop", "--config", str(p)]) == 1
    assert p.read_bytes() == before


@pytest.mark.parametrize("lang", ["ja", "ja_kansai"])
def test_help_mentions_app_limit(lang):
    M.set_lang(lang)
    try:
        assert "shl app limit" in M.CLI_HELP
    finally:
        M.set_lang(None)
