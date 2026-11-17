"""ネイティブ版（native/）が読むファイルを、Python 版から書き出す。

いま書き出すもの:
- native/shared/messages.<lang>.json  画面と記録の文面（pc/shiftlock/i18n/ の言語ファイルから。C# には文を直書きしない）
- docs/test-vectors/*.json            テストベクタ（合成の設定・状態・時刻を Python 版に通した結果。C# のテストが読んで一致を確かめる）

使い方（リポジトリ直下で。WSL でも Windows でも同じ結果になる）:
    python3 pc/tools/export_native.py           書き出す
    python3 pc/tools/export_native.py --check   コミット済みの生成物と違えば終了コード 1（何も書かない）

文面や Python 版の判断を足した・直したら書き出し直してコミットする（tests/test_export_native.py が古い生成物を見つける）。

形式:
- messages.<lang>.json は {キー: 文字列 または {キー: 文字列}}。キーは名前順
- プレースホルダは {name} だけ。{{ と }} は波かっこそのもの（Python の書式のエスケープ）で、直さずに残す。C# 側が解釈する
- テストベクタは {節: [{"name", "files", "input", "expect"}]}。files は実行フォルダに置くファイル（名前 → JSON の中身。
  true は空のファイル、文字列はそのままの中身）。日付は合成（2026-12-01 以降）だけを使う。実データの値を入れない
- UTF-8（BOM なし）・LF・インデント 1・日本語はエスケープしない
"""
from __future__ import annotations

import argparse
import copy
import json
import string
import sys
import tempfile
from datetime import date as Date, datetime, timedelta
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent   # pc/
REPO = ROOT.parent
sys.path.insert(0, str(ROOT))
import shiftlock as sl  # noqa: E402
from shiftlock import i18n  # noqa: E402

LANGS = ("ja", "ja_kansai")   # 書き出す言語（リポジトリで配る言語だけ。任意の言語は書き出さない）
SHARED = REPO / "native" / "shared"
VECTORS = REPO / "docs" / "test-vectors"

# 書き出すキー。ネイティブ版が使う文だけに絞る（設定メニューや CLI の説明は持っていかない）
EXACT = ("PREFIX", "INTENT_LABEL", "INTENT_LABEL_SEP", "DAYS_NOTE", "DAYS_EXCEPTION_MARK",
         # 見張り（shiftlock.Agent）が使う文
         "PHONE_LATE_PREFIX", "PHONE_OKITA", "RECORDS_YASUMI_PAID", "CONFIRM_HOME_HINT", "CONFIRM_MORE", "CONFIRM_USER",
         # 強制モードは発動と解除で使う文だけ（宣言・状態表示の文は持っていかない。C# に宣言の入口を作らない）
         "ENFORCE_LABEL", "ENFORCE_SUFFIX", "ENFORCE_SUFFIX_STOP", "ENFORCE_WARN", "ENFORCE_WARN_SOON", "ENFORCE_REWARN",
         "ENFORCE_RUN", "ENFORCE_RELEASED", "ENFORCE_RELEASE_ALREADY")
PREFIXES = ("TIME_DUR_", "RECORDS_KITAKU", "RECORDS_NERU", "RECORDS_OKITA", "RECORDS_SHUPPATSU", "RECORDS_UNDO_",
            "CONFIG_", "APP_",
            "PHASE_", "AUTOWAKE_", "RECORDS_LATE_", "RECORDS_PAID_", "ABSENCE_", "NOTIFY_", "TICK_", "CAL_")
SKIP_SUFFIX = "_CTX"   # Claude 向けの文（hook が使う）。ネイティブ版は使わない


def _wanted(key: str) -> bool:
    return (key in EXACT or key.startswith(PREFIXES)) and not key.endswith(SKIP_SUFFIX)


def _check_template(key: str, text) -> str:
    """文面が C# 側で解釈できる形か確かめる（文字列で、プレースホルダは {name} だけ）。"""
    if not isinstance(text, str):
        raise ValueError(f"{key}: 文字列ではない（{type(text).__name__}）")
    try:
        fields = list(string.Formatter().parse(text))
    except ValueError as e:
        raise ValueError(f"{key}: 波かっこが閉じていない（{e}）") from e
    for _, name, spec, conv in fields:
        if name is None:
            continue
        if not name.isidentifier():
            raise ValueError(f"{key}: 位置引数か変な名前 {{{name}}}")
        if spec or conv:
            raise ValueError(f"{key}: 書式指定・変換は使えない {{{name}}}")
    return text


def build_messages(lang: str) -> dict:
    """lang の文面のうちネイティブ版が使うものを {キー: 文字列 または {キー: 文字列}} で返す。選んでいた言語は元に戻す。"""
    if lang not in LANGS:
        raise ValueError(f"書き出せない言語: {lang!r}（{', '.join(LANGS)} のどれか）")
    before = i18n.LANG
    try:
        i18n.set_lang(lang)
        words = {k: v for k, v in vars(i18n).items() if k.isupper() and _wanted(k)}
    finally:
        i18n.set_lang(before)
    out = {}
    for key in sorted(words):
        val = words[key]
        if isinstance(val, dict):
            out[key] = {}
            for sub in val:
                if not isinstance(sub, str):
                    raise ValueError(f"{key}: 辞書のキーが文字列ではない（{sub!r}）")
                out[key][sub] = _check_template(f"{key}.{sub}", val[sub])
        else:
            out[key] = _check_template(key, val)
    return out


# ---- テストベクタ ----

# 合成データの土台。この設定で 2026-12-01（火）の乗務は
# 就寝準備 07:20 / 就寝目安 08:00 / 最低ライン 10:30 / 出発 16:00・16:30 / 出庫 17:00・17:30
BASE = {"lang": "ja", "work_weekdays": ["mon", "tue", "wed", "thu", "fri"],
        "start_target": "17:00", "start_latest": "17:30", "prep_minutes": 60,
        "sleep_target_hours": 8, "sleep_min_hours": 6, "lastcall_minutes": 40}


def _base(**over) -> dict:
    return {**BASE, **over}


def _iso(t: datetime) -> str:
    return t.isoformat(timespec="minutes")


DIR_MARK = "{DIR}"   # shifts.json の中でこの文字列は実行フォルダのパス（区切りは /）に置き換える。結果に出たパスは {DIR} に戻す


def _put_files(d: Path, files: dict) -> None:
    for name, body in files.items():
        (d / name).parent.mkdir(parents=True, exist_ok=True)   # "log/pc-2026-12.jsonl" のように / を含む名前
        if body is True:
            (d / name).write_bytes(b"")
        elif isinstance(body, str):
            (d / name).write_bytes(body.encode("utf-8"))
        else:
            (d / name).write_bytes(json.dumps(body, ensure_ascii=False).encode("utf-8"))


def _with_config(files: dict, fn):
    """一時フォルダに files を書き、設定を読んで fn(cfg, store) の結果を返す。選んでいた言語は元に戻す。"""
    before = i18n.LANG
    try:
        with tempfile.TemporaryDirectory() as t:
            _put_files(Path(t), files)
            path = Path(t) / "shifts.json"
            raw = path.read_bytes()
            if DIR_MARK.encode() in raw:
                path.write_bytes(raw.replace(DIR_MARK.encode(), Path(t).resolve().as_posix().encode("utf-8")))
            return fn(sl.load_config(path), sl.Store(path))
    finally:
        i18n.set_lang(before)


def _cases(rows, expect) -> list:
    """rows: (名前, files, input)。expect(cfg, store, input) の結果を付けてケースの並びにする。"""
    return [{"name": name, "files": files, "input": inp,
             "expect": _with_config(files, lambda cfg, store, inp=inp: expect(cfg, store, inp))}
            for name, files, inp in rows]


def _plan_dict(p) -> dict:
    return {"day": p.day, "start_target": _iso(p.start_target), "start_latest": _iso(p.start_latest),
            "depart_target": _iso(p.depart_target), "depart_latest": _iso(p.depart_latest),
            "soft_close": _iso(p.soft_close), "hard_close": _iso(p.hard_close), "lastcall": _iso(p.lastcall)}


def _workday_rows() -> list:
    b = {"shifts.json": _base()}
    cal_new = {"sources": {"A": {"off": {"2026-12-04": "休み"}, "work": {"2026-12-05": ""}},
                           "B": {"work": {"2026-12-04": "出勤"}, "off": {"2026-12-05": "休み"}}}}
    return [
        ("平日は乗務", b, {"day": "2026-12-01"}),
        ("土曜は休み", b, {"day": "2026-12-05"}),
        ("手動の休み（理由つき）", {**b, "days.json": {"off": {"2026-12-02": "用事"}}}, {"day": "2026-12-02"}),
        ("手動の出勤（土曜）", {**b, "days.json": {"work": {"2026-12-05": ""}}}, {"day": "2026-12-05"}),
        ("カレンダー旧形式の休み", {**b, "calendar.json": {"off": {"2026-12-03": "公休"}, "work": {}}}, {"day": "2026-12-03"}),
        ("カレンダー新形式: 同じ日は上のソース（休み）が勝つ", {**b, "calendar.json": cal_new}, {"day": "2026-12-04"}),
        ("カレンダー新形式: 同じ日は上のソース（出勤）が勝つ", {**b, "calendar.json": cal_new}, {"day": "2026-12-05"}),
        ("手動の出勤がカレンダーの休みに勝つ",
         {**b, "days.json": {"work": {"2026-12-03": ""}}, "calendar.json": {"off": {"2026-12-03": "公休"}}}, {"day": "2026-12-03"}),
        ("手動の休みがカレンダーの出勤に勝つ",
         {**b, "days.json": {"off": {"2026-12-05": ""}}, "calendar.json": {"work": {"2026-12-05": "出勤"}}}, {"day": "2026-12-05"}),
        ("旧形式 shifts の日付（文字列）", {"shifts.json": _base(shifts=["2026-12-05"])}, {"day": "2026-12-05"}),
        ("旧形式 shifts の日付（辞書）", {"shifts.json": _base(shifts=[{"date": "2026-12-06"}])}, {"day": "2026-12-06"}),
        ("手動の休みが旧形式 shifts に勝つ",
         {"shifts.json": _base(shifts=["2026-12-05"]), "days.json": {"off": {"2026-12-05": ""}}}, {"day": "2026-12-05"}),
        ("work_weekdays が空", {"shifts.json": _base(work_weekdays=[])}, {"day": "2026-12-01"}),
        ("days.json が壊れていれば無いものとして扱う", {**b, "days.json": "{"}, {"day": "2026-12-01"}),
    ]


def _config_error_cases() -> list:
    """(名前, shifts.json, 文を比べるか)。Python 版が i18n の文で断るものは文も比べる。
    Python の例外の文（英語）になるものは「断ること」だけ比べる（expect.error が null）。"""
    rows = [
        ("土台の設定は通る", _base(), True),
        ("知らない曜日", _base(work_weekdays=["mon", "holiday"]), True),
        ("知らない曜日が 2 つ", _base(work_weekdays=["holiday", "mon", "funday"]), True),
        ("知らない曜日（関西弁）", _base(lang="ja_kansai", work_weekdays=["holiday"]), True),
        ("usage が文字列", _base(usage="120"), True),
        ("enforce が null", _base(enforce=None), True),
        ("calendar.sources が辞書", _base(calendar={"sources": {}}), True),
        ("start_target が 25:99", _base(start_target="25:99"), False),
        ("start_latest の後ろに空白", _base(start_latest="17:30 "), False),
        ("notify.sound がリストでも通る", _base(notify={"sound": []}), True),
        ("数字の文字列と 0 埋めなしの時刻は通る", _base(prep_minutes="90", start_target="8:00", start_latest="9:5"), True),
        ("最上位が配列", ["mon"], False),
    ]
    out = []
    for name, shifts, exact in rows:
        files = {"shifts.json": shifts}
        try:
            _with_config(files, lambda cfg, store: None)
            expect = {"ok": True}
        except Exception as e:   # noqa: BLE001  Python 版が断る全部（ValueError・TypeError・AttributeError）
            expect = {"error": str(e) if exact else None}
        out.append({"name": name, "files": files, "input": {}, "expect": expect})
    return out


def _make_plan_rows() -> list:
    def ns(*rules):
        return {"shifts.json": _base(no_sleep=list(rules))}
    early = {"start_target": "08:00", "start_latest": "08:30"}
    return [
        ("土台の設定", {"shifts.json": _base()}, {"day": "2026-12-01"}),
        ("start_latest が start_target より早ければ揃える", {"shifts.json": _base(start_latest="16:00")}, {"day": "2026-12-01"}),
        ("days.json の times で上書き",
         {"shifts.json": _base(), "days.json": {"times": {"2026-12-01": {"start_target": "18:00", "start_latest": "19:00",
                                                                           "sleep_target_hours": 7.5}}}}, {"day": "2026-12-01"}),
        ("times の無い日は上書きしない",
         {"shifts.json": _base(), "days.json": {"times": {"2026-12-01": {"start_target": "18:00"}}}}, {"day": "2026-12-02"}),
        ("旧形式 shifts の辞書で上書き",
         {"shifts.json": _base(shifts=[{"date": "2026-12-01", "start_target": "15:00", "sleep_min_hours": "5"}])}, {"day": "2026-12-01"}),
        ("times が旧形式 shifts に勝つ",
         {"shifts.json": _base(shifts=[{"date": "2026-12-01", "start_target": "15:00", "start_latest": "15:30"}]),
          "days.json": {"times": {"2026-12-01": {"start_target": "16:00"}}}}, {"day": "2026-12-01"}),
        ("no_sleep が睡眠の途中に 1 つ（曜日）", ns({"weekday": "tue", "from": "10:00", "to": "11:00"}), {"day": "2026-12-01"}),
        ("no_sleep は曜日が違えば効かない", ns({"weekday": "tue", "from": "10:00", "to": "11:00"}), {"day": "2026-12-02"}),
        ("no_sleep が日をまたぐ",
         {"shifts.json": _base(**early, no_sleep=[{"weekday": "mon", "from": "23:00", "to": "02:00"}])}, {"day": "2026-12-08"}),
        ("no_sleep が 2 つ（日付と曜日の長い書き方）",
         ns({"date": "2026-12-01", "from": "12:00", "to": "13:00"}, {"weekday": "Tuesday", "from": "09:00", "to": "09:30"}),
         {"day": "2026-12-01"}),
        ("no_sleep が起きる時刻をまたぐ", ns({"weekday": "tue", "from": "15:30", "to": "17:00"}), {"day": "2026-12-01"}),
        ("no_sleep が起きる時刻ちょうどに終わる", ns({"weekday": "tue", "from": "15:00", "to": "16:00"}), {"day": "2026-12-01"}),
        ("no_sleep が重なる", ns({"weekday": "tue", "from": "09:00", "to": "11:00"}, {"date": "2026-12-01", "from": "10:00", "to": "12:00"}),
         {"day": "2026-12-01"}),
        ("数字の文字列と 0 埋めなしの時刻",
         {"shifts.json": _base(prep_minutes="90", start_target="8:00", start_latest="9:05", sleep_target_hours="4.5",
                               sleep_min_hours=3.0)}, {"day": "2026-12-08"}),
        ("lastcall_minutes が無ければ 40 分", {"shifts.json": {k: v for k, v in BASE.items() if k != "lastcall_minutes"}},
         {"day": "2026-12-01"}),
    ]


def _state(day: str, **rec) -> dict:
    return {"shifts": {day: rec}}


def _current_rows() -> list:
    b = {"shifts.json": _base()}
    d = "2026-12-01"
    okita = {**b, "state.json": _state(d, status="okita", at="2026-12-01T12:00")}
    rows = [(f"{hm} 記録なし", b, {"now": f"{d}T{hm}"})
            for hm in ("07:19", "07:20", "07:59", "08:00", "10:29", "10:30", "17:29", "17:30")]
    rows += [(f"{hm} 起きた", okita, {"now": f"{d}T{hm}"}) for hm in ("12:00", "15:44", "15:45", "16:14", "16:15", "17:29")]
    rows += [
        ("09:00 休み", {**b, "state.json": _state(d, status="yasumi", at="2026-12-01T06:00")}, {"now": f"{d}T09:00"}),
        ("16:20 出発済み", {**b, "state.json": _state(d, status="worked", at="2026-12-01T16:10")}, {"now": f"{d}T16:20"}),
        ("09:00 寝た時刻だけ（状態なし）", {**b, "state.json": _state(d, sleep_at="2026-12-01T08:10")}, {"now": f"{d}T09:00"}),
        ("土曜 12:00", b, {"now": "2026-12-05T12:00"}),
        ("state.json が壊れている", {**b, "state.json": "{"}, {"now": f"{d}T09:00"}),
    ]
    return rows


def _target_rows() -> list:
    b = {"shifts.json": _base()}
    return [(name, b, {"now": now}) for name, now in (
        ("月曜 17:29 → その日", "2026-12-07T17:29"), ("月曜 17:30 → 翌日", "2026-12-07T17:30"),
        ("火曜 17:29 → その日", "2026-12-08T17:29"), ("金曜 17:30 → なし（次は月曜で 24 時間より先）", "2026-12-11T17:30"),
        ("土曜 18:00 → なし", "2026-12-12T18:00"), ("日曜 17:30 → 月曜（ちょうど 24 時間）", "2026-12-13T17:30"),
        ("日曜 17:29 → なし", "2026-12-13T17:29"))]


def _expect_current(cfg, store, inp) -> dict:
    phase, p = sl.current(cfg, store.shift_status(), datetime.fromisoformat(inp["now"]))
    return {"phase": phase, "day": p.day if p else None}


def _expect_target(cfg, store, inp) -> dict:
    p = sl.target_plan(cfg, datetime.fromisoformat(inp["now"]))
    return {"day": p.day if p else None}


# ---- 記録（ネイティブ版の画面のボタン）----

APP_VIA = "app"   # events.log のメモに書く出所


def _event_lines(store) -> list:
    path = store.p("events.log")
    return path.read_text(encoding="utf-8").splitlines() if path.exists() else []


def _read_state(store) -> dict:
    path = store.p("state.json")
    return json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}


def app_record(kind: str, cfg: dict, store, now: datetime) -> dict:
    """ネイティブ版のボタン 1 つぶんを、Python 版の部品で組む。kind: neru / okita / shuppatsu / kitaku。
    Python 版の do_* を出所 "app" で呼ぶ。events.log に行が足されたら記録したとみなし、
    shuppatsu 以外は取り消し用の undo を apply_intents と同じ形で state.json に残す
    （prev は呼ぶ前の shifts[乗務日]。乗務が無ければ乗務日は "-"。shuppatsu は do_shuppatsu 自身が残す）。
    断ったとき（早すぎる出発・強制中の起きた）は undo に触らない。
    返り値 {"message": PREFIX を外した文, "recorded": bool}"""
    p = sl.target_plan(cfg, now)
    day = p.day if p else "-"
    prev = copy.deepcopy(_read_state(store).get("shifts", {}).get(day, {}))
    before = len(_event_lines(store))
    if kind == "neru":
        msg = sl.do_neru(cfg, store, now, p, via=APP_VIA)
    elif kind == "okita":
        msg = sl.do_okita(cfg, store, now, p)
    elif kind == "kitaku":
        msg = sl.do_kitaku(cfg, store, now, p, via=APP_VIA)
    elif kind == "shuppatsu":
        msg = sl.do_shuppatsu(cfg, store, now, p)
    else:
        raise ValueError(kind)
    recorded = len(_event_lines(store)) > before
    if recorded and kind != "shuppatsu":
        st = _read_state(store)
        st["undo"] = {"day": day, "prev": prev, "at": _iso(now), "what": [kind]}
        store.p("state.json").write_text(json.dumps(st, ensure_ascii=False), encoding="utf-8")
    return {"message": msg.replace(i18n.PREFIX, ""), "recorded": recorded}


def _expect_steps(cfg, store, inp) -> dict:
    results = []
    for step in inp["steps"]:
        now = datetime.fromisoformat(step["now"])
        if step["op"] == "undo":
            before = len(_event_lines(store))
            msg = sl.undo_fuzzy(store, now)
            results.append({"message": msg.replace(i18n.PREFIX, ""), "recorded": len(_event_lines(store)) > before})
        else:
            results.append(app_record(step["op"], cfg, store, now))
    return {"results": results, "state": _read_state(store) if store.p("state.json").exists() else None,
            "events": _event_lines(store)}


def _steps_rows() -> list:
    b = {"shifts.json": _base()}
    d = "2026-12-01"

    def at(hm, day=d):
        return f"{day}T{hm}"

    def steps(*pairs):
        return {"steps": [{"op": op, "now": now} for op, now in pairs]}

    no_gap = {"usage-pc.json": {"gaps": []}}
    enforce = {"consented_at": at("06:00"), "action": "lock"}
    old_undo = {"day": d, "prev": {}, "at": at("10:50"), "what": ["kitaku"]}
    gate = {**b, **no_gap, "state.json": {"shifts": {d: {"kitaku": at("07:00"), "enforce": enforce}}, "undo": old_undo}}
    return [
        ("寝る", b, steps(("neru", at("08:10")))),
        ("寝る → 起きた（寝た時間が出る）", b, steps(("neru", at("08:10")), ("okita", at("14:30")))),
        ("寝る → 起きた（最低ラインに届かない）", b, steps(("neru", at("10:00")), ("okita", at("14:00")))),
        ("寝る 2 回（後の時刻で上書き）", b, steps(("neru", at("08:10")), ("neru", at("08:40")))),
        ("起きた: PC の記録なし", b, steps(("okita", at("14:00")))),
        ("起きた: まとまった離席あり",
         {**b, "usage-pc.json": {"gaps": [[at("09:00:00"), at("13:00:00")]]}}, steps(("okita", at("14:00")))),
        ("起きた: 離席の記録が空（確かめる文が付く）", {**b, **no_gap}, steps(("okita", at("14:00")))),
        ("起きた: 離席が短い・古い", {**b, "usage-pc.json": {"gaps": [[at("11:00:00"), at("13:00:00")],
                                                                     [at("02:00:00"), at("09:30:00")]]}},
         steps(("okita", at("14:00")))),
        ("休みの乗務で 寝る → 起きた（休みのまま）",
         {**b, "state.json": _state(d, status="yasumi", at=at("06:00"), paid=True)},
         steps(("neru", at("08:10")), ("okita", at("14:30")))),
        ("出発済みの乗務で 起きた（寝た記録なし）",
         {**b, "state.json": _state(d, status="worked", at=at("16:10"))}, steps(("okita", at("16:40")))),
        ("強制中の 起きた は記録しない（前の undo が残る）", gate, steps(("okita", at("11:00")))),
        ("強制中でも STOP.txt があれば記録する", {**gate, "STOP.txt": True}, steps(("okita", at("11:00")))),
        ("強制中でも解除済みなら記録する",
         {**gate, "state.json": {"shifts": {d: {"enforce": {**enforce, "released_at": at("10:40")}}}}},
         steps(("okita", at("11:00")))),
        ("強制中でも最低ラインの前なら記録する", gate, steps(("okita", at("10:29")))),
        ("強制中でも寝た記録があれば記録する", gate, steps(("neru", at("08:10")), ("okita", at("14:30")))),
        ("行ってきます: 早すぎる", b, steps(("shuppatsu", at("13:59")))),
        ("行ってきます: 出発の 2 時間前から", b, steps(("shuppatsu", at("14:00")))),
        ("起きた → 行ってきます", b, steps(("okita", at("14:00")), ("shuppatsu", at("16:05")))),
        ("乗務の無い日の 行ってきます は断る", b, steps(("shuppatsu", at("18:00", "2026-12-05")))),
        ("乗務の無い日の 寝る・起きた・帰ってきた", b,
         steps(("neru", at("18:00", "2026-12-05")), ("okita", at("18:01", "2026-12-05")), ("kitaku", at("18:02", "2026-12-05")))),
        ("帰ってきた（次の乗務に付く）", b, steps(("kitaku", at("07:30", "2026-12-02")))),
        ("寝る → 取り消す", b, steps(("neru", at("08:10")), ("undo", at("08:20")))),
        ("寝る → 取り消す（ちょうど 60 分は戻せる）", b, steps(("neru", at("08:10")), ("undo", at("09:10")))),
        ("寝る → 取り消す（61 分は戻せない）", b, steps(("neru", at("08:10")), ("undo", at("09:11")))),
        ("起きた → 取り消す（前の記録に戻る）",
         {**b, "state.json": _state(d, sleep_at=at("08:10"))}, steps(("okita", at("14:30")), ("undo", at("14:31")))),
        ("取り消す → もう一度 取り消す", b, steps(("neru", at("08:10")), ("undo", at("08:20")), ("undo", at("08:21")))),
        ("取り消せる記録が無い", b, steps(("undo", at("08:20")))),
        ("強制の宣言がある乗務で 行ってきます → 取り消す（enforce は残る）",
         {**b, "state.json": {"shifts": {d: {"status": "okita", "at": at("14:00"), "enforce": enforce}}}},
         steps(("shuppatsu", at("16:00")), ("undo", at("16:05")))),
        ("乗務の無い日の 寝る → 取り消す（free-day の行は残る）", b,
         steps(("neru", at("18:00", "2026-12-05")), ("undo", at("18:05", "2026-12-05")))),
        ("知らないキーは残す",
         {**b, "state.json": {"pending": {"phrase": "x", "kind": "neru", "at": at("08:00")}, "confirm_since": "2026-12-01",
                              "shifts": {d: {"x": 1}, "2026-12-02": {"status": "yasumi", "at": at("07:00")}}}},
         steps(("neru", at("08:10")))),
    ]


# ---- 画面に出す値 ----

def app_countdown(phase: str, p, now: datetime):
    """画面の「〜まで あと …」の相手（文面 APP_COUNTDOWN のキー, 時刻）。出さないときは (None, None)。
    Python 版に同じ表示は無いので、ここが決まりの置き場所（C# の Snapshot が同じ結果を返すことをベクタで確かめる）。
    相手の時刻を過ぎている・ちょうどのときは出さない（負の残り時間を出さない）。"""
    if phase == "lastcall":
        key, to = "lastcall", p.soft_close
    elif phase == "soft":
        key, to = "soft", p.hard_close
    elif phase == "hard":
        key, to = "hard", p.depart_latest
    elif phase == "go":
        key, to = ("go", p.depart_target) if now < p.depart_target else ("go_late", p.depart_latest)
    elif phase == "go_last":
        key, to = "go_last", p.start_latest
    else:
        return None, None
    return (key, to) if to > now else (None, None)


def _expect_snapshot(cfg, store, inp) -> dict:
    now = datetime.fromisoformat(inp["now"])
    status = store.shift_status()
    phase, cur = sl.current(cfg, status, now)
    # 「次の乗務」の選び方は commands.status_text と同じ
    p = cur or sl.target_plan(cfg, now) or next((x for x in sl.plans(cfg, now.date(), back=0) if x.start_latest > now), None)
    key, to = app_countdown(phase, cur, now)
    rec = status.get(p.day, {}) if p else {}
    dd = cfg["_days"]
    schedule = []
    for i in range(14):
        d = now.date() + timedelta(days=i)
        ds = d.isoformat()
        work = sl.is_workday(cfg, d)
        why = dd["work"].get(ds) if work else dd["off"].get(ds)
        schedule.append({"date": ds, "work": work, "why": why or None, "exception": ds in dd["off"] or ds in dd["work"]})
    return {"phase": phase, "countdown_key": key, "countdown_to": _iso(to) if to else None,
            "next_day": p.day if p else None, "record_status": rec.get("status"),
            "sleep_at": rec.get("sleep_at"), "kitaku": rec.get("kitaku"), "schedule": schedule}


def _snapshot_rows() -> list:
    b = {"shifts.json": _base()}
    d = "2026-12-01"
    okita = {**b, "state.json": _state(d, status="okita", at=f"{d}T14:00")}
    rows = [(f"{hm} {name}", b, {"now": f"{d}T{hm}"}) for hm, name in (
        ("07:30", "就寝準備"), ("09:00", "就寝目安を過ぎた"), ("12:00", "最低ラインを過ぎた"),
        ("16:30", "出発の最終ちょうど（残り時間なし）"), ("17:00", "出発の最終を過ぎた（残り時間なし）"),
        ("18:00", "自由時間（次は翌日）"))]
    rows += [(f"{hm} 起きた: {name}", okita, {"now": f"{d}T{hm}"}) for hm, name in (
        ("15:00", "まだ自由時間"), ("15:50", "そろそろ出発"), ("16:05", "出発目安を過ぎた"), ("16:20", "出発の最終が近い"))]
    rows += [
        ("金曜 18:00（次は月曜で 24 時間より先）", b, {"now": "2026-12-04T18:00"}),
        ("work_weekdays が空（次の乗務なし）", {"shifts.json": _base(work_weekdays=[])}, {"now": f"{d}T09:00"}),
        ("寝た時刻と帰宅がある乗務",
         {**b, "state.json": _state(d, status="okita", at=f"{d}T14:30", sleep_at=f"{d}T08:10", kitaku=f"{d}T07:05")},
         {"now": f"{d}T15:00"}),
        ("休みにした乗務（次の乗務としては出る）",
         {**b, "state.json": _state(d, status="yasumi", at=f"{d}T06:00")}, {"now": f"{d}T09:00"}),
        ("例外日: 理由つきの休みと，理由なしの出勤（土曜）",
         {**b, "days.json": {"off": {"2026-12-03": "用事"}}, "calendar.json": {"sources": {"A": {"work": {"2026-12-05": ""}}}}},
         {"now": f"{d}T18:00"}),
    ]
    return rows


# ---- 見張り（tick）----
# input:  {"steps": [{"now", "idle", "game", "uptime"?, "dry_run"?}], "fetch"?: {URL: ics の中身 / null（失敗させる）},
#          "watch": [最後に比べるファイル]}
# expect: {"results": [run_tick の戻り値], "files": {watch の名前: JSON（*.json）/ 行の配列（それ以外）/ null（無い）}}
# 節は run_tick の段ごと。確認の段より前の節は confirm.mode を off にする（毎回全部の段を通るので、確認が state.json を書く）。
# 各節は自分の段が書くファイルだけを watch に入れる。

def _snapshot_file(store, name: str):
    path = store.p(name)
    if not path.exists():
        return None
    if name.endswith(".json"):
        return json.loads(path.read_text(encoding="utf-8"))
    return path.read_text(encoding="utf-8").splitlines()


def _scrub_dir(obj, store):
    """結果に出た実行フォルダのパスを {DIR} に戻す（一時フォルダの名前をベクタに残さない）。"""
    text = json.dumps(obj, ensure_ascii=False)
    for d in {store.dir.as_posix(), str(store.dir)}:
        text = text.replace(json.dumps(d, ensure_ascii=False)[1:-1], DIR_MARK)
    return json.loads(text)


def _expect_tick(cfg, store, inp) -> dict:
    from shiftlock import calendar_sync
    table = inp.get("fetch")

    def fetch(url: str) -> str:
        if table is None or table.get(url) is None:
            raise OSError("fetch failed")   # 取得しない設定のケースで呼ばれたら、同期失敗として結果に出る
        return table[url]

    real = calendar_sync._http_get
    calendar_sync._http_get = fetch
    try:
        results = [sl.run_tick(store.p("shifts.json"), step["idle"], bool(step["game"]), datetime.fromisoformat(step["now"]),
                               step.get("uptime"), None, bool(step.get("dry_run", False)))
                   for step in inp["steps"]]
    finally:
        calendar_sync._http_get = real
    return _scrub_dir({"results": results, "files": {name: _snapshot_file(store, name) for name in inp.get("watch", [])}},
                      store)


def _tick_steps(*rows, watch=(), fetch=None, uptime=7200) -> dict:
    """rows: (時刻, idle 秒, game 0/1) か (時刻, idle 秒, game, {上書き})。"""
    steps = []
    for row in rows:
        step = {"now": row[0], "idle": row[1], "game": row[2], "uptime": uptime}
        if len(row) > 3:
            step.update(row[3])
        steps.append({k: v for k, v in step.items() if v is not None})
    inp = {"steps": steps, "watch": list(watch)}
    if fetch is not None:
        inp["fetch"] = fetch
    return inp


NO_CONFIRM = {"confirm": {"mode": "off"}}
USAGE_WATCH = ("usage-pc.json", "log/pc-2026-12.jsonl")


def _tick_usage_rows() -> list:
    """稼働・離席の記録（usage-pc.json と log/pc-*.jsonl）。乗務の無い土日を使い、通知が出ない時刻にする。"""
    b = {"shifts.json": _base(**NO_CONFIRM)}
    d = "2026-12-05"   # 土曜

    def at(hm, day=d):
        return f"{day}T{hm}"

    def run(*rows):
        return _tick_steps(*rows, watch=USAGE_WATCH)

    away = {"events.log": "2026-12-04T16:05\t2026-12-04\tworked\tshuppatsu\n",
            "state.json": _state("2026-12-04", status="worked", at="2026-12-04T16:05")}
    many = [[f"2026-12-02T{h:02d}:00", f"2026-12-02T{h:02d}:40"] for h in range(20)]
    return [
        ("触っている 3 回", b, run((at("10:00"), 30, 0), (at("10:05"), 30, 0), (at("10:10"), 30, 0))),
        ("離席して 45 分後に戻る", b,
         run((at("10:00"), 30, 0), (at("10:25"), 1500, 0), (at("10:30"), 1800, 0), (at("10:45"), 30, 0))),
        ("30 分未満の離席は gaps に入らない", b, run((at("10:00"), 30, 0), (at("10:25"), 1500, 0), (at("10:30"), 20, 0))),
        ("31 分空いたら（スリープ）起点を切り直す", b, run((at("10:00"), 30, 0), (at("10:05"), 30, 0), (at("10:36"), 30, 0))),
        ("30 分ちょうどなら起点はそのまま", b, run((at("10:00"), 30, 0), (at("10:30"), 30, 0))),
        ("ゲーム中は 150 分の無操作でも触っている。180 分を超えたら離席", b,
         run((at("13:00"), 9000, 1), (at("13:05"), 10900, 1))),
        ("game_idle_minutes 0 は無制限", {"shifts.json": _base(usage={"game_idle_minutes": 0}, **NO_CONFIRM)},
         run((at("13:00"), 100000, 1))),
        ("game_idle_minutes が壊れていれば 180 分", {"shifts.json": _base(usage={"game_idle_minutes": "長め"}, **NO_CONFIRM)},
         run((at("13:00"), 10700, 1), (at("13:05"), 10900, 1))),
        ("idle_reset_minutes 5", {"shifts.json": _base(usage={"idle_reset_minutes": 5}, **NO_CONFIRM)},
         run((at("10:00"), 299, 0), (at("10:05"), 300, 0))),
        ("外出中（行ってきます の後）はゲームを見ない", {**b, **away}, run((at("02:00"), 3000, 1))),
        ("帰ってきた後はゲームを見る",
         {**b, **away, "events.log": away["events.log"] + "2026-12-05T01:30\t2026-12-07\tkitaku\tclaude 2026-12-05T01:30\n"},
         run((at("02:00"), 3000, 1))),
        ("ゲーム中に戻ったら離席の終わりは今", b, run((at("13:00"), 30, 0), (at("13:25"), 1500, 0), (at("14:10"), 1500, 1))),
        ("gaps は 20 件まで（21 件目で最初の 1 件が落ちる）",
         {**b, "usage-pc.json": {"gaps": many, "gap_start": "2026-12-05T09:00:00", "start": "2026-12-05T08:00:00",
                                 "last": "2026-12-05T08:55:00", "last_nag": "2026-12-05T08:30:00"}},
         run((at("10:00"), 30, 0))),
        ("idle が負なら 0 として扱う", b, run((at("10:00"), -5, 0))),
        ("idle の小数は切り捨てて書く", b, run((at("10:00"), 12.9, 0), (at("10:25"), 1500.7, 0))),
        ("月をまたぐとログのファイルが分かれる", b,
         _tick_steps(("2026-12-31T23:58", 30, 0), ("2027-01-01T00:03", 30, 0),
                     watch=USAGE_WATCH + ("log/pc-2027-01.jsonl",))),
    ]


NOTIFY_WATCH = ("notified.json", "usage-pc.json")


def _tick_notify_rows() -> list:
    """フェーズ通知の間引き（1 回だけ・30 分おき・10 分おき）と声かけ。乗務日は 2026-12-01（火）。"""
    def cfg(**over):
        return {"shifts.json": _base(**{**NO_CONFIRM, **over})}

    b = cfg()
    d = "2026-12-01"

    def at(hm, day=d):
        return f"{day}T{hm}"

    def run(*hms, idle=30, game=0, day=d):
        return _tick_steps(*[(at(hm, day), idle, game) for hm in hms], watch=NOTIFY_WATCH)

    def used(start, last, day=d):
        """start から触り続けている usage-pc.json（声かけまでの時間を縮める）。"""
        return {"usage-pc.json": {"gaps": [], "start": f"{day}T{start}:00", "last": f"{day}T{last}:00"}}

    okita = {"state.json": _state(d, status="okita", at=at("14:00"))}
    sat = "2026-12-05"
    return [
        ("lastcall は 1 回だけ", b, run("07:20", "07:25", "07:30")),
        ("soft は 30 分おき", b, run("07:55", "08:00", "08:05", "08:30", "08:35")),
        ("hard は 10 分おき", b, run("10:30", "10:35", "10:40", "10:45", "10:50")),
        ("go と go_last は 1 回ずつ", {**b, **okita}, run("15:40", "15:45", "15:50", "16:15", "16:20")),
        ("離席中は出さない。戻ったら出す", b,
         _tick_steps((at("08:00"), 1500, 0), (at("08:05"), 30, 0), watch=NOTIFY_WATCH)),
        ("休みにした乗務には出さない", {**b, "state.json": _state(d, status="yasumi", at=at("06:00"))}, run("08:00", "10:30")),
        ("notify.hard を toast に", cfg(notify={"hard": "toast"}), run("10:30")),
        ("notify.phase を popup に・読み上げあり", cfg(notify={"phase": "popup", "speak": True}), run("07:20", "10:30")),
        ("文に入る時間の表記（7.5 と 5.0）", cfg(sleep_target_hours=7.5, sleep_min_hours=5.0), run("08:30", "11:30")),
        ("声かけは連続 120 分で 1 回、その 30 分後にもう 1 回", {**b, **used("10:00", "11:50", sat)},
         run("11:55", "12:00", "12:05", "12:30", "12:35", day=sat)),
        ("離席から戻ると声かけは数え直し", {**b, **used("10:00", "11:50", sat)},
         _tick_steps((at("11:55", sat), 30, 0), (at("12:20", sat), 1500, 0), (at("12:25", sat), 20, 0), watch=NOTIFY_WATCH)),
        ("ゲーム中の声かけ", {**b, **used("10:00", "11:55", sat)}, run("12:00", game=1, day=sat)),
        ("声かけの間隔を変える（10 分から 5 分おき）", cfg(usage={"nag_after_minutes": 10, "nag_every_minutes": 5}),
         run("10:00", "10:05", "10:10", "10:15", day=sat)),
        ("notify.nag を popup に", {**cfg(notify={"nag": "popup"}), **used("10:00", "11:55", sat)}, run("12:00", day=sat)),
        ("同じ回にフェーズ通知と声かけ", {**b, **used("06:00", "07:55")}, run("08:00")),
        ("関西弁", {**cfg(lang="ja_kansai"), **used("05:20", "07:15")}, run("07:20", "08:00", "10:30")),
        ("関西弁: 出発", {**cfg(lang="ja_kansai"), **okita}, run("15:45", "16:15")),
    ]


def _tick_sound_rows() -> list:
    """通知音の解決（種類 → default → なし）と音量・最長秒数。"""
    d = "2026-12-01"

    def cfg(**notify):
        return {"shifts.json": _base(notify=notify, **NO_CONFIRM)}

    def run(*hms):
        return _tick_steps(*[(f"{d}T{hm}", 30, 0) for hm in hms], watch=("notified.json",))

    two = {"usage-pc.json": {"gaps": [], "start": f"{d}T06:00:00", "last": f"{d}T07:55:00"}}   # 08:00 に soft と声かけが重なる
    return [
        ("種類ごとの音", {**cfg(sound={"hard": DIR_MARK + "/hard.wav"}), "hard.wav": True}, run("07:20", "10:30")),
        ("default だけ", {**cfg(sound={"default": DIR_MARK + "/d.wav"}), "d.wav": True}, run("07:20")),
        ("種類の音が default より先", {**cfg(sound={"default": DIR_MARK + "/d.wav", "lastcall": DIR_MARK + "/l.wav"}),
                                "d.wav": True, "l.wav": True}, run("07:20")),
        ("設定なしは空", cfg(), run("07:20")),
        ("無いファイルの警告は 1 日 1 回", cfg(sound={"hard": DIR_MARK + "/none.wav"}), run("10:30", "10:40")),
        ("同じ回に 2 件あれば優先の高い 1 件だけ",
         {**cfg(sound={"soft": DIR_MARK + "/s.wav", "nag": DIR_MARK + "/n.wav"}), **two, "s.wav": True, "n.wav": True},
         run("08:00")),
        ("優先の高いほうのファイルが無ければ次の 1 件",
         {**cfg(sound={"soft": DIR_MARK + "/none.wav", "nag": DIR_MARK + "/n.wav"}), **two, "n.wav": True}, run("08:00")),
        ("ドライブでも / でも始まらないパスは確かめずに渡す", cfg(sound={"hard": "  chime.wav "}), run("10:30")),
        ("音量と最長秒数", cfg(sound_volume=0.5, sound_max_seconds=7), run("07:20")),
        ("数字の文字列と小数の秒数", cfg(sound_volume="0.3", sound_max_seconds=7.9), run("07:20")),
        ("sound_volume が数字でなければ 0.8 と警告", cfg(sound_volume="大"), run("07:20")),
        ("sound_max_seconds が数字でなければ両方とも既定と警告", cfg(sound_volume=0.5, sound_max_seconds="7.5"), run("07:20")),
        ("sound_volume が null でも既定と警告", cfg(sound_volume=None), run("07:20")),
        ("sound がリストなら空として扱う", cfg(sound=["a.wav"]), run("07:20")),
    ]


STATE_WATCH = ("state.json", "events.log")


def _tick_autowake_rows() -> list:
    """自動「起きた」。乗務日は 2026-12-07（月。前日は休み）と 2026-12-02（水。前日も乗務）。
    離席は usage-pc.json に離席中（gap_start）を置き、戻った回の見張りで区間が確定する形で作る。"""
    def cfg(**over):
        return {"shifts.json": _base(**{**NO_CONFIRM, **over})}

    b = cfg()
    mon, wed, tue = "2026-12-07", "2026-12-02", "2026-12-01"

    def away(since: str) -> dict:
        """since（"日付T時:分"）から離席中の usage-pc.json。"""
        return {"usage-pc.json": {"gaps": [], "gap_start": since + ":00"}}

    def back(when: str, idle=30):
        """when に戻ってきた（触っている）1 回の見張り。"""
        return _tick_steps((when, idle, 0), watch=STATE_WATCH)

    neru = {"state.json": _state(mon, sleep_at=f"{mon}T08:10")}
    prompt = '{"t": "2026-12-07T11:00:00", "k": "p"}\n'
    return [
        ("離席だけ 6 時間（auto_wake の既定）", {**b, **away(f"{mon}T08:10")}, back(f"{mon}T14:30")),
        ("「ねる」あり＋離席", {**b, **neru, **away(f"{mon}T08:12")}, back(f"{mon}T14:30")),
        ("「ねる」だけ（PC は切っていた）", {**b, **neru}, back(f"{mon}T14:30")),
        ("auto_wake が neru なら離席だけでは何もしない", {**cfg(auto_wake="neru"), **away(f"{mon}T08:10")}, back(f"{mon}T14:30")),
        ("auto_wake が neru でも「ねる」があれば起きたにする", {**cfg(auto_wake="neru"), **neru, **away(f"{mon}T08:12")},
         back(f"{mon}T14:30")),
        ("auto_wake が false なら何もしない", {**cfg(auto_wake=False), **neru, **away(f"{mon}T08:12")}, back(f"{mon}T14:30")),
        ("5 時間 50 分では足りない", {**b, **away(f"{mon}T08:40")}, back(f"{mon}T14:30")),
        ("auto_wake_hours 3 なら 3 時間で成立", {**cfg(auto_wake_hours=3), **away(f"{mon}T11:20")}, back(f"{mon}T14:30")),
        ("no_sleep の時間帯で分断されて足りない",
         {**cfg(no_sleep=[{"weekday": "mon", "from": "11:00", "to": "12:00"}]), **away(f"{mon}T08:10")}, back(f"{mon}T14:30")),
        ("Claude Code のプロンプトで区切られて足りない",
         {**b, **away(f"{mon}T08:10"), "log/claude-2026-12.jsonl": prompt}, back(f"{mon}T14:30")),
        ("プロンプト以外の行（k が p でない）は区切らない",
         {**b, **away(f"{mon}T08:10"), "log/claude-2026-12.jsonl": prompt.replace('"p"', '"s"')}, back(f"{mon}T14:30")),
        ("離席中（触っていない）は判定しない", {**b, **away(f"{mon}T08:10")}, back(f"{mon}T14:30", idle=1500)),
        ("もう記録がある乗務は何もしない",
         {**b, **away(f"{mon}T08:10"), "state.json": _state(mon, status="yasumi", at=f"{mon}T06:00")}, back(f"{mon}T14:30")),
        ("乗務日の 0 時より前は何もしない", {**b, **away("2026-12-06T15:00")}, back("2026-12-06T23:30")),
        ("出発の最終を過ぎた分は数えない（足りない）", {**b, **away(f"{mon}T11:00")}, back(f"{mon}T17:00")),
        ("出発の最終までで足りる（そのまま最終出発の通知も出る）", {**b, **away(f"{mon}T10:20")}, back(f"{mon}T17:00")),
        ("前日が乗務: 出発目安〜home_by_latest の離席は数えない", {**b, **away(f"{tue}T17:00")}, back(f"{wed}T08:00")),
        ("前日が乗務: home_by_latest の後から数える", {**b, **away(f"{tue}T17:00")}, back(f"{wed}T15:30")),
        ("前日が乗務: 帰宅の記録があればそこから数える",
         {**b, **away(f"{tue}T17:00"), "state.json": _state(wed, kitaku=f"{wed}T07:05")}, back(f"{wed}T13:30")),
        ("前日が乗務: home_by_latest を変える", {**cfg(home_by_latest="07:30"), **away(f"{tue}T17:00")}, back(f"{wed}T13:40")),
        ("前日が乗務でも休みにしていれば数える",
         {**b, **away(f"{tue}T22:00"), "state.json": _state(tue, status="yasumi", at=f"{tue}T12:00")}, back(f"{wed}T06:00")),
        ("関西弁", {**cfg(lang="ja_kansai"), **away(f"{mon}T08:10")}, back(f"{mon}T14:30")),
    ]


def build_vectors() -> dict:
    """テストベクタの ファイル名 → 中身。合成の設定・状態・時刻を Python 版に通した結果。"""
    def day(inp):
        return Date.fromisoformat(inp["day"])
    return {
        "plan.json": {
            "workday": _cases(_workday_rows(), lambda cfg, store, inp: {"work": sl.is_workday(cfg, day(inp))}),
            "config_error": _config_error_cases(),
            "make_plan": _cases(_make_plan_rows(), lambda cfg, store, inp: _plan_dict(sl.make_plan(cfg, day(inp)))),
        },
        "phase.json": {
            "current": _cases(_current_rows(), _expect_current),
            "target_plan": _cases(_target_rows(), _expect_target),
        },
        "records.json": {
            "steps": _cases(_steps_rows(), _expect_steps),
        },
        "snapshot.json": {
            "build": _cases(_snapshot_rows(), _expect_snapshot),
        },
        "tick.json": {
            "usage": _cases(_tick_usage_rows(), _expect_tick),
            "notify": _cases(_tick_notify_rows(), _expect_tick),
            "sound": _cases(_tick_sound_rows(), _expect_tick),
            "autowake": _cases(_tick_autowake_rows(), _expect_tick),
        },
    }


def render(obj) -> str:
    """生成物の書き方（インデント 1・日本語はそのまま・末尾に改行）。"""
    return json.dumps(obj, ensure_ascii=False, indent=1) + "\n"


def outputs() -> dict:
    """書き出す全ファイルの パス → 中身。"""
    out = {SHARED / f"messages.{lang}.json": render(build_messages(lang)) for lang in LANGS}
    out.update({VECTORS / name: render(body) for name, body in build_vectors().items()})
    return out


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="ネイティブ版（native/）が読むファイルを Python 版から書き出す")
    ap.add_argument("--check", action="store_true", help="書き出さず、コミット済みの生成物と違えば終了コード 1")
    args = ap.parse_args(argv)
    stale = 0
    for path, text in outputs().items():
        data = text.encode("utf-8")
        same = path.is_file() and path.read_bytes() == data
        rel = path.relative_to(REPO) if REPO in path.parents else path
        if args.check:
            if not same:
                stale += 1
                print(f"古い: {rel}（python3 pc/tools/export_native.py を実行する）", file=sys.stderr)
        elif not same:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)   # 改行を OS に合わせて変えさせない（LF のまま）
            print(f"書いた: {rel}")
    return 1 if stale else 0


if __name__ == "__main__":
    sys.exit(main())
