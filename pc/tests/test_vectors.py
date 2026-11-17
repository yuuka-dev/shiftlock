"""docs/test-vectors/*.json が今の Python 版と合っているか（古いままコミットされるのを防ぐ）。
ネイティブ版（native/shiftlock.Core）はこのベクタで Python 版との一致を確かめるので、ここがずれると意味が無くなる。
直し方: cd pc && ../.venv/bin/python tools/make_test_vectors.py"""
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "tools"))
import make_test_vectors as V  # noqa: E402


def test_vectors_are_fresh():
    for name, text in V.render().items():
        path = V.OUT / name
        assert path.exists(), f"{path} が無い。make_test_vectors.py で作る"
        assert path.read_text(encoding="utf-8") == text, f"{path} が古い。make_test_vectors.py で作り直す"


def test_plan_vectors_cover_every_phase():
    doc = json.loads(V.render()["plan-scenarios.json"])
    seen = {cur[0] for sc in doc["scenarios"] for m in sc["moments"] for cur in m["current"].values()}
    assert seen == {"free", "lastcall", "soft", "hard", "go", "go_last"}
    assert any(m["target"] is None for sc in doc["scenarios"] for m in sc["moments"])
    assert any(p["plan"] is None for sc in doc["scenarios"] for p in sc["plans"])
