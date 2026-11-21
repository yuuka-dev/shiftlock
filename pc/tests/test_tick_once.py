"""pc/tools/tick_once.py（ネイティブ版と突き合わせるための、時刻を指定した 1 回だけの見張り）"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "tools"))

import export_native as ex  # noqa: E402
import tick_once  # noqa: E402
from shiftlock import i18n as M  # noqa: E402


def _first_notify_case():
    return ex.build_vectors()["tick.json"]["notify"][0]


def _args(d, step, *extra):
    return ["--dir", str(d), "--now", step["now"], "--idle", str(step["idle"]), "--game", str(step["game"]),
            "--uptime", str(step["uptime"]), *extra]


def test_same_result_as_the_vector(tmp_path, capsys):
    """tick.json の節 notify の最初のケースの 1 回目と同じ結果を出す（キーは名前順・インデント 1・日本語はそのまま）"""
    case = _first_notify_case()
    ex._put_files(tmp_path, case["files"])
    before = M.LANG
    try:
        assert tick_once.main(_args(tmp_path, case["input"]["steps"][0])) == 0
    finally:
        M.set_lang(before)
    out = capsys.readouterr().out
    assert json.loads(out) == case["expect"]["results"][0]
    assert out.startswith('{\n "actions": [\n  {\n   "kind": "lastcall",')
    assert "\\u" not in out and list(json.loads(out)) == ["actions", "sound", "warnings"]
    assert (tmp_path / "notified.json").exists()   # 渡したフォルダのファイルは書き換える


def test_refuses_the_production_folder(tmp_path, monkeypatch, capsys):
    """本番の実行フォルダ（%USERPROFILE%\\shift-lock）は断る（終了コード 2、何も書かない）"""
    case = _first_notify_case()
    prod = tmp_path / "shift-lock"
    prod.mkdir()
    ex._put_files(prod, case["files"])
    monkeypatch.setenv("USERPROFILE", str(tmp_path))
    assert tick_once.main(_args(prod, case["input"]["steps"][0])) == 2
    assert tick_once.main(_args(str(prod) + "/", case["input"]["steps"][0])) == 2
    assert capsys.readouterr().out == ""
    assert sorted(p.name for p in prod.iterdir()) == ["shifts.json"]


def test_production_folder_shapes():
    """WSL から見た本番のフォルダも断る。コピーは通す"""
    assert tick_once.is_production(Path("/mnt/c/Users/someone/shift-lock"))
    assert tick_once.is_production(Path("/mnt/C/Users/someone/shift-lock/"))
    assert tick_once.is_production(Path.home() / "shift-lock")
    assert not tick_once.is_production(Path("/mnt/c/Users/someone/shift-lock-copy"))
    assert not tick_once.is_production(Path("/mnt/c/Users/someone/work/shift-lock"))
    assert not tick_once.is_production(Path("/tmp/copy-py"))


def test_bad_arguments_are_refused(tmp_path, capsys):
    """--now が読めない・引数が足りないときは終了コード 2（例外で落ちない）"""
    case = _first_notify_case()
    ex._put_files(tmp_path, case["files"])
    step = dict(case["input"]["steps"][0], now="きのう")
    assert tick_once.main(_args(tmp_path, step)) == 2
    assert tick_once.main(["--dir", str(tmp_path)]) == 2
    assert capsys.readouterr().out == ""
    assert sorted(p.name for p in tmp_path.iterdir()) == ["shifts.json"]


def test_dry_run_is_passed(tmp_path, capsys):
    """--dry-run は強制モードの状態を進めない（run_tick の dry_run）"""
    case = next(c for c in ex.build_vectors()["tick.json"]["enforce"] if c["name"] == "dry_run の実行は記録しない")
    ex._put_files(tmp_path, case["files"])
    before = M.LANG
    try:
        for step in case["input"]["steps"]:
            extra = ["--dry-run"] if step.get("dry_run") else []
            assert tick_once.main(_args(tmp_path, step, *extra)) == 0
    finally:
        M.set_lang(before)
    outs = capsys.readouterr().out
    assert '"method": "enforce"' in outs
    assert not (tmp_path / "events.log").exists()
