"""実行フォルダのファイル（state.json / events.log / log/*.jsonl …）の読み書き。書き手ごとにファイルを分けて競合を避ける。"""
from __future__ import annotations

import json
import os
from datetime import datetime, date as Date
from pathlib import Path


def _read(path: Path, default):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (FileNotFoundError, json.JSONDecodeError):
        return default


def _write(path: Path, data) -> None:
    tmp = path.with_name(path.name + ".tmp")
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:   # Windows の Python でも LF（WSL と同じファイルを読み書きする）
        json.dump(data, f, ensure_ascii=False, indent=1)
    os.replace(tmp, path)


class Store:
    """書き手ごとにファイルを分けて競合を避ける。
    state.json        乗務ごとの 休み/起きた（hook と CLI）
    usage-claude.json Claude Code の連続稼働（hook）
    usage-pc.json     PC の連続稼働とアイドル空白（Windows tick）
    notified.json     Windows 通知の送信履歴（tick）
    events.log        休み/起きた の記録（自分用・主治医に見せる用）
    """

    def __init__(self, cfg_path: Path):
        self.dir = cfg_path.resolve().parent

    def p(self, name: str) -> Path:
        return self.dir / name

    def shift_status(self) -> dict:
        return _read(self.p("state.json"), {}).get("shifts", {})

    def set_shift_status(self, day: str, status: str | None, now: datetime, note: str = "") -> None:
        st = _read(self.p("state.json"), {})
        sh = st.setdefault("shifts", {})
        cur = sh.get(day, {})
        if status is None:
            cur.pop("status", None)
            cur.pop("at", None)
            cur.pop("paid", None)
        else:
            cur.update({"status": status, "at": now.isoformat(timespec="minutes")})
            if status != "yasumi":
                cur.pop("paid", None)   # 有給の印は休みの記録にだけ付く
        if cur:
            sh[day] = cur
        else:
            sh.pop(day, None)
        _write(self.p("state.json"), st)
        self.log_event(day, status or "undo", now, note)

    def log_event(self, day: str, kind: str, now: datetime, note: str = "") -> None:
        """events.log に 1 行足す（state.json は触らない）。乗務の状態を変えずに記録だけ残すとき用。"""
        with open(self.p("events.log"), "a", encoding="utf-8", newline="\n") as f:
            f.write(f"{now.isoformat(timespec='minutes')}\t{day}\t{kind}\t{note}\n")

    def set_paid(self, day: str, on: bool, now: datetime, note: str = "") -> None:
        """休みの記録に『有給』の印を付ける／外す（当日欠勤の目安に数えへんようにする）。
        events.log は種類 paid、メモ『on 出所』『off 出所』。"""
        st = _read(self.p("state.json"), {})
        sh = st.setdefault("shifts", {})
        cur = sh.setdefault(day, {})
        if on:
            cur["paid"] = True
        else:
            cur.pop("paid", None)
        if not cur:
            sh.pop(day, None)
        _write(self.p("state.json"), st)
        memo = f"{'on' if on else 'off'} {note}".rstrip()
        with open(self.p("events.log"), "a", encoding="utf-8", newline="\n") as f:
            f.write(f"{now.isoformat(timespec='minutes')}\t{day}\tpaid\t{memo}\n")


def set_mark(store: "Store", day: str, key: str, t: datetime | None, now: datetime, note: str = "") -> None:
    """乗務日ごとの自己申告（sleep_at=寝た時刻 / kitaku=帰宅時刻）。
    state には最後の値だけ残る。events.log のメモは『出所 時刻』（例 'phone 2026-11-04T10:07'）で、
    レポートはこの行を帰宅の履歴として読む（同じ乗務日に 2 回帰っても最初の帰宅が分かる）。"""
    st = _read(store.p("state.json"), {})
    cur = st.setdefault("shifts", {}).setdefault(day, {})
    when = t.isoformat(timespec="minutes") if t else "clear"
    if t is None:
        cur.pop(key, None)
    else:
        cur[key] = when
    _write(store.p("state.json"), st)
    memo = f"{note} {when}" if note and t else (note or when)
    with open(store.p("events.log"), "a", encoding="utf-8", newline="\n") as f:
        f.write(f"{now.isoformat(timespec='minutes')}\t{day}\t{key}\t{memo}\n")


def _memo_time(memo: str) -> str | None:
    """events.log のメモの末尾にある時刻（『claude 2026-11-04T10:07』や時刻だけの形）。無ければ None。"""
    toks = memo.split()
    tok = toks[-1] if toks else ""
    try:
        datetime.fromisoformat(tok)
    except ValueError:
        return None
    return tok


def departures(store: Store) -> tuple[set[tuple[str, str]], set[str]]:
    """events.log から『行ってきます』と『ただいま』の履歴。state は乗務日ごとに最後の 1 件しか持たないのでここから読む。
    返り値 (出発, 帰宅)。出発は {(乗務日, 時刻)}（worked 行でメモが shuppatsu のもの。state の at と突き合わせて使う）、
    帰宅は {時刻}（kitaku 行。メモ末尾の時刻、無い古い行は行の時刻。乗務のない日の free-day 行も含み、clear は除く）。"""
    shuppatsu: set[tuple[str, str]] = set()
    kitaku: set[str] = set()
    p = store.p("events.log")
    if not p.exists():
        return shuppatsu, kitaku
    for line in p.read_text(encoding="utf-8").splitlines():
        parts = line.split("\t")
        if len(parts) < 3:
            continue
        memo = parts[3] if len(parts) >= 4 else ""
        if parts[2] == "worked" and memo == "shuppatsu":
            shuppatsu.add((parts[1], parts[0]))
        elif parts[2] == "kitaku" and memo != "clear":
            kitaku.add(_memo_time(memo) or parts[0])
    return shuppatsu, kitaku


TICK_MIN = 5


def log_append(store: Store, kind: str, now: datetime, rec: dict) -> None:
    d = store.p("log")
    d.mkdir(exist_ok=True)
    with open(d / f"{kind}-{now:%Y-%m}.jsonl", "a", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps({"t": now.isoformat(timespec="seconds"), **rec}, ensure_ascii=False) + "\n")


def log_read(store: Store, kind: str, start: datetime, end: datetime) -> list[dict]:
    out, m = [], datetime(start.year, start.month, 1)
    while m <= end:
        f = store.p("log") / f"{kind}-{m:%Y-%m}.jsonl"
        if f.exists():
            for line in f.read_text(encoding="utf-8").splitlines():
                try:
                    r = json.loads(line)
                    r["_t"] = datetime.fromisoformat(r["t"])
                except (ValueError, KeyError, TypeError):   # TypeError: 行が辞書でない・t が文字列でない
                    continue
                if start <= r["_t"] < end:
                    out.append(r)
        m = datetime(m.year + (m.month == 12), m.month % 12 + 1, 1)
    return sorted(out, key=lambda r: r["_t"])


def first_log_date(store: Store) -> Date | None:
    """記録を始めた日（PC の見張りログの最初の行）。"""
    files = sorted(store.p("log").glob("pc-????-??.jsonl")) if store.p("log").is_dir() else []
    for f in files:
        for line in f.read_text(encoding="utf-8").splitlines():
            try:
                return datetime.fromisoformat(json.loads(line)["t"]).date()
            except (ValueError, KeyError):
                continue
    return None
