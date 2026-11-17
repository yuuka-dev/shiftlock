# ネイティブ版 区切り 2：トレイ常駐と見張り（設計）

言語と全体の構成は [native-app-plan.md](../../native-app-plan.md)（C# / **.NET 10**．2026-11 時点の最新 LTS．区切り 1 と同じ），仕様の正は [pc-spec.md](../../pc-spec.md)（§7 見張り，§8 声かけ，§9 カレンダー，§10 整理，§17 強制モードほか），ファイル形式は [data-format.md](../../data-format.md)．区切り 1 の設計は [2026-11-17-native-status-window-design.md](2026-11-17-native-status-window-design.md)．

issue #4．開発の場所は公開リポジトリ．ブランチは main から切った `feature/native-agent`．コミットには `(#4)` を付ける．

## 1. 目的と理由

**この区切りで作るもの**: トレイに常駐する `shiftlock.Agent`．いま 5 分おきに動いている `guard.ps1`（PowerShell）と Python の `tick` を置き換え，**見張りと通知に Python も PowerShell も要らない**状態にする．

**なぜ今か**: 本人が次に欲しい機能は「NTAPI による稼働計測」と「宣言した乗務での減速・一時停止」（§9）．どちらも常に動いている土台が要る．その土台を，計画どおりの区切り 2（トレイ常駐と通知）として先に完成させる，と本人が決めた（2026-11-17）．

区切りの順番（本人決定）:

| 順 | できるもの | 設計書 |
|---|---|---|
| **①（これ）** | トレイ常駐と見張り（`guard.ps1` と `tick` の置き換え） | この文書 |
| ② | NTAPI の稼働計測（exe 単位の記録，レポートはカテゴリ単位） | 別に書く |
| ③ | 減速・一時停止・解除（強制モードの動作に `throttle`／`suspend` を足す） | 別に書く |

## 2. ゴール・やらないこと・やってはいけないこと

### ゴール

- `shiftlock.Agent` が 5 分おきに見張りを行い，Python の `tick` と**同じ入力に対して同じ通知・同じファイルの書き込み**をする（テストベクタと，本番フォルダのコピーでの突き合わせで確かめる）．
- 通知（トースト／ポップアップ／読み上げ／通知音）と，宣言済みの強制の実行（ロック／スリープ／シャットダウン）を Agent が行う．
- トレイのアイコンでいまのフェーズが分かり，メニューからウィンドウ（`shiftlock.exe`）を開ける．緊急解除ができる．
- タスクスケジューラの `shiftlock-guard` を止めて Agent に切り替えられる．戻すこともできる．

### やらないこと（この区切りでは）

- 強制モードの**宣言**（`shl enforce on`）．Python 版のまま．C# に宣言の入口を作らない．
- Claude Code の hook，設定の変更，レポートの生成（タスク `shiftlock-report` はそのまま）．Python 版のまま．
- NTAPI の計測，減速，一時停止（②③）．
- Agent を守るサービス（native-app-plan.md §5.2 の段階 2），インストーラ，MSIX，署名（区切り 5．この区切りでは「発行して置く」手順だけ用意する．§6.2）．
- リモート操作中の判定を足すこと（今の `guard.ps1` は `remote` を渡していない．同じく常に false）．

### やってはいけないこと

- **Python の `tick` と Agent が同時に見張ること**（二重の通知，`usage-pc.json`・`notified.json` の取り合い）．
- **5 分より短い間隔で `log/pc-*.jsonl` に行を足すこと**．レポートは 1 行を 5 分として数える（`TICK_MIN = 5`）．行が増えると PC／ゲームの時間が水増しされる．
- 強制を促す通知・質問を出すこと．出してよいのは「宣言済みの強制の予告」と「実行・解除の報告」だけ（今と同じ）．
- 画面（トレイを含む）に強制モードの**宣言・設定**を置くこと．
- 文面を C# に直書きすること．
- 本番の実行フォルダに向けて開発中の Agent を動かすこと（切り替えの手順を除く）．
- 見張りの失敗で Agent が落ちること．1 回の見張りの中の例外はログに書いて次の回に進む．

## 3. 作り方の候補と選んだもの

**どこまで移すか**

| 案 | 内容 | 判断 |
|---|---|---|
| 殻と Windows 側だけ移し，判断は Python の `tick` を呼ぶ | 早いが Python が要り続ける | 不採用 |
| **`tick` の判断も全部 C# に移す** | 見張りに Python が要らなくなる．量は区切り 1 以上 | **採用（本人）** |
| 通知の判断だけ移す | 判断が 2 か所に分かれる期間ができる | 不採用 |

**プロセスの形**

| 案 | 内容 | 判断 |
|---|---|---|
| ウィンドウ（`shiftlock.exe`）を常駐させる | 閉じる・落ちると見張りが止まる | 不採用 |
| **WinUI を使わない軽い常駐 `shiftlock.Agent` を別プロセスで作る** | 画面が落ちても見張りが続く．③ の一時停止の復旧にも効く | **採用** |
| Windows サービス | セッション 0 からは通知もロックも出せない | 不採用 |

**切り替え方**

| 案 | 内容 | 判断 |
|---|---|---|
| **コピーで突き合わせてから一度に切り替える** | 区切り 1 と同じ．戻すのはタスクを有効に戻すだけ | **採用（本人）** |
| 数日並走（Agent は記録だけ） | 読み取り専用の仕掛けが要り，数日遅れる | 不採用 |
| 機能ごとに少しずつ | 同じ状態ファイルを交互に書く | 不採用 |

## 4. Agent の見た目と動き

### 4.1 トレイ

```
[アイコン]  ← フェーズで見た目が変わる．ホバーで 1 行（例:「就寝目安まで 40 分」）
├ 状態を開く      （同じフォルダの shiftlock.exe を起動．起動済みなら前に出る）
├ 緊急解除        （確認のダイアログ →「はい」で解除）
└ 終了            （確認なし．Agent が終われば見張りも強制も止まる）
```

- アイコンの種類は「平常／lastcall・soft（注意）／hard・go_last（警告）／設定エラー／見張りを止めている（§6.2）」の区別がつけばよい．ホバーの 1 行は区切り 1 の `Snapshot` が出す残り時間の行を使う．
- アイコンのダブルクリックは「状態を開く」と同じ．
- メニューとダイアログの文面は言語ファイルから（§5.4）．
- `shifts.json` が無い・壊れているとき: Agent は終了せず，設定エラーのアイコンとホバーで知らせ，見張りは行わない．次の回に読み直す．

### 4.2 緊急解除（本人決定: トレイに置く）

- 「緊急解除」→ 確認（はい／いいえ．既定は「いいえ」）→「はい」で，今の `guard.ps1 -Release` と同じことをする: 予約済みシャットダウンの中止（`shutdown /a`）を最初に → `STOP.txt` に 1 行追記 → 同意済みで未解除の宣言に `released_at` を付け `events.log` に `enforce_release` を書く（理由は `tray`）．各手順は独立に行い，どれかが失敗しても次へ進む．
- 結果は通知で 1 回知らせる（解除の報告）．
- `kaijo.cmd`／`shl kaijo`／`STOP.txt` を手で置く，は今までどおり効く（どれもファイルを置くだけなので Agent はそれを読む）．
- 宣言が無いときも押せる（メニューは常に有効）．動きは Python の `enforce_release` と同じ: `STOP.txt` を置き，`events.log` に乗務日 `-` で `enforce_release` を書く．
- 再開（`shl enforce resume`）と宣言はトレイに置かない．
- これに合わせて `CLAUDE.md` の決まりを「画面に強制モードの宣言・設定を置かない．解除は置いてよい」に直す．

### 4.3 見張りの周期

- 起動して 1 分後に 1 回目，以後 5 分おき（今のタスクと同じ）．
- 直前の見張りから 5 分たっていなければ行わない（スリープからの復帰で続けて走らないようにする）．「直前」は **`log/pc-YYYY-MM.jsonl` の最後の行の時刻**で見る（Agent を起動し直した直後や，落ちた直後でも行が重ならない）．起動 1 分後の 1 回目もこの判定を通す．Python の `tick` 自体には間引きが無い（タスクの間隔に任せている）ので，これは Agent 側だけの決まり．
- 復帰後は「前回から 5 分以上たっていれば，次のタイマーを待たずに 1 回」行ってよい．
- 見張りは UI のスレッドで行わない（カレンダーの取得で最長 10 秒，通知音で最長 20 秒かかる）．前の見張りが終わっていなければ次は飛ばす．

### 4.4 1 回の見張りでやること

Python の `run_tick()`（`pc/shiftlock/tick.py`）と同じ順番・同じ条件．

1. Windows 側の値を取る: アイドル秒数（`GetLastInputInfo`），ゲームが起動中か（§5.2），起動からの秒数．
2. `Tick.Run`（Core．§5.1）を呼ぶ → 通知と強制の実行の一覧，警告，通知音の音量と最長秒数．
3. 警告を `agent.log` に書く．
4. 一覧を順に出す: 通知は「音 → 通知 → 読み上げ」の順．強制の実行は，直前に `STOP.txt` をもう一度見てから（音を鳴らしたならその後にも見る）．

### 4.5 通知と強制の実行（今の `guard.ps1` と同じ意味）

| 種類 | 動き | 失敗したとき |
|---|---|---|
| `toast` | Windows のトースト．自前で音を鳴らせたときは既定音を消す | `popup` に切り替え |
| `popup` | 最前面のメッセージ．20 秒で自動で閉じる．警告アイコン | ログだけ |
| `speak: true` | 読み上げ（`♡` は除く） | ログだけ |
| 通知音 | 音声ファイルを音量 `sound.volume`，最長 `sound.max_seconds` 秒で鳴らす．WAV／MP3／M4A／WMA | ログだけ．通知は続ける |
| `enforce` の `lock` | 画面のロック | ログだけ |
| `enforce` の `sleep` | スリープ | ログだけ |
| `enforce` の `shutdown` | 60 秒後のシャットダウンを予約（`kaijo` で中止できる） | ログだけ |

通知に付けるアプリ名は `shiftlock`（今は PowerShell の名前を借りている）．

トーストの「失敗」は**例外が出たとき**だけを指す（集中モードや通知オフで見えない場合の検出はしない．今と同じ）．ただし「名前の登録が無くて何も出ない」状態は失敗の検出に頼らず，登録（§6.2）で防ぎ，§6.1 の 2 で実際に出ることを確かめる．

### 4.6 起動と二重起動

- 同時に動く Agent は 1 つだけ．2 つ目は何もせず終わる．
- ログオン時に自動で起動する（§6.2 の切り替えで登録）．

## 5. 構成

```
native/
  shiftlock.Core/          判断を足す（§5.1）．Windows API を呼ばない
  shiftlock.Core.Tests/    tick.json などのベクタと突き合わせる
  shiftlock.Agent/         新規．トレイ・タイマー・Windows 側の値・通知・強制の実行（§5.2）
  shiftlock.Agent.Tests/   新規．Agent のうち純粋な部分と，差し替えた Windows 側での流れ
  shiftlock.App/           変更なし（Agent から起動されるだけ）
```

依存は `Agent → Core` の一方向．`App` と `Agent` は互いを参照しない（やりとりはファイルと，`shiftlock.exe` の起動だけ）．

### 5.1 shiftlock.Core に移すもの

Python の実装を正とし，同じ名前の役割で移す．どれも `now` を引数で受ける．

| Python | 役割 | 書くファイル |
|---|---|---|
| `tick.run_tick` | 1 回の見張りの本体．順番は §4.4 | `log/pc-YYYY-MM.jsonl`，`usage-pc.json` |
| `usage`（`pc_active`・`shift_away`・`due_nag`・`pc_gaps`・`recent_long_gap`・`game_idle_limit`） | 触っているかの判定，離席区間，連続稼働の声かけ | `usage-pc.json` |
| `notify`（`_once`・`NOTIFY_EVERY`・`resolve_sound`・`attach_sounds`） | 通知の間引き，通知音の解決 | `notified.json` |
| `plan.phase_messages` | フェーズ通知の文 | — |
| `autowake`（`slept_before`・`maybe_auto_okita`） | 自動「起きた」 | `state.json`，`events.log` |
| `phone`（`read_phone_marks`・`import_phone_marks`） | iPhone の記録の取り込み | `state.json`，`events.log` |
| `records._late_report` と，それが使うもの | 事後報告（iPhone の「休んだ」「出た」から呼ばれる） | `state.json`，`events.log` |
| `confirm`（`pick_unconfirmed`・`confirm_text`・`mark_confirm_asked` と，それが使うもの） | 終わった乗務の「出た？休んだ？」 | `state.json` |
| `enforce`（`enforce_due`・`enforce_suffix`・`enforce_release`・`stop_active`・`enforce_state`・`set_enforce`） | 強制の発動判定と緊急解除．**宣言（`enforce_declare`）・`enforce_off`・`enforce_resume`・表示は移さない** | `state.json`，`events.log`，`STOP.txt` |
| `calendar_sync`（`parse_ics`・`sync_calendar`） | カレンダー同期．取得は差し替えられる関数で受ける | `calendar.json` |
| `maintenance`（`cleanup`・`daily_maintenance`）と `report.cleanup_reports` | 1 日 1 回の整理 | `days.json`，`state.json`，`log/`，`reports/`，`events.log`，`maintenance.json` |
| `store`（`log_append`・`log_read`・`departures`・`set_mark`） | ログの読み書きなど，上が使う分 | — |
| `config.to_local_path` | 設定に書かれた `/mnt/c/...` を `C:\...` として読む | — |

入口（名前は作る人に任せるが，形はこれ）:

```
TickResult Tick.Run(ShiftConfig cfg, Store store, Messages m, TickInput input, DateTime now, Func<string, string>? fetch = null)
  TickInput  : IdleSeconds, Game, UptimeSeconds（無ければ null）, Remote, DryRun
  TickResult : Actions（method, kind, title, text, speak, sound, action）, Warnings, SoundVolume, SoundMaxSeconds
```

- 設定の既定値（`usage`・`notify`・`retention`・`confirm`・`enforce`・`calendar` など）は Python の `DEFAULTS` と同じ値．`ShiftConfig` に足りない項目は足す．
- ゲームかどうかの**判定**（exe のパスがゲームフォルダ配下か，除外フォルダ・除外名に当たるか，プロセス名の直接指定）は純粋な関数として Core に置く．フォルダの一覧と起動中のプロセスを集めるのは Agent．
- ゲームの判定は Python 版に無く `guard.ps1` にある．Python から作るベクタは無いので，`guard.ps1` の決まり（pc-spec.md §7.1．フォルダ配下・除外フォルダ・除外名の既定値・プロセス名の直接指定・大文字小文字を区別しない）を C# の単体テストに書き起こす．設定の項目は今と同じ（`steam_libraries`・`game_dirs`・`game_exclude_dirs`・`game_exclude`・`game_processes`）．`guard.ps1 -ScanGames`／`-ShowGames`（`game-scan.txt` を書くだけの確認用）はそのまま残し，Agent には移さない．
- ファイルの書き方は区切り 1 の `JsonFiles`・`Store` に揃える（Python 版が読める形）．ベクタとの比較は，JSON は読み込んだ後の値の一致，`*.jsonl` と `events.log` は行の一致で行う（区切り 1 と同じ）．
- 既知の制約はそのまま: Agent・ウィンドウ・Python の hook が同時に `state.json` を書くと後勝ち（区切り 1 と同じ．`.tmp` → 置き換えで壊れはしない）．

### 5.2 shiftlock.Agent

- 実行ファイル名 `shiftlock-agent.exe`，名前空間 `Shiftlock.Agent`．`net10.0-windows`．WinUI は使わない．
- Windows に触る部分はインターフェースの裏に置き，テストで差し替える．少なくとも次の 3 つに分ける:
  - **値を取る側**: アイドル秒数，起動からの秒数，起動中のプロセスの exe パスと名前，Steam のライブラリ（レジストリ `HKCU\Software\Valve\Steam\SteamPath` と `steamapps\libraryfolders.vdf`）．
  - **知らせる側**: トースト，ポップアップ，読み上げ，通知音．
  - **止める側**: ロック，スリープ，シャットダウンの予約と中止．
- ② で 10 秒間隔の計測，③ で減速・一時停止が同じ Agent に載る．周期の違う仕事を足せる形（仕事ごとに間隔を持つ，など）にしておく．今回載せるのは 5 分の見張りだけ．
- ログは実行フォルダの `agent.log`（`guard.log` と同じ役割．1 行 1 件，時刻つき）．大きくなりすぎない仕組みを入れる．
- データのフォルダは区切り 1 と同じ（`SHIFTLOCK_DIR` があればそれ，無ければ `%USERPROFILE%\shift-lock`）．
- 言葉づかいは `shifts.json` の `lang`．区切り 1 と同じ決まり（無い・知らない値は標準語）．

### 5.3 突き合わせ用の入口

本番フォルダのコピーに対して，時刻と Windows 側の値を**指定して** 1 回だけ見張りを行い，結果を Python の `tick` と同じ形の JSON で出す入口を用意する（通知は出さない．強制も実行しない）．Python 側は `shiftlock.py tick --idle-seconds N --game 0/1 --uptime-seconds S`（時刻の指定が要るなら Python 側の道具に足す）．置き場所（Agent の引数か，別の小さな道具か）は作る人に任せる．

### 5.4 Python から書き出すもの

- **文面**: `pc/tools/export_native.py` の対象に，見張りが使う文（`TICK_*`・`AUTOWAKE_*`・`PHONE_*`・確認・事後報告・`ENFORCE_*` のうち発動と解除で使うもの・`CAL_*`・`NOTIFY_*`・フェーズ通知の文）を足す．
- **Agent の画面の文**（メニュー 3 つ，確認のダイアログ，解除の報告，設定エラー，二重見張りの知らせ）: `ja.py` と `ja_kansai.py` の両方に `AGENT_` で始まる名前で足し，同じく書き出す．
- **テストベクタ** `docs/test-vectors/tick.json`: 「最初に置くファイル」と「見張りの列（時刻・アイドル秒数・ゲーム・起動からの秒数）」に対して，各回の結果（通知と実行の一覧・警告）と，最後のファイルの中身を Python 版で作る．カレンダーの取得は固定の文字列を返す関数に差し替える．少なくとも次を含める:
  - 触っている／離席／ゲーム中の放置（180 分）／外出中のゲーム，離席区間の確定（30 分以上），スリープ明けの起点の切り直し
  - フェーズ通知の間引き（1 回だけ・30 分おき・10 分おき），声かけ（120 分から 30 分おき）
  - 自動「起きた」（「ねる」あり／離席だけ／`no_sleep`／前日が乗務），iPhone の取り込み（ねる・おきた・ただいま・休んだ・出た，二度寝，適用済み）
  - 確認（1 日 1 件），強制（予告 → 残り 5 分 → 実行 → 再予告 → 再実行 → 回数の上限 → 時間切れ，離席をまたいだやり直し，`STOP.txt`，起動直後，`dry_run`）
  - カレンダー（成功・失敗で前回分を残す・間隔内は取りに行かない・旧形式の引き継ぎ），整理（1 日 1 回）
  - 通知音（種類 → `default` → なし，無いファイルの警告は 1 日 1 回，同じ回で鳴らすのは 1 件）
- 既存のベクタ（区切り 1）と生成スクリプトの古さの確認（pytest）は今の仕組みに乗せる．

## 6. 確かめ方と切り替え

### 6.1 確かめ方

1. **単体**: Core は `tick.json` ほかのベクタと一致．Agent は Windows 側を差し替えて，「一覧の順に出す」「強制の直前に `STOP.txt` を見る」「見張りの例外で落ちない」「5 分未満では行わない」「前の回が終わっていなければ飛ばす」を確かめる．テストを先に書く．
2. **合成データ**: `SHIFTLOCK_DIR` を合成データのフォルダに向けて Agent を動かし，トレイ・通知 4 種・緊急解除・「状態を開く」・二重起動を実際に見る．強制の実行は `lock` を 1 回だけ実機で確かめる（`sleep`・`shutdown` は本人が確かめる）．
3. **本番のコピーで突き合わせ**: 本番フォルダをコピーし，同じ入力の列を Python の `tick` と §5.3 の入口に与えて，結果の JSON と書いたファイルが一致することを確かめる．実データの値を文書・テスト・コミット・報告に書き写さない．コピーは確認後に消す．

### 6.2 切り替え（本人が行う．手順は文書にする）

- **登録**: Agent を置く場所（下）に発行物を置き，「ログオン時に Agent を起動する」登録と，タスク `shiftlock-guard` の**無効化**を 1 つの操作で行う．管理者権限は要らないこと．`shiftlock-report` には触らない．
- **戻す**: 登録を外し，`shiftlock-guard` を有効に戻す操作を用意する．
- **二重の見張りを防ぐ**: Agent は見張りの前に `shiftlock-guard` が有効かを確かめ，有効なら見張りをせず，トレイで知らせる（「見張りを止めている」アイコン）．確かめるのは見張りのたび（途中でタスクを有効に戻されても次の回で止まる）．タスクが無い → 見張る．確かめられない（読めない・失敗）→ 見張りは行い，`agent.log` に警告を 1 日 1 回書く（黙って何も見張らなくなるほうを避ける）．
- 切り替え後に `shiftlock.py tick` を手で実行することは防がない（Python 側は変えない）．切り替えの手順書に「実行しない」と書く．
- 登録には，トーストを `shiftlock` の名前で出すための登録も含める（方法は §8）．
- **置く場所**: `%LOCALAPPDATA%\Programs\shiftlock\` に `shiftlock-agent.exe` と `shiftlock.exe` を並べて置く（ビルドのフォルダから常駐させない）．発行して置くまでを 1 つの手順にする．
- Python 版のファイル（`guard.ps1`・`kaijo.cmd` など）は消さない．`guard.ps1 -Release` と `-TestNotify` は切り替え後も使える．

### 6.3 文書

`docs/pc-spec.md`（§7 に「ネイティブ版では Agent が行う」旨と違い），`native/README.md`，`docs/native-app-plan.md`（§5.3 と区切りの表），`docs/data-format.md`（`enforce_release` の理由 `tray`，`agent.log`），`CLAUDE.md`（解除の決まり，Agent の確かめ方），`README.md`．

## 7. 決めたこと（本人）

- 順番は ① Agent（この文書）→ ② 稼働計測 → ③ 減速・一時停止．
- ① では `tick` の判断を全部 C# に移す．
- トレイに「緊急解除」を置く．宣言と設定は置かない．「終了」は確認なしで置く．
- 切り替えは，コピーで突き合わせてから一度に行う．

## 8. 作る人に任せること

- トレイの実装（Windows Forms の `NotifyIcon` か，Win32 を直接呼ぶか）とアイコンの絵．
- トーストの出し方（非パッケージのアプリから `shiftlock` の名前で出す方法）．最初に小さく試して決める．出せなければ `popup` に切り替わること．
- 読み上げと通知音に使うライブラリ（標準で足りなければ NuGet を足してよい．足したら `native/README.md` に書く）．
- 自動起動の登録先（レジストリの Run か，ログオン時のタスクか）と，登録・戻すの入口の形（Agent の引数か，スクリプトか．スクリプトなら `*.ps1` は UTF-8 BOM・CRLF）．
- タスクが有効かの確かめ方，`agent.log` の大きさの抑え方，Core のクラスの分け方と名前，§5.3 の入口の置き場所．
- ベクタの形（区切り 1 の `records.json` の「手順の列」に合わせるのが自然）．

## 9. 次の区切りに送ること

②③ で本人が決めた内容（2026-11-17．設計書を書くときの前提）:

- **同意**: 減速・一時停止は乗務ごとの宣言制．強制モードの動作に `throttle`／`suspend` を足し，対話同意・`STOP.txt`・`kaijo` をそのまま使う．`CLAUDE.md` の「ゲーム・アプリを個別に止めない」は「宣言した乗務で，登録した exe に限り減速・一時停止できる．終了はさせない」に直す．
- **対象**: ホワイトリストに登録した exe だけ．システムのプロセス，shiftlock 自身，Claude Code，Discord は対象外．登録はメニューかコマンドで行い，JSON を直接編集させない．アンチチートの警告を登録時に出す．
- **計測**: `NtQuerySystemInformation(SystemProcessInformation)`．10 秒間隔，5 分ごとにまとめて書く．プロセスは (PID, CreateTime) で識別．CPU 時間の差分から使用率，子プロセスを含むツリー合計．失敗したら `System.Diagnostics.Process`．ネイティブ呼び出しはインターフェースの裏．
- **アクティブ**: 前面にある ＋（直近 5 分に入力がある か ツリー合計の CPU が 5% 以上）．数値は設定可．
- **レポート**: カテゴリ単位（game／dev／adobe の帯と日ごとの時間．アプリ名は出さない）．ログは exe 単位．
- **Claude Code の処理中**: hook の `UserPromptSubmit` から `Stop` まで．声かけは処理が終わるまで待つ．CPU では判定しない．
- **減速**: 就寝目安〜最低ラインを 4 等分し，そのアプリの直近の使用率の 75 → 50 → 25 → 10% に CPU 上限を下げる（ジョブオブジェクトの HardCap）．段階ごとに予告 1 回．解除は CPU 上限の制御を無効にする設定の入れ直し．
- **一時停止**: 最低ラインで `NtSuspendProcess`．終了はさせない．(PID, CreateTime) を保存し，Agent の起動時に必ず再開する．
- **解除**: 「行ってきます」の記録（経路を問わず `state.json`）か「今日休む」で即時に全解除．出発の判定はインターフェースにする．
- 強制シャットダウン・再起動・ログオフは ②③ では使わない．
