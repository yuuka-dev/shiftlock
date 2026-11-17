# shiftlock

乗務（タクシー夜勤）の前にちゃんと寝て、ちゃんと出るための見張り役。
開発・ゲーム・配信をやりつつ、乗務予定日は寝てから出る、をシステムで支える。PC は止めへん。声かけと、最低ラインを切ったときの Claude Code の新規プロンプト拒否だけ。

| フォルダ | 中身 | 状態 |
|---|---|---|
| [`pc/`](pc/) | Windows ＋ WSL 版（Claude Code hook、5 分おきの見張り、訪看さん向けレポート、iPhone ショートカット連携） | 運用中 |
| [`app/`](app/) | Android 版（KMP ＋ Compose Multiplatform）。iOS は Mac が来てから | 計画中 |
| [`docs/`](docs/) | [PC 版の仕様とロジック](docs/pc-spec.md)、[v11 の設計書](docs/pc-v11-design.md)、[v11 の作業記録](docs/pc-v11-worklog.md)、[ネイティブ化の計画](docs/native-app-plan.md)、[データ形式の決まり](docs/data-format.md)、[Android 版の計画](docs/android-plan.md) | |

## PC 版の使い方
詳しくは [pc/README.md](pc/README.md)。リポジトリから実行フォルダ（`%USERPROFILE%\shift-lock`）への反映は:

```powershell
cd C:\dev\repo\private\shiftlock\pc
powershell -ExecutionPolicy Bypass -File .\deploy.ps1          # コードを反映して hook を入れ直す
powershell -ExecutionPolicy Bypass -File .\deploy.ps1 -Full    # タスクとプロファイルも登録し直す
```

## テスト
WSL 側で仮想環境を作って実行する（システムの python3 には入れない）。
```bash
python3 -m venv .venv && .venv/bin/pip install pytest   # 初回だけ
source .venv/bin/activate && cd pc && python3 -m pytest -q tests
```

## 個人データについて
記録（ログ・状態・気分・レポート）と個人設定（`shifts.json`：カレンダーの秘密のアドレス入り）は実行フォルダにだけ置き、リポジトリには入れない。`.gitignore` でも弾いてある。

## ライセンス
Copyright (C) 2026 Proletariat Yuuka

[GNU General Public License v3.0](LICENSE)（GPL-3.0-only）。使う・直す・配るのは自由。ただし直したものを配るときは、同じ GPL-3.0 でソースも公開すること。無保証。
