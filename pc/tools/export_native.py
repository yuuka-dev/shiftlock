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

# 書き出すキー。ネイティブ版が使う文だけに絞る（設定メニューは持っていかない。CLI_・SHL_ は shl.exe が使う）
EXACT = ("PREFIX", "INTENT_LABEL", "INTENT_LABEL_SEP",
         # 見張り（shiftlock.Agent）が使う文
         "PHONE_LATE_PREFIX", "PHONE_OKITA", "RECORDS_YASUMI_PAID",
         # shl.exe の記録のコマンド（yasumi・deru・day）が使う文。CMD_ の残り（CMD_DAY_*・CMD_SCHEDULE）は hook 用で、まだ持っていかない
         "CMD_BAD_DATE", "CMD_NO_PLAN", "CMD_YASUMI", "CMD_YASUMI_PAID", "CMD_UNDO", "CMD_UNDO_WORKED", "CMD_UNDO_YASUMI",
         # 強制モードは発動と解除で使う文だけ（宣言・状態表示の文は持っていかない。C# に宣言の入口を作らない）
         "ENFORCE_LABEL", "ENFORCE_SUFFIX", "ENFORCE_SUFFIX_STOP", "ENFORCE_SUFFIX_LIMIT", "ENFORCE_WARN", "ENFORCE_WARN_SOON", "ENFORCE_REWARN",
         "ENFORCE_RUN", "ENFORCE_RELEASED", "ENFORCE_RELEASE_ALREADY")
PREFIXES = ("TIME_DUR_", "RECORDS_KITAKU", "RECORDS_NERU", "RECORDS_OKITA", "RECORDS_SHUPPATSU", "RECORDS_UNDO_",
            "CONFIG_", "APP_", "AGENT_", "CLI_", "SHL_", "STATUS_", "DAYS_", "CONFIRM_",
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
        ("limit が配列", _base(limit=[75, 50, 25, 10]), True),
        ("limit が null", _base(limit=None), True),
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


PHONE_WATCH = STATE_WATCH + ("days.json",)


def _tick_phone_rows() -> list:
    """iPhone の記録（mood.csv）の取り込みと事後報告。乗務日は 2026-12-07（月）ほか。"""
    def cfg(**over):
        return {"shifts.json": _base(mood_file=DIR_MARK + "/mood.csv", **{**NO_CONFIRM, **over})}

    b = cfg()
    mon = "2026-12-07"

    def mood(*lines):
        return {"mood.csv": "".join(ln + "\n" for ln in lines)}

    def run(*whens):
        return _tick_steps(*[(w, 30, 0) for w in whens], watch=PHONE_WATCH)

    first_log = {"log/pc-2026-12.jsonl": '{"t": "2026-12-01T00:00:00", "idle": 0, "game": 0}\n'}
    return [_skip_cleanup(row) for row in [
        ("neru → 寝た時刻", {**b, **mood(f"{mon} 08:10,neru")}, run(f"{mon}T08:15")),
        ("neru の後の okita → 起きた（時刻は iPhone の記録）", {**b, **mood(f"{mon} 08:10,neru", f"{mon} 14:20,okita")},
         run(f"{mon}T14:30")),
        ("2 回の見張りに分かれても同じ", {**b, **mood(f"{mon} 08:10,neru", f"{mon} 14:20,okita")},
         run(f"{mon}T08:15", f"{mon}T14:30", f"{mon}T14:35")),
        ("二度寝をまとめて取り込んでも最後の okita が効く",
         {**b, **mood(f"{mon} 08:10,neru", f"{mon} 10:00,okita", f"{mon} 10:30,neru", f"{mon} 14:20,okita")}, run(f"{mon}T14:30")),
        ("二度寝を途中で 1 回取り込んでいても最後の okita が効く",
         {**b, **mood(f"{mon} 08:10,neru", f"{mon} 10:00,okita", f"{mon} 10:30,neru", f"{mon} 14:20,okita")},
         run(f"{mon}T10:05", f"{mon}T14:30")),
        ("neru の無い okita は何もしない", {**b, **mood(f"{mon} 14:20,okita")}, run(f"{mon}T14:30")),
        ("kitaku → 帰宅の時刻。2 回目は書かない", {**b, **mood(f"{mon} 07:05,kitaku")}, run(f"{mon}T07:30", f"{mon}T07:35")),
        ("日本語の印（ねる・ただいま・起きた）", {**b, **mood(f"{mon} 07:05,ただいま", f"{mon} 08:10,ねる", f"{mon} 14:20,起きた")},
         run(f"{mon}T14:30")),
        ("T 区切り・秒つきの日時と前後の空白", {**b, **mood(f"{mon}T08:10:30 , neru ")}, run(f"{mon}T08:15")),
        ("乗務日の前日の昼より前の印は見ない", {**b, **mood("2026-12-06 11:00,neru")}, run(f"{mon}T08:15")),
        ("日付つきの yasumi → その日の事後報告。2 回目の見張りでは適用しない",
         {**b, **mood("2026-12-03 09:50,yasumi:2026-12-02")}, run("2026-12-03T10:00", "2026-12-03T10:05")),
        ("日付なしの 休んだ → 記録した時点で直近に終わった乗務", {**b, **mood("2026-12-08 09:00,休んだ")}, run("2026-12-08T09:05")),
        ("日付なしの deta → 直近に終わった乗務", {**b, **mood("2026-12-08 09:00,deta")}, run("2026-12-08T09:05")),
        ("休みの日（土曜）に deta → 出勤の例外日に足す", {**b, **mood("2026-12-06 10:00,deta:2026-12-05")}, run("2026-12-06T10:05")),
        ("休みの日に yasumi は記録しない（適用済みには残る）", {**b, **mood("2026-12-06 10:00,yasumi:2026-12-05")},
         run("2026-12-06T10:05")),
        ("まだ終わっていない乗務の yasumi は記録しない", {**b, **mood(f"{mon} 09:00,yasumi:{mon}")}, run(f"{mon}T09:05")),
        ("もう同じ記録がある日は何もしない",
         {**b, **mood("2026-12-03 09:50,yasumi:2026-12-02"), "state.json": _state("2026-12-02", status="yasumi", at="2026-12-02T06:00")},
         run("2026-12-03T10:00")),
        ("未来の時刻と lookback_days より前の印は無視",
         {**b, **mood("2026-12-20 09:00,yasumi:2026-12-02", "2026-12-01 09:00,yasumi:2026-12-01")}, run("2026-12-10T10:00")),
        ("気分の行と知らない印は無視", {**b, **mood(f"{mon} 09:00,1,メモ", f"{mon} 09:01,sanpo", "こわれた行")}, run(f"{mon}T09:05")),
        ("mood_file のファイルが無ければ何も起きない", b, run(f"{mon}T09:05")),
        ("当日欠勤の目安に達したら一文が付く",
         {**cfg(absence={"warn_at": 1}), **first_log, **mood("2026-12-03 09:50,yasumi:2026-12-02")}, run("2026-12-03T10:00")),
        ("関西弁", {**cfg(lang="ja_kansai"), **mood(f"{mon} 08:10,neru", f"{mon} 14:20,okita", "2026-12-07 14:25,休んだ:2026-12-04")},
         run(f"{mon}T14:30")),
    ]]


def _tick_confirm_rows() -> list:
    """終わった乗務の「出た？休んだ？」（1 日 1 件、新しい乗務から）。ここからは confirm.mode の既定（ask）。
    記録開始日は log/pc-2026-12.jsonl の最初の行で作る。いまは 2026-12-10（木）で、昨日の乗務は 12/09。"""
    def cfg(**over):
        return {"shifts.json": _base(**over)}

    def log(*stamps, idle=30, game=0):
        return "".join(json.dumps({"t": s, "idle": idle, "game": game}) + "\n" for s in stamps)

    def state(since="2026-12-01", **shifts):
        st = {"shifts": {d.replace("_", "-"): v for d, v in shifts.items()}}
        if since:
            st["confirm_since"] = since
        return {"state.json": st}

    def run(*whens, idle=30):
        return _tick_steps(*[(w, idle, 0) for w in whens], watch=("state.json",))

    worked = {"status": "worked", "at": "2026-12-08T16:00"}
    older = {f"d2026_12_0{n}": worked for n in (3, 4, 7, 8)}        # 12/09 より前は記録済み
    start = {"log/pc-2026-12.jsonl": log("2026-12-01T00:00:00")}
    b = cfg()
    now = "2026-12-10T10:00"

    def keyed(**shifts):
        return state(**{k[1:]: v for k, v in shifts.items()})

    evening = [f"2026-12-09T{16 + i // 12:02d}:{i % 12 * 5:02d}:00" for i in range(12)]   # 16:00〜16:55 の 12 行 = 60 分
    return [
        ("昨日の乗務が未記録なら聞く", {**b, **start, **keyed(**older)}, run(now)),
        ("同じ日の 2 回目は聞かない。翌日は次の 1 件", {**b, **start, **keyed(**{k: v for k, v in older.items() if "08" not in k})},
         run(now, "2026-12-10T10:05", "2026-12-11T10:00")),
        ("2 件あれば新しいほうと「ほか 1 件」", {**b, **start, **keyed(**{k: v for k, v in older.items() if "08" not in k})}, run(now)),
        ("起きた記録があれば聞かない（okita_means_worked の既定）",
         {**b, **start, **keyed(**older, d2026_12_09={"status": "okita", "at": "2026-12-09T14:00"})}, run(now)),
        ("okita_means_worked が false なら起きた記録があっても聞く",
         {**cfg(confirm={"okita_means_worked": False}), **start,
          **keyed(**older, d2026_12_09={"status": "okita", "at": "2026-12-09T14:00"})}, run(now)),
        ("休みにした乗務は聞かない", {**b, **start, **keyed(**older, d2026_12_09={"status": "yasumi", "at": "2026-12-09T06:00"})}, run(now)),
        ("乗務の時間帯に PC を 60 分触っていたら一言付く",
         {**b, "log/pc-2026-12.jsonl": log("2026-12-01T00:00:00") + log(*evening), **keyed(**older)}, run(now)),
        ("55 分なら付かない",
         {**b, "log/pc-2026-12.jsonl": log("2026-12-01T00:00:00") + log(*evening[:11]), **keyed(**older)}, run(now)),
        ("放置（idle 300 秒以上）は数えず、ゲーム中は数える",
         {**b, "log/pc-2026-12.jsonl": log("2026-12-01T00:00:00") + log(*evening[:6], idle=300) + log(*evening[6:], idle=9000, game=1)
          + log(*[s.replace("T16", "T17") for s in evening[:6]], idle=299), **keyed(**older)}, run(now)),
        ("翌朝 07:00（shift_end_earliest）より前は聞かない", {**b, **start, **keyed(**older)},
         run("2026-12-10T06:55", "2026-12-10T07:00")),
        ("shift_end_earliest を遅くすると、まだ終わっていない扱い",
         {**cfg(shift_end_earliest="11:00"), **start, **keyed(**{k: v for k, v in older.items() if "08" not in k})}, run(now)),
        ("記録開始日より前の乗務は聞かない", {**b, "log/pc-2026-12.jsonl": log("2026-12-09T12:00:00"), **state()}, run(now)),
        ("confirm_since が無ければ前日を書き、それより前は聞かない", {**b, **start}, run(now)),
        ("ログが無い初回は聞かない（今日が記録開始日）", b, run(now)),
        ("lookback_days 2 ならその中だけ", {**cfg(confirm={"lookback_days": 2}), **start, **state()}, run(now)),
        ("mode が off なら聞かない", {**cfg(**NO_CONFIRM), **start, **keyed(**older)}, run(now)),
        ("離席中は聞かない", {**b, **start, **keyed(**older)}, run(now, idle=1500)),
        ("関西弁", {**cfg(lang="ja_kansai"), **start, **keyed(**{k: v for k, v in older.items() if "08" not in k})}, run(now)),
    ]


ENFORCE_WATCH = ("state.json", "events.log", "notified.json")


def _declared_enforce(**over) -> dict:
    """同意済みの宣言（enforce_declare が state.json に書く形）。宣言そのものは Python 版でしか作れない。"""
    return {"action": "lock", "duration_hours": 2, "declared_at": "2026-12-07T06:00", "consented_at": "2026-12-07T06:00",
            "via": "shl", "runs": [], "grace_minutes": 15, "relock_minutes": 5, "max_runs": 6, "boot_grace_minutes": 15, **over}


def _tick_enforce_rows() -> list:
    """強制モードの発動判定（宣言・同意済みの乗務だけ）。乗務日は 2026-12-07（月）。最低ラインは 10:30。"""
    def cfg(**over):
        return {"shifts.json": _base(**{**NO_CONFIRM, **over})}

    b = cfg()
    d = "2026-12-07"

    def decl(day=d, shift=None, **over):
        return {"state.json": {"shifts": {day: {**(shift or {}), "enforce": _declared_enforce(**over)}}}}

    def run(*rows):
        """rows: "HH:MM" か ("HH:MM", idle) か ("HH:MM", idle, {上書き})。"""
        steps = []
        for row in rows:
            row = (row,) if isinstance(row, str) else row
            steps.append((f"{d}T{row[0]}", row[1] if len(row) > 1 else 30, 0) + ((row[2],) if len(row) > 2 else ()))
        return _tick_steps(*steps, watch=ENFORCE_WATCH)

    warned = {"warned_at": f"{d}T10:30:00", "seen_at": f"{d}T10:40:00"}
    return [
        ("予告 → あと 5 分 → 実行 → 再予告 → 再実行", {**b, **decl()}, run("10:30", "10:35", "10:40", "10:45", "10:50", "10:55")),
        ("実行のあと誰も触っていなければ（入力が実行より前）何もしない", {**b, **decl()},
         run("10:30", "10:35", "10:40", "10:45", ("10:50", 400))),
        ("回数の上限（max_runs 2）で止まる", {**b, **decl(max_runs=2)},
         run("10:30", "10:35", "10:40", "10:45", "10:50", "10:55", "11:00", "11:05")),
        ("時間切れ（duration_hours 0.5）で止まる", {**b, **decl(duration_hours=0.5)},
         run("10:30", "10:35", "10:40", "10:45", "10:50", "10:55", "11:00", "11:05", "11:10", "11:15", "11:20")),
        ("12 分以上空いたら予告からやり直し", {**b, **decl()}, run("10:30", "10:43", "10:48", "10:58")),
        ("実行のあと 12 分以上空いたら再予告からやり直し", {**b, **decl()},
         run("10:30", "10:35", "10:40", "10:45", "10:50", "11:03", "11:08")),
        ("STOP.txt があれば何もしない（通知の添え字に書く）", {**b, **decl(), "STOP.txt": "2026-12-07 09:00 kaijo\n"},
         run("10:30", "10:45")),
        ("解除済みの宣言は何もしない（添え字も出さない）", {**b, **decl(released_at=f"{d}T09:00")}, run("10:30", "10:45")),
        ("起動直後（uptime 600 秒）は発動しない", {**b, **decl()}, run(("10:30", 30, {"uptime": 600}), ("10:35", 30, {"uptime": 899}))),
        ("起動から 15 分ちょうどで発動する", {**b, **decl()}, run(("10:30", 30, {"uptime": 900}))),
        ("uptime が 0 なら発動しない", {**b, **decl()}, run(("10:30", 30, {"uptime": 0}))),
        ("uptime が無ければ発動しない", {**b, **decl()}, run(("10:30", 30, {"uptime": None}))),
        ("dry_run の予告は状態を進めない", {**b, **decl()}, run(("10:30", 30, {"dry_run": True}), "10:35")),
        ("dry_run の実行は記録しない", {**b, **decl(**warned)}, run(("10:45", 30, {"dry_run": True}))),
        ("dry_run の「あと 5 分」は何度でも出る", {**b, **decl(**warned)},
         run(("10:41", 30, {"dry_run": True}), ("10:42", 30, {"dry_run": True}), "10:43", "10:44")),
        ("宣言があれば lastcall・soft・hard の通知に添え字", {**b, **decl()}, run("07:20", "08:00", "10:30")),
        ("action が shutdown", {**b, **decl(action="shutdown")}, run("10:30", "10:35", "10:40", "10:45")),
        # throttle／suspend は Agent の段階の処理が持つ。見張りは予告も実行もせず、state.json にも書かない
        ("action が suspend なら見張りは何もしない", {**b, **decl(action="suspend", steps=[75, 50, 25, 10])}, run("10:30", "10:35", "10:50")),
        ("action が throttle なら見張りは何もしない", {**b, **decl(action="throttle", steps=[75, 50, 25, 10])}, run("10:30", "10:35", "10:50")),
        ("action が sleep・予告 2.5 分", {**b, **decl(action="sleep", grace_minutes=2.5)}, run("10:30", "10:35")),
        ("離席中は何もしない", {**b, **decl()}, run(("10:30", 1500), ("10:45", 1500))),
        ("起きた記録のある乗務は発動しない", {**b, **decl(shift={"status": "okita", "at": f"{d}T10:00"})}, run("10:30", "10:45")),
        ("宣言が別の乗務日のものなら何もしない", {**b, **decl(day="2026-12-08")}, run("10:30", "10:45")),
        ("古い宣言（項目が無い）は設定の値を使う",
         {**cfg(enforce={"grace_minutes": 10, "action": "sleep"}),
          "state.json": {"shifts": {d: {"enforce": {"consented_at": f"{d}T06:00"}}}}}, run("10:30", "10:40")),
        ("関西弁", {**cfg(lang="ja_kansai"), **decl()}, run("10:30", "10:40", "10:45", "10:50")),
    ]


def _expect_release(cfg, store, inp) -> dict:
    """緊急解除（tick を通らない）。input {"now", "reason"}、expect {"message", "state", "events", "stop": STOP.txt の行}。"""
    msg = sl.enforce_release(cfg, store, datetime.fromisoformat(inp["now"]), inp["reason"])
    return {"message": msg, "state": _read_state(store) if store.p("state.json").exists() else None,
            "events": _event_lines(store), "stop": store.p(sl.STOP_FILE).read_text(encoding="utf-8").splitlines()}


def _release_rows() -> list:
    b = {"shifts.json": _base()}
    inp = {"now": "2026-12-07T11:00", "reason": "tray"}
    one = {"2026-12-07": {"enforce": _declared_enforce(runs=[{"at": "2026-12-07T10:45:00", "action": "lock"}])}}
    return [
        ("宣言が 1 件", {**b, "state.json": {"shifts": one}}, inp),
        ("宣言が 2 件（別の乗務日）",
         {**b, "state.json": {"shifts": {"2026-12-08": {"enforce": _declared_enforce(action="sleep")}, **one}}}, inp),
        ("宣言なし（乗務日は -）", b, inp),
        ("STOP.txt が既にあって宣言なし", {**b, "STOP.txt": "2026-12-07 09:00 kaijo\n"}, inp),
        ("STOP.txt が既にあっても宣言があれば解除する", {**b, "STOP.txt": "2026-12-07 09:00 kaijo\n", "state.json": {"shifts": one}}, inp),
        ("解除済みの宣言と同意の無い宣言には触らない",
         {**b, "state.json": {"shifts": {"2026-12-04": {"enforce": _declared_enforce(released_at="2026-12-04T11:00")},
                                         "2026-12-08": {"enforce": {"action": "lock", "declared_at": "2026-12-07T06:00"}},
                                         "2026-12-09": {"status": "okita", "at": "2026-12-09T14:00"}}}}, inp),
        ("関西弁", {"shifts.json": _base(lang="ja_kansai"), "state.json": {"shifts": one}}, inp),
    ]


CAL_A = "https://cal.example/a.ics"
CAL_B = "https://cal.example/b.ics"


def _ics(*events) -> str:
    """iCal の本文（行末は CRLF）。events は _vevent の戻り値。"""
    return "".join(ln + "\r\n" for ln in ["BEGIN:VCALENDAR", "VERSION:2.0", *[ln for e in events for ln in e], "END:VCALENDAR"])


def _vevent(summary, start, end=None, *extra) -> list:
    """予定 1 件の行。start・end は "名前の後ろ:値"（";VALUE=DATE:20261207" や ":20261207T030000Z"）。"""
    lines = ["BEGIN:VEVENT", "UID:x@example", "DTSTART" + start]
    if end is not None:
        lines.append("DTEND" + end)
    return [*lines, "SUMMARY:" + summary, *extra, "END:VEVENT"]


def _allday(summary, start, end=None, *extra) -> list:
    return _vevent(summary, ";VALUE=DATE:" + start, None if end is None else ";VALUE=DATE:" + end, *extra)


def _tick_calendar_rows() -> list:
    """カレンダー同期（calendar.json）。同期の結果が乗務日の判定に効くのは次の見張りから（2 回目の就寝準備の通知で見る）。
    2026-12-07 は月曜（乗務日）、2026-12-05 は土曜（休み）。時刻つきの Z は、どのタイムゾーンで書き出しても同じ日になる時刻にしてある。"""
    def cfg(**cal):
        return {"shifts.json": _base(calendar=cal or {"ics_url": CAL_A}, **NO_CONFIRM)}

    one = cfg()
    mon, sat = "2026-12-07", "2026-12-05"

    def run(day=mon, fetch=None, watch=("calendar.json",), hms=("07:15", "07:20")):
        return _tick_steps(*[(f"{day}T{hm}", 30, 0) for hm in hms], watch=watch, fetch=fetch)

    def got(*events):
        return {CAL_A: _ics(*events)}

    def old(fetched, **src):
        """前回の同期の結果（新形式）。"""
        return {"calendar.json": {"fetched": fetched, "sources": {"カレンダー": {
            "fetched": fetched, "error": None, "off": {}, "work": {}, "recurring_skipped": 0, **src}}}}

    two = cfg(sources=[{"name": "会社", "ics_url": CAL_A}, {"name": "私用", "ics_url": CAL_B}])
    return [
        ("カレンダー未設定なら何もしない", {"shifts.json": _base(**NO_CONFIRM)}, run()),
        ("終日の「公休」で乗務日が休みになる", one, run(fetch=got(_allday("公休", "20261207", "20261208")))),
        ("「出勤」で休みの日が乗務日になる", one, run(sat, fetch=got(_allday("臨時出勤", "20261205", "20261206")))),
        ("複数日の終日予定（DTEND は翌日）", one, run(fetch=got(_allday("有給", "20261207", "20261210")))),
        ("DTEND が無い・DTSTART より前なら 1 日だけ", one,
         run(fetch=got(_allday("公休", "20261207"), _allday("休み", "20261209", "20261208")))),
        ("VALUE=DATE の無い 8 桁も終日", one, run(fetch=got(_vevent("公休", ":20261207", ":20261208")))),
        ("Z つきの時刻はローカルの日付", one, run(fetch=got(_vevent("休み", ":20261207T030000Z", ":20261207T040000Z")))),
        ("時刻つき（TZID）は書いてある日付", one,
         run(fetch=got(_vevent("公休", ";TZID=Asia/Tokyo:20261207T090000", ";TZID=Asia/Tokyo:20261208T180000")))),
        ("折り返した行（空白・タブ）", one,
         run(fetch={CAL_A: _ics(["BEGIN:VEVENT", "DTSTART;VALUE=DATE:2026", " 1207", "SUMMARY:公", "\t休（折り", " 返し）", "END:VEVENT"])})),
        ("SUMMARY のエスケープを戻す", one, run(fetch=got(_allday("公休\\, 午後\\; メモ\\n2 行目", "20261207")))),
        ("STATUS:CANCELLED は無視", one,
         run(fetch=got(_allday("公休", "20261207", None, "STATUS:CANCELLED"), _allday("公休", "20261208", None, "STATUS:CONFIRMED")))),
        ("RRULE は初回だけ（recurring_skipped 1）", one,
         run(fetch=got(_allday("公休", "20261207", "20261208", "RRULE:FREQ=WEEKLY;BYDAY=MO")))),
        ("キーワードの無い予定・DTSTART の無い予定は拾わない", one,
         run(fetch=got(_allday("歯医者", "20261207"), ["BEGIN:VEVENT", "SUMMARY:公休", "END:VEVENT"]))),
        ("休みと出勤の両方の言葉があれば休み", one, run(fetch=got(_allday("出勤の代わりに公休", "20261207")))),
        ("62 日を超える予定は 62 日ぶん", one, run(fetch=got(_allday("休み", "20261207", "20270401")))),
        ("180 日より先・60 日より前は残さない", one,
         run(fetch=got(_allday("公休", "20270605"), _allday("公休", "20270606"), _allday("公休", "20261001")))),
        ("LF だけの行末・VCALENDAR の外の行", one,
         run(fetch={CAL_A: "X-JUNK:1\nBEGIN:VEVENT\nDTSTART;VALUE=DATE:20261207\nSUMMARY:公休\nEND:VEVENT\nEND:VEVENT\nSUMMARY:出勤\n"})),
        ("sync_minutes 内は取りに行かない（取りに行けば失敗が書かれる）",
         {**one, **old("2026-12-07T06:30:00", off={"2026-12-08": "公休"})}, run(hms=("07:15",))),
        ("sync_minutes を過ぎたら取りに行く", {**one, **old("2026-12-07T06:15:00", off={"2026-12-08": "公休"})},
         run(fetch=got(_allday("公休", "20261209")), hms=("07:15",))),
        ("sync_minutes を 10 に", {**cfg(ics_url=CAL_A, sync_minutes=10), **old("2026-12-07T07:00:00")},
         run(fetch=got(_allday("公休", "20261209")), hms=("07:05", "07:10"))),
        ("取得に失敗したら前回分が残り error が入る", {**one, **old("2026-12-07T05:00:00", off={mon: "公休"})}, run()),
        ("初回から失敗", one, run()),
        ("失敗の後に成功すれば error は消える",
         {**one, "calendar.json": {"fetched": "2026-12-07T05:00:00", "sources": {"カレンダー": {
             "fetched": None, "error": "12/07 05:00 OSError: fetch failed"}}}},
         run(fetch=got(_allday("公休", "20261207")))),
        ("2 本のうち 1 本だけ失敗", two, run(fetch=got(_allday("公休", "20261207")))),
        ("旧形式の calendar.json は先頭のソースに引き継ぐ",
         {**two, "calendar.json": {"fetched": "2026-12-07T07:00:00", "error": None, "off": {mon: "公休"}, "work": {},
                                   "recurring_skipped": 2}},
         run(fetch={CAL_B: _ics(_allday("出勤", "20261205"))})),
        ("同じ URL は 1 本・ics_url が先頭", cfg(ics_url=CAL_A, sources=[{"name": "二重", "ics_url": CAL_A},
                                                                 {"name": "私用", "ics_url": " " + CAL_B + " "}]),
         run(fetch={CAL_A: _ics(_allday("公休", "20261207")), CAL_B: _ics(_allday("公休", "20261208"))})),
        ("名前の重複に ' が付く・名前が無ければ番号", cfg(sources=[{"name": "会社", "ics_url": CAL_A}, {"name": "会社", "ics_url": CAL_B},
                                                    {"name": " ", "ics_url": CAL_A + "?3"}, {"ics_url": ""}, "x"]),
         run(fetch={CAL_A: _ics(), CAL_B: _ics(), CAL_A + "?3": _ics()}, hms=("07:15",))),
        ("ソースごとのキーワード（無ければ calendar の値）",
         cfg(off_keywords=["明け"], sources=[{"name": "会社", "ics_url": CAL_A, "off_keywords": ["研修"], "work_keywords": ["当番"]},
                                          {"name": "私用", "ics_url": CAL_B}]),
         run(fetch={CAL_A: _ics(_allday("研修", "20261207"), _allday("当番", "20261205"), _allday("明け", "20261208")),
                    CAL_B: _ics(_allday("明け", "20261209"), _allday("研修", "20261210"), _allday("出勤", "20261212"))})),
        ("設定から消えたソースは calendar.json からも消える",
         {**one, "calendar.json": {"fetched": "2026-12-07T07:00:00", "sources": {
             "カレンダー": {"fetched": "2026-12-07T07:00:00", "error": None, "off": {}, "work": {}, "recurring_skipped": 0},
             "昔の": {"fetched": "2026-12-07T07:00:00", "error": None, "off": {mon: "公休"}, "work": {}, "recurring_skipped": 0}}}},
         run(hms=("07:15",))),
        # 日付が読めなければその回は失敗（前回の休みが残る → 就寝準備の通知は出ない）。error の文は言語ごとに違うので calendar.json は比べない
        ("日付の読めない DTSTART は失敗として前回分を残す", {**one, **old("2026-12-07T05:00:00", off={mon: "公休"})},
         run(fetch=got(_allday("公休", "2026XX07")), watch=("notified.json",))),
        ("前回の fetched が読めなければ同期しない（見張りは続く）",
         {**one, "calendar.json": {"fetched": "x", "sources": {"カレンダー": {"fetched": "こわれた", "off": {mon: "公休"}}}}},
         run(fetch=got(_allday("公休", "20261208")))),
    ]


def _tick_maintenance_rows() -> list:
    """1 日 1 回の整理（古い記録を消す）。今は 2027-12-04（土）10:00。既定は days 60 日・ログ 180 日・events 365 日・レポート 3 か月。"""
    now = "2027-12-04T10:00"

    def cfg(**over):
        return {"shifts.json": _base(**{**NO_CONFIRM, **over})}

    def log(month, kind="pc"):
        return f"log/{kind}-{month}.jsonl"

    def line(day):
        return '{"t": "%sT00:00:00", "idle": 0, "game": 0}\n' % day

    old = {
        "days.json": {"off": {"2027-10-04": "公休", "2027-10-05": "公休"}, "work": {"2027-10-03": "出勤", "2027-12-11": "出勤"},
                      "times": {"2027-10-04": {"start_target": "18:00"}, "2027-12-06": {"start_target": "18:00"}}, "memo": "残る"},
        "state.json": {"shifts": {"2027-10-04": {"status": "worked", "at": "2027-10-04T16:00"},
                                  "2027-10-05": {"status": "yasumi", "at": "2027-10-05T09:00"}}, "other": 1},
        log("2026-12"): line("2026-12-01"), log("2027-05"): line("2027-05-01"), log("2027-06"): line("2027-06-01"),
        log("2027-05", "phone"): line("2027-05-02"), "log/pc-xxxx-yy.jsonl": "x\n", "log/memo.txt": "x\n",
        "events.log": "2026-12-03T09:00\t2026-12-03\tyasumi\tapp\n\n2026-12-04T09:00\t2026-12-04\tyasumi\tapp\n",
        "reports/report_2027-07-21_2027-08-20.html": "<html>a</html>\n",
        "reports/report_2027-08-05_2027-09-04.html": "<html>b</html>\n",
        "reports/report_2027-07-04_2027-09-03.pdf": "c\n",
        "reports/report-2027-08-01.html": "<html>d</html>\n",
        "reports/report-2027-09-04.pdf": "e\n",
        "reports/memo_2027-01-01_2027-01-31.html": "<html>f</html>\n",
    }
    watch = ("maintenance.json", *[k for k in old], "log/pc-2027-12.jsonl")

    def run(*whens, watch=watch):
        return _tick_steps(*[(w, 30, 0) for w in whens], watch=watch)

    small = ("maintenance.json", "days.json", "state.json", "events.log")
    return [
        ("古い記録が消える", {**cfg(), **old}, run(now)),
        ("同じ日の 2 回目は整理しない", {**cfg(), **old, "maintenance.json": {"last_cleanup": "2027-12-04", "result": {"days": 9}}},
         run(now)),
        ("翌日は整理する", {**cfg(), **old, "maintenance.json": {"last_cleanup": "2027-12-03", "result": {"days": 9}, "memo": "残る"}},
         _tick_steps(("2027-12-03T23:58", 30, 0), ("2027-12-04T00:03", 30, 0), watch=watch)),
        ("何も無いフォルダ（days.json は空で作られる）", cfg(), run(now, watch=small + ("reports/x.html",))),
        ("消すものが無ければ events.log は書き直さない",
         {**cfg(), "events.log": "2026-12-04T09:00\t2026-12-04\tyasumi\tapp\n", "state.json": {"other": 1}}, run(now, watch=small)),
        ("events.log が全部古ければ空になる", {**cfg(), "events.log": "2026-12-01T09:00\t2026-12-01\tyasumi\tapp\n"},
         run(now, watch=small)),
        ("保持期間を変える",
         {**cfg(retention={"days_keep": 59, "log_keep_days": 150, "events_keep_days": 366}, report={"keep_months": 4}), **old},
         run(now)),
        ("保持期間が小数なら日数は切り捨て", {**cfg(retention={"days_keep": 59.5, "log_keep_days": 180, "events_keep_days": 365}), **old},
         run(now, watch=small)),
        ("レポートの期限は月末に丸める（12/31 の 1 か月前は 11/30）",
         {**cfg(report={"keep_months": 1}), "reports/report_2027-10-30_2027-11-29.html": "a\n",
          "reports/report_2027-10-31_2027-11-30.html": "b\n"},
         run("2027-12-31T10:00", watch=("maintenance.json", "reports/report_2027-10-30_2027-11-29.html",
                                        "reports/report_2027-10-31_2027-11-30.html"))),
        ("レポートの保存が 14 か月（年をまたいで数える）",
         {**cfg(report={"keep_months": 14}), "reports/report_2026-12-10_2027-01-09.html": "a\n",
          "reports/report_2026-12-11_2027-01-10.html": "b\n"},
         run("2028-03-10T10:00", watch=("maintenance.json", "reports/report_2026-12-10_2027-01-09.html",
                                        "reports/report_2026-12-11_2027-01-10.html"))),
    ]


def _skip_cleanup(row):
    """その日の整理は済んだことにする（maintenance.json を置く）。1 日 1 回の整理は days.json を書き直すので、
    days.json を見る節（phone）に整理の書き込みが混ざらないようにする。整理そのものは節 maintenance で見る。"""
    name, files, inp = row
    day = inp["steps"][0]["now"][:10]
    return name, {**files, "maintenance.json": {"last_cleanup": day}}, inp


# ---- 見るだけのコマンド（shl status / yotei / day list / kakunin）----
# status_text・kakunin_text: input {"now"}、expect {"text", "state": 終わったあとの state.json（無ければ null）}
#   （未確認の乗務を数えるとき、confirm_since が無ければ state.json に書く）
# schedule_text: input {"now", "n": 日数}、expect {"text"}

def _pc_log(*stamps, idle=30, game=0) -> str:
    return "".join(json.dumps({"t": s, "idle": idle, "game": game}) + "\n" for s in stamps)


def _state_or_none(store):
    return _read_state(store) if store.p("state.json").exists() else None


def _expect_status_text(cfg, store, inp) -> dict:
    text = sl.status_text(cfg, store, datetime.fromisoformat(inp["now"]))
    return {"text": text, "state": _state_or_none(store)}


def _expect_kakunin_text(cfg, store, inp) -> dict:
    text = sl.kakunin_text(cfg, store, datetime.fromisoformat(inp["now"]))
    return {"text": text, "state": _state_or_none(store)}


def _expect_schedule_text(cfg, store, inp) -> dict:
    return {"text": sl.schedule_text(cfg, datetime.fromisoformat(inp["now"]).date(), inp["n"])}


def _status_rows() -> list:
    """いまは 2026-12-10（木）。記録開始日は log/pc-2026-12.jsonl の最初の行（12/01）。"""
    def cfg(**over):
        return {"shifts.json": _base(**over)}

    b = cfg()
    d = "2026-12-10"
    log = {"log/pc-2026-12.jsonl": _pc_log("2026-12-01T09:00")}
    worked = {"status": "worked", "at": "2026-12-08T16:00"}
    cal4 = {"sources": [{"name": "A", "ics_url": CAL_A}, {"name": "B", "ics_url": CAL_B},
                        {"ics_url": "https://cal.example/c.ics"}, {"name": "A", "ics_url": "https://cal.example/d.ics"}]}
    declared = {
        "2026-12-08": {"enforce": _declared_enforce()},                                   # おとといより前は出さない
        "2026-12-09": {"enforce": _declared_enforce(action="sleep", duration_hours=2.5, released_at="2026-12-09T11:00",
                                                    runs=[{"at": "2026-12-09T10:45:00", "action": "sleep"}])},
        "2026-12-10": {"enforce": _declared_enforce(runs=[{"at": f"{d}T10:45:00", "action": "lock"},
                                                          {"at": f"{d}T10:55:00", "action": "lock"}])},
        "2026-12-11": {"enforce": _declared_enforce(action="suspend", steps=[75, 50, 25, 10])},
        "2026-12-14": {"enforce": {"consented_at": f"{d}T06:00"}},                        # 古い宣言（項目が無い）
        "2026-12-15": {"enforce": {"action": "lock", "declared_at": f"{d}T06:00"}},       # 同意が無い宣言は出さない
        "2026-12-16": {"enforce": _declared_enforce(action="shutdown", duration_hours="3")},
    }
    return [
        ("記録なし・就寝準備の時間", b, {"now": f"{d}T07:30"}),
        ("起きた乗務", {**b, "state.json": _state(d, status="okita", at=f"{d}T14:00")}, {"now": f"{d}T15:50"}),
        ("金曜の夜（次は月曜で 24 時間より先）", b, {"now": "2026-12-11T18:00"}),
        ("work_weekdays が空（乗務なし）", cfg(work_weekdays=[]), {"now": f"{d}T09:00"}),
        ("睡眠の時間が小数（7.5・6.0）", cfg(sleep_target_hours=7.5, sleep_min_hours=6.0), {"now": f"{d}T09:00"}),
        ("当日欠勤が目安に達している（有給は数えない・窓より前の休みも数えない）",
         {**cfg(absence={"window_shifts": 6, "warn_at": 2}), **log,
          "state.json": {"confirm_since": "2026-12-01",
                         "shifts": {"2026-12-03": {"status": "yasumi", "at": "2026-12-03T10:00"},
                                    "2026-12-04": {"status": "yasumi", "at": "2026-12-04T10:00", "paid": True},
                                    "2026-12-07": {"status": "yasumi", "at": "2026-12-07T10:00"},
                                    "2026-12-08": worked, "2026-12-09": worked,
                                    "2026-12-01": {"status": "yasumi", "at": "2026-12-01T10:00"}}}},
         {"now": f"{d}T18:00"}),
        ("当日欠勤の目安が 0（行を出さない）", cfg(absence={"warn_at": 0}), {"now": f"{d}T09:00"}),
        ("当日欠勤の設定が壊れている（既定に戻す）", cfg(absence={"window_shifts": "abc", "warn_at": 500}), {"now": f"{d}T09:00"}),
        ("カレンダー 4 本（同期済み・未・エラー・名前の重なり）",
         {**cfg(calendar=cal4),
          "calendar.json": {"sources": {"A": {"fetched": "2026-12-10T06:00:12", "off": {"2026-12-14": "公休"}, "work": {}},
                                        "カレンダー3": {"fetched": "2026-12-09T23:10:00", "error": "HTTP Error 404: Not Found",
                                                    "off": {}, "work": {"2026-12-12": ""}}}}},
         {"now": f"{d}T09:00"}),
        ("カレンダーが旧形式（ics_url が 1 本・calendar.json に sources が無い）",
         {**cfg(calendar={"ics_url": CAL_A}), "calendar.json": {"fetched": "2026-12-09T23:00", "off": {"2026-12-11": "有給"}, "work": {}}},
         {"now": f"{d}T09:00"}),
        ("カレンダーの設定はあるが calendar.json が無い", cfg(calendar={"ics_url": CAL_A}), {"now": f"{d}T09:00"}),
        ("未確認の乗務（聞いた乗務も並ぶ・出た乗務は並ばない）",
         {**b, **log, "state.json": {"confirm_since": "2026-12-01",
                                    "shifts": {"2026-12-08": worked, "2026-12-07": {"confirm_asked": "2026-12-08T09:00"}}}},
         {"now": f"{d}T12:00"}),
        ("confirm_since が無ければ前日を書く（未確認は昨日の乗務だけ）", {**b, **log}, {"now": f"{d}T12:00"}),
        ("強制モードの宣言（実行済み・解除済み・一時停止・古い宣言）と STOP.txt",
         {**b, "state.json": {"shifts": declared}, "STOP.txt": "2026-12-10 11:00 kaijo\n"}, {"now": f"{d}T12:00"}),
        ("強制モードの宣言（STOP.txt なし）", {**b, "state.json": {"shifts": declared}}, {"now": f"{d}T12:00"}),
        ("減速の宣言", {**b, "state.json": {"shifts": {d: {"enforce": _declared_enforce(action="throttle", steps=[75, 50, 25, 10])}}}},
         {"now": f"{d}T12:00"}),
        ("STOP.txt だけ（宣言なし）", {**b, "STOP.txt": "2026-12-10 11:00 kaijo\n"}, {"now": f"{d}T12:00"}),
        ("例外日（理由つき・理由なし）",
         {**b, "days.json": {"off": {"2026-12-11": "通院", "2026-12-14": ""}, "work": {"2026-12-12": "振替"}}},
         {"now": f"{d}T18:00"}),
        ("関西弁",
         {**cfg(lang="ja_kansai", calendar=cal4, absence={"window_shifts": 6, "warn_at": 1}), **log,
          "state.json": {"confirm_since": "2026-12-01",
                         "shifts": {**declared, "2026-12-07": {"status": "yasumi", "at": "2026-12-07T10:00"}}},
          "STOP.txt": "2026-12-10 11:00 kaijo\n"},
         {"now": f"{d}T12:00"}),
        ("関西弁・乗務なし", cfg(lang="ja_kansai", work_weekdays=[]), {"now": f"{d}T09:00"}),
    ]


def _kakunin_rows() -> list:
    b = {"shifts.json": _base()}
    log = {"log/pc-2026-12.jsonl": _pc_log("2026-12-01T09:00")}
    worked = {"status": "worked", "at": "2026-12-08T16:00"}
    now = {"now": "2026-12-10T12:00"}

    def state(**shifts):
        return {"state.json": {"confirm_since": "2026-12-01", "shifts": {d.replace("_", "-"): v for d, v in shifts.items()}}}

    return [
        ("記録が無ければ未確認もない", b, now),
        ("全部確認済み（出た・休んだ・起きた）",
         {**b, **log, **state(**{"2026_12_03": worked, "2026_12_04": {"status": "yasumi", "at": "2026-12-04T10:00"},
                                 "2026_12_07": {"status": "okita", "at": "2026-12-07T14:00"}, "2026_12_08": worked,
                                 "2026_12_09": worked})}, now),
        ("未確認が並ぶ（古い順・聞いた乗務も）",
         {**b, **log, **state(**{"2026_12_08": worked, "2026_12_07": {"confirm_asked": "2026-12-08T09:00"}})}, now),
        ("起きただけの乗務を「出た」と数えない設定",
         {"shifts.json": _base(confirm={"okita_means_worked": False}), **log,
          **state(**{"2026_12_09": {"status": "okita", "at": "2026-12-09T14:00"}, "2026_12_08": worked, "2026_12_07": worked,
                     "2026_12_04": worked, "2026_12_03": worked})}, now),
        ("今朝終わったばかりの乗務は 07:00 から", {**b, **log, **state()}, {"now": "2026-12-10T06:59"}),
        ("confirm_since が無ければ前日を書く", {**b, **log}, now),
        ("lookback_days 2", {"shifts.json": _base(confirm={"lookback_days": 2}), **log, **state()}, now),
        ("関西弁", {"shifts.json": _base(lang="ja_kansai"), **log, **state()}, now),
        ("関西弁・未確認なし", {"shifts.json": _base(lang="ja_kansai")}, now),
    ]


def _schedule_rows() -> list:
    b = {"shifts.json": _base()}
    days = {"days.json": {"off": {"2026-12-11": "通院", "2026-12-14": ""}, "work": {"2026-12-12": "振替", "2026-12-20": ""}},
            "calendar.json": {"sources": {"A": {"off": {"2026-12-16": "公休", "2026-12-12": "手動が勝つ"}, "work": {"2026-12-19": ""}}}}}
    return [
        ("14 日（yotei）", b, {"now": "2026-12-10T12:00", "n": 14}),
        ("28 日（day list）・年またぎ", b, {"now": "2026-12-10T12:00", "n": 28}),
        ("例外日（理由つき・理由なし・カレンダー）", {**b, **days}, {"now": "2026-12-10T00:00", "n": 14}),
        ("例外日・28 日", {**b, **days}, {"now": "2026-12-10T23:59", "n": 28}),
        ("7 で割り切れない日数", b, {"now": "2026-12-10T12:00", "n": 10}),
        ("1 日だけ", b, {"now": "2026-12-10T12:00", "n": 1}),
        ("関西弁", {"shifts.json": _base(lang="ja_kansai"), **days}, {"now": "2026-12-10T12:00", "n": 14}),
    ]


# ---- 記録のコマンド（shl okita / neru / itte / yasumi / deru，shl day off|work|rm）----
# cmd_keyword・late_report: input {"now", "cmd", "words"}、expect {"text", "state", "events", "days"}
#   cmd_keyword は語に日付が無いもの（cli.record_cmd → commands.cmd_keyword）、late_report は日付つき（→ records.late_report）
# days_set: input {"now", "action", "date", "note"}、expect {"code", "text", "days"}
#   state は終わったあとの state.json、days は days.json（無ければ null）、events は events.log の行

def _json_or_none(store, name: str):
    path = store.p(name)
    return json.loads(path.read_text(encoding="utf-8")) if path.exists() else None


def _expect_record_cmd(cfg, store, inp) -> dict:
    from shiftlock import cli
    text = cli.record_cmd(inp["cmd"], list(inp["words"]), cfg, store, datetime.fromisoformat(inp["now"]))
    return {"text": text, "state": _json_or_none(store, "state.json"), "events": _event_lines(store),
            "days": _json_or_none(store, "days.json")}


def _expect_day_cmd(cfg, store, inp) -> dict:
    from shiftlock import cli
    code, text = cli.day_cmd(inp["action"], inp["date"], list(inp["note"]), store.p("shifts.json"), store,
                             datetime.fromisoformat(inp["now"]))
    return {"code": code, "text": text, "days": _json_or_none(store, "days.json")}


def _cmd_keyword_rows() -> list:
    """乗務日は 2026-12-10（木）。就寝目安 08:00・最低ライン 10:30・出発 16:00／16:30・出庫 17:00／17:30。"""
    def cfg(**over):
        return {"shifts.json": _base(**over)}

    b = cfg()
    k = cfg(lang="ja_kansai")
    d = "2026-12-10"
    sat, sun, mon = "2026-12-12", "2026-12-13", "2026-12-14"

    def at(hm, day=d):
        return f"{day}T{hm}"

    def run(cmd, hm, *words, day=d):
        return {"now": at(hm, day), "cmd": cmd, "words": list(words)}

    def state(day=d, **rec):
        return {"state.json": _state(day, **rec)}

    no_gap = {"usage-pc.json": {"gaps": []}}
    log = {"log/pc-2026-12.jsonl": _pc_log("2026-12-01T09:00")}
    gate = {**b, **no_gap, "state.json": _state(d, kitaku=at("07:00"), enforce=_declared_enforce())}
    return [
        ("neru", b, run("neru", "08:10")),
        ("neru: auto_wake_hours 3.5", cfg(auto_wake_hours=3.5), run("neru", "08:10")),
        ("neru: 乗務の無い日", b, run("neru", "18:00", day=sat)),
        ("neru: 取り消し用の undo は書かない（前の undo が残る）",
         {**b, "state.json": {"undo": {"day": d, "prev": {}, "at": at("08:00"), "what": ["kitaku"]},
                              "shifts": {d: {"kitaku": at("07:05")}}}}, run("neru", "08:10")),
        ("okita: 記録なし", b, run("okita", "14:00")),
        ("okita: 寝た記録あり", {**b, **state(sleep_at=at("08:10"))}, run("okita", "14:30")),
        ("okita: 寝た時間が最低ラインに届かない", {**b, **state(sleep_at=at("10:00"))}, run("okita", "14:00")),
        ("okita: 離席の記録が空（確かめる文が付く）", {**b, **no_gap}, run("okita", "14:00")),
        ("okita: 休みの乗務（寝た記録あり．休みのまま）",
         {**b, **state(status="yasumi", at=at("06:00"), paid=True, sleep_at=at("08:10"))}, run("okita", "14:30")),
        ("okita: 出発済みの乗務（寝た記録なし）", {**b, **state(status="worked", at=at("16:10"))}, run("okita", "16:40")),
        ("okita: 強制中は記録しない", gate, run("okita", "11:00")),
        ("okita: 強制中でも STOP.txt があれば記録する", {**gate, "STOP.txt": True}, run("okita", "11:00")),
        ("okita: 乗務の無い日", b, run("okita", "18:00", day=sat)),
        ("itte: 早すぎる", b, run("itte", "13:59")),
        ("itte: 出発の 2 時間前から（undo を残す）", {**b, **state(status="okita", at=at("13:00"))}, run("itte", "14:00")),
        ("itte: 乗務の無い日", b, run("itte", "18:00", day=sat)),
        ("yasumi: 次の乗務を休みにする", b, run("yasumi", "07:00")),
        ("yasumi 有給", b, run("yasumi", "07:00", "有給")),
        ("yasumi 有休（空白まじり）", b, run("yasumi", "07:00", " 有　休 ")),
        ("yasumi 有給なし（印は付けない）", b, run("yasumi", "07:00", "有給なし")),
        ("yasumi: 今月 2 回目", {**b, **state("2026-12-03", status="yasumi", at="2026-12-03T07:00")}, run("yasumi", "07:00")),
        ("yasumi: 出庫の最終を過ぎたら今日の乗務（事後報告）", b, run("yasumi", "18:00")),
        ("yasumi 有給: 出庫の最終を過ぎたら今日の乗務（事後報告）", b, run("yasumi", "17:30", "有給")),
        ("yasumi: 24 時間以内に乗務が無い", b, run("yasumi", "18:00", day=sat)),
        ("yasumi: 日曜の夜は月曜の乗務", b, run("yasumi", "18:00", day=sun)),
        ("yasumi: 当日欠勤の目安に達した（これから休む乗務も数える）",
         {**cfg(absence={"window_shifts": 6, "warn_at": 2}), **log,
          **state("2026-12-09", status="yasumi", at="2026-12-09T07:00")}, run("yasumi", "18:00", day=sun)),
        ("yasumi 有給: 目安には数えない",
         {**cfg(absence={"window_shifts": 6, "warn_at": 2}), **log,
          **state("2026-12-09", status="yasumi", at="2026-12-09T07:00")}, run("yasumi", "18:00", "有給", day=sun)),
        ("yasumi: 休みにしても宣言は残る", {**b, **state(mon, enforce=_declared_enforce())}, run("yasumi", "18:00", day=sun)),
        ("deru: 休みを取り消す（有給の印も消える）",
         {**b, **state(status="yasumi", at=at("06:00"), paid=True)}, run("deru", "07:00")),
        ("deru: 出発済みを取り消す（ほかの記録は残る）",
         {**b, **state(status="worked", at=at("14:10"), sleep_at=at("08:10"))}, run("deru", "14:20")),
        ("deru: 記録が無くても行は残る", b, run("deru", "07:00")),
        ("deru 有給（印は関係ない）", {**b, **state(status="yasumi", at=at("06:00"))}, run("deru", "07:00", "有給")),
        ("deru: 24 時間以内に乗務が無い", b, run("deru", "18:00", day=sat)),
        ("関西弁: neru", k, run("neru", "08:10")),
        ("関西弁: okita", {**k, **state(sleep_at=at("10:00"))}, run("okita", "14:00")),
        ("関西弁: okita 強制中", {**gate, **k}, run("okita", "11:00")),
        ("関西弁: itte", k, run("itte", "15:00")),
        ("関西弁: itte 早すぎる", k, run("itte", "09:00")),
        ("関西弁: yasumi 有給", k, run("yasumi", "07:00", "有給")),
        ("関西弁: deru", {**k, **state(status="worked", at=at("14:10"))}, run("deru", "14:20")),
        ("関西弁: 乗務が無い", k, run("yasumi", "18:00", day=sat)),
    ]


def _late_report_rows() -> list:
    """いまは 2026-12-10（木）12:00。昨日 12/09（水）は乗務、12/05（土）は休み。"""
    def cfg(**over):
        return {"shifts.json": _base(**over)}

    b = cfg()
    k = cfg(lang="ja_kansai")
    now = "2026-12-10T12:00"
    y = "2026-12-09"
    log = {"log/pc-2026-12.jsonl": _pc_log("2026-12-01T09:00")}

    def run(cmd, *words, at=now):
        return {"now": at, "cmd": cmd, "words": list(words)}

    def state(day=y, **rec):
        return {"state.json": _state(day, **rec)}

    yasumi = state(status="yasumi", at=f"{y}T18:00")
    return [
        ("yasumi 12/9", b, run("yasumi", "12/9")),
        ("yasumi 昨日 有給", b, run("yasumi", "昨日", "有給")),
        ("yasumi 有給 12/09（語の順が逆）", b, run("yasumi", "有給", "12/09")),
        ("yasumi 12／9（全角の斜線）", b, run("yasumi", "12／9")),
        ("yasumi 2026-12-09", b, run("yasumi", "2026-12-09")),
        ("yasumi 一昨日 有給なし（印は付けない）", b, run("yasumi", "一昨日", "有給なし")),
        ("yasumi: もう休みになっている", {**b, **yasumi}, run("yasumi", "12/9")),
        ("yasumi 有給: もう休みなら印だけ付ける", {**b, **yasumi}, run("yasumi", "12/9", "有給")),
        ("yasumi 有給なし: もう休みなら印だけ外す",
         {**b, **state(status="yasumi", at=f"{y}T18:00", paid=True)}, run("yasumi", "12/9", "有給なし")),
        ("yasumi 有給なし: 印を外して目安に達した",
         {**cfg(absence={"window_shifts": 6, "warn_at": 1}), **log, **state(status="yasumi", at=f"{y}T18:00", paid=True)},
         run("yasumi", "12/9", "有給なし")),
        ("yasumi: 出たことになっている乗務を休みに直す（undo に前の状態）",
         {**b, **state(status="worked", at=f"{y}T16:05", sleep_at=f"{y}T08:10")}, run("yasumi", "12/9")),
        ("yasumi: 目安に達した", {**cfg(absence={"window_shifts": 6, "warn_at": 1}), **log}, run("yasumi", "12/9")),
        ("yasumi: 今月 2 回目", {**b, **state("2026-12-03", status="yasumi", at="2026-12-03T07:00")}, run("yasumi", "12/9")),
        ("yasumi 今日: まだ始まっていない乗務", b, run("yasumi", "今日")),
        ("yasumi 明日: 先の日付", b, run("yasumi", "明日")),
        ("yasumi 12/5: 休みの日は休めない", b, run("yasumi", "12/5")),
        ("yasumi: 古すぎる日付", b, run("yasumi", "2026-12-01", at="2027-03-10T12:00")),
        ("yasumi: 保持期間を変える（ちょうど境目は記録する）", cfg(retention={"days_keep": 9, "log_keep_days": 180, "events_keep_days": 365}),
         run("yasumi", "12/1")),
        ("yasumi 12/30: 年始に書いた 12 月は去年", b, run("yasumi", "12/30", at="2027-01-05T12:00")),
        ("deru 12/9", b, run("deru", "12/9")),
        ("deru 12/9 有給（印は関係ない）", b, run("deru", "12/9", "有給")),
        ("deru: もう出たことになっている", {**b, **state(status="worked", at=f"{y}T16:05")}, run("deru", "12/9")),
        ("deru: 休みを出たに直す（有給の印は消える）",
         {**b, **state(status="yasumi", at=f"{y}T18:00", paid=True)}, run("deru", "12/9")),
        ("deru 12/5: 休みの日に出た（出勤の例外日も足す）", b, run("deru", "12/5")),
        ("deru 12/5: 手動の休みを出勤に置き換える",
         {**b, "days.json": {"off": {"2026-12-05": "用事", "2026-12-11": "通院"}, "times": {"2026-12-14": {"start_target": "18:00"}}}},
         run("deru", "12/5")),
        ("deru 今日: まだ始まっていない乗務", b, run("deru", "今日")),
        ("関西弁: yasumi 昨日 有給", k, run("yasumi", "昨日", "有給")),
        ("関西弁: deru 12/9", k, run("deru", "12/9")),
        ("関西弁: もう休みになっている", {**k, **yasumi}, run("yasumi", "12/9")),
        ("関西弁: 休みの日は休めない", k, run("yasumi", "12/5")),
        ("関西弁: 先の日付", k, run("deru", "明日")),
    ]


def _days_set_rows() -> list:
    """いまは 2026-12-10（木）。"""
    b = {"shifts.json": _base()}
    now = "2026-12-10T12:00"

    def run(action, date=None, *note, at=now):
        return {"now": at, "action": action, "date": date, "note": list(note)}

    days = {"days.json": {"off": {"2026-12-11": "通院"}, "work": {"2026-12-12": "振替"},
                          "times": {"2026-12-14": {"start_target": "18:00"}}}}
    return [
        ("off 12/11（メモは空白でつなぐ）", b, run("off", "12/11", "通院", "予約")),
        ("work 12/12（メモなし）", b, run("work", "12/12")),
        ("rm 12/11（ほかの例外と times は残る）", {**b, **days}, run("rm", "12/11")),
        ("rm: 例外の無い日（days.json は空で作られる）", b, run("rm", "12/15")),
        ("off 12/12: 出勤の例外を休みに置き換える", {**b, **days}, run("off", "12/12", "やっぱり休み")),
        ("work 12/11: 休みの例外を出勤に置き換える", {**b, **days}, run("work", "12/11")),
        ("off 明日", b, run("off", "明日", "用事")),
        ("off 今日", b, run("off", "今日")),
        ("off 2026-12-18", b, run("off", "2026-12-18")),
        ("off 12／16（全角の斜線）", b, run("off", "12／16")),
        ("off 12/3: 7 日前までは今年", b, run("off", "12/3")),
        ("off 12/2: 8 日より前は来年", b, run("off", "12/2")),
        ("work 1/4: 年またぎ", b, run("work", "1/4", at="2026-12-28T12:00")),
        ("work 12/16: カレンダーの休みを出勤にする",
         {**b, "calendar.json": {"sources": {"A": {"off": {"2026-12-16": "公休"}, "work": {}}}}}, run("work", "12/16", "振替")),
        ("日付が無い", b, run("off")),
        ("日付が空", {**b, **days}, run("rm", "")),
        ("関西弁", {"shifts.json": _base(lang="ja_kansai")}, run("off", "12/11", "通院")),
        ("関西弁: 日付が無い", {"shifts.json": _base(lang="ja_kansai")}, run("work")),
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
            "phone": _cases(_tick_phone_rows(), _expect_tick),
            "confirm": _cases(_tick_confirm_rows(), _expect_tick),
            "enforce": _cases(_tick_enforce_rows(), _expect_tick),
            "calendar": _cases(_tick_calendar_rows(), _expect_tick),
            "maintenance": _cases(_tick_maintenance_rows(), _expect_tick),
            "release": _cases(_release_rows(), _expect_release),
        },
        "commands.json": {
            "status_text": _cases(_status_rows(), _expect_status_text),
            "schedule_text": _cases(_schedule_rows(), _expect_schedule_text),
            "kakunin_text": _cases(_kakunin_rows(), _expect_kakunin_text),
            "cmd_keyword": _cases(_cmd_keyword_rows(), _expect_record_cmd),
            "late_report": _cases(_late_report_rows(), _expect_record_cmd),
            "days_set": _cases(_days_set_rows(), _expect_day_cmd),
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
