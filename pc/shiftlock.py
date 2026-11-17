#!/usr/bin/env python3
"""shiftlock のエントリ。実体は同じフォルダの shiftlock/ パッケージ（全体の説明は shiftlock/__init__.py）。

このファイルの名前と場所は変えへん（hook のコマンド・*.cmd・shiftlock-profile.ps1・guard.ps1・deploy.ps1 が指してる）。
python3 shiftlock.py で起動すると sys.path[0] がこのフォルダになり、同名のパッケージ shiftlock/ が優先して読まれる。
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))   # -P / PYTHONSAFEPATH で起動されても同じフォルダのパッケージを読む

try:
    from shiftlock.cli import main
except ImportError as e:   # パッケージが無い（deploy / install-hook の途中など）。hook はプロンプトを止めへん（exit 2 にしない）
    # ここはパッケージが読めへんときの文なので i18n（shiftlock/i18n/ja.py）には置けへん
    sys.stderr.write(f"[shiftlock] 本体の shiftlock/ フォルダが読めへん（{e}）。deploy.ps1 か install-hook をやり直してな。\n")
    sys.exit(1)

if __name__ == "__main__":
    sys.exit(main())
