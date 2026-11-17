"""Windows 通知の送信履歴（notified.json）と通知音の解決。"""
from __future__ import annotations

import re
from datetime import datetime, timedelta

from . import i18n as M
from .config import to_local_path
from .store import _read, _write


# 通知間隔（分）。None = フェーズごとに1回
NOTIFY_EVERY = {"lastcall": None, "soft": 30, "hard": 10, "go": None, "go_last": None}


# 同じ tick に複数の通知があるとき、音を鳴らすのは 1 つだけ（この順で強い）
SOUND_PRIORITY = ["enforce", "hard", "go_last", "confirm", "lastcall", "soft", "go", "auto_okita", "nag"]


def _once(store: "Store", key: str, now: datetime, every_minutes: int | None = None) -> bool:
    """notified.json の送信履歴。key は 'YYYY-MM-DD:...' で始める（3 日より古いキーは捨てる）。
    初回、または every_minutes 以上空いていれば True を返して記録する。"""
    sent = _read(store.p("notified.json"), {})
    last = sent.get(key)
    if last is not None and (every_minutes is None or now - datetime.fromisoformat(last) < timedelta(minutes=every_minutes)):
        return False
    sent[key] = now.isoformat(timespec="seconds")
    cutoff = (now - timedelta(days=3)).date().isoformat()
    sent = {k: v for k, v in sent.items() if k.split(":")[0] >= cutoff}
    _write(store.p("notified.json"), sent)
    return True


def resolve_sound(cfg: dict, store: "Store", kind: str, now: datetime, warnings: list) -> str:
    """通知の種類に合う音声ファイル（Windows のパス）。種類 → default → なし。無いファイルは 1 日 1 回だけ warnings に載せる。"""
    snd = cfg.get("notify", {}).get("sound")
    if not isinstance(snd, dict):
        return ""
    path = str(snd.get(kind) or snd.get("default") or "").strip()
    if not path:
        return ""
    if re.match(r"^([A-Za-z]:[\\/]|/)", path) and not to_local_path(path).exists():   # UNC などは guard.ps1 側で確認
        if _once(store, f"{now.date().isoformat()}:warn:sound:{path}", now):
            warnings.append(M.NOTIFY_SOUND_MISSING.format(path=path))
        return ""
    return path


def attach_sounds(cfg: dict, store: "Store", actions: list, now: datetime) -> list:
    """actions に sound を付ける。鳴らすのは優先度の高い 1 件だけ。戻り値は警告の一覧。"""
    warnings: list = []
    for a in actions:
        a["sound"] = ""
    for kind in SOUND_PRIORITY:
        for a in actions:
            if a.get("kind") == kind:
                a["sound"] = resolve_sound(cfg, store, kind, now, warnings)
                if a["sound"]:
                    return warnings
    return warnings
