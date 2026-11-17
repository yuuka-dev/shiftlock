"""ネイティブ版（native/）が読むファイルを、Python 版から書き出す。

いま書き出すもの:
- native/shared/messages.<lang>.json  画面と記録の文面（pc/shiftlock/i18n/ の言語ファイルから。C# には文を直書きしない）

使い方（リポジトリ直下で。WSL でも Windows でも同じ結果になる）:
    python3 pc/tools/export_native.py           書き出す
    python3 pc/tools/export_native.py --check   コミット済みの生成物と違えば終了コード 1（何も書かない）

文面を足した・直したら書き出し直してコミットする（tests/test_export_native.py が古い生成物を見つける）。

形式:
- messages.<lang>.json は {キー: 文字列 または {キー: 文字列}}。キーは名前順
- プレースホルダは {name} だけ。{{ と }} は波かっこそのもの（Python の書式のエスケープ）で、直さずに残す。C# 側が解釈する
- UTF-8（BOM なし）・LF・インデント 1・日本語はエスケープしない
"""
from __future__ import annotations

import argparse
import json
import string
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent   # pc/
REPO = ROOT.parent
sys.path.insert(0, str(ROOT))
from shiftlock import i18n  # noqa: E402

LANGS = ("ja", "ja_kansai")   # 書き出す言語（リポジトリで配る言語だけ。任意の言語は書き出さない）
SHARED = REPO / "native" / "shared"

# 書き出すキー。ネイティブ版が使う文だけに絞る（設定メニューや CLI の説明は持っていかない）
EXACT = ("PREFIX", "INTENT_LABEL", "INTENT_LABEL_SEP", "DAYS_NOTE", "DAYS_EXCEPTION_MARK")
PREFIXES = ("TIME_DUR_", "RECORDS_KITAKU", "RECORDS_NERU", "RECORDS_OKITA", "RECORDS_SHUPPATSU", "RECORDS_UNDO_",
            "CONFIG_", "APP_")


def _wanted(key: str) -> bool:
    return key in EXACT or key.startswith(PREFIXES)


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


def render(obj) -> str:
    """生成物の書き方（インデント 1・日本語はそのまま・末尾に改行）。"""
    return json.dumps(obj, ensure_ascii=False, indent=1) + "\n"


def outputs() -> dict:
    """書き出す全ファイルの パス → 中身。"""
    return {SHARED / f"messages.{lang}.json": render(build_messages(lang)) for lang in LANGS}


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
