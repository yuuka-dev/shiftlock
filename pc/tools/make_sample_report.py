"""見本レポート（pc/sample-report.png）のもとになる HTML を、架空のデータから作る。

実データは使わない。乱数の種を固定してあるので、何回作っても同じ内容になる。
レポートの列や見た目を変えたら、これで作り直して画像を差し替える。

使い方（WSL、リポジトリ直下の .venv）:
    cd pc && ../.venv/bin/python tools/make_sample_report.py <新しいフォルダ>
<新しいフォルダ> に架空の記録（shifts.json・log/・events.log・mood.csv など）と report.html ができる。
既に shifts.json があるフォルダには書かない（本番の実行フォルダを壊さないため）。

画像にする（Windows、Edge のヘッドレス）:
    msedge.exe --headless=new --hide-scrollbars --blink-settings=preferredColorScheme=1 --window-size=1300,1130
               --screenshot=<png> <report.html の file URI>
（preferredColorScheme=1 は明るい配色。付けないと Windows がダークモードのとき暗い配色で撮れる）

中身（2026-10-05〜11-01 の 28 日間、月・水・金が乗務）:
- 乗務の日は『行ってきます』『ただいま』を記録して、帰ってから寝る
- 睡眠は iPhone の記録（mood.csv の neru / okita）。片方だけの日・記録の無い日・昼寝も混ぜてある
- 10/17 は乗務明けに寝なかった日（覚醒長）、10/21 は当日欠勤、10/21〜10/22 は PC をほとんど触っていない日（低活動）
"""
from __future__ import annotations

import json
import random
import sys
from datetime import date as Date, datetime, timedelta
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
import shiftlock as sl

NOW = datetime(2026, 11, 1, 14, 0)        # レポートを作った時刻（架空）
DAYS = 28
SEED = 20261101
WORK_WEEKDAYS = ["mon", "wed", "fri"]
ABSENT = Date(2026, 10, 21)               # 当日欠勤（『今日休む』）
LOW_DAYS = {Date(2026, 10, 21), Date(2026, 10, 22)}   # PC をほとんど触っていない日
NO_SLEEP_AFTER_SHIFT = Date(2026, 10, 17)             # 乗務明けに寝なかった日
NAP = (datetime(2026, 10, 25, 15, 0), datetime(2026, 10, 25, 16, 20))
TICK = timedelta(minutes=5)
MOOD_MEMOS = {-2: ["しんどい"], -1: ["だるい", "ねむい"], 0: [""], 1: ["よく寝た", "調子いい"], 2: ["やる気ある"]}


def UPS(prompt: str) -> str:
    return json.dumps({"hook_event_name": "UserPromptSubmit", "prompt": prompt})


def at(d: Date, hm: str) -> datetime:
    return datetime.combine(d, datetime.strptime(hm, "%H:%M").time())


def in_any(t: datetime, spans) -> bool:
    return any(s <= t < e for s, e in spans)


def scenario(rng: random.Random):
    """架空の 4 週間。(外出の区間, 睡眠の区間, mood.csv の行, hook に打つ言葉) を返す。時刻は全部 5 分単位"""
    def jit(base: datetime, minutes: int) -> datetime:
        return base + TICK * rng.randint(-(minutes // 5), minutes // 5)

    away, sleeps, marks, hooks = [], [], [], []
    d = NOW.date() - timedelta(days=DAYS)
    while d <= NOW.date():
        nxt = d + timedelta(days=1)
        if d.weekday() in (0, 2, 4) and d != ABSENT:
            dep, home = jit(at(d, "18:25"), 10), jit(at(nxt, "06:40"), 20)
            away.append((dep, home))
            hooks += [(dep, "行ってきます"), (home, "ただいま")]
            if nxt != NO_SLEEP_AFTER_SHIFT:
                sleeps.append((jit(home + timedelta(minutes=70), 20), jit(at(nxt, "13:30"), 40)))
        elif d in LOW_DAYS:
            sleeps.append((jit(at(d, "21:30"), 30), jit(at(nxt, "11:30"), 30)))
        else:
            sleeps.append((jit(at(nxt, "01:15"), 45), jit(at(nxt, "09:15"), 45)))
        if d == ABSENT:
            hooks.append((at(d, "15:10"), "今日休む"))
        d = nxt
    sleeps.append(NAP)
    sleeps = sorted(s for s in sleeps if s[1] <= NOW)

    # 本人の記録（mood.csv）。寝た・起きたの片方だけの日、記録の無い日も混ぜる
    for n, o in sleeps:
        r = rng.random()
        if o.date() in LOW_DAYS or o.date() - timedelta(days=1) in LOW_DAYS:
            r = 0.99 if o.date() in LOW_DAYS else 0.85     # 低活動の日は記録なし、明けた日は起きた時刻だけ
        if r < 0.80 or (n, o) == NAP:
            marks += [(n, "neru", ""), (o, "okita", "")]
        elif r < 0.88:
            marks.append((o, "okita", ""))
        elif r < 0.94:
            marks.append((n, "neru", ""))
    d = NOW.date() - timedelta(days=DAYS - 1)
    while d <= NOW.date():
        if d in LOW_DAYS or rng.random() < 0.7:
            v = rng.choice([-2, -1]) if d in LOW_DAYS else rng.choice([-1, 0, 0, 0, 1, 1, 2])
            t = at(d, "14:30") + TICK * rng.randint(0, 36)
            while in_any(t, sleeps) or in_any(t, away):
                t += timedelta(hours=1)
            if t <= NOW:
                marks.append((t, str(v), rng.choice(MOOD_MEMOS[v]) if rng.random() < 0.5 else ""))
        d += timedelta(days=1)
    return away, sleeps, sorted(marks), sorted(h for h in hooks if h[0] <= NOW)


def write_ticks(store, rng: random.Random, away, sleeps) -> None:
    """5 分おきの PC の記録と、Claude Code にプロンプトを送った記録"""
    t = at(NOW.date() - timedelta(days=DAYS), "00:00")
    last, hour_on, game_on = t, {}, {}
    while t < NOW:
        t += TICK
        mid = t - TICK / 2
        d, h = mid.date(), mid.hour
        if (d, h) not in hour_on:
            hour_on[(d, h)] = rng.random() < 0.82
        if d not in game_on:
            game_on[d] = d.weekday() not in (0, 2, 4) and rng.random() < 0.55
        home_awake = not in_any(mid, sleeps) and not in_any(mid, away)
        if d in LOW_DAYS:
            on = home_awake and h == 16 and mid.minute < 30
        else:
            on = home_awake and hour_on[(d, h)]
        game = on and game_on[d] and h >= 21
        if on:
            last = t - timedelta(seconds=30)
            if not game and rng.random() < 0.18:
                sl.log_append(store, "claude", mid, {"k": "p"})
        sl.log_append(store, "pc", t, {"idle": int((t - last).total_seconds()), "game": int(game)})


def main(argv: list[str]) -> int:
    if len(argv) != 1:
        print(__doc__)
        return 1
    out = Path(argv[0])
    if (out / "shifts.json").exists():
        print(f"shifts.json があるフォルダには書かない: {out}")
        return 1
    out.mkdir(parents=True, exist_ok=True)
    cp, mood = out / "shifts.json", out / "mood.csv"
    cfg = json.loads((ROOT / "shifts.example.json").read_text(encoding="utf-8"))
    cfg.update(work_weekdays=WORK_WEEKDAYS, mood_file=str(mood))
    cp.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")
    (out / "days.json").write_text("{}", encoding="utf-8")

    rng = random.Random(SEED)
    away, sleeps, marks, hooks = scenario(rng)
    store = sl.Store(cp)
    write_ticks(store, rng, away, sleeps)

    def write_mood(until: datetime) -> None:   # その時点までに iPhone が書いた行だけ置く
        mood.write_text("".join(f"{t:%Y-%m-%d %H:%M},{v},{memo}\n" for t, v, memo in marks if t <= until), encoding="utf-8")
    for t, word in hooks:
        write_mood(t)
        sl.run_hook(cp, UPS(word), t)
    write_mood(NOW)

    cfg = sl.load_config(cp)
    data = sl.build_report(cfg, store, NOW, days=DAYS)
    (out / "report.html").write_text(sl.render_report(data), encoding="utf-8")
    s = data["summary"]
    print(f"{out / 'report.html'}")
    print({k: s[k] for k in ("planned", "worked", "unconfirmed", "yasumi", "sleep_days", "sleep_paired_days",
                             "sleep_median", "long_days", "low_days", "mood_days")})
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
