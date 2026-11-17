"""当日欠勤の目安。直近 N 乗務のうち、有給の印が無い休み（当日欠勤）が K 回以上なら注意する。
設定は shifts.json の absence.window_shifts（N、既定 24）/ absence.warn_at（K、既定 5。0 で注意なし）。
『今日休む』『休んだ』の返事（records / commands）、『状況』、レポート（report）が使う。"""
from __future__ import annotations

from datetime import datetime, date as Date

from . import i18n as M
from .plan import plans
from .store import first_log_date, Store


def _num(v, default: int, lo: int, hi: int) -> int:
    """設定値を整数に。無い・壊れている（文字列・inf・nan）・範囲外のときは既定（任意機能の設定ミスで『今日休む』を止めない）。
    範囲は設定メニューと同じ（window_shifts 1..365 / warn_at 0..100）。範囲外を黙って通すと窓や注意が実質無効になるため。"""
    try:
        n = int(float(v)) if v is not None else default
    except (TypeError, ValueError, OverflowError):
        return default
    return n if lo <= n <= hi else default


def absence_stats(cfg: dict, store: Store, now: datetime, upto: Date | None = None) -> dict:
    """upto（既定は今日）までの直近 window_shifts 乗務日について
    {window, limit, shifts, absent, paid, over, from, to} を返す。
    乗務日は曜日ルール＋例外日（plans）。記録開始日（最初の pc ログ）より前の乗務日は数えない（出た／休んだが分からへんので）。
    absent = status が yasumi で paid が無い日、paid = yasumi で paid がある日。over = limit > 0 かつ absent >= limit。"""
    a = cfg.get("absence", {}) if isinstance(cfg.get("absence"), dict) else {}
    window, limit = _num(a.get("window_shifts"), 24, 1, 365), _num(a.get("warn_at"), 5, 0, 100)
    upto = upto or now.date()
    since = first_log_date(store)
    status = store.shift_status()
    days: list[str] = []
    if window > 0:
        # 週 1 乗務でも窓が埋まるように、乗務日の 7 倍の日数をさかのぼる
        days = [p.day for p in plans(cfg, upto, back=window * 7 + 14, fwd=0)
                if since is None or Date.fromisoformat(p.day) >= since][-window:]
    ys = [d for d in days if status.get(d, {}).get("status") == "yasumi"]
    paid = [d for d in ys if status[d].get("paid")]
    absent = len(ys) - len(paid)
    return {"window": window, "limit": limit, "shifts": len(days), "absent": absent, "paid": len(paid),
            "over": limit > 0 and absent >= limit,
            "from": days[0] if days else None, "to": days[-1] if days else None}


def absence_note(cfg: dict, store: Store, now: datetime, upto: Date | None = None) -> str:
    """目安に達していれば注意の一文（『今日休む』『休んだ』『有給なし』の返事に添える）。達していなければ空文字。"""
    s = absence_stats(cfg, store, now, upto)
    return M.ABSENCE_OVER.format(n=s["absent"], shifts=s["shifts"], limit=s["limit"]) if s["over"] else ""
