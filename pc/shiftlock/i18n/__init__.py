"""ユーザー向け・Claude 向けの文面。言語は shifts.json の lang で選ぶ（load_config が set_lang を呼ぶ）。

- 土台は ja（標準語）。選んだ言語ファイルにあるキーだけ上書きするので、足りないキーは標準語のまま出る
- 他のモジュールは `from . import i18n as M` → `M.X`。M.X は使う場所で毎回引く（モジュール直下で `X = M.Y` と束縛すると
  import 時の言語で固定されて lang が効かない。tests/test_i18n_lang.py が見張る）
- 言語を足すとき: i18n/<name>.py を置いて BUILTIN_LANGS に名前を足し、ja.py の LANG_LABEL に表示名を足す。キーは ja.py と
  同じ名前・同じプレースホルダで（足りない分は標準語のまま出る）
- 任意の言語（OPTIONAL_LANGS）はリポジトリで配らない。i18n/<name>.py を手元に置いた環境でだけ選べる。ファイルが無い環境では
  lang に書かれていても既定に戻る（#33）
"""
from __future__ import annotations

import importlib
import importlib.util

from . import ja as _base

BUILTIN_LANGS = ("ja", "ja_kansai")  # いつも配る言語
OPTIONAL_LANGS = ("inm",)            # 配らない言語（淫夢語録）。ファイルがある環境でだけ選べる


def available_langs(optional=OPTIONAL_LANGS) -> tuple:
    """選べる言語。いつも配る言語のあとに、ファイルが手元にある任意の言語を並べる。"""
    return BUILTIN_LANGS + tuple(n for n in optional if importlib.util.find_spec(f"{__name__}.{n}") is not None)


LANGS = available_langs()            # shifts.json の lang に書ける値（設定メニュー 20 の並び順）
DEFAULT_LANG = "ja_kansai"           # lang が無い・知らない値のとき
LANG = DEFAULT_LANG                  # いま選ばれている言語（set_lang が更新する）


def set_lang(name) -> str:
    """文面を name の言語にする。知らない名前（None や設定ミス、ファイルが無い任意の言語）は DEFAULT_LANG。実際に選んだ名前を返す。"""
    global LANG
    if name not in LANGS:
        name = DEFAULT_LANG
    words = {k: v for k, v in vars(_base).items() if k.isupper()}
    if name != "ja":
        mod = importlib.import_module(f".{name}", __name__)
        words.update({k: v for k, v in vars(mod).items() if k.isupper()})
    globals().update(words)
    LANG = name
    return name


set_lang(DEFAULT_LANG)
