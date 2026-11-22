"""パッケージ構成の整合（shiftlock.py を shiftlock/ に分割したときの決まりが守られているか）。
機能のテストは test_shiftlock.py。ここは「構成」と「i18n の決まり」だけ。"""
from __future__ import annotations

import ast
import builtins
import importlib
import json
import os
import shutil
import string
import subprocess
import symtable
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
import shiftlock as sl
import shiftlock.i18n as M
import shiftlock.i18n.ja as JA

PKG = ROOT / "shiftlock"
MODULES = sorted(p.stem for p in PKG.glob("*.py") if p.stem != "__init__")


def _src(m: str) -> str:
    return (PKG / f"{m}.py").read_text(encoding="utf-8")


def _tree(m: str) -> ast.Module:
    return ast.parse(_src(m))


def _defined_names(m: str) -> list[str]:
    """そのファイルの top-level で定義している名前（他のモジュールから import しただけの名前は含めない）"""
    names = []
    for node in _tree(m).body:
        if isinstance(node, (ast.FunctionDef, ast.ClassDef)):
            names.append(node.name)
        elif isinstance(node, ast.Assign):
            names += [t.id for t in node.targets if isinstance(t, ast.Name)]
        elif isinstance(node, ast.AnnAssign) and isinstance(node.target, ast.Name):
            names.append(node.target.id)
    return [n for n in names if not n.startswith("_")]


def _env():
    t = Path(tempfile.mkdtemp())
    cfg = json.loads((ROOT / "shifts.example.json").read_text(encoding="utf-8"))
    (t / "shifts.json").write_text(json.dumps(cfg, ensure_ascii=False), encoding="utf-8")
    return t / "shifts.json"


def _run(args, **kw):
    env = {k: v for k, v in os.environ.items() if k != "PYTHONPATH"}
    return subprocess.run([sys.executable] + args, capture_output=True, text=True, env=env, **kw)


# ---------------------------------------------------------------- 構成

def test_all_public_names_reexported():
    """各モジュールの公開名（_ で始まらない関数・クラス・定数）は全部 shiftlock.__all__ から同じものが取れる。
    サブモジュールと同名の関数（install / settings_menu）があるので、モジュールは importlib で取る"""
    assert MODULES, "モジュールが見つからへん"
    assert len(sl.__all__) == len(set(sl.__all__))
    for m in MODULES:
        mod = importlib.import_module(f"shiftlock.{m}")
        for n in _defined_names(m):
            assert n in sl.__all__, f"{m}.{n} が shiftlock.__all__ に無い"
            assert getattr(sl, n) is getattr(mod, n), f"shiftlock.{n} が {m}.{n} と別物"
    for n in sl.__all__:
        assert hasattr(sl, n), f"__all__ の {n} が無い"


def test_all_global_names_resolve():
    """関数・クラスの中で参照するグローバル名が、そのモジュールか builtins に実在する
    （機械的な分割で残りやすい import 漏れを、通らへん分岐でも検出する）"""
    checked = 0
    for m in MODULES:
        mod = importlib.import_module(f"shiftlock.{m}")
        top = symtable.symtable(_src(m), f"{m}.py", "exec")

        def walk(table):
            nonlocal checked
            if table.get_type() != "module":
                for s in table.get_symbols():
                    if s.is_global() and s.is_referenced() and not hasattr(builtins, s.get_name()):
                        checked += 1
                        assert hasattr(mod, s.get_name()), f"{m}.py: {table.get_name()} が使う {s.get_name()} がモジュールに無い"
            for c in table.get_children():
                walk(c)
        walk(top)
    assert checked > 300


def test_no_import_cycles():
    """モジュール間の import グラフに循環が無い（AST で調べる。__init__ 経由の読み込みでは順番が固定されて分からへん）"""
    edges = {}
    for m in MODULES:
        deps = set()
        for node in ast.walk(_tree(m)):
            if isinstance(node, ast.ImportFrom) and node.level == 1:
                deps.update([node.module.split(".")[0]] if node.module else [a.name for a in node.names])
            elif isinstance(node, ast.ImportFrom) and node.module and node.module.startswith("shiftlock."):
                deps.add(node.module.split(".")[1])
            elif isinstance(node, ast.Import):
                deps.update(a.name.split(".")[1] for a in node.names if a.name.startswith("shiftlock."))
        edges[m] = deps - {m}
    assert {"hook", "tick", "install", "settings_menu"} <= edges["cli"]
    assert edges["timeutil"] == {"i18n"} and edges["store"] == set()
    color = {}

    def visit(m, path):
        color[m] = "gray"
        for n in sorted(edges.get(m, ())):
            if color.get(n) == "gray":
                raise AssertionError("循環 import: " + " -> ".join(path + [n]))
            if n not in color:
                visit(n, path + [n])
        color[m] = "black"

    for m in MODULES:
        if m not in color:
            visit(m, [m])


def test_each_module_imports_alone():
    """どのモジュールも別プロセスで import できる（構文エラーや import 時の例外が無い）"""
    for m in MODULES + ["i18n"]:
        r = _run(["-c", f"import shiftlock.{m}"], cwd=str(ROOT))
        assert r.returncode == 0, f"shiftlock.{m}: {r.stderr[-400:]}"


def test_entry_runs_with_safe_path():
    """python -P（sys.path[0] にスクリプトのフォルダを入れない）で起動しても、エントリが自分で sys.path を足すので動く"""
    if sys.version_info < (3, 11):
        return
    r = _run(["-P", str(ROOT / "shiftlock.py"), "help"], cwd=str(ROOT.parent))
    assert r.returncode == 0 and "shl status" in r.stdout, r.stderr[-400:]


def test_install_copies_package_and_removes_stale():
    """install() はパッケージを丸ごとコピーしてからエントリを置き、前回の残り（無くなったモジュール・__pycache__）は消す。
    入れ替え用の .new / .old も残さない"""
    cp = _env()
    hooks = cp.parent / "hooks"
    (hooks / "shiftlock" / "__pycache__").mkdir(parents=True)
    (hooks / "shiftlock" / "stale.py").write_text("x = 1\n", encoding="utf-8")
    (hooks / "shiftlock.old").mkdir()
    script = sl.install(cp, cp.parent / "settings.json", hooks)
    assert script == hooks / "shiftlock.py" and script.exists()
    assert (hooks / "shiftlock" / "cli.py").exists() and (hooks / "shiftlock" / "i18n" / "ja.py").exists()
    assert not (hooks / "shiftlock" / "stale.py").exists()
    assert not (hooks / "shiftlock" / "__pycache__").exists()
    assert not (hooks / "shiftlock.old").exists() and not (hooks / "shiftlock.new").exists()
    r = _run([str(script), "status", "--config", str(cp)])
    assert r.returncode == 0 and "フェーズ" in r.stdout


def test_install_from_hooks_dir_keeps_package():
    """hook のフォルダにコピーされたエントリから install-hook を実行しても（コピー元 == コピー先）自分を消さへん"""
    cp = _env()
    hooks = cp.parent / "hooks"
    script = sl.install(cp, cp.parent / "settings.json", hooks)
    r = _run([str(script), "install-hook", "--config", str(cp),
              "--settings", str(cp.parent / "settings.json"), "--hooks-dir", str(hooks)])
    assert r.returncode == 0, r.stderr
    assert script.exists() and (hooks / "shiftlock" / "cli.py").exists()
    d = json.loads((cp.parent / "settings.json").read_text(encoding="utf-8"))
    assert len(d["hooks"]["UserPromptSubmit"]) == 1


def test_entry_without_package_exits_1():
    """エントリだけあってパッケージが無い（deploy の途中など）→ hook は exit 1（プロンプトを消す exit 2 にはしない）"""
    t = Path(tempfile.mkdtemp())
    shutil.copy2(ROOT / "shiftlock.py", t / "shiftlock.py")
    r = _run([str(t / "shiftlock.py"), "hook", "--config", str(t / "shifts.json")],
             input='{"hook_event_name": "UserPromptSubmit", "prompt": "x"}', cwd=str(t))
    assert r.returncode == 1 and "shiftlock/" in r.stderr and r.stdout == ""


# ---------------------------------------------------------------- i18n の決まり

def _strings(v):
    if isinstance(v, str):
        yield v
    elif isinstance(v, dict):
        for x in v.values():
            yield from _strings(x)
    elif isinstance(v, (list, tuple)):
        for x in v:
            yield from _strings(x)


def _placeholders(template: str) -> set[str]:
    return {f for _, f, _, _ in string.Formatter().parse(template) if f is not None}


def _m_refs(m: str):
    """M.X の参照（Attribute ノード）と、その親ノード"""
    tree = _tree(m)
    parents = {c: p for p in ast.walk(tree) for c in ast.iter_child_nodes(p)}
    for node in ast.walk(tree):
        if isinstance(node, ast.Attribute) and isinstance(node.value, ast.Name) and node.value.id == "M":
            yield node, parents.get(node)


def test_i18n_templates_use_named_placeholders_only():
    """ja.py の文面は名前付き {name} だけ。位置引数 {} と書式指定 {x:g} は使わない（数や日付は呼ぶ側で文字列にする）"""
    n = 0
    for name in dir(JA):
        if name.startswith("_") or not name.isupper():
            continue
        for s in _strings(getattr(JA, name)):
            for _, fname, spec, conv in string.Formatter().parse(s):
                if fname is None:
                    continue
                n += 1
                assert fname.isidentifier(), f"{name}: 位置引数か変な名前 {fname!r}"
                assert not spec and not conv, f"{name}: 書式指定 {{{fname}:{spec}}} は呼ぶ側でやる"
    assert n > 100


def test_i18n_references_exist_and_templates_go_through_format():
    """M.X の参照は全部 ja.py に定義があり、プレースホルダ付きの文は必ず .format(...) で使う（{name} が素のまま出えへん）"""
    refs = 0
    for m in MODULES:
        for node, parent in _m_refs(m):
            refs += 1
            assert hasattr(M, node.attr), f"{m}.py:{node.lineno} M.{node.attr} が ja.py に無い"
            val = getattr(M, node.attr)
            if isinstance(val, str) and _placeholders(val):
                assert isinstance(parent, ast.Attribute) and parent.attr == "format", \
                    f"{m}.py:{node.lineno} M.{node.attr} はプレースホルダ付きなので .format で使う"
    assert refs > 300


def test_i18n_format_calls_match_templates():
    """M.X.format(...) に渡す名前の集合 = テンプレート X の {名前} の集合（余分も不足も無い。str.format は余分を黙って捨てるので）"""
    calls = 0
    for m in MODULES:
        for node in ast.walk(_tree(m)):
            if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute) and node.func.attr == "format"):
                continue
            tgt = node.func.value
            if not (isinstance(tgt, ast.Attribute) and isinstance(tgt.value, ast.Name) and tgt.value.id == "M"):
                continue
            calls += 1
            where = f"{m}.py:{node.lineno} M.{tgt.attr}"
            tmpl = getattr(M, tgt.attr)
            assert isinstance(tmpl, str), f"{where} が文字列やない"
            assert not node.args and all(k.arg for k in node.keywords), f"{where}: 位置引数か ** は使わへん"
            kws = {k.arg for k in node.keywords}
            assert _placeholders(tmpl) == kws, f"{where}: テンプレ {sorted(_placeholders(tmpl))} / 渡した {sorted(kws)}"
    assert calls > 100


def test_i18n_constants_are_all_used():
    """ja.py の定数は少なくとも 1 箇所から M.X で参照される（使われへん文が残らんように）"""
    used = {node.attr for m in MODULES for node, _ in _m_refs(m)}
    for name in dir(JA):
        if name.startswith("_") or not name.isupper():
            continue
        # COMMON_ は他の定数から参照される（= 定義側で使う）ので除く
        if name.startswith("COMMON_"):
            continue
        # APP_ はネイティブ版（native/）の画面の文。Python 側では使わず、tools/export_native.py が JSON に書き出す
        # （書き出しに入っていることは test_export_native.py が見張る）
        if name.startswith(("APP_", "AGENT_")):   # AGENT_ は常駐（native/shiftlock.Agent）の文。同じく書き出すだけ
            continue
        assert name in used, f"ja.{name} はどこからも使われてへん"
