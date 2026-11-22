"""強制モードの動作 throttle／suspend（アプリの減速・一時停止）の段階の計算．
Python（宣言時の表示）と C#（native/shiftlock.Core/LimitStages.cs．実行）で同じ結果になるよう，
docs/test-vectors/limit-stages.json で揃える．基盤の側に置く（plan・store を import しない）．"""
from __future__ import annotations

from datetime import datetime

LIMIT_ACTIONS = ("throttle", "suspend")


def check_steps(steps) -> list[int] | None:
    """割合の並び．4 個ちょうど・整数・1 以上 100 未満・大きい順（同じ値は不可）なら list，そうでなければ None．
    bool は整数と見ない．"""
    if not isinstance(steps, list) or len(steps) != 4:
        return None
    if any(type(x) is not int or not 1 <= x < 100 for x in steps):
        return None
    if any(a <= b for a, b in zip(steps, steps[1:])):
        return None
    return list(steps)


def limit_stages(soft: datetime, hard: datetime, steps: list[int], action: str) -> list[dict]:
    """段階 1〜5 の {"step", "at", "percent"}．D = (hard - soft) / 4．hard > soft なら 1〜4 が soft + (k-1)*D，5 が hard．
    hard <= soft なら 5 だけ．percent が None は一時停止．時刻は秒より下を切り捨てる．"""
    last = None if action == "suspend" else steps[3]
    out = []
    if hard > soft:
        span = hard - soft
        for k in range(4):
            out.append({"step": k + 1, "at": (soft + span * k // 4).replace(microsecond=0), "percent": steps[k]})
    out.append({"step": 5, "at": hard.replace(microsecond=0), "percent": last})
    return out
