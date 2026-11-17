# CLAUDE.md（このリポジトリで作業する Claude Code 向け）

## 絶対
- 個人データをコミットしない：`shifts.json`、`log/`、`state.json`、`days.json`、`calendar.json`、`learned.json`、`events.log`、`usage-*.json`、`reports/`、`report.html`、`mood.csv`。設定の例は `pc/shifts.example.json` だけ
- 実行フォルダ `%USERPROFILE%\shift-lock`（WSL からは `/mnt/c/Users/<ユーザー名>/shift-lock`）は本番。直接編集せず、`pc/` を直して `pc/deploy.ps1` で反映する
- hook（UserPromptSubmit）で exit 2 を返すとユーザーのプロンプトが消える。**失敗時に exit 2 にならない**こと（本体が見つからない・設定が壊れてる → exit 0 か 1）。hook を変えたら Git Bash 経由（Windows 版 Claude Code）と WSL 経由の両方で確認する
- PC を止める処理（ロック／スリープ／シャットダウン）は、**ユーザー本人が `shl enforce on` を対話的に実行し、表示された内容に `y` で同意した乗務にだけ**入れる。既定はオフ。設計は [docs/pc-v11-design.md](docs/pc-v11-design.md) の B
  - 強制を促す通知・質問・Claude への文脈は入れない。出してよいのは「宣言済みの強制の予告」と「実行・解除の報告」だけ
  - Claude Code（hook・会話・Bash）からは強制の宣言・解除・設定変更をしない。`--yes` のような非対話で同意を省く入口も作らない（同意は対話入力のみ）
  - ゲーム・アプリを個別に止める処理は入れない。それ以外は声かけだけ

## ファイルの決まり
- `*.ps1` は **UTF-8 BOM 付き・CRLF**（Windows PowerShell 5.1 が日本語を読めるように）
- `*.cmd` は **ASCII のみ・CRLF**（cmd は読み込み時の文字コードが違うので日本語を書かない）
- `*.py` は UTF-8・LF、標準ライブラリのみ（Python 3.8+）
- `pc/shiftlock.py` はエントリだけ。本体は `pc/shiftlock/`（役割ごとのモジュール、依存は基盤 → 判断 → 記録 → 入口の一方向）。ユーザー向け・Claude 向けの文面は `pc/shiftlock/i18n/ja.py` の定数に置き、コードには書かない（入力の語彙・ファイルに書く値・設定の既定値・日付の表記は置かない）。決まりは [docs/superpowers/specs/2026-11-04-pc-refactor-modules-design.md](docs/superpowers/specs/2026-11-04-pc-refactor-modules-design.md)
- PC 版とスマホ版のやりとりは [docs/data-format.md](docs/data-format.md) の形式だけ。形式を変えるならまずそこを直す
- `native/`（C#）の文面は C# に直書きしない。`pc/shiftlock/i18n/` から書き出した JSON（`native/shared/messages.*.json`）で読む。`*.cs`・`*.xaml` は UTF-8・LF。判断は `shiftlock.Core` に置き、画面（`shiftlock.App`）には書かない。画面に強制モードの宣言・解除・設定を置かない

## テスト
- WSL で `source .venv/bin/activate && cd pc && python3 -m pytest -q tests`（全部通してからコミット）。`.venv` はリポジトリ直下に WSL 側で作った仮想環境（`python3 -m venv .venv && .venv/bin/pip install pytest`）。システムの python3 には入れない
- 時刻は全部ローカル時刻（JST）、テストでは `now` を引数で渡す
- C#（`native/`）は Windows 側で `dotnet test native`。Python 版と同じ結果になることを `docs/test-vectors/*.json` で確かめている
- Python 版の判断や、ネイティブ版が使う文面（`pc/tools/export_native.py` の対象のキー）を変えたら `python3 pc/tools/export_native.py` と `python3 pc/tools/make_test_vectors.py`（逆算・フェーズのシナリオ `plan-scenarios.json`）で文面とテストベクタを書き出し直し、一緒にコミットする（ずれは pytest が落ちて知らせる）
- ネイティブ版の画面を動かして確かめるときは、環境変数 `SHIFTLOCK_DIR` を合成データか本番のコピーのフォルダに向ける。本番の実行フォルダに向けて動かさない

## 文体
- ユーザー向け・Claude 向けの文面は `pc/shiftlock/i18n/` の言語ファイルに置く（コードに直書きしない）。既定は関西弁 `ja_kansai.py`、土台は標準語 `ja.py`。文を足すときは `ja.py`（標準語）と `ja_kansai.py`（関西弁。既存のメッセージに合わせる）の両方に同じ名前で足す。`inm.py`（淫夢語録）は Gemini が足すので触らなくてよい（無いキーは標準語で出る）。`inm.py` はリポジトリで配らない任意の言語（`.gitignore` で除外。コミットしない）。無い環境でも動く・テストが通ること

## ドキュメント
- PC 版の仕様・ロジック（時刻の逆算、フェーズ、hook の入出力と処理順、tick、レポート）は [docs/pc-spec.md](docs/pc-spec.md)。動作を変えたらここも直す
- v11 で足す機能（事後報告・強制モード・通知音・カレンダー複数・shl ヘルプ）の設計は [docs/pc-v11-design.md](docs/pc-v11-design.md)。上の「絶対」項目は 2026-11-03 に改訂済み
- ネイティブアプリ化の言語・構成は [docs/native-app-plan.md](docs/native-app-plan.md)

## 公開
- 開発はこのリポジトリ（非公開）。公開リポジトリには `tools/publish-public.sh` で中身を 1 コミットとして積む（`inm.py` を外し、個人データの名前と `.publish-deny` の語を確かめる）。スクリプトは push しない。push はユーザーに確認してから
