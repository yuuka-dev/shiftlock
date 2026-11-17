"""対話入力の部品（設定メニューと強制モードの同意で共用）。StdIO を差し替えればテストできる。"""
from __future__ import annotations

from datetime import datetime

from . import i18n as M
from .config import WEEKDAYS


WD_IN = {"月": "mon", "火": "tue", "水": "wed", "木": "thu", "金": "fri", "土": "sat", "日": "sun"}   # 入力の語彙


WD_OUT = {v: k for k, v in WD_IN.items()}


def _wd_str(ws: list[str]) -> str:
    return "".join(WD_OUT[w] for w in WEEKDAYS if w in ws)


def _ask(io, prompt: str, cur, conv):
    while True:
        ans = io.input(M.CONSOLE_ASK.format(prompt=prompt, cur=cur)).strip()
        if ans == "":
            return None
        try:
            return conv(ans)
        except Exception as e:
            io.print(M.CONSOLE_ERROR.format(err=e))


def _hm_conv(s: str) -> str:
    s = s.replace("：", ":")
    if ":" not in s:
        s = f"{int(s)}:00"
    datetime.strptime(s, "%H:%M")
    h, m = s.split(":")
    return f"{int(h):02d}:{m}"


def _num_conv(lo: float, hi: float):
    def f(s: str):
        v = float(s)
        if not lo <= v <= hi:
            raise ValueError(M.CONSOLE_NUM_RANGE.format(lo=lo, hi=hi))
        return int(v) if v == int(v) else v
    return f


def _wd_conv(s: str) -> list[str]:
    out = [WD_IN[c] for c in s if c in WD_IN]
    if not out and s not in ("なし", "-"):
        raise ValueError(M.CONSOLE_WD_HINT)
    return [w for w in WEEKDAYS if w in out]


class StdIO:
    def input(self, p):
        return input(p)

    def print(self, *a):
        print(*a)


def _list_edit(io, title: str, items: list[str], conv=lambda s: s) -> list[str] | None:
    items = list(items)
    while True:
        io.print(M.CONSOLE_LIST_TITLE.format(title=title))
        for i, x in enumerate(items, 1):
            io.print(M.CONSOLE_LIST_ITEM.format(i=i, x=x))
        if not items:
            io.print(M.CONSOLE_LIST_EMPTY)
        a = io.input(M.CONSOLE_LIST_PROMPT).strip()
        if a == "":
            return items
        if a.lower() == "a":
            v = io.input(M.CONSOLE_LIST_ADD).strip().strip('"')
            if v:
                try:
                    items.append(conv(v))
                except Exception as e:
                    io.print(M.CONSOLE_ERROR.format(err=e))
        elif a.lower().startswith("d") and a[1:].isdigit() and 1 <= int(a[1:]) <= len(items):
            items.pop(int(a[1:]) - 1)
