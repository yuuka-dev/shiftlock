# ネイティブ版 区切り 1：状態と記録の画面（設計）

issue #38．言語と全体の構成は [native-app-plan.md](../../native-app-plan.md) で決定済み（C# / .NET 10，UI は WinUI 3）．仕様の正は [pc-spec.md](../../pc-spec.md)，ファイル形式は [data-format.md](../../data-format.md)．

## 1. 目的と理由

**最終の目的**: Python も WSL も無い PC に，ほかの人が入れて使えるアプリにする．

**この区切りで作るもの**: 今の状態が見えて，「寝る」「起きた」「行ってきます」「帰ってきた」をボタンで記録できるウィンドウ．本人が最初に欲しいと言った画面．

計画書（native-app-plan.md §8）では GUI は最後の段階（v12）だったが，順番を入れ替える．画面ごとに「その画面に要るロジックだけ」を C# に移し，区切りごとに動くものを出す（2026-11-17 決定）．

| # | できるもの | Python 版から移るもの |
|---|---|---|
| **1（これ）** | 状態と記録の画面（ウィンドウ） | 設定の読み込み，乗務日，時刻の逆算，フェーズ，記録の書き込み，取り消し |
| 2 | トレイ常駐と通知 | 見張り，通知，カレンダー同期 |
| 3 | 設定画面と初回ウィザード | 設定の書き込み |
| 4 | レポート | 集計と HTML |
| 5 | 配布 | インストーラと署名 |

Claude Code の hook（`shl.exe`）と強制モードは開発者・本人向けなので，この 5 つとは別に扱う．区切り 1 の間，見張り・通知・hook・設定の変更は Python 版のまま動かす．

## 2. ゴール・やらないこと・やってはいけないこと

### ゴール
- `%USERPROFILE%\shift-lock` のファイルを Python 版と同じ形式で読み書きし，Python 版と並べて動かせる．
- 同じ入力（設定・状態・時刻）に対して，Python 版と同じ判断・同じ記録・同じ返事を出す．これをテストで確かめる．
- Python 版や hook が書いた変更が，開いたままの画面に反映される．

### やらないこと（この区切りでは）
- トレイ常駐，通知，見張り，カレンダーの取得（`calendar.json` は読むだけ）．
- 設定の変更，初回ウィザード．設定ファイルが無い PC では「まだ設定がない」と出すだけ．
- 休む・事後報告・有給・例外日（`11/5 休み`）の操作．話し言葉の意図判定．
- レポート，hook，`shl.exe` の中身，インストーラ，英語の画面．

### やってはいけないこと
- **本番のデータを壊す**: 知らないキーを落とす，形式を変える，途中まで書いたファイルを残す．
- **Python 版と違う判断をする**（同じ時刻に片方は記録し片方は断る，など）．
- **強制モードの宣言・解除・設定を画面に置く**，強制を促す表示を出す（CLAUDE.md「絶対」）．
- 個人データや実データの値をテスト・テストベクタ・文書に入れる（`native/` は公開リポジトリにも出る）．

## 3. 作り方の候補と選んだもの

| 案 | 中身 | 判断 |
|---|---|---|
| **A. 画面ごとにロジックを C# に移す（採用）** | `shiftlock.Core` に必要な分だけ移し，WinUI 3 の画面が直接呼ぶ．データは Python 版と同じファイル | 目的（Python 無しで動く）に届く唯一の道．区切りごとに動くものが出る |
| B. Python を同梱する | 画面は C#，ロジックは同梱した Python を呼ぶ | 配布物が大きく警告も出やすい．計画書で外してある |
| C. 画面だけ先に作り Python 版を呼ぶ | `shiftlock.py status` の出力を表示 | 本人用には最速だが，目的のためには全部作り直しになる |

本人が A を選んだ（2026-11-17）．

## 4. 画面

値は架空．

```text
┌ shiftlock ───────────────────────────────────────┐
│ 11/19(木) 7:30                                    │
│                                                   │
│ 就寝準備の時間                                     │
│ 就寝目安まで あと 30 分                            │
│                                                   │
│ 次の乗務  11/19(木)                                │
│   就寝準備 7:20 → 就寝目安 8:00 → 最低ライン 10:00 │
│   出発 16:00（遅くとも 16:30）                     │
│   出庫 17:00（遅くとも 17:30）                     │
│   記録: まだなし                                   │
│                                                   │
│ [ 寝る ] [ 起きた ] [ 行ってきます ] [ 帰ってきた ] │
│                                                   │
│ ✓ 7:30 就寝で記録した                  [ 取り消す ] │
│                                                   │
│ 予定  木 乗務 / 金 乗務 / 土 休み / 日 休み / …     │
└───────────────────────────────────────────────────┘
```

### 4.1 表示する内容

| 場所 | 中身 | 元になる Python 版の処理 |
|---|---|---|
| 現在時刻 | `MM/DD(曜) H:MM` | — |
| フェーズ | 下の表の表示名と，次の節目までの残り時間 | `plan.current` |
| 次の乗務 | 就寝準備（`lastcall`）・就寝目安（`soft_close`）・最低ライン（`hard_close`）・出発（`depart_target`／`depart_latest`）・出庫（`start_target`／`start_latest`） | `commands.status_text` と同じ選び方: `current` の乗務 → `target_plan` → これから来る最初の乗務．無ければ「14 日以内に乗務はない」 |
| 記録 | その乗務の `status`（なし／起きた／出発済み／休み），`sleep_at`，`kitaku` があればその時刻 | `state.json` の `shifts[乗務日]` |
| 予定 | 今日から 14 日の 乗務／休み（7 日ずつ 2 行）．例外日には理由か印 | `days.schedule_text` と同じ判定（`is_workday` と `_days`） |
| 結果 | 直前に押したボタンの返事．取り消せる間は「取り消す」を出す | `records.do_*` の返事（先頭の `[shiftlock] ` は外す） |

| フェーズ | 表示名（標準語の案） | 残り時間の相手 |
|---|---|---|
| `free` | いまは自由時間 | なし |
| `lastcall` | 就寝準備の時間 | 就寝目安まで |
| `soft` | 就寝目安を過ぎている | 最低ラインまで |
| `hard` | 最低ラインを過ぎている | 出発（最終）まで |
| `go` | そろそろ出発 | 出発目安まで（過ぎていれば出発の最終まで） |
| `go_last` | 出発の最終が近い | 出庫の最終まで |

表示名の言い回しは実装時に `ja.py`／`ja_kansai.py` の既存の言い方（就寝準備・就寝目安・最低ライン）に合わせて決めてよい．

### 4.2 ボタン

- 4 つとも常に押せる．時刻で無効にしない．合わないときは Python 版と同じ判断で記録せず，その返事を結果に出す（例: 出発目安の 2 時間より前の「行ってきます」）．
- 対象の乗務は `plan.target_plan(cfg, now)`（`commands.cmd_keyword` と同じ）．

| ボタン | Python 版の処理 | 書くもの |
|---|---|---|
| 寝る | `records.do_neru` | `state.json` の `sleep_at`，`events.log` に `sleep_at` 行 |
| 起きた | `records.do_okita` | `status=okita` と `events.log`（休み／出発済みの乗務では `events.log` の行だけ．強制中で寝た記録が無ければ記録しない） |
| 行ってきます | `records.do_shuppatsu` | `status=worked`，メモ `shuppatsu` |
| 帰ってきた | `records.do_kitaku`（「ただいま」） | `kitaku`，`events.log` に `kitaku` 行 |

- 対象の乗務が無いとき（24 時間以内に乗務が無い）は Python 版と同じく `events.log` に `free-day` の行だけ書く（「行ってきます」は書かずに断る）．
- `events.log` のメモに書く出所は `app`（`set_mark` の出所．Python 版は `claude`／`phone`）．出所の語で処理を分けている箇所は Python 版に無い（確認済み）．「起きた」「行ってきます」のメモ（`neru HH:MM-HH:MM`，`no-gap`，`shuppatsu`）は Python 版と同じ文字列にする（`store.departures` などが読む）．

### 4.3 取り消す

- 4 つのボタンはどれも，**記録すると決まったときだけ**，書く前に `state.json` の `undo` に直前の状態を残す（断るとき・強制中で通さないときは `undo` に触らない．前の `undo` を消さないため）．形は `records.apply_intents` と同じ（`day`・`prev`・`at`・`what`）．`what` は `neru`／`okita`／`kitaku`／`worked`．
  - 対象の乗務が無い日の記録（`free-day`）は `day` が `-`．取り消すと Python 版と同じく `events.log` に `undo` 行が足されるだけで，`free-day` の行は残る．
  - Python 版はキーワード経由の 寝る・起きた・ただいま で `undo` を残さない．アプリは残す（ボタンは押し間違いがあるので）．形式は同じなので，Python 版の「今のなし」でも戻せる．
- 「取り消す」は `records.undo_fuzzy` と同じ処理（60 分以内，`enforce` は戻さない，`events.log` に `undo` 行）．
- 「取り消す」を出すのは，**このアプリがこの起動中に最後に書いた記録**で，`state.json` の `undo` が自分の書いたもの（`at` と `what` が一致）のままのときだけ．Python 版が書いた `undo` はアプリからは戻さない．
- 記録されなかった返事（断った，強制中で通さなかった）には「取り消す」を出さない．

### 4.4 更新

- 1 分ごとに読み直す．加えて実行フォルダの `shifts.json`・`state.json`・`days.json`・`calendar.json`・`STOP.txt` の変更を見張り，変わったら読み直す（短い間隔の連続した変更はまとめる）．
- 読み直しに失敗したとき（書き込み途中など）は前の表示を残し，次の機会にやり直す．

### 4.5 設定が無い・壊れている

- `shifts.json` が無い: 「まだ設定がない」と出し，ボタンは押せなくする．
- `shifts.json` が読めない・検証に落ちる（`config.load_config` と同じ条件）: 何が悪いかを出し，ボタンは押せなくする．アプリは落とさない．
- `state.json` などが無い・壊れている: Python 版の `_read` と同じく空として扱う．

### 4.6 文面と言葉づかい

- `shifts.json` の `lang` に従う（`ja`／`ja_kansai`）．**`lang` が無い・知らない値（`inm` を含む）のときは標準語 `ja`**．Python 版の既定は関西弁なので，`lang` を書いていない設定では両者の言葉づかいが変わる（ほかの人向けに標準語を既定にする，という判断）．
- 画面用の文（ボタン名・見出し・フェーズの表示名・設定が無いときの文）は `ja.py` と `ja_kansai.py` に `APP_` で始まる名前で足す．C# には直書きしない．
- C# は文面を **Python の言語ファイルから書き出した JSON** で読む（§5.3）．二重に持たない．

## 5. 構成

```text
native/
  shiftlock.Core/         ロジック．Windows API も UI も呼ばない．時刻は引数で受ける
  shiftlock.Core.Tests/   テストベクタを読んで Python 版と同じ結果になることを確かめる
  shiftlock.App/          WinUI 3 のウィンドウ．Core を呼んで表示するだけの薄い層（新規）
  shl/                    骨組みのまま（この区切りでは中身を作らない）
  shared/messages.ja.json, messages.ja_kansai.json   Python から書き出した文面（Core に埋め込む）
docs/test-vectors/*.json  Python から書き出したテストベクタ
pc/tools/export_native.py 文面とテストベクタを書き出す
```

- 骨組み（`Directory.Build.props`・`shiftlock.slnx`・Core・Core.Tests・`shl`・`native/README.md`・`.gitignore` の追記）はブランチ `chore/native-skeleton`（d69c5f8）にある．今の dev に載せ直す．同じコミットに入っている `docs/proposal-discord-kill.md` は**載せない**（別件．ブランチに残す）．
- 名前の付け方は `native/README.md` のとおり（フォルダは小文字，名前空間は `Shiftlock.Core`／`Shiftlock.App`）．

### 5.1 shiftlock.Core に移すもの

| Python 版 | 移す範囲 |
|---|---|
| `config.py` | `DEFAULTS` と `deep_merge`，`load_config` の検証，`calendar_days`，`merged_days`．書き込み・パス変換・`calendar_sources` は移さない |
| `timeutil.py` | `at`・`fmt`・`fmt_d`・`fmt_dur`・曜日 |
| `plan.py` | `no_sleep_intervals`・`latest_bedtime`・`Plan`・`is_workday`・`day_times`・`make_plan`・`plans`・`current`・`target_plan`．`phase_messages`・`last_past_plan` は移さない |
| `store.py` | `_read`・`_write`・`shift_status`・`set_shift_status`・`log_event`・`set_mark` |
| `records.py` | `do_kitaku`・`do_neru`・`do_okita`・`do_shuppatsu`・`undo_fuzzy`（`days_work_added` の付いた `undo` はアプリからは扱わない） |
| `usage.py` | `recent_long_gap`（`usage-pc.json` を読むだけ） |
| `enforce.py` | `stop_active`（`STOP.txt` があるか）だけ．`do_okita` の判定に要る |
| `days.py` | 14 日の予定（文字列ではなく，日ごとの値の並びで返す） |

加えて，画面に出す値をひとまとめにして返す入口を Core に置く（設定・状態・時刻 → 表示用の値）．App はそれを表示するだけにして，判断を App に書かない．テストはこの入口と記録の処理に集中する．

### 5.2 ファイルの読み書き

- 書くのは `state.json` と `events.log` だけ．
- `state.json` は読んで，必要なところだけ変えて，全体を書き戻す．**知らないキーはそのまま残す**（`enforce`・`pending`・今後足されるキー）．キーの順も保つ．
- 書き方は Python 版の `_write` に合わせる: 同じフォルダの `<名前>.tmp` に書いてから置き換える．UTF-8（BOM なし），改行は LF，インデント 1，日本語はエスケープしない．**JSON として同じ内容になることは必須**．バイト単位で同じになることは目標（差分が出ないように）．
- `events.log` は 1 行ずつ追記．タブ区切り，LF，時刻は分まで（`YYYY-MM-DDTHH:MM`）．
- 時刻は全部ローカル時刻．タイムゾーンを付けない．
- 排他はしない（Python 版同士と同じ．書き手が同時に `state.json` を書くと後勝ちになりうる）．既知の制約として残し，区切り 2（常駐）で見直す．

### 5.3 Python から書き出すもの

`pc/tools/export_native.py`（標準ライブラリのみ）が 2 種類を書き出す．どちらも生成物をコミットする．

1. **文面**: `ja.py`／`ja_kansai.py` から，C# が使うキー（`PREFIX`・`TIME_DUR_*`・`INTENT_LABEL`・`INTENT_LABEL_SEP`・`RECORDS_KITAKU*`・`RECORDS_NERU*`・`RECORDS_OKITA*`・`RECORDS_SHUPPATSU*`・`RECORDS_UNDO_*`・`CONFIG_*`・`DAYS_*`・`APP_*`）を `native/shared/messages.<言語>.json` に書く．`ja_kansai` は `ja` に上書きした結果を書く．プレースホルダは Python の `{name}` のまま（対象のキーに書式指定つきのものは無い．あれば書き出しでエラーにする）．C# 側は `{name}` を置き換える小さな処理を持つ．
2. **テストベクタ**: `docs/test-vectors/*.json`．合成の設定・状態・時刻を Python 版に通した結果．
   - 逆算: 設定 ＋ 日 → `Plan` の 7 つの時刻（`no_sleep`・日ごとの時刻の上書き・`start_latest < start_target` を含む）
   - 乗務日: 曜日・手動の例外・カレンダーの例外（新旧の形式）・旧形式の `shifts`
   - フェーズ: 設定 ＋ 状態 ＋ 時刻 → フェーズと乗務日（6 つのフェーズ全部，境界の時刻，`okita`／`yasumi`／`worked` の乗務）
   - 対象の乗務: `target_plan`
   - 記録: 設定 ＋ 始めのファイル（`state.json`・`usage-pc.json`・`STOP.txt` の有無）＋ 時刻 ＋ 操作 → 返事の文（`ja`），終わりの `state.json`，`events.log` に足された行．`do_okita` の分岐を全部（寝た記録あり／なし，離席の記録あり／なし／不明，休み・出発済みの乗務，強制中），早すぎる「行ってきます」，乗務の無い日，取り消し（60 分以内／超過，`enforce` を戻さない）
   - 画面の値: 設定 ＋ 状態 ＋ 時刻 → §5.1 の入口が返す値（「次の乗務」の選び方を含む）

- Python 側のテスト（pytest）で「書き出し直しても，コミットしてある生成物と同じ」を確かめる．文面や Python 版の判断を変えたのに書き出しを忘れると，ここで落ちる．
- アプリ特有の点（出所 `app`，4 つとも `undo` を残す）は Python 版に同じ入口が無い．書き出しのスクリプトの中で，Python 版の部品（`set_mark`・`do_*`・`undo_fuzzy`）を使ってアプリと同じ手順を組み，その結果を期待値にする．

### 5.4 shiftlock.App

- WinUI 3（Windows App SDK）．ウィンドウ 1 つ．この区切りではパッケージ化しない（インストーラは区切り 5）．
- 実行フォルダは既定で `%USERPROFILE%\shift-lock`．環境変数 `SHIFTLOCK_DIR` があればそちら（検証を本番のコピーで行うため）．
- 2 つ目を起動したら，新しいウィンドウは作らず既にあるほうを前に出す．
- 判断は Core に置く．App に書くのは表示，ボタン，タイマー，ファイルの見張りだけ．

## 6. 確かめ方

1. `shiftlock.Core.Tests`: テストベクタ全部が通る．
2. Python 側: pytest が全部通る（書き出しのずれが無い）．`inm.py` の無い環境でも通る．
3. 本番データの**コピー**に `SHIFTLOCK_DIR` を向けてアプリを起動し，同じコピーに対する Python 版の `status` と見比べる（フェーズ・次の乗務の時刻・記録・予定）．本番の実行フォルダに向けては確かめない．値は文書やテストに書き写さない．
4. コピーの上でボタン 4 つと取り消しを押し，`state.json` の差分と `events.log` の追記を見る．Python 版の `status` がその結果を読めること，Python 版の「今のなし」で戻せること．
5. Python 版でコピーに記録を書き，開いたままのアプリに反映されること．

## 7. 決めたこと（本人）

- 最初の区切りは「表示 ＋ 記録ボタン 4 つ」．ボタンは 寝る・起きた・行ってきます・帰ってきた．
- 画面の並びは §4 の案でよい．
- 取り消すボタンを付ける．ボタンは常に押せる．文面は標準語を既定．自動で更新する．強制モードは置かない．設定が無いときは知らせるだけ．出所は `app`．

## 8. 作る人に任せること

- Windows App SDK の版，XAML の組み方，MVVM の道具を使うか．
- Core の型と入口の名前，テストベクタの JSON の細かい形とケースの数（§5.3 の範囲を満たすこと）．
- `APP_*` の文の言い回し（既存の言い方に合わせる）．
- ファイルの見張りをまとめる間隔．
- 見た目（余白・文字の大きさ・配色）．Windows の標準の見た目に従い，明るい／暗い配色の両方で読めること．

## 9. 区切り 2 以降に送ること

- トレイ常駐にしたときのプロセスの分け方（計画書は Agent と App を別プロセスにしている）．
- 書き手が増えたときの `state.json` の排他．
- 文面のうち Python 版・Claude Code を前提にした言い回し（「ロック解除」「kaijo.cmd」など）の，ほかの人向けの見直し．
- 実行フォルダの場所（ほかの人の PC でも `%USERPROFILE%\shift-lock` でよいか）．
