"""睡眠の記録（本人が記録した『ねる』『おきた』の時刻）。レポートの睡眠はここから出す。

PC を触っていない時間からの推定はしない（寝込んで何日も PC を触らなかったあいだを「睡眠」にしてしまうため）。
記録が無い日は「無い」まま返す。催促もしない。思い出せた分だけ後から足せる（record_sleep）。

読むもの: events.log（hook／CLI が書いた行）と mood.csv（iPhone のショートカット）。
書くもの: 後から足す／消す行だけ（events.log。state.json は触らないので、乗務の状態・ロック・強制モードには影響しない）。"""
from __future__ import annotations

import re
import unicodedata
from datetime import datetime, timedelta

from . import i18n as M
from .keywords import RE_CLOCK, SLEEP_CLEAR
from .phone import read_phone_marks
from .store import _memo_time, Store
from .timeutil import fmt, fmt_d, fmt_dur, parse_day


SLEEP_PAIR_MAX_HOURS = 24   # 『ねる』からこの時間を超えた『おきた』は同じ睡眠にしない（間の記録が抜けているとみなす）


SLEEP_REDO_HOURS = 2        # この時間内の『ねる』の言い直し・『おきた』の重複は 1 回として扱う


_SPAN_END = re.compile(r"\d{1,2}:\d{2}-(\d{1,2}):(\d{2})$")   # okita 行のメモ『neru 07:10-11:40』の終わり（＝起きた時刻）


# undo 行（『今のなし』）が戻せる記録の古さ。『今のなし』が効くのは記録から 60 分（records.undo_fuzzy）、
# 聞き返しに答えて記録した場合は行の時刻が元の発言の時刻で最大 20 分古い（intents.take_pending）
_UNDO_WINDOW = timedelta(minutes=80)


def sleep_marks(cfg: dict, store: Store) -> list[tuple[datetime, str]]:
    """本人が記録した (時刻, 'neru'｜'okita') を時刻順に。同じ時刻・同じ種類は 1 つ。

    - mood.csv の neru / okita（乗務のない日のぶんも含めて全部）
    - events.log の sleep_at（時刻はメモの発言時刻）、neru / okita（乗務のない日の free-day、後から足した late は行の時刻）
    - 乗務日の okita 行はメモ『neru 07:10-11:40』『phone 07:10-11:40』の終わりの時刻が起きた時刻
      （行の時刻は記録・取り込みの時刻で、聞き返しや古い iPhone の行ではずれる）。メモに無ければ行の時刻
    - メモに時刻を持たない古い sleep_at 行: claude は行の時刻で代用。phone は使わない（取り込みの時刻しか無く、mood.csv が正）
    - 取り消し: 『今のなし』（undo 行。同じ乗務日の直前の同じ種類の記録を戻す。_UNDO_WINDOW より古ければ何も戻さない
      ＝記録されなかった発言への『今のなし』で前の記録を消さない。同じ分に iPhone の記録があればそれは残る）と、
      メモが clear の行（同じ時刻・同じ種類を消す。iPhone の分も）
    - clear したあとの入れ直し（late・Claude への発言）は有効。iPhone 由来の行（メモが phone）は clear のあとに
      取り込まれても戻さない（mood.csv に行が残っていて、取り込みのたびに書かれうるため）
    - 数えないもの: 自動の『起きた』（メモが auto。PC の無操作からの推定）"""
    marks: dict[tuple[datetime, str], list[str]] = {}   # マーク → 出所の列（'phone' / 'log'）。『今のなし』は log の 1 件だけ戻す
    for t, k, _ in read_phone_marks(cfg):
        if k in ("neru", "okita"):
            marks.setdefault((t, k), []).append("phone")
    path = store.p("events.log")
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError:
        lines = []
    undoable: list[tuple[datetime, str, str, tuple[datetime, str]]] = []   # 『今のなし』で戻せる記録（行の時刻, 乗務日, 種類, マーク）
    cleared: set[tuple[datetime, str]] = set()                            # clear で消した（時刻, 種類）
    for line in lines:
        parts = line.split("\t")
        if len(parts) < 3:
            continue
        day, kind, memo = parts[1], parts[2], (parts[3] if len(parts) >= 4 else "")
        try:
            t = datetime.fromisoformat(parts[0]).replace(second=0, microsecond=0)
        except ValueError:
            continue
        if kind == "undo":
            for w in memo.split(","):
                for i in range(len(undoable) - 1, -1, -1):
                    if undoable[i][1] == day and undoable[i][2] == w:
                        if timedelta(0) <= t - undoable[i][0] <= _UNDO_WINDOW:
                            mk = undoable.pop(i)[3]
                            srcs = marks.get(mk, [])
                            if "log" in srcs:
                                srcs.remove("log")          # 戻すのは記録した分だけ。同じ分の iPhone の記録（phone）は残す
                            if not srcs:
                                marks.pop(mk, None)
                        break     # 見るのは直前の 1 件だけ（古ければ何も戻さない）
            continue
        if kind == "sleep_at":
            mt = _memo_time(memo)
            if mt is not None:
                mark = (datetime.fromisoformat(mt).replace(second=0, microsecond=0), "neru")
            elif memo in ("clear", "phone"):   # 値を消しただけの行／時刻を持たない古い iPhone の行
                continue
            else:
                mark = (t, "neru")
        elif kind in ("neru", "okita"):
            if memo.startswith("clear"):
                marks.pop((t, kind), None)
                cleared.add((t, kind))
                continue
            if memo.startswith("auto"):
                continue
            m = _SPAN_END.search(memo) if kind == "okita" else None
            if m and int(m.group(1)) <= 23 and int(m.group(2)) <= 59:
                end = t.replace(hour=int(m.group(1)), minute=int(m.group(2)))
                mark = (end if end <= t else end - timedelta(days=1), kind)   # 記録が日をまたいだら前の日
            else:
                mark = (t, kind)
        else:
            continue
        if memo.startswith("phone"):
            if mark in cleared:
                continue
        else:
            cleared.discard(mark)
        marks.setdefault(mark, []).append("phone" if memo.startswith("phone") else "log")
        if not memo.startswith(("phone", "late")):   # iPhone の記録と後から足した記録は『今のなし』の対象外
            undoable.append((t, day, mark[1], mark))
    return sorted(marks)


def sleep_sessions(marks: list[tuple[datetime, str]]) -> list[dict]:
    """記録の列をひと続きの睡眠に組む。返すのは {"neru": 時刻｜None, "okita": 時刻｜None} の列（時刻順）。

    - 『ねる』のあと SLEEP_PAIR_MAX_HOURS 以内の最初の『おきた』と組む。超えていたら組まず、別々に残す
    - 片方しか無い記録は、もう片方を None にして残す（分からないものを埋めない）
    - 『ねる』の言い直し（SLEEP_REDO_HOURS 以内）は後のほうを採る。離れていれば前のほうは起きた時刻なしで残す
    - 『おきた』の重複（直前の『おきた』から SLEEP_REDO_HOURS 以内で、間に『ねる』が無い）は最初の 1 回だけ"""
    out: list[dict] = []
    open_at: datetime | None = None
    last_okita: datetime | None = None
    pair_max = timedelta(hours=SLEEP_PAIR_MAX_HOURS)
    redo = timedelta(hours=SLEEP_REDO_HOURS)
    for t, k in sorted(marks):
        if k == "neru":
            if open_at is not None and t - open_at > redo:
                out.append({"neru": open_at, "okita": None})
            open_at = t
        elif k == "okita":
            if open_at is not None and t - open_at <= pair_max:
                out.append({"neru": open_at, "okita": t})
                open_at, last_okita = None, t
                continue
            if open_at is not None:
                out.append({"neru": open_at, "okita": None})
                open_at = None
            elif last_okita is not None and t - last_okita <= redo:
                continue
            out.append({"neru": None, "okita": t})
            last_okita = t
    if open_at is not None:
        out.append({"neru": open_at, "okita": None})
    return out


def parse_clock(text: str) -> tuple[int, int] | None:
    """'23:30' '7:00' '7時' '7時30分' '22時半'（全角も可）→ (時, 分)。時刻の形でなければ None、形は合うが範囲外なら ValueError。"""
    m = RE_CLOCK.match(unicodedata.normalize("NFKC", text).strip())
    if not m:
        return None
    h = int(m.group(1))
    mi = int(m.group(2) or m.group(3) or 0) if not m.group(4) else 30
    if not (0 <= h <= 23 and 0 <= mi <= 59):
        raise ValueError(text)
    return h, mi


def record_sleep(cfg: dict, store: Store, now: datetime, kind: str, words: list[str], via: str = "shl") -> str:
    """後から足す睡眠の記録。kind: 'neru'（寝た）/ 'okita'（起きた）。words: ['23:30'] / ['11/7', '7時'] / [..., '取消']。

    - 日付なし → 今日のその時刻（まだ来ていなければ昨日）。日付あり → その日のその時刻
    - まだ来ていない時刻・events.log の保存期間より前は記録しない
    - 同じ時刻・同じ種類が既にあれば足さない。末尾に『取消』で、その時刻・種類の記録を消す（iPhone から入れたものも）
    - events.log に `時刻\\t-\\t種類\\tlate 出所`（消すときは `clear 出所`）を足すだけ。乗務の状態は変えない"""
    clear, clock, day_text = False, None, None
    for w in (x.strip() for x in words):
        if not w:
            continue
        if w in SLEEP_CLEAR:
            clear = True
            continue
        try:
            c = parse_clock(w)
        except ValueError:
            return M.SLEEP_USAGE
        if c is not None:
            if clock is not None:
                return M.SLEEP_USAGE
            clock = c
        elif day_text is None:
            day_text = w
        else:
            return M.SLEEP_USAGE
    if clock is None:
        return M.SLEEP_USAGE
    today = now.date()
    if day_text:
        try:
            d = parse_day(day_text, today, past=True)
        except ValueError as e:
            return M.SLEEP_BAD_DATE.format(err=e)
        when = datetime(d.year, d.month, d.day, *clock)
    else:
        when = datetime(today.year, today.month, today.day, *clock)
        if when > now:
            when -= timedelta(days=1)
    label, what = fmt_d(when), M.SLEEP_KIND[kind]
    if when > now:
        return M.SLEEP_FUTURE.format(when=label)
    keep = int(cfg["retention"]["events_keep_days"])
    if when.date() < today - timedelta(days=keep):
        return M.SLEEP_TOO_OLD.format(when=label, days=keep)
    has = (when, kind) in sleep_marks(cfg, store)
    if clear:
        if not has:
            return M.SLEEP_CLEAR_NONE.format(when=label, what=what)
        _append(store, when, kind, f"clear {via}")
        return M.SLEEP_CLEARED.format(when=label, what=what)
    if not has:
        _append(store, when, kind, f"late {via}")
    msg = M.SLEEP_RECORDED.format(when=label, what=what)
    pair = next((s for s in sleep_sessions(sleep_marks(cfg, store))
                 if s[kind] == when and s["neru"] and s["okita"]), None)
    if pair:
        msg += M.SLEEP_PAIR.format(start=fmt_d(pair["neru"]), end=fmt(pair["okita"]), dur=fmt_dur(pair["okita"] - pair["neru"]))
    return msg


def _append(store: Store, when: datetime, kind: str, memo: str) -> None:
    with open(store.p("events.log"), "a", encoding="utf-8", newline="\n") as f:   # Windows の Python でも LF
        f.write(f"{when.isoformat(timespec='minutes')}\t-\t{kind}\t{memo}\n")
