# ネイティブ版への一本化：Python／PS1 版との差と移植の計画

作成: 2026-11-25．対象のコミット: `dev`（e292c47）．**この文書は計画だけ．コードは変えていない．**

Python／PS1 版（`pc/`）にあって C# 版（`native/`）に無い機能を洗い出し，1 タスク＝1 PR くらいの大きさで移植の順番を決める．

## 0. 読み方

### 0.1 印

| 印 | 意味 |
|---|---|
| 【確認】 | ファイルを読んで確かめた |
| 【推測】 | 読んだ内容からの見込み．動かして確かめてはいない |
| 【未確認】 | 読んでいない・動かしていない |

### 0.2 読んだ範囲

| 範囲 | ファイル |
|---|---|
| 全文を読んだ | `pc/shiftlock.py`，`pc/shiftlock/{cli,commands,console,enforce,apps,limit,hook}.py`，`pc/*.cmd` の全部，`pc/{shiftlock-profile,make-report,install-task,install-profile,deploy}.ps1`，`native/shl/Program.cs`，`native/shl/shl.csproj` |
| 一部だけ読んだ | `pc/guard.ps1`（1〜60 行と，オプションの分岐・関数の宣言の行），`pc/find-python.ps1`（先頭 12 行），`pc/shiftlock/config.py`（`load_raw`・`save_raw`・`apply_lang`），`pc/tools/export_native.py`（先頭，キーの絞り方，`build_vectors`），`pc/tools/make_test_vectors.py`（先頭と関数名），`native/shiftlock.Core/*.cs`（公開メンバーの宣言の行と，`LateReport.Report`・`Maintenance`・`Messages.Load` の説明），`native/shiftlock.Agent/Program.cs`（引数），`Tray.cs`・`MainWindow.xaml`（操作の項目），`native/deploy.ps1`（先頭 22 行），設計書（「宣言」「対話」などで探した行） |
| 関数名と冒頭の説明だけ | `pc/shiftlock/{intents,keywords,sleeplog,report,report_html,attendance,settings_menu,install,maintenance,calendar_sync,records,days,confirm,phone,usage,notify,autowake,tick,plan,store,timeutil}.py` |
| 読んでいない | 上の Python モジュールの本体，C# の各メソッドの本体，`pc/tests/`，`native/*.Tests/`，`docs/pc-spec.md`，`docs/data-format.md` |

「Core に処理がある」は，**同じ役割の公開メンバーが在る**ことまでの確認．Python 版と結果が一致するかは，既にテストベクタがある範囲（`plan`・`phase`・`records`・`snapshot`・`tick`・`limit-stages`）しか確かめられていない．

### 0.3 依頼文の食い違いと，その解釈

- 依頼の「守ること」は「C# に宣言の入口を作らない．宣言は Python に残る前提」．同じ依頼の末尾は「宣言もネイティブ版に移す（2026-11-25 本人決定）」．**末尾の方針変更を採った．**
- 「enforce on/off/resume/release/status を独立したタスクにする」は，「強制モードをほかのコマンドと混ぜず，強制モードだけの PR にする」と解釈した．同意の要る `on` は 1 本の PR に分け，同意の要らない 4 つ（`status`・`off`・`release`・`resume`）は 1 本にまとめた（§5 の E1・E2）．5 本に分けたいなら E1 を割る．
- この文書を書いた後，同じ日に本人が方針を足した: **画面（`shiftlock.exe`）が主．コマンド（`shl.exe`）も残す．強制モードの宣言・再開・設定も画面に置く**（§7）．§5 のタスクと §4 の表はこの方針に合わせてある．§2・§3 は Python 版と native の現状の記録なので変えていない．

## 1. native/ の現状

- `shl.exe`（`native/shl/Program.cs`）は `return 0;` だけのスタブ【確認】（計画を書いた時点．S1 で骨組みと `help` を入れた．§5.4）．`shl.csproj` は NativeAOT（`PublishAot`，`InvariantGlobalization`），参照は `shiftlock.Core` だけ【確認】．
- Core は参照されているだけで何も呼ばれていない．**Core が NativeAOT で警告なく発行できるか，起動が数十 ms かは【未確認】．**
- 入口は 3 つ【確認】:
  - 画面 `shiftlock.exe`: 状態の表示，「ねる」「おきた」「出発」「帰宅」のボタン，取り消し．
  - トレイ `shiftlock-agent.exe`: 「状態を開く」「緊急解除」「終了」．引数は `--test-notify`・`--install`・`--uninstall`・`--stop`・`--tick-once`・`--meter-once`・`--limit-once`．
  - 見張り（Agent の中）: `Tick.Run`，計測，減速・一時停止．
- 文面は `native/shared/messages.{ja,ja_kansai}.json`（各 134 キー）を Core に埋め込んで読む【確認】．`ja.py` のキーは全部で約 530．書き出していない主な接頭辞: `MENU_`(136)，`REPORT_`(69)，`ENFORCE_`(64 のうち 54)，`APPS_`(34)，`STATUS_`(20)，`CLI_`(18)，`CMD_`(10)，`HOOK_`(9)，`SLEEP_`(9)，`CONSOLE_`(9)，`INTENT_`(9 のうち 7)【確認】．
- `ShiftConfig` に `shifts.json` を**書く**メンバーは無い（`Raw` は読むだけ）【確認】．設定を書き換えるコマンドは全部，まず保存の処理が要る．

## 2. 差の表

区分は次の 4 つ．

| 区分 | 意味 |
|---|---|
| 済 | Core に処理があり，native の入口もある |
| 入口だけ無い | Core に同じ役割の公開メンバーがある．`shl.exe` から呼ぶ入口と，表示の文面が無い |
| 一部 | 部品は Core にあるが，組み立て（Python の関数 1 つぶん）が無い |
| 処理から無い | Core に相当するものが無い |

### 2.1 `shl` のサブコマンド（`pc/shiftlock/cli.py`．22 個）

| コマンド | Python の処理 | native の現状 | 区分 |
|---|---|---|---|
| `help`（引数なしの `shl` も） | `M.CLI_HELP` を出す．設定が無くても動く | 文面 `CLI_*` を書き出していない | 入口だけ無い |
| `status` | `commands.status_text` | 値は `Snapshot.Build`．文は画面用（`APP_*`）で，`STATUS_*` の 20 キーは無い．当日欠勤の目安・カレンダーの取得状況・強制の行を `Snapshot` が持つかは【未確認】 | 一部 |
| `yotei`，`day list` | `days.schedule_text`（14 日／28 日） | `Snapshot` に日ごとの出／休（`DayCell`・`DayLabel`）はある．`schedule_text` と同じ文を作るものは無い | 一部 |
| `day off\|work\|rm <日付> [メモ]` | `timeutil.parse_day` → `days.days_set` | `LateReport.ParseDay`・`LateReport.DaysSet` がある | 入口だけ無い |
| `yasumi <日付> [有給\|有給なし]`，`deru <日付>` | `records.late_report`（`via="shl"`） | `LateReport.Report`（`paid`・`via` の引数あり）．今の呼び出し元は `Phone.ImportMarks` だけ | 入口だけ無い |
| `yasumi`（日付なし），`deru`（日付なし） | `commands.cmd_keyword("今日休む"／"やっぱ出る")` | 部品（`Store.SetShiftStatus`・`SetPaid`，`LateReport.MonthYasumiCount`・`AbsenceNote`）はある．`cmd_keyword` の組み立てと `CMD_*` の文面が無い | 一部 |
| `okita`，`neru`，`itte`（引数なし） | `cmd_keyword` → `records.do_okita`／`do_neru`／`do_shuppatsu` | `Records.Okita`／`Neru`／`Shuppatsu`．画面のボタンから呼ばれている．`events.log` の出所を `shl` にできるかは【未確認】（`Records.*` に `via` の引数は無い） | 入口だけ無い |
| `okita <時刻>`，`neru <時刻>`（あとから睡眠を足す・消す） | `sleeplog.record_sleep` | 無い | 処理から無い |
| `kakunin` | `confirm.kakunin_text` | `Confirm` は `PickUnconfirmed`・`UserText`・`MarkAsked` だけ．一覧の文を作るものは無い | 一部 |
| `sync` | `calendar_sync.sync_calendar(force=True)` | `CalendarSync.Sync(..., force)`．今は見張りの `Maintenance.Daily` から `force` なしで呼ぶだけ | 入口だけ無い |
| `cleanup` | `maintenance.cleanup`．結果の辞書をそのまま `print` する | `Maintenance.Cleanup`（戻り値は `JsonObject`）．今は `Daily` から呼ぶだけ【推測．`Daily` の本体は未読】 | 入口だけ無い |
| `hook` | `hook.run_hook` | §2.3 | 処理から無い |
| `learned`，`learn <種類> <語>`，`unlearn <語>` | `intents.learned_text`／`learn`／`unlearn` | 無い | 処理から無い |
| `report [--days N] [--out] [--period] [--auto]` | `report.build_report`・`write_period_reports`，`report_html.render_report` | 無い．`Maintenance` に古いレポートの掃除だけある | 処理から無い |
| `settings` | `settings_menu.settings_menu`（対話のメニュー．343 行） | 無い．設定を書く処理も無い | 処理から無い |
| `app add\|remove\|list` | `apps.app_add`／`app_remove`／`app_list` | 読む側（`MeterSettings.Read`・`NormalizeExe`）だけ | 処理から無い |
| `app limit remove\|exclude\|list` | `apps.limit_remove`／`limit_exclude`／`limit_list` | 読む側（`LimitSettings.Read`，`LimitFiles.ReadGames`）だけ | 処理から無い |
| `app limit add\|include`（範囲を広げる） | `apps.limit_add`／`limit_include`．対話の端末からだけ | 無い | 処理から無い |
| `enforce on [動作] [時間]` | `enforce.enforce_declare` | 無い（今までは「作らない」決まり） | 処理から無い |
| `enforce off` | `enforce.enforce_off` | 無い | 処理から無い |
| `enforce resume` | `enforce.enforce_resume` | 無い | 処理から無い |
| `enforce release` | `enforce.enforce_release(reason="kaijo")` | `Enforce.Release`．入口はトレイだけ（理由は `tray`） | 入口だけ無い |
| `enforce`／`enforce status` | `enforce.enforce_status_text` | 無い | 処理から無い |
| `tick` | `tick.run_tick`（`guard.ps1` が呼ぶ） | `Tick.Run`．Agent の見張りと `--tick-once` | 済（`shl.exe` には作らない） |
| `install-hook` | `install.install`（hook の本体を `~/.claude/hooks` にコピーし，`settings.json` に書く） | 無い | 処理から無い |

### 2.2 PowerShell・cmd

| ファイル・オプション | やること | native の現状 | 区分 |
|---|---|---|---|
| `guard.ps1`（引数なし．5 分おき） | アイドル秒数とゲームを測って `tick` に渡し，通知と強制を実行 | Agent の見張り | 済 |
| `guard.ps1 -DryRun` | 通知を出さずに判定を表示 | `--tick-once --dry-run`．ただし本番のフォルダは断る作り（CLAUDE.md）．本番の「今の判定」を見る入口は無い | 一部 |
| `guard.ps1 -TestNotify [-Sound <パス>]` | 通知・読み上げ・通知音を試す | `--test-notify`．`-Sound` に当たる引数があるかは【未確認】 | 済（`-Sound` は未確認） |
| `guard.ps1 -ScanGames`（`scan-games`） | ゲーム扱いになる exe を一覧し，`game-scan.txt` に保存 | 判定（`Games.IsGameExe`，Agent の `WindowsProbe.SteamCommonDirs`）はある．一覧を出す入口は無い | 入口だけ無い |
| `guard.ps1 -ShowGames`（`show-games`） | 今ゲーム扱いで動いているプロセスを表示 | 判定（`Games.IsGame`，`probe.Processes()`）はある．入口は無い | 入口だけ無い |
| `guard.ps1 -Release`（`kaijo`，`shl kaijo`，`kaijo.cmd`） | 予約済みシャットダウンの中止 → `STOP.txt` → 記録 | トレイの「緊急解除」（`ReleaseFlow`）．コマンドラインからは無い | 入口だけ無い |
| `find-python.ps1` | Windows の Python を探す | 要らなくなる | 対象外 |
| `install-task.ps1`（`-IntervalMinutes`・`-NoHeadless`・`-Uninstall`） | タスク `shiftlock-guard` の登録 | `shiftlock-agent.exe --install`／`--uninstall` | 済 |
| `install-task.ps1`（`-ReportDay`・`-ReportTime`） | タスク `shiftlock-report`（毎週火曜 9:00） | 無い | 処理から無い |
| `make-report.ps1 [-Open]` | `report --auto` → Edge のヘッドレスで PDF → 開く | 無い | 処理から無い |
| `install-profile.ps1 [-Uninstall]` | pwsh 7 と PowerShell 5.1 のプロファイルに `shiftlock-profile.ps1` を読む 1 行を足す | 無い | 処理から無い |
| `shiftlock-profile.ps1` | `shl`・`shiftlock`・`neru`・`okita`・`yasumi`・`deru`・`yotei`・`itte`・`kakunin`・`kaijo`・`scan-games`・`show-games` の関数．`shl report [N]` は作って開く | 無い | 処理から無い |
| `deploy.ps1`（`-Target`・`-Full`・`-DryRun`・`-SkipHook`） | コードを実行フォルダに置き，hook を入れ直す | `native/deploy.ps1` は画面と Agent を置く（先頭の説明で確認）．`shl.exe` を置くかは【未確認】 | 一部 |
| `shiftlock.cmd`・`status.cmd`・`settings.cmd`・`yasumi.cmd`・`deru.cmd`・`okita.cmd`・`neru.cmd` | ダブルクリックで WSL の Python を呼び，`pause` で止まる | 無い | 処理から無い |
| `report.cmd [日数]` | 引数なしは `make-report.ps1 -Open`．日数つきは `report --days` | 無い | 処理から無い |
| `scan-games.cmd`・`kaijo.cmd` | `guard.ps1` を呼び，`pause` で止まる | 無い | 処理から無い |

### 2.3 hook の中身（`pc/shiftlock/hook.py`）

処理の順と，C# に部品があるか【確認．Python 側は全文，C# 側は公開メンバーの名前まで】．

| 順 | Python の処理 | C# の部品 |
|---|---|---|
| 1 | 標準入力の JSON を読む．壊れていても空として続ける | 無い |
| 2 | 設定を読む．失敗は**終了コード 1**（プロンプトは通る） | `ShiftConfig.Load` |
| 3 | `usage.touch_usage` で `usage-claude.json` を更新．`Stop` はここで終わり（0） | **無い**（`Usage` は `DueNag`・`PcActive` など読む側だけ） |
| 4 | 固定キーワード・日付コマンド・事後報告・有給の判定（`keywords.py` の語彙と正規表現，`intents.norm_kw`／`norm_text`） | **無い** |
| 5 | `phone.import_phone_marks` | `Phone.ImportMarks` |
| 6 | 聞き返しへの返事（`records.answer_pending`） | **無い** |
| 7 | `log/claude-*.jsonl` に 1 行（本文は書かない） | `Store.LogAppend` |
| 8 | キーワードなら応答して**終了コード 2**（`cmd_keyword`・`cmd_paid`・`cmd_day`・`cmd_past`） | 一部（§2.1 の `yasumi`・`day`・事後報告と同じ部品） |
| 9 | 話し言葉から拾う（`detect_intents`・`apply_intents`・`weak_intent`・`set_pending`・`learned.json`） | **無い** |
| 10 | `autowake.maybe_auto_okita` | `AutoWake.MaybeAutoOkita` |
| 11 | フェーズの判定．`hard` で `block_claude_on_hard` なら**終了コード 2** | `Planner.Current`，`PhaseText.User`，`Enforce.Suffix` |
| 12 | 未確認の乗務を 1 日 1 件 Claude に聞かせる | `Confirm.PickUnconfirmed`・`MarkAsked`．Claude 向けの文（`confirm_text` の 2 つ目）は無い |
| 13 | フェーズの文（ユーザー向け＋Claude 向け） | ユーザー向けだけ．Claude 向け（`*_CTX`）は書き出しから外している |
| 14 | 連続稼働の声かけ（`due_nag`） | `Usage.DueNag` |
| 15 | JSON（`systemMessage`・`additionalContext`）を 1 行で出す | 無い |
| 16 | `claude-busy.json` に印を残す／消す | 読む側（`LimitFiles.ClaudeBusy`）だけ．**書く側は無い** |

終了コード 2 を返すのは「キーワードへの応答」「聞き返しへの返事」「ハードで止める」の 3 つだけで，どれも意図した動き．失敗では返さない．

## 3. 同意の確かめ方（Python 版．これと同じにする）

### 3.1 `enforce_declare`（`shl enforce on`）【確認】

`cli.py` は `opts` から，`ENFORCE_ACTIONS` に一致する最初の語を動作に，`^\d+(\.\d+)?h?$` に一致する最初の語を時間にする．そのあとの順番:

| 順 | 確かめること | だめなときの返事 | 状態 |
|---|---|---|---|
| 1 | 動作（省略なら `enforce.action`）が `lock`・`sleep`・`shutdown`・`throttle`・`suspend` のどれか | `ENFORCE_BAD_ACTION` | 変えない |
| 2a | `throttle`／`suspend`: 時間の指定が無い．`limit.steps` が正しい（4 個・整数・1 以上 100 未満・大きい順） | `ENFORCE_LIMIT_NO_HOURS`／`ENFORCE_LIMIT_BAD_STEPS` | 変えない |
| 2b | それ以外: 時間が数として読め，0.5〜12 の範囲（省略なら `enforce.duration_hours`） | `ENFORCE_BAD_HOURS`／`ENFORCE_HOURS_RANGE` | 変えない |
| 3 | 対象の乗務がある（`target_plan`） | `ENFORCE_NO_PLAN` | 変えない |
| 4 | その乗務が「休み」「出た」でない | `ENFORCE_SHIFT_OFF` | 変えない |
| 5 | まだ実行が始まっていない（`runs` が空） | `ENFORCE_ALREADY_RUNNING` | 変えない |
| 6 | **標準入力が端末**（`sys.stdin.isatty()`） | `ENFORCE_NEED_TTY`．内容の表示もしない | 変えない |
| 7 | 内容を表示（`enforce_terms`） | — | — |
| 8 | `ENFORCE_ASK_OK` を出して 1 行読む．**前後の空白を取り小文字にして `y` ちょうど**のときだけ先へ．EOF は空として扱う | `ENFORCE_DECLINED` | 変えない |
| 9 | **Windows のダイアログ**（はい／いいえ．既定は「いいえ」．最前面．60 秒）で「はい」 | `ENFORCE_DIALOG_DENIED`（いいえ・無応答・出せない） | 変えない |
| 10 | `state.json` に宣言を書き，`events.log` に `enforce_declare` と `enforce_consent` | — | 書く |

- 表示する内容（順 7）:
  - `lock`／`sleep`／`shutdown`: 乗務日，何を，最低ラインの時刻，猶予の分，続く時間，終わりの時刻，再ロックの間隔，回数の上限．`sleep`／`shutdown` は注意の 1 行を足す．
  - `throttle`／`suspend`: 乗務日，何を，最低ラインの時刻，段階ごとの時刻と割合（最後は「一時停止」），対象になるゲーム・アンチチートで外したもの・調べられなかったもの，足したアプリ，戻る時刻，アンチチートの注意，`suspend` の注意，注意の 1 行．Agent が動いていないようなら（`log/app-*.jsonl` の最後の行が 15 分より古い）その旨．
- 宣言に写して固定する値（順 10）:
  - 共通: `action`，`declared_at`，`consented_at`，`via`（`"shl"`），`runs`（空）．
  - `lock` 系: `duration_hours`，`grace_minutes`，`relock_minutes`，`max_runs`，`boot_grace_minutes`．
  - `throttle`／`suspend`: `steps`，`limit_apps`，`limit_exclude`．
- 返事は `ENFORCE_DECLARED`／`ENFORCE_DECLARED_LIMIT`．`STOP.txt` が在れば注記を足す．
- **終了コードはどの分岐でも 0**（`cli.py` は返事を `print` して 0 を返す）．
- 非対話の入口（`--yes` など）は無い．

### 3.2 `enforce_resume`（`shl enforce resume`）【確認】

- 端末かどうかは見ない．質問もダイアログも無い．
- `STOP.txt` が無ければ `ENFORCE_RESUME_NONE`．
- 在れば消し，**解除していない宣言に `released_at` を付ける**（理由は `resume`）．古い同意が再開で生き返らないようにするため．`events.log` に `enforce_resume`．
- つまり「再開」は強制を再び効かせる操作ではなく，次の宣言を出せる状態に戻す操作．

### 3.3 `limit_add`・`limit_include`（`shl app limit add|include <名前>`）【確認】

| 順 | 確かめること | だめなとき（終了コード 1） |
|---|---|---|
| 1 | 名前が exe 名として正しい（`normalize_exe`） | `APPS_BAD_NAME` |
| 2 | **標準入力が端末**（`cli.py` が `sys.stdin.isatty()` を渡す） | `APPS_LIMIT_NEED_TTY` |
| 3 | `shifts.json` の `limit` の形が正しい | `APPS_BAD_SECTION` |
| 4（`add`） | `limit.never` に無い | `APPS_LIMIT_NEVER` |
| 5（`add`） | `apps.registered` に登録済み | `APPS_LIMIT_NOT_REGISTERED` |
| 4（`include`） | `limit.exclude` に在る | `APPS_LIMIT_NOT_EXCLUDED` |

- **`y` の質問もダイアログも無い．** 端末であることだけを見る．
- 成功は終了コード 0．`add` は `APPS_LIMIT_ADDED`＋アンチチートの注意＋守る名前の注記の 3 行，`include` は `APPS_LIMIT_INCLUDED`．
- 保存は `save_raw`（一時ファイル → 置き換え，UTF-8，LF，インデント 2，日本語はそのまま，末尾に改行，`_` で始まるキーは落とす）．

### 3.4 C# で同じにするときの対応（計画）

| Python | C#（`shl.exe`） | 印 |
|---|---|---|
| `sys.stdin.isatty()` | `Console.IsInputRedirected` が真なら断る | 【推測】同じ場面で同じ結果になるかは下の 3 つを実機で見る |
| `input()` の 1 行，EOF は空 | `Console.ReadLine()`，`null` は空 | 【推測】 |
| `strip().lower() == "y"` | 前後の空白を取り，小文字にして `"y"` と比べる．全角の `ｙ` は通さない（Python も通さない） | 【確認．Python 側】 |
| `WScript.Shell.Popup`（`powershell.exe` 経由） | `user32` の `MessageBoxW` か `MessageBoxTimeoutW`（はい／いいえ，既定「いいえ」，最前面，60 秒） | 【未確認】NativeAOT で使えるか，60 秒の打ち切りを同じにできるか |

実機で見ること（本人．Claude Code からは宣言を実行しない）:

1. Windows Terminal で `shl.exe enforce on` → 端末と判定される．
2. `echo y | shl.exe enforce on`，Claude Code の Bash，hook から → 断られる．
3. WSL の端末から `/mnt/c/.../shl.exe enforce on` → どう判定されるか【未確認】．断られる側に倒れるなら，宣言は Windows の端末からだけになる．

`shl.exe` は hook の本体でもある．**`hook` のコードの道から強制モードのコマンドに届かない**作りにする（サブコマンドの振り分けより先で分ける）．

### 3.5 画面での同意（未設計）

宣言を画面にも置くと決まった（§7 の 1）．画面での同意の確かめ方は**まだ決まっていない**．設計書（§5 の D1）で決めるまで，画面に宣言・再開・強制モードの設定・範囲を広げる操作は作らない．

設計で引き継ぐこと（Python 版の決まりから）:

- 見せる内容は §3.1 の順 7 と同じ（Core の同じ処理が作る文をそのまま出す）．
- 順 1〜5 の断り方と，宣言に写して固定する値は同じ．
- 既定は「同意しない」側．押し間違い・Enter の連打で通らない．
- 促さない．宣言の画面は本人が開いたときだけ出す（通知・トースト・起動時の案内から開かない）．
- `--yes` に当たるもの（引数・設定・ファイルで同意を済ませる道）を作らない．

設計で決めること:

- 端末の `y` と Windows のダイアログの二段に当たるものを，画面でどうするか．
- Claude Code から画面を操作される道（画面の自動操作など）をどう扱うか．`shl.exe` は標準入力のリダイレクトで断れるが，画面には同じ判定が無い．
- `CLAUDE.md` の「絶対」の 1 行目（「`shl enforce on` を対話的に実行し、表示された内容に `y` で同意した乗務にだけ」）の書き換え．今回は変えていない．

## 4. 設計書で直すべき箇所（今回は直していない）

`CLAUDE.md` の「ファイルの決まり」の 1 か所は直した（§8）．次は未着手．「画面」の行は，画面での同意の設計（D1）が決まってから直す．

| ファイル | 場所 | 今の内容 | 直す向き |
|---|---|---|---|
| `docs/superpowers/specs/2026-11-17-native-agent-design.md` | §2「やらないこと」（32 行目） | 宣言は Python 版のまま．C# に宣言の入口を作らない | 「宣言は `shl.exe` と画面に置く（2026-11-25）．Agent（トレイ）には置かない」 |
| 同上 | §4.2（97 行目） | CLAUDE.md を「画面に宣言・設定を置かない．解除は置いてよい」に直す | 2026-11-25 の文面への参照を足す |
| 同上 | §5.1 の表（163 行目） | 宣言・`enforce_off`・`enforce_resume`・表示は移さない | 「`shl.exe` のために Core に移す（§5 の E1・E2）」 |
| 同上 | §2（43 行目） | 画面（トレイを含む）に宣言・設定を置くこと（禁止） | 「トレイに」だけにする．画面は D1 の設計に従う |
| 同上 | §4.2・§7（96・238 行目） | トレイに宣言・再開・設定を置かない | **そのまま** |
| `docs/superpowers/specs/2026-11-17-native-status-window-design.md` | §2（41 行目） | 強制モードの宣言・解除・設定を画面に置く（禁止） | D1 の設計への参照に置き換える |
| `CLAUDE.md` | 「絶対」の 1 行目 | `shl enforce on` を対話的に実行し，`y` で同意した乗務にだけ | D1 で文面を決めて直す（本人の承認が要る） |
| `docs/pc-v11-design.md` | B の原則（35 行目），B の前書き（153 行目） | 宣言の入口は `shl enforce on` だけ | 中身は同じ．「`shl` の実体が `shl.exe` になる」ことを書く |
| 同上 | B（155 行目） | `--yes` を作らない（Claude Code の Bash は stdin が無いので同意できない） | 判定の方法（標準入力がリダイレクトされていたら断る）を書く |
| 同上 | B.3 の表「同意の確認」（192 行目） | `y/N` ＋ Windows のダイアログ．両方そろって有効化 | ダイアログの出し方（PowerShell 経由 → 直接）だけ直す．二段の確認は残す |
| 同上 | ファイルの表（397 行目） | `shiftlock.py` に `cmd_enforce` | 置き場所を足す |
| `docs/superpowers/specs/2026-11-23-native-app-limit-design.md` | §2「やってはいけないこと」（50 行目） | 宣言の入口を C#・画面・トレイ・hook に作ること | 「C#・画面」を外し「トレイ・hook」に |
| 同上 | 38・322・336 行目 | 宣言・`app limit` は Python のコマンドライン | 置き場所を足す |
| `docs/native-app-plan.md` | §6（133 行目） | 「`shl.exe enforce on` か，**アプリ内の宣言ボタン**」 | **そのまま**（画面にも置くと決まったので食い違わない） |
| `native/README.md` | 44・64 行目 | 同意は Python 版の `shl enforce on` だけ | `shl.exe` に置いた時点で直す |
| `pc/tools/export_native.py` | 45 行目のコメント | 宣言・状態表示の文は持っていかない．C# に宣言の入口を作らない | E1・E2 で書き出すときに直す |

`CLAUDE.md` の「絶対」の中身（強制は本人が内容を見て同意した乗務だけ／促す文面を入れない／Claude Code から操作しない／`--yes` を作らない）は**変えない**．変わるのは同意の入口が 2 つ（`shl.exe` と画面）になることだけ．

## 5. 移植の順番（1 タスク＝1 PR）

### 5.1 どのタスクにも当てはまる決まり

- **画面が主，コマンドも残す**（§7 の 5）．判断と文の組み立ては Core に置き，画面（`shiftlock.App`）とコマンド（`shl.exe`）はどちらも薄い入口にする．同じ操作は同じ Core の処理を呼ぶ．
- 進め方は機能のまとまりごとに 2 本: ① Core＋テストベクタ＋`shl.exe` の入口（Python 版との一致を機械で確かめる）→ ② 画面．画面の PR は Core を変えない．
- **Python 版が正．** 新しいコマンドは，合成データを Python 版に通した結果を `docs/test-vectors/` に書き出し，C# のテストが同じ入力で同じ結果（表示文・終了コード・書かれたファイル）になることを確かめる．
- テストベクタと文面は `pc/tools/export_native.py` に足す（`plan-scenarios.json` と `limit-stages.json` だけは `make_test_vectors.py`）．書き出した JSON は同じ PR でコミットする．
- 文面は C# に直書きしない．`ja.py` と `ja_kansai.py` の既存のキーを書き出して使う．
- `shl.exe` の終了コード: いちばん外で例外を受けて **1**．2 を返すのは hook の 3 つの場面（§2.3）だけ．
- NativeAOT: PR ごとに `dotnet publish shl -c Release` が警告なしで通ることと，起動時間を確かめる．
- 強制を促す文面・`--yes` のような入口は足さない．
- 本番への反映と切り替えは本人が行う．

### 5.2 タスク

依存の無いものは順番を入れ替えてよい．「ベクタ」は新しく足すテストベクタ，「書き出し」は `export_native.py` のキーの絞り方（`EXACT`／`PREFIXES`）に足すもの．

| # | タスク | 中身 | ベクタ | 書き出し | 前提 |
|---|---|---|---|---|---|
| S0 | 文書の直し | §4 の表．`docs/data-format.md` に変更が要るかの確認 | — | — | — |
| S1 | `shl.exe` の骨組み | 引数の解釈（`--config`．無ければ `SHIFTLOCK_DIR` → 既定のフォルダ），UTF-8 での出力，終了コードの決まり，`help`．AOT の発行と起動時間の計測（空の `hook` で） | — | `CLI_` | — |
| S2 | 見るだけのコマンド | `status`・`yotei`・`day list`・`kakunin` | `commands.json`: `status_text`・`schedule_text`・`kakunin_text` | `STATUS_`，`DAYS_` と `CONFIRM_` の残り | S1 |
| S3 | 記録のコマンド | `okita`・`neru`・`itte`（引数なし），`yasumi`・`deru`（日付なし＝`cmd_keyword`，日付つき＝`LateReport.Report`，有給の印），`day off\|work\|rm` | `commands.json` に `cmd_keyword`・`late_report`（`via="shl"`）・`days_set` | `CMD_`，`RECORDS_` の残り | S1 |
| S4 | あとから睡眠を足す | `okita <時刻>`・`neru <時刻>`・取り消し（`sleeplog.record_sleep`） | `sleeplog.json` | `SLEEP_` | S3 |
| S5 | 入口だけのコマンド | `sync`（`force`）・`cleanup`・`kaijo`（＝`enforce release`．予約済みシャットダウンの中止も） | `release` は既存．`cleanup` の表示の形を決めて足す | 無し（`CAL_`・`ENFORCE_RELEASED` は書き出し済み） | S1 |
| S6 | 設定を書く土台とアプリの登録 | `shifts.json` の保存（`save_raw` と同じ形式）．`app add\|remove\|list`，`app limit remove\|exclude\|list` | `apps.json`（結果の文・終了コード・保存後の `shifts.json`） | `APPS_` | S1 |
| E1 | 強制モード：同意の要らない側 | `enforce status`・`off`・`release`・`resume`（§3.2） | `enforce-cli.json`: 4 つの返事と，`state.json`・`events.log`・`STOP.txt` | `ENFORCE_STATUS_*`・`ENFORCE_STATE_*`・`ENFORCE_OFF_*`・`ENFORCE_RESUME*` | S1，S0 |
| E2 | 強制モード：宣言 | `enforce on`（§3.1 の順番どおり）．端末の判定とダイアログは差し替えられる形にして，ベクタでは「端末か」「入力の 1 行」「ダイアログの結果」を入力にする | `enforce-cli.json` に §3.1 の全分岐 | `ENFORCE_` の残り全部 | E1 |
| E3 | 強制モード：範囲を広げる操作 | `app limit add\|include`（§3.3） | `apps.json` に「端末か」を入力にした分岐 | `APPS_LIMIT_*`（S6 で書き出し済みなら無し） | S6，E2 |
| H1 | hook：キーワードとフェーズ | §2.3 の 1〜8，10〜16（9 以外）．`Stop` の記録，`claude-busy.json`，ハードで止める | `hook.json`:（入力の JSON，時刻）→（終了コード，標準出力，標準エラー，書かれたファイル） | `HOOK_`，`*_CTX` を外す決まり（`SKIP_SUFFIX`）をやめる | S3 |
| H2 | hook：話し言葉と聞き返し | §2.3 の 6 と 9．`learned`・`learn`・`unlearn` | `intents.json`（語 → 判定の表），`hook.json` に追加 | `INTENT_` の残り | H1 |
| H3 | hook の登録 | `install-hook`（`settings.json` の hook を `shl.exe` に向ける．WSL 側と Windows 側）．切り替えは本人 | 登録後の `settings.json` | `CLI_INSTALL_*` | H2 |
| R1 | レポート：集計 | `build_report`・`closing_period`・睡眠の区切り（`sleep_sessions`） | `report.json`（集計の結果） | — | S4 |
| R2 | レポート：HTML | `render_report`．HTML の全文を Python 版と一致させる（§7 の 3） | `report-html.json`（集計の結果 → HTML の全文） | `REPORT_` | R1 |
| R3 | レポート：入口 | `report`（`--days`・`--period`・`--auto`・`--out`），PDF（Edge のヘッドレス），開く，週 1 回の自動実行（Agent の予定に足す【推測】） | — | — | R2 |
| U1 | 画面：予定と報告 | 予定の表（28 日），例外日の追加・取り消し，未確認の乗務の一覧と「出た／休んだ」（有給の印），今日休む・やっぱ出る | — | 画面用の文（`APP_`） | S2，S3 |
| U2 | 画面：睡眠の追記 | あとから「ねる」「おきた」の時刻を足す・消す | — | `APP_` | S4 |
| U3 | 画面：アプリ | 時間を測るアプリの登録，減速の対象から外す・一覧．同期・掃除のボタン | — | `APP_` | S5，S6 |
| D1 | 設計：画面での同意 | §3.5．設計書を書き，本人の承認をもらう．`CLAUDE.md` の「絶対」の 1 行目と §4 の「画面」の行を直す | — | — | — |
| U4 | 画面：強制モード | 状態，宣言，取り消し，再開，範囲を広げる操作（対象に足す・外したゲームを戻す）．解除はトレイのまま | 画面の同意の分岐（D1 で決めた入力） | D1 しだい | D1，E2，E3 |
| U5 | 画面：設定 | 設定画面（区切り 3）．強制モードの既定値は U4 の後 | 保存後の `shifts.json` | `APP_` | S6 |
| U6 | 画面：レポートと学習した語 | レポートを作って開く（期間・日数），学習した語の一覧・追加・削除 | — | `APP_` | R3，H2 |
| G1 | ゲームの一覧 | コマンドは作らない．画面（`shiftlock.exe`）に「ゲーム扱いになる exe」「今ゲーム扱いで動いているもの」を出す（§7 の 4）．調べる部分（今は Agent の `WindowsProbe`）を画面からどう使うかはこのタスクで決める | — | 画面用の文（`APP_`）を足す | — |
| M1 | 設定メニュー（コマンド） | 対話のメニュー（`settings_menu`．強制モードの既定値・カレンダー・通知音・ゲーム・学習した語・例外日の各メニューを含む）を `shl.exe` に移す（§7 の 2）．設定は画面（U5）が主になったので，**U5 の後に回し，手前で要否を本人に確かめる**．移すなら 343 行あるので 2〜3 本の PR に割る | `settings.json`（入力の並び → 表示の並びと保存後の `shifts.json`） | `MENU_`・`CONSOLE_`・`COMMON_`・`LANG_` | S6 |
| Z1 | 切り替えと片付け | `shl.exe` を置く（`native/deploy.ps1`），短い名前（`neru` など）と `*.cmd` の置き換え，`shiftlock-report` のタスクの置き換え，`pc/` の入口の片付け | — | — | 全部 |

おすすめの順: S1 → S2 → S3 → U1 → S4 → U2 → S5 → S6 → U3 → G1 → U5 → E1 → E2 → E3 → D1 → U4 → H1 → H2 → H3 → R1 → R2 → R3 → U6 → M1 → Z1．
D1（設計）は実装と並べて早めに始めてよい．hook（H1〜H3）は毎プロンプトで動くので，コマンドで骨組みを固めた後にする．

### 5.3 先に確かめておくこと（S1 の中で）

- Core が NativeAOT で発行できるか（S1 で届く範囲は確認．§5.4．残りは【未確認】）．`JsonNode` と生成の正規表現は AOT 向きだが【推測】，警告が出たらそこから直す．
- 起動時間（S1 で確認．§5.4）．文面の JSON を起動のたびに全部読む作り（`Messages.Load`）なので，キーが約 4 倍になったときの時間も測る．
- `InvariantGlobalization` の影響【未確認】．`intents.norm_text`・`to_hira` が文字の正規化（全角・半角，かな）をどうしているかを読んでいない．C# で同じ結果にできるかは H2 の前に確かめる．
- Python の `re` と .NET の正規表現の違い【未確認】．`keywords.py`・`intents.py` の式を 1 本ずつベクタで確かめる．
- WSL から `/mnt/c/.../shl.exe` を hook として呼んだときの起動時間（S1 で確認．§5.4）（`docs/native-app-plan.md` §4 は「数十 ms」としている）．

### 5.4 進み具合と，確かめた結果

| # | 状態 | 確かめたこと |
|---|---|---|
| S1 | 済み（2026-11-25） | 下の「S1 の結果」 |

**S1 の結果**（合成の設定のフォルダで．本番のフォルダには向けていない）

- 入口は `native/shl/Program.cs`（文字コードと終了コードの受け皿），引数の解釈と振り分けは Core の `ShlCli`．`help`（引数なし・`-h`・`--help`・`?`・`/?` も）と，空の `hook`（何も読まず何も出さず 0）だけが動く．ほかの Python 版のコマンドは「まだ無い」（`SHL_NOT_YET`）を標準エラーに出して 1，知らないコマンド・オプションは `SHL_UNKNOWN` で 1【確認】．
- NativeAOT の発行は警告なしで通った【確認】．ただし発行で調べられるのは `shl.exe` から届くコードだけ．Core の残り（見張り・記録など）は，コマンドを足した PR で初めて調べられる【未確認】．
- 起動時間（Windows の PowerShell から 30 回）: 空の `hook` は中央値 25 ms，`help`（文面の JSON を読む．186 キー）は 27 ms．同じ測り方の `cmd /c exit` は 19 ms【確認】．文面を読む分は 2 ms ほどなので，キーが約 530 になっても数 ms の見込み【推測】．
- WSL から `/mnt/c/.../shl.exe hook` を 20 回続けて呼ぶと 1 回あたり約 65 ms，Git Bash からは約 80 ms（どちらも `echo` とパイプの分を含む）【確認】．
- 出力は UTF-8（BOM なし）・行末 LF．ファイルに受けた `help` の出力は，WSL から呼んで書き出した文面（`CLI_HELP`＋改行）とバイト数が一致．Git Bash のパイプでも先頭と末尾のバイトが UTF-8・LF【確認】．
- 端末に直接出すときは，コードページを UTF-8 にして書き，終わったら戻す作りにした．本物の端末（Windows Terminal・conhost）で文字化けしないこと・戻ることは見ていない【未確認】．
- Python 版との違い（`ShlCli` の頭のコメントにも書いた）: 知らないコマンドは 1（Python 版は argparse が 2）／`--config` は省ける／設定が無いときの言葉づかいは標準語／行末は LF．

## 6. 一本化しても Python に残るもの

当初の前提（宣言は Python に残す）は，同じ依頼の末尾の方針変更で無くなった．**宣言はこの節に入らない．**

残るのは次．

| 残るもの | 理由 | いつまで |
|---|---|---|
| `pc/shiftlock/`（判断の本体）と `pc/tools/export_native.py`・`make_test_vectors.py` | Python 版が正で，テストベクタと文面の出どころ | 「正」を C# に移すと決めるまで．移すなら文面の元（`i18n/*.py`）の置き場所も決め直す |
| `pc/tools/tick_once.py` | 本番のコピーでの突き合わせ | 同上 |
| 任意の言語 `inm.py` | 書き出しの対象外（`LANGS` は `ja`・`ja_kansai` だけ）．`shl.exe` では使えない | 使い続けるなら，手元で書き出して読む仕組みが要る【未決】 |

実行フォルダで動くもの（`guard.ps1`・`*.cmd`・`shiftlock-profile.ps1`・hook の Python）は，Z1 で全部置き換わる．

## 7. 本人が決めたこと（2026-11-25）

| # | 決めること | 決定 |
|---|---|---|
| 1 | 宣言を画面にも置くか | **置く．** 宣言・再開・強制モードの設定・範囲を広げる操作は，`shl.exe` と画面（`shiftlock.App`）の両方に置く．トレイは解除だけ．2026-11-23 の決定どおり．（同じ日に一度「`shl.exe` だけ」と答えた後，下の 5 と合わせて決め直した）画面での同意の確かめ方は未設計（§3.5，D1） |
| 2 | 設定メニュー（`shl settings`） | 対話のメニューを `shl.exe` に移す．**ただし 5 で画面が主になったので，設定は画面（U5）を先に作り，メニューの移植（M1）は手前で要否を確かめ直す**（この読み替えは Claude の解釈） |
| 3 | レポートの HTML の一致 | **全文を Python 版と一致させる**（R2） |
| 4 | `scan-games`・`show-games` | **コマンドは作らず，画面で見せる**（G1） |
| 5 | CUI と GUI | **画面が主．コマンドも残す．** 全部の操作を画面に作り，`shl.exe` にも同じ操作のコマンドを置く（4 のゲームの一覧だけは画面のみ） |

## 8. この文書と一緒に変えたもの

- `CLAUDE.md`「ファイルの決まり」の `native/` の項．「画面とトレイに強制モードの宣言・再開・設定を置かない。…C# に宣言の入口を作らない」を次の内容に置き換えた（2 回直した．下は今の内容）:
  - 宣言の入口（enforce on 相当）・再開・強制モードの設定・範囲を広げる操作（app limit add／include）は，`shl.exe` と画面（`shiftlock.App`）にだけ置く．トレイ・常駐（`shiftlock.Agent`）・hook には置かない．
  - 同意は本人の対話の操作のみ．`shl.exe` は標準入力がリダイレクトされていたら断る．
  - 画面での同意の確かめ方は設計書で決める．決まるまで画面に宣言・再開・強制モードの設定・範囲を広げる操作を作らない．
  - 解除（緊急解除）はトレイに置いてよい（2026-11-17 の決定のまま）．
- 「絶対」の項目は変えていない．
