"""訪看さん向けレポートの集計（本人が記録した睡眠・外出除外・締め期間）と reports/ の管理。"""
from __future__ import annotations

import math
import re
from datetime import datetime, timedelta, date as Date
from pathlib import Path

from .attendance import absence_stats
from .confirm import confirm_since
from .phone import read_mood
from .plan import no_sleep_intervals, Plan, plans
from .report_html import render_report
from .sleeplog import sleep_marks, sleep_sessions
from .store import departures, first_log_date, log_read, Store, TICK_MIN
from .timeutil import at, _clamp_day, _subtract, WD_JA
from .usage import game_idle_limit, pc_active


def _in_any(t: datetime, intervals) -> bool:
    return any(s <= t < e for s, e in intervals)


def _gaps_from_ticks(ticks: list[dict], away=(), game_idle: timedelta | None = None) -> list[tuple[datetime, datetime]]:
    """PC を触ってない区間（離席）。データ欠損（tick が来てない）で区間は打ち切る。
    away（乗務の外出区間）の中はゲームが起動していても idle だけで見る。
    game_idle: ゲーム中でも入力がこれだけ無ければ離席（None なら無制限）。tick と同じ判定（pc_active）。issue #24
    reset は tick 間隔（この 5 分枠に入力があったか）。tick の idle_reset_minutes は連続稼働の起点を切る閾値で役割が違い、
    区間の端は t − idle（最後の入力時刻）で遡るので、ここを 20 分にしても離席区間は変わらない。"""
    hole = timedelta(minutes=TICK_MIN * 3)
    reset = timedelta(seconds=TICK_MIN * 60)
    gaps, cur, prev = [], None, None
    for r in ticks:
        t, idle = r["_t"], timedelta(seconds=int(r.get("idle", 0)))
        game = bool(r.get("game")) and not _in_any(t, away)
        if prev is not None and t - prev > hole and cur is not None:
            gaps.append((cur, prev))
            cur = None
        if pc_active(idle, game, reset, game_idle):
            if cur is not None:
                end = t if game else t - idle
                gaps.append((cur, max(cur, end)))
                cur = None
        elif cur is None:
            cur = t - idle
        prev = t
    if cur is not None and prev is not None:
        gaps.append((cur, prev))
    return gaps


def closing_period(day: Date, closing) -> tuple[Date, Date]:
    """day を含む締め期間（前の締め日の翌日〜次の締め日）。20 日締めなら 10/21〜11/20。
    closing が 0 / None（締め日オフ）なら月単位（1 日〜月末）。"""
    closing = int(closing or 0) or 31
    this_close = _clamp_day(day.year, day.month, closing)
    if day <= this_close:
        py, pm = (day.year - 1, 12) if day.month == 1 else (day.year, day.month - 1)
        return _clamp_day(py, pm, closing) + timedelta(days=1), this_close
    ny, nm = (day.year + 1, 1) if day.month == 12 else (day.year, day.month + 1)
    return this_close + timedelta(days=1), _clamp_day(ny, nm, closing)


def _num(v) -> bool:
    """0 以上の有限の数か（bool は数と見ない。json.loads が通す Infinity・NaN も弾く）。"""
    if not isinstance(v, (int, float)) or isinstance(v, bool):
        return False
    try:
        return math.isfinite(v) and v >= 0
    except OverflowError:   # float にできないほど桁の多い整数
        return False


def _app_seconds(lines: list[dict], away) -> dict:
    """稼働計測の行（log/app-*.jsonl）を (日, 時) → {カテゴリ: 秒} にする。
    計測した区間の中央の時刻でマスを決める。外出中のマスは「計測あり」にするだけで秒は足さない。
    壊れた行（span・apps の型が違う）は丸ごと飛ばし，そのマスを「計測あり」にしない。壊れた項目（cat・act）はその項目だけ飛ばす。"""
    out: dict = {}
    for r in lines:
        span = r.get("span")
        if not _num(span) or span > 86400:   # 1 行は 5 分ぶん。1 日を超える行は壊れている
            continue
        apps = r.get("apps")
        if not isinstance(apps, dict):
            continue
        mid = r["_t"] - timedelta(seconds=span / 2)
        cell = out.setdefault((mid.date(), mid.hour), {"game": 0.0, "dev": 0.0, "adobe": 0.0})
        if _in_any(mid, away):
            continue
        for a in apps.values():
            if not isinstance(a, dict) or not isinstance(a.get("cat"), str) or a["cat"] not in cell:
                continue
            act = a.get("act")
            if _num(act) and act <= span:   # アクティブの秒は測れた秒を超えない
                cell[a["cat"]] += act
    return out


def build_report(cfg: dict, store: Store, now: datetime, days: int = 28,
                 period: tuple[Date, Date] | None = None) -> dict:
    period_end = None
    if period:
        start_day, period_end = period
        end_day = period_end   # 途中経過でも締め日までの行を出す（今日より後の日は「予定」か記録なし）
        days = (end_day - start_day).days + 1
    else:
        end_day = now.date()
        start_day = end_day - timedelta(days=days - 1)
    t0 = datetime.combine(start_day, datetime.min.time())
    t1 = datetime.combine(end_day + timedelta(days=1), datetime.min.time())
    ticks = log_read(store, "pc", t0 - timedelta(days=1), t1)
    claude = log_read(store, "claude", t0, t1)
    moods = [m for m in read_mood(cfg) if t0 <= m["t"] < t1]
    status = store.shift_status()
    shift_end = cfg.get("shift_end_earliest", "07:00")

    since = first_log_date(store)
    # 記録を始める前の日は「出た／休んだ」がわからんので乗務扱いにしない（勝手に出勤実績を作らない）
    plan_by_day = {p.day: p for p in plans(cfg, end_day, back=days + 2, fwd=0)
                   if since is not None and Date.fromisoformat(p.day) >= since}
    omw = bool(cfg.get("confirm", {}).get("okita_means_worked", True))
    csince = confirm_since(store, now).isoformat()
    yasumi_note: dict = {}   # (乗務日, 行の時刻) → yasumi 行のメモ。state の at と突き合わせる（あとの okita 行・『今のなし』に左右されへん）
    # 『行ってきます』の (乗務日, at) と『ただいま』の時刻の履歴。state の at と突き合わせる（取り消し・再記録に左右されへん）。
    # tick の shift_away と同じ読み方（store.departures）
    shuppatsu_at, kitaku_hist = departures(store)
    if store.p("events.log").exists():
        for line in store.p("events.log").read_text(encoding="utf-8").splitlines():
            parts = line.split("\t")
            if len(parts) < 3:
                continue
            memo = parts[3] if len(parts) >= 4 else ""
            if parts[2] == "yasumi":
                yasumi_note[(parts[1], parts[0])] = memo

    def shift_state(d: str, p: Plan) -> str:
        st_ = status.get(d, {}).get("status")
        if st_ == "yasumi":
            return "yasumi"
        if p.start_latest > now:
            return "planned"
        if st_ == "worked" or (st_ == "okita" and omw) or d < csince:
            return "worked"
        return "unconfirmed"   # 本人の確認が無い日（出た前提で外出を除外する。表示は「出?」）
    states = {d: shift_state(d, p) for d, p in plan_by_day.items()}
    worked = {d for d, s in states.items() if s in ("worked", "unconfirmed")}
    # 乗務で家にいない時間は離席から除外。出発は『行ってきます』の時刻（無ければ出発目安）、
    # 帰宅は『ただいま』の記録（出発後 24 時間以内の最初のもの。無ければ翌朝 shift_end_earliest）
    kitaku_hist |= {v["kitaku"] for v in status.values() if v.get("kitaku")}
    kitakus = sorted(datetime.fromisoformat(k) for k in kitaku_hist)
    away = []
    for d in worked:
        p = plan_by_day[d]
        st_ = status.get(d, {})
        dep = p.depart_target
        if st_.get("status") == "worked" and st_.get("at") and (d, st_["at"]) in shuppatsu_at:
            dep = datetime.fromisoformat(st_["at"])
        home = at(Date.fromisoformat(d) + timedelta(days=1), shift_end)
        for k in kitakus:
            if dep < k <= dep + timedelta(hours=24):
                home = k
                break
        away.append((dep, home))

    metered = _app_seconds(log_read(store, "app", t0 - timedelta(days=1), t1), away)
    game_idle = game_idle_limit(cfg)
    pieces = []
    for g in _gaps_from_ticks(ticks, away, game_idle):
        pieces += _subtract(g, away)
    # 睡眠は本人が記録した『ねる』『おきた』だけから出す（PC を触っていない時間からは推定しない）
    sessions = sleep_sessions([m for m in sleep_marks(cfg, store) if m[0] <= now])

    rows = []
    for i in range(days):
        d = start_day + timedelta(days=i)
        ds = d.isoformat()
        ds0 = datetime.combine(d, datetime.min.time())
        pc, game, cl, cov = [0] * 24, [0] * 24, [0] * 24, [False] * 24
        dev, adobe = [0] * 24, [0] * 24
        for r in ticks:
            mid = r["_t"] - timedelta(minutes=TICK_MIN / 2)
            if mid.date() != d:
                continue
            h = mid.hour
            cov[h] = True
            g = bool(r.get("game")) and not _in_any(r["_t"], away)   # 外出中は起動したままのゲームを見ない（離席区間と同じ判定）
            on = pc_active(timedelta(seconds=int(r.get("idle", 0))), g, timedelta(seconds=TICK_MIN * 60), game_idle)
            if on:
                pc[h] = min(60, pc[h] + TICK_MIN)
            if g and on:   # 起動したまま game_idle_minutes 触っていないゲームは PC にもゲームにも足さない
                game[h] = min(60, game[h] + TICK_MIN)
        for h in range(24):   # 稼働計測のあるマスは game もこちらの値にする（pc の game は使わない）
            m = metered.get((d, h))
            if m is not None:
                game[h], dev[h], adobe[h] = (min(60, int(m[k] + 30) // 60) for k in ("game", "dev", "adobe"))
        for r in claude:
            if r["_t"].date() == d and r.get("k") == "p":
                cl[r["_t"].hour] += 1
        mask = [False] * 24
        for s, e in away:
            for h in range(24):
                hs = ds0 + timedelta(hours=h)
                if s < hs + timedelta(hours=1) and e > hs:
                    mask[h] = True
        visit = [False] * 24
        for s, e in no_sleep_intervals(cfg, d, d):
            for h in range(24):
                hs = ds0 + timedelta(hours=h)
                if s < hs + timedelta(hours=1) and e > hs:
                    visit[h] = True

        # まとまった離席（PC の記録だけ。「覚醒長」の判定用で、睡眠としては表示しない）:
        # その日に終わった離席は全体の長さ、日をまたぐ／まだ続いている離席はその日に重なる長さで見る
        # （丸一日 PC を触っていない日を「離席なし＝寝ていない」と数えないため）
        day1 = ds0 + timedelta(days=1)
        gap_best = max([e - s for s, e in pieces if e.date() == d]
                       + [min(e, day1) - max(s, ds0) for s, e in pieces if s < day1 and e > ds0],
                       default=timedelta(0))
        shift = states.get(ds)
        slept = [s for s in sessions if s["neru"] and s["okita"] and s["okita"].date() == d]
        covered = sum(cov)
        pc_total = sum(pc)
        flags = []
        if covered >= 20 and gap_best < timedelta(hours=3):
            flags.append("long")      # 長時間覚醒の疑い
        if covered >= 20 and pc_total < 60 and shift not in ("worked", "unconfirmed"):
            flags.append("low")       # 低活動
        rows.append({
            "date": ds, "label": f"{d:%m/%d}({WD_JA[d.weekday()]})", "weekday": d.weekday(),
            "pc": pc, "game": game, "claude": cl, "cov": cov, "dev": dev, "adobe": adobe, "away": mask, "visit": visit,
            "shift": shift, "future": d > now.date(),
            # 寝た／起きた はその日に記録した時刻、睡眠はその日に起きた分の合計（寝た・起きたが組になった記録だけ）
            "neru": [f"{s['neru']:%H:%M}" for s in sessions if s["neru"] and s["neru"].date() == d],
            "okita": [f"{s['okita']:%H:%M}" for s in sessions if s["okita"] and s["okita"].date() == d],
            "sleep_min": sum(int((s["okita"] - s["neru"]).total_seconds() // 60) for s in slept) if slept else None,
            "sleep_span": " / ".join(f"{s['neru']:%H:%M}–{s['okita']:%H:%M}" for s in slept),
            "pc_total": pc_total, "game_total": sum(game), "dev_total": sum(dev), "adobe_total": sum(adobe),
            "claude_total": sum(cl),
            "covered_hours": covered, "flags": flags,
            "mood": [{"hm": f"{m['t']:%H:%M}", "v": m["v"], "memo": m["memo"]}
                     for m in moods if m["t"].date() == d],
        })

    sleeps = sorted(r["sleep_min"] for r in rows if r["sleep_min"] is not None)
    events = []
    ev = store.p("events.log")
    if ev.exists():
        for line in ev.read_text(encoding="utf-8").splitlines():
            parts = line.split("\t")
            if len(parts) >= 3 and parts[0][:10] >= start_day.isoformat():
                events.append(parts)
    planned_past = [r for r in rows if r["shift"] in ("worked", "unconfirmed", "yasumi")]
    yasumi_paid = [r for r in rows if r["shift"] == "yasumi" and status.get(r["date"], {}).get("paid")]
    return {
        "generated": now.strftime("%Y-%m-%d %H:%M"), "shift_end": shift_end,
        "since": since.isoformat() if since else None,
        "start": start_day.isoformat(), "end": end_day.isoformat(), "today": now.date().isoformat(),
        "period_end": period_end.isoformat() if period_end else None,
        "closed": bool(period_end) and period_end < now.date(),   # 締め日を過ぎた確定版か（途中経過なら False）
        "rows": rows,
        "summary": {
            "planned": len(planned_past),
            "worked": sum(r["shift"] in ("worked", "unconfirmed") for r in rows),   # 未確認も「出た」側に数える
            "unconfirmed": sum(r["shift"] == "unconfirmed" for r in rows),
            "yasumi": sum(r["shift"] == "yasumi" for r in rows),
            "yasumi_late": sum(1 for r in rows if r["shift"] == "yasumi"
                               and yasumi_note.get((r["date"], status.get(r["date"], {}).get("at", "")), "").startswith("late")),
            "absent": sum(r["shift"] == "yasumi" for r in rows) - len(yasumi_paid),   # 期間内の当日欠勤（有給の印が無い休み）
            "absent_paid": len(yasumi_paid),
            "absence": absence_stats(cfg, store, now, min(end_day, now.date())),      # 期間の終わり（途中経過なら今日）時点の目安
            "enforce_declare": sum(1 for e in events if len(e) >= 3 and e[2] == "enforce_declare"),
            "enforce_run": sum(1 for e in events if len(e) >= 3 and e[2] == "enforce_run"),
            "enforce_release": sum(1 for e in events if len(e) >= 3 and e[2] == "enforce_release"),
            "sleep_median": sleeps[len(sleeps) // 2] if sleeps else None,
            "sleep_days": sum(1 for r in rows if r["neru"] or r["okita"]),        # 寝た／起きたのどちらかを記録した日（タイルの「本人の記録がある日」）
            "sleep_paired_days": len(sleeps),                                     # 睡眠の長さが出た日（中央値の母数）
            "long_days": sum("long" in r["flags"] for r in rows),
            "low_days": sum("low" in r["flags"] for r in rows),
            "nogap_okita": sum(1 for e in events if len(e) >= 4 and e[3] == "no-gap"),
            "data_days": sum(r["covered_hours"] >= 20 for r in rows),
            "mood_days": sum(bool(r["mood"]) for r in rows),
            "mood_down": sum(any(m["v"] < 0 for m in r["mood"]) for r in rows),
            "mood_up": sum(any(m["v"] > 0 for m in r["mood"]) for r in rows),
        },
        "memos": [{"date": f"{m['t']:%m/%d}({WD_JA[m['t'].weekday()]}) {m['t']:%H:%M}", "v": m["v"], "memo": m["memo"]}
                  for m in moods if m["memo"]],
        "mood_enabled": bool(cfg.get("mood_file")),
    }


def report_name(period: tuple[Date, Date]) -> str:
    return f"report_{period[0]:%Y-%m-%d}_{period[1]:%Y-%m-%d}"


def write_period_reports(cfg: dict, store: Store, now: datetime) -> list[Path]:
    """今の締め期間（途中経過）と前の締め期間（確定版）を reports/ に書く。古いのは消す。"""
    closing = cfg["report"]["closing_day"]   # 0 / None は月単位（closing_period が丸める）
    cur = closing_period(now.date(), closing)
    prev = closing_period(cur[0] - timedelta(days=1), closing)
    d = store.p("reports")
    d.mkdir(exist_ok=True)
    out = []
    for per in (prev, cur):
        f = d / (report_name(per) + ".html")
        f.write_text(render_report(build_report(cfg, store, now, period=per)), encoding="utf-8")
        out.append(f)
    store.p("report.html").write_text(out[-1].read_text(encoding="utf-8"), encoding="utf-8")   # 最新＝今の期間
    cleanup_reports(cfg, store, now)
    return out


def cleanup_reports(cfg: dict, store: Store, now: datetime) -> int:
    """締め期間の終わりが keep_months か月より前のレポート（HTML/PDF）を消す。"""
    keep = int(cfg.get("report", {}).get("keep_months", 3))
    y, m = now.year, now.month - keep
    while m <= 0:
        y, m = y - 1, m + 12
    cutoff = _clamp_day(y, m, now.day).isoformat()
    d = store.p("reports")
    if not d.is_dir():
        return 0
    n = 0
    for f in d.iterdir():
        mt = re.match(r"report_(\d{4}-\d{2}-\d{2})_(\d{4}-\d{2}-\d{2})\.(?:html|pdf)$", f.name) or \
             re.match(r"report-()(\d{4}-\d{2}-\d{2})\.(?:html|pdf)$", f.name)
        if mt and mt.group(2) < cutoff:
            f.unlink()
            n += 1
    return n
