"""強制モードの動作 throttle／suspend（アプリの減速・一時停止）の段階の計算．
Python（宣言時の表示）と C#（native/shiftlock.Core/LimitStages.cs．実行）で同じ結果になるよう，
docs/test-vectors/limit-stages.json で揃える．基盤の側に置く（plan・store を import しない）．"""
from __future__ import annotations

import json
from datetime import datetime, timedelta

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


_GAMES_FILE = "limit-games.json"
_GAME_VERDICTS = ("target", "anticheat", "unknown")
_GAMES_KEEP = timedelta(days=30)        # これより古い判定は捨てる
_AGENT_ALIVE = timedelta(minutes=15)    # app ログの最後の行がこれより古ければ Agent は動いていないとみなす


def read_games(store, now: datetime) -> dict[str, str]:
    """limit-games.json（Agent が書く．見たゲームの判定）を読む．exe 名 → "target"｜"anticheat"｜"unknown"．
    seen が 30 日より古い・形がおかしい項目は捨てる．ファイルが無い・壊れている → {}．store は p(名前) を持つもの（基盤なので import しない）．"""
    try:
        raw = json.loads(store.p(_GAMES_FILE).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}
    if not isinstance(raw, dict):
        return {}
    out = {}
    for exe, v in raw.items():
        if not isinstance(v, dict) or v.get("verdict") not in _GAME_VERDICTS or not isinstance(v.get("seen"), str):
            continue
        try:
            seen = datetime.fromisoformat(v["seen"])
        except ValueError:
            continue
        if seen.tzinfo is not None or now - seen > _GAMES_KEEP:
            continue
        out[exe] = v["verdict"]
    return out


def agent_seems_running(store, now: datetime) -> bool:
    """log/app-*.jsonl の最後の行の t が 15 分以内なら True．読めない・無い・未来の時刻 → False（宣言時の注意を出す側に倒す）．"""
    logdir = store.p("log")
    try:
        files = sorted(logdir.glob("app-????-??.jsonl"), reverse=True)
    except OSError:
        return False
    for f in files[:2]:   # 今月と先月（月の変わり目）
        try:
            lines = [x for x in f.read_text(encoding="utf-8").splitlines() if x.strip()]
        except OSError:
            continue
        if not lines:
            continue
        try:
            t = datetime.fromisoformat(json.loads(lines[-1])["t"])
            return timedelta(0) <= now - t <= _AGENT_ALIVE
        except (ValueError, KeyError, TypeError):
            return False
    return False
