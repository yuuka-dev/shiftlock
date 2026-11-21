"""時刻と Windows 側の値を指定して、見張り（tick）を 1 回だけ行う。ネイティブ版との突き合わせ用。

ネイティブ版の `shiftlock-agent.exe --tick-once` と同じ引数・同じ形の出力（キーは名前順、日本語はそのまま、インデント 1）。
通知は出さず、強制も実行しない（結果の JSON を出すだけ）。**渡したフォルダのファイルは書き換える**ので、
本番フォルダのコピーに向けて使う。本番の実行フォルダ（%USERPROFILE%\\shift-lock、WSL からは /mnt/c/Users/<名前>/shift-lock）を
指していたら、何もせず終了コード 2 で断る。

使い方:
    python3 pc/tools/tick_once.py --dir <フォルダ> --now 2026-12-01T09:00 --idle 30 --game 0 --uptime 7200 [--dry-run]
    → {"actions": [...], "sound": {"max_seconds": 20, "volume": 0.8}, "warnings": [...]}
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent   # pc/
sys.path.insert(0, str(ROOT))

import shiftlock as sl  # noqa: E402

REFUSED = 2   # 引数がおかしい・本番のフォルダを指している


def is_production(d: Path) -> bool:
    """本番の実行フォルダか。Windows は %USERPROFILE%\\shift-lock、WSL は /mnt/<ドライブ>/Users/<名前>/shift-lock、ほかはホームの下。"""
    here = os.path.normcase(os.path.abspath(str(d))).rstrip("\\/")
    homes = [os.environ.get("USERPROFILE"), str(Path.home())]
    for home in homes:
        if home and here == os.path.normcase(os.path.abspath(os.path.join(home, "shift-lock"))).rstrip("\\/"):
            return True
    return re.fullmatch(r"/mnt/[a-z]/users/[^/]+/shift-lock", here.replace("\\", "/"), re.IGNORECASE) is not None


def render(result: dict) -> str:
    """出力の形（ネイティブ版と同じ）。"""
    return json.dumps(result, ensure_ascii=False, indent=1, sort_keys=True)


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--dir", required=True)
    ap.add_argument("--now", required=True)
    ap.add_argument("--idle", required=True, type=float)
    ap.add_argument("--game", required=True, type=int, choices=(0, 1))
    ap.add_argument("--uptime", type=float, default=None)
    ap.add_argument("--dry-run", action="store_true")
    try:
        a = ap.parse_args(argv)
        now = datetime.fromisoformat(a.now)
    except (SystemExit, ValueError) as e:
        if isinstance(e, SystemExit) and e.code == 0:   # --help
            return 0
        print(f"tick_once: 引数が読めない: {e}", file=sys.stderr)
        return REFUSED
    d = Path(a.dir)
    if is_production(d):
        print(f"tick_once: 本番の実行フォルダには使わない（コピーに向けて使う）: {d}", file=sys.stderr)
        return REFUSED
    result = sl.run_tick(d / "shifts.json", a.idle, bool(a.game), now, a.uptime, None, a.dry_run)
    print(render(result))
    return 0


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(encoding="utf-8", newline="\n")
    except Exception:
        pass
    raise SystemExit(main())
