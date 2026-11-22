# ネイティブ版 区切り 2（トレイ常駐と見張り）の作業記録

issue #4．設計は [superpowers/specs/2026-11-17-native-agent-design.md](superpowers/specs/2026-11-17-native-agent-design.md)，計画は [superpowers/plans/2026-11-17-native-agent.md](superpowers/plans/2026-11-17-native-agent.md)．
実装中に設計書・計画が決めていなかった点をどう決めたか（決めたこと — 理由 — 違っていた場合に起きること），レビューの結果，まだ確かめていないことを残す．

## 実装中に決めたこと

- **Task 1**: 計画の ATTENDANCE_ というキーは無い．attendance.py が使うのは ABSENCE_OVER — 接頭辞 ABSENCE_ を書き出す — 間違っていても文が 1 つ余分に出るだけ
- **Task 1**: ShiftConfig の新しい節は型つきプロパティでなく Raw（DEFAULTS に重ねた JsonObject）と Section(key) で持つ — Python は使う場所ごとに壊れた値の扱いが違う（sound_volume は警告して 0.8，game_idle_minutes は 180 など）ので，読み込みで型に落とすと同じ動きにできない — 各段で読み方を書く手間が増える
- **Task 1**: state.json が在るのに読めない（壊れている・掴まれている）とき，SetShiftStatus/SetMark/SetPaid は StateBusyException（Python は空として上書きする）— 区切り 1 と同じ「読めなかったものを空で書き戻さない」を優先 — state.json が本当に壊れていると，書く段は直すまで毎回失敗する（通知の判断は読むだけなので動く）
- **Task 2**: usage-pc.json の壊れた値（読めない時刻・配列でない gaps・形の違う gap）は読んだ時点で無いものにそろえる（Python は落ちるか，壊れた値を残す）— Review Focus 2「壊れた分を無いものとして進む」— 正しい値のときの動きは Python と同じ（ベクタで確認）．壊れた値を手で入れたときだけ違う
- **Task 2**: usage.idle_reset_minutes などが数でないとき ConfigException（Python は TypeError で tick が落ちる．数字の文字列も同じく落ちる）— 同じ「その回は失敗」になる — 文字列の数字を許さない点は Python と同じ
- **Task 2**: ベクタで fetch を渡さないケースは「呼ばれたら失敗する取得」に差し替える（Python 側も C# 側も）— 書き出しやテストが本物のネットに出ないように — なし
- **Task 5**: 計画の例 yasumi:2026-11-30 は 2026-12 の日付に置き換えた（yasumi:2026-12-02 など）— Global Constraints と pytest が 2026-12-01 以降だけを許す — なし
- **Task 5**: 節 phone の全ケースに maintenance.json（その日の整理は済み）を置く — Python の 1 日 1 回の整理は days.json を必ず書き直すので，days.json を見る節に整理の書き込みが混ざる．整理は Task 8 の節で見る — 整理と取り込みが同じ回に重なる組み合わせはベクタで見ていない（Task 8 で整理を足した後は usage など他の節が毎回通る）
- **Task 5**: 事後報告で日付が読めないときの文（RECORDS_LATE_BAD_DATE の err）は C# の文になる（Python は例外の英語の文）— 見張りでは失敗の文は捨てられ表に出ない — 将来 C# に「休んだ」の入口を作ると文が Python と違う
- **Task 6**: 計画の log/pc-2026-11.jsonl・confirm_since 2026-11-20 は 2026-12 に置き換えた（記録開始 2026-12-01，いまは 2026-12-10）— 合成の日付は 2026-12-01 以降だけ — なし
- **Task 6**: 確認の段は confirm_since / confirm_asked を state.json に書く．state.json が在るのに読めないと StateBusyException でその回の見張りが失敗する（Task 1 の Ruling の帰結．Python は空として上書きして続ける）— 記録を消さないほうを優先．Agent は失敗をログとトレイで知らせる（Task 10・13）— state.json が壊れたままだと通知が出ない
- **Task 7**: 宣言の action が文字列でないとき C# は発動しない（InvalidDataException → TICK_ENFORCE_ERROR）．Python はそのまま予告・実行の要素を返す（guard.ps1 が知らない action として無視する）— 迷ったら発動しない側 — 手で壊した宣言のときだけ警告の文が増える
- **Task 7**: 通知の添え字（Suffix）で duration_hours が数として読めないとき，C# は値をそのまま見せて通知を続ける（Python は例外で tick が落ちる）— 壊れた宣言で通知全体を止めない — 文が Python と違う（壊れた宣言のときだけ）
- **Task 7**: Enforce.Release は state.json が在るのに読めないとき StateBusyException（STOP.txt は先に書く）．Python は空として扱い「宣言なし」の行を書く — 読めなかった宣言を「無い」と記録しない．STOP.txt があるので強制は止まる — 履歴に enforce_release の行が残らない場合がある（Task 13 の ReleaseFlow が知らせる）
- **Task 8**: calendar.json の error の例外名は，取得の失敗（IOException）だけ Python と同じ "OSError"，ほかは .NET の型名と .NET の文 — 同じファイルを両方の版が読み書きするのでよくある失敗の表記を揃える．解析の失敗の文までは真似ない（節 calendar の「日付の読めない DTSTART」は calendar.json を比べず，次の回の通知で前回分が残ることを見る）— 状態表示に出るエラーの文が版で違う
- **Task 8**: iCal の日付は 8 桁の日付・6 桁の時刻だけ受ける（Python の strptime は 1 桁の月日なども通す）— 厳しい側に倒す．読めなければそのソースは失敗で前回分が残る — 変わった書き方のカレンダーが Python では通り C# では失敗になる
- **Task 8**: 在るのに読めない days.json は整理で書き直さない（Python は空の 3 項目で上書きして手動の例外日が消える）．state.json も読めなければ触らない（こちらは Python と同じ）— 読めなかったものを空で書き戻さない（Task 1 の Ruling と同じ方針）— その日は days.json の古い日が残る
- **Task 8**: 月として読めない名前のログ（-2027-00・-2027-13・-0000-01）は消さない（Python は 00・13 を消し，0000 は整理全体が失敗）— 解釈できないものを消さない — そういう名前のファイルが残り続ける（実際には作られない名前）
- **Task 8**: キーワードに文字列でない要素があるソースは，予定を読む前にそのソースだけ失敗にする（Python は該当の予定に当たったときだけ TypeError）— 設定ミスを早く見せる — Python で通っていた設定が失敗になる場合がある
- **Task 8**: 節 maintenance の日付は 2027-12-04 を今にした（計画の 2026-05 などは「2026-12-01 以降だけ」の検査に反する）．Z つきの時刻のベクタは UTC-3〜+20 で同じ日になる時刻にし，変換そのものは IcsTests がマシンのタイムゾーンで確かめる — 書き出すマシンのタイムゾーンでベクタが変わらないように — なし
- **Task 9**: game_dirs・game_exclude_dirs の相対パスは使わない（guard.ps1 は実行時のフォルダから解釈する）— 常駐する Agent ではどこから解釈されるか決まらず，意図しないフォルダがゲーム扱いになる — 相対パスを書いていた設定ではそのフォルダが効かなくなる
- **Task 9**: 除外名のワイルドカードは * と ? だけ（PowerShell の -like は [abc] も解釈する）．文字列でない要素は捨てる（guard.ps1 は文字列に変換して使う）— brief の決まりどおり — [ ] を使った除外名が文字そのものとして比べられる
- **Task 10**: Watcher.RunOnce は Busy（前の回が動いている）を最初に見る（brief は guardTask → 設定 → TooSoon → Busy の順）— 前の回と状態（LastOutcome など）を同時に触らないため．どちらの順でもその回は見張らない — 重なった回の結果が TooSoon でなく Busy と出る
- **Task 10**: TooSoon は「最後の行から 4 分 50 秒未満」（10 秒の許容）— タイマーは 5 分ちょうどより少し早く来ることがあり，秒単位の比較だと 1 回飛んで 10 分空く — 起動し直しや復帰の直後に，行の間隔が最短 4 分 50 秒になりうる（「5 分より短い間隔で足さない」から 10 秒ずれる）
- **Task 10**: Watcher.LastOutcome は WatchOutcome?（まだ 1 回も見張っていなければ null）．Busy で飛ばした回は入れない — 「まだ分からない」を Failed などと区別する — Task 13 の TrayState は null を受ける
- **Task 10**: トレイとメッセージの窓は Windows Forms（UseWindowsForms）にした（設計書 §8 で任されている）．agent.log の文は guard.log と同じく英語で固定（文面の対象外）— 一番小さい実装 — なし
- **Task 11**: プロセスの exe のパスは QueryFullProcessImageName（PROCESS_QUERY_LIMITED_INFORMATION）で取る（guard.ps1 は Get-Process の Path）— 管理者として動くゲームなど，Get-Process では取れないパスも取れる．遅い MainModule を使わない — guard.ps1 ではゲーム扱いにならなかったプロセス（権限が上のもの）がゲーム扱いになりうる
- **Task 12**: トーストは WinRT（ToastNotificationManager）で，名前は HKCU\Software\Classes\AppUserModelId\shiftlock.Agent（DisplayName=shiftlock）に登録して出す．登録は Agent の起動時と --install の両方で行う — NuGet を足さず，ショートカットも要らない．登録して出した通知が履歴に入ることをテスト（SHIFTLOCK_TEST_TOAST=1）で確認した — 目で見た確認はまだ（本人）
- **Task 12**: ポップアップは user32 の MessageBoxTimeoutW（20 秒），読み上げは SAPI.SpVoice（COM の遅延バインド），通知音は WinRT の MediaPlayer．NuGet の追加なし．Agent の TFM は net10.0-windows10.0.26100.0（ウィンドウと同じ）— 依存を増やさない — MessageBoxTimeoutW は文書化されていない関数（Windows XP から在り，WScript.Shell の Popup も使っている）
- **Task 12**: Watcher.RunOnce(dryRun: true) は結果を deliver に渡さない（件数をログに書くだけ）— dry_run で強制の実行が走る道を残さない（guard.ps1 -DryRun も出さない）— dryRun で通知の見た目は確かめられない
- **Task 13**: アイコンは絵のファイルを持たず実行時に描く（丸＋印: 平常は無地，注意は横棒，警告は !，設定エラーは ×，見張りを止めているときは縦棒 2 本）— 設計書 §8 で絵は任されている．バイナリを増やさず，色だけに頼らない — brief の Assets/*.ico は作っていない．exe 自体のアイコンは既定のまま
- **Task 13**: 文を足した: AGENT_RELEASE_PARTIAL（記録の一部が書けなかったが STOP.txt はある），AGENT_RELEASE_FAILED（STOP.txt も書けなかった．手で置く場所を知らせる），AGENT_TEST_TOAST・AGENT_TEST_POPUP（--test-notify）— 緊急解除が半端に終わったときに「解除できたのか」を本人に正しく伝える — brief の 8 個より 4 個多い
- **Task 13**: shiftlock-agent.exe --test-notify を足した（guard.ps1 -TestNotify と同じ．トースト → ポップアップと読み上げ．見張りはしない）— 通知が出るかを本人が確かめる入口が要る — 計画に無い入口が 1 つ増えた（強制には触れない）
- **Task 13**: TrayState.From の outcome は WatchOutcome?．Failed のときは見た目はフェーズのまま，ホバーだけ AGENT_TIP_FAILED．設定が今読めなければ（snapshot が null）最後の見張りが Ran でも ConfigError — brief の対応表に無い組み合わせを埋めた — なし
- **Task 14**: 置く形は <場所>\shiftlock.exe と <場所>\agent\shiftlock-agent.exe（設計書 §6.2 は「並べて置く」）．トレイは同じフォルダ → 1 つ上の順で shiftlock.exe を探す — どちらも自己完結で .NET のランタイム一式を抱えており，同じフォルダに混ぜると同じ名前のファイルを上書きし合う（apphost は同じフォルダの hostfxr.dll を使うので，片方だけ依存型にしても壊れる）— 設計書の文面と置き場所が 1 階層ずれる．--install に渡すパスが agent\ の下になる
- **Task 14**: ウィンドウは -p:PublishTrimmed=false で発行する（Release の既定の「削る発行」は IL2104 が警告→エラーで止まる．区切り 1 では発行していなかった）．Agent は自己完結・削らない（WinForms・COM・dynamic を使う）— 動く発行物を 1 手順で置く — 置く物が約 350 MB
- **Task 14**: 自動起動は HKCU の Run（値 shiftlock-agent）．タスクの状態と切り替えは タスク スケジューラの COM（Schedule.Service．schtasks の出力は表示言語で変わる）．タスク名は環境変数 SHIFTLOCK_GUARD_TASK で差し替えられる — 管理者権限なし・言語に依らない — なし
- **Task 14**: 動いている Agent を終わらせる合図（名前付きイベント）と --stop を足した．--uninstall と deploy.ps1 が使う．合図で終わらなければプロセスを止める — トレイのアイコンを残さず，書き込みの途中で止めない — 計画に無い入口が 1 つ増えた（強制には触れない）
- **Task 14**: --install は，タスクを無効にした後に状態を確かめ直し，無効になっていなければ失敗にして自動起動を取り消す．--uninstall でタスクを有効に戻せなければ終了コード 1（何も見張っていない状態を黙って残さない）．結果の文は AGENT_SETUP_*（両方の言語）— 二重の見張りと見張りなしを黙って残さない — なし
- **Task 15**: 本番のフォルダの判定は，Python 側は %USERPROFILE%\shift-lock・ホームの下の shift-lock・/mnt/<ドライブ>/Users/<名前>/shift-lock（WSL），C# 側は %USERPROFILE%\shift-lock（大文字小文字・末尾の区切りを区別しない）— WSL から本番を指しても断る — コピーを本番と同じ名前で別の場所に置いた場合は断らない（名前ではなく場所で見ている）
- **Final**: 設定の言語が inm のとき Agent の通知は標準語になる（Python 版は inm）— inm.py は配らない任意の言語で native/shared に書き出していない（区切り 1 と同じ決まり）— 切り替えると通知の口調が変わる．任意の言語を Agent でも使うなら別の作業
- **Final**: レビューが判断を保留した点 — リモート操作中かは常に「いいえ」（設計書どおり），起動からの秒数は Environment.TickCount64（guard.ps1 は LastBootUpTime．スリープや高速スタートアップをまたいだときの違いは未確認），ポップアップを UI でないスレッドから出す，捨てられた通知を出し直さない（Python 版と同じ）— どれも今のままにする — boot_grace_minutes（起動直後は強制しない）の効き方が guard.ps1 と変わる場合がある

## ブランチ全体のレビュー（2026-11-21）

Critical 0，Important 5（全部直した），Minor 9（下に送った）．

直したもの:

- #1 掴まれているファイルを空として読んで書き戻す — BusyFileTests.ReadJson_HeldFile_ThrowsBusy RED→GREEN（Store.ReadJson は掴まれていれば StateBusyException．壊れた中身は今までどおり空），suite 742/742
- #2 緊急解除が半端に終わったときトーストだけ — ReleaseFlowTests（Complete が偽）RED→GREEN．Tray は半端なら消えない窓で知らせる，suite 742/742
- #3 緊急解除とシャットダウンの予約の競合 — ReleaseFlowTests（abort が STOP.txt の前後 2 回）と ActionRunnerTests.Enforce_StopFilePlacedWhileScheduling_AbortsTheShutdown RED→GREEN，suite 742/742
- #4 --install の結果が PowerShell から見えない — README の手順を「| Out-Host; $LASTEXITCODE」に直した（実機で終了コードが見えることを確認．文書だけ）
- #5 deploy.ps1 が Agent を止めた後に失敗すると見張りが止まったまま — 新しい常駐を隣に置いてから名前を替える形にし，失敗したら残っている常駐を起動し直す（スクラッチで置き直しを確認．スクリプトなので自動テストなし）

送ったもの（直していない）:

- 緊急解除と見張りが同時に state.json を書くと released_at が上書きされうる（STOP.txt がある間は止まる．Python 版と同じ性質）
- Enforce.Release は最初の書き込み失敗で残りの乗務日の released_at を付けない（STOP.txt があれば止まる）
- 設定が後から現れても，トレイの見た目は次の見張り（最大 5 分）まで設定エラーのまま
- 古い Agent が動いている状態で --install すると，新しい exe はすぐ終わるのに「起動した」と出る
- 二重起動の防止はログオンのセッションごと（ユーザーの切り替えで 2 つ動きうる）
- --test-notify と --install は設定の読み込みで IOException 以外の例外を握らない
- トレイの毎分の更新が UI のスレッドでファイルを読む（応答しないドライブだとメニューが固まる）
- 通知音の MediaSource を明示的に解放していない
- --tick-once と tick_once.py はコピーの設定のカレンダーを実際に取りに行く（今回の突き合わせでも取りに行った）

## Python 版との突き合わせ（2026-11-21）

本番フォルダのコピー 2 つに，`pc/tools/tick_once.py` と `shiftlock-agent.exe --tick-once` を同じ入力（5 分おき 12 回．idle と game を混ぜる）で 3 通り通した．コピーは消した．

- 各回の結果の JSON と，`usage-pc.json`・`notified.json`・`state.json`・`maintenance.json`・`calendar.json`・`days.json`・`log/pc-*.jsonl`・`events.log` が一致．
- 違い 1: 設定の言語が配っていない任意の言語のとき，通知の文が違う（Agent は標準語）．言語をそろえたコピーでは文字列まで一致．
- 違い 2: カレンダーを実際に取りに行く回で，取れた時刻が 1 度だけずれた．2 回やり直して再現せず（取得の一時的な失敗とみる）．

## まだ確かめていないこと（本人が行う）

- 画面ロックの実機試験（合成フォルダに宣言を置いて 1 回）．スリープ・シャットダウンの実機確認．
- 目と耳での確認: トレイのアイコンとメニュー，ポップアップが 20 秒で閉じるか，読み上げ，聞こえる通知音（`shiftlock-agent.exe --test-notify`）．
- 本番への切り替え（`native/deploy.ps1` → `--install`）．手順は [native/README.md](../native/README.md)．

---

# ネイティブ版 区切り 3（アプリの稼働計測）の作業記録

issue #6．設計は [superpowers/specs/2026-11-22-native-app-meter-design.md](superpowers/specs/2026-11-22-native-app-meter-design.md)，計画は [superpowers/plans/2026-11-22-native-app-meter.md](superpowers/plans/2026-11-22-native-app-meter.md)．ブランチは `feature/native-app-meter`（`feature/native-agent` の上）．

## この計画で決めたこと

- 区切りをまたいだサンプルの増分は前の区間に入れる — そのサンプルは前の区間の終わりまでの経過を表すので — 違っていると区切りの直後の 10 秒ぶんが次の区間に寄る
- `t ≤ 最後に書いた t` の行は書かず，たまった分は次の区間に持ち越す — 同じ `t` を 2 回書かない決まりのため — 持ち越した区間の `span` が 5 分より長くなる
- 外出中の行もマスを「計測あり」にする（秒は足さない）— 外出中のマスだけ見張りのログのゲームが出る，という二重の数え方を避ける — 外出中にだけ動いたゲームは見張りのログからも出ない
- 分は四捨五入（`int(秒 + 30) // 60`，1 時間は 60 分で頭打ち）— 数十秒の端数を切り捨てて続けて 0 分に見えないように — 1 マスで最大 30 秒の誤差
- 書けなかった行はやり直さない — やり直すと同じ `t` を二度書きうる — 書けなかった 5 分ぶんが記録から欠ける（`agent.log` には 1 日 1 回書く）
- `--meter-once` は本番のフォルダでも断らない — ファイルを書かず `shifts.json` を読むだけなので — 本番の登録を読むことはある（出力に載るのは登録した名前だけ）
- `AppMeter` の `lastWritten` は「最初の時刻の 5 分の枠の終わりより後なら無視する（枠の終わりちょうどまでは有効）」— 別の日の値・時計が大きく戻った値を引き継がず，再起動直後に同じ `t` をもう一度書かないため（計画の字面どおりだと `SameT` と `Restart_DoesNotRepeatT` が通らない）— 時計が大きく戻った直後の行が 1 本，書かれないか重なりうる
- `Meter` のコンストラクタは `AgentLog` でなく `OncePerDay` を受ける — 同じ失敗を 1 日 1 回にする入れ物を `FallbackProcessSource` と共有するため — なし
- 文面のキーは `APPS_` で始める — `APP_` は `export_native.py` がネイティブの画面用に書き出す接頭辞なので，足すと書き出しに混ざる — 設計書 §4.1 は直した
- レポートは `span` が 86400 秒を超える行と，数が有限でない行（`Infinity`・`NaN`・bool・桁の多すぎる整数），`apps` が辞書でない行を飛ばす．`act` が `span` を超える項目も飛ばす — 壊れた行でレポートが落ちないように — 1 日を超える `span` の行は正しくても数えない
- PR #7 の Copilot の指摘 6 件（`shl app` が最上位が辞書でない `shifts.json` で落ちる／手書きの大文字のキーが二重に残る／レポートが壊れた行で落ちる 3 件／`apps` が壊れた行がマスを「計測あり」にする）は全部直した．先送りにしていた Minor のうち 4 件がこれで消えた

## レビューで送ったもの（直していない）

- `AppMeterTests` の `Sim.Go` は pid の再利用で `First` が効かない（期待値には影響しない）
- `AppMeter.cs` に冗長な 1 行がある
- `STATUS_INFO_LENGTH_MISMATCH` の再試行にテストが無い
- `DiagnosticsProcessSource` は想定外の例外が出ると，残りのプロセスを Dispose しない
- `meter failed` の文に `e.Message` が入る（Snapshot 失敗の経路．プロセス名が漏れる経路は見つかっていない）
- `--meter-once` は代替に落ちたとき `agent.log` に 1 行書きうる
- `MeterOnce` の 2 回目の時刻は実測でなく，`start + 10 秒`
- 手書きの大文字キーが `registered` にあると，`shl app add` で二重に並ぶ
- `shifts.json` の最上位が辞書でないと，`shl app` が `AttributeError` になる
- `log_read`（`store.py`）は JSON が辞書でない行・`t` が文字列でない行で `TypeError`（以前からの性質．app ログにも同じ経路）
- bool を数として受けないことのテストが無い
- `broken_lines` のテストは，壊れた行でもマスが「計測あり」になったことを見ていない
- `act` に上限が無く，`1e308` 級の行が 2 本あると合計が `inf` になり `int()` で `OverflowError`（`act > span` を飛ばせば直る）

## まだ確かめていないこと

- レポートの「開発(h)」の列と帯を本番のデータで見た確認（下の「本番で確かめたこと」の時点では，行が出ることまで）．
- 合成データでの Agent の 11 分の通し（5 分の区切りの行が 2 本出ること）: この PC では本番の Agent が動いていて，試験の Agent が同じ Mutex 名（`Local\shiftlock-agent`）でぶつかり起動できないので動かしていない．本番の Agent を止めない・触らないため．本人が本番に反映した後の確認に回す．
- 終了時の行（`Flush`）: Agent を終わらせるときにたまった分が書かれること．実機では未確認（テストでは確認済み）．
- 32 ビットでの `NtProcessSource`（構造体の大きさ・並びが違う）．x64 でしか確かめていない．
- `STATUS_INFO_LENGTH_MISMATCH` の再試行の経路（バッファが足りなかったときに取り直す部分）．テストは無い．実機で通ったかは不明．
- レポートの帯（15 分以上のカテゴリの並び・開発の緑と Adobe の紫が明るい・暗いテーマで見分けられるか）をブラウザで見た確認．

## 本番で確かめたこと（2026-11-22．本人が反映）

- `native\deploy.ps1` と `pc\deploy.ps1` で反映し，Agent を起動し直して `shl app add` で 1 件登録した．
- `log\app-*.jsonl` に 5 分の区切りごとの行が出る．登録した exe は `dev` で載り，登録していない名前は載らない．
- 10 分放置すると，最後の入力から 5 分でアクティブが止まり，次の区間は 0 秒になる（決まりどおり）．
- `agent.log` に `meter failed`・`fell back`・`not measuring` は無い（`NtQuerySystemInformation` は代替に落ちていない）．
- 確認の途中で分かったこと（計測の不具合ではない）: ゲームのコントローラーをつないだままにすると，Windows の無操作秒数（`GetLastInputInfo`）が 0 のまま増えない．見張りのログの `idle` も同じ間ずっと 0 だった．抜くと正しく増える．Steam がスティックをマウス操作に変換しているためと見ている（推測）．つないだまま正しく測るには別の手当てが要る（未着手．issue も未発行）．

# ネイティブ版 区切り 4（アプリの減速・一時停止）の作業記録

issue #9．設計は [superpowers/specs/2026-11-23-native-app-limit-design.md](superpowers/specs/2026-11-23-native-app-limit-design.md)，計画は [superpowers/plans/2026-11-23-native-app-limit.md](superpowers/plans/2026-11-23-native-app-limit.md)．ブランチは `feature/native-app-limit`．

## 区切り 4：下調べ（2026-11-23．設計書 §6.3）

自分で起動した CPU を回すだけの小さなプログラム（`burn`）と，ジョブ・一時停止を試す小さなプログラム（`probe`）で確かめた（どちらもリポジトリに入れない．本番のフォルダ・本人のアプリには向けていない）．詳細は設計書 §6.3 の各項目の下．

- **1 名前でジョブを開き直せるか: 開けない（要・本人の判断）．** ハンドルを持つプロセスを `Stop-Process -Force` で落とすと `OpenJobObjectW` は `GetLastError=2`（`Global\`・`Local\` をつけても同じ）．上限は効いたまま残る．Agent が落ちた後の戻し方は §6.3 の「代わりの案」になる．対象プロセスにジョブのハンドルを複製しておけば名前で開ける（実機で確認）が，対象に `PROCESS_DUP_HANDLE` で触るため，採るかは本人が決める．
  - **本人の決定（2026-11-23）: 「代わりの案」にする**（§6.3）．対象に `DuplicateHandle` しない．`Uncap` は Agent が持っているハンドルでだけ行い，名前で開き直さない．落ちた後に残った上限は，次の起動時に 1 回知らせるだけ（`AGENT_LIMIT_STALE_CAP`．対象のアプリを起動し直すと消える）．
- **2 入れ子のジョブ: 入れられる．緩められない．** すでにジョブにいるプロセスを別のジョブに入れて上限をかけられる．外側の上限より緩い値にしても，きつい側が効く．
- **3 上限の効き方:** `CpuRate` は PC 全体 = 10000 の値で，16 コアでも `--meter-once` の `cpu` が `CpuRate`/100 にほぼ一致（5000 → 52.5，2500 → 25.5，1000 → 10.0，50 → 0.4）．範囲は 1〜10000（0 と 10001 以上は `GetLastError=87`）．外すのは `ControlFlags = 0`．
- **4 一時停止と再開:** `notepad.exe` で，止めると全スレッドが `Suspended`・`Responding` が false，再開で戻る．suspend 2 回 → resume 1 回では止まったまま．もう 1 回で戻る．文字・窓・音を手で見る部分は本人の確認待ち．
- **5 権限が上のプロセス:** 保護されたプロセスは `OpenProcess` が `GetLastError=5`．管理者で起動したプロセスは未確認．
- **追加:** ジョブに入れるだけなら `PROCESS_SET_QUOTA | PROCESS_TERMINATE` で通る．

まだ確かめていないこと: 管理者で起動したプロセスの開き方，Steam が起動したゲームそのもの，止めた・上限をかけたアプリの体感（窓・音・文字入力），`OBJ_PERMANENT` のジョブ．

## 実装中に決めたこと（計画から変わった点を含む）

決めたこと — 理由 — 違っていた場合に起きること．

- 対象でなくなったアプリ（登録を外した・ゲームの判定が変わった・アンチチートの印が見つかった）は，上限を外し，止めていれば再開する．`LimitInput.Alive`（今動いている (PID, 開始時刻) の集合）に無いものは，終わったか PID が使い回されたとみなして再開せず記録から消す — 計画の記録の掃除（手順 4）だと，登録を外したアプリが止まったまま・上限つきのまま残るため．設計書の「対象でなくなったときは戻す」「止めたものは必ず再開」に合わせた（レビューで直した） — 違っていたら，余分な `Uncap`／`Resume` の呼び出しが増えるだけ
- Agent が持っていないジョブは外せない（`Uncap` は辞書に無ければ偽．名前で開き直さない）．`Uncap` が偽の操作は記録から外す — 下調べの本人決定（§6.3）．ハンドルが無いと再試行もできない — Agent が落ちた後の上限は次の起動時の知らせだけで，戻すにはアプリの起動し直しが要る
- `Limiter.RestoreAll`（終了時・トレイの緊急解除）は通知を出さない — 結果はトレイが出し，終了時は通知を出せない（`quit` 済み）ため．`RunOnce` の中で解除したときの報告は出る — `RestoreAll` 経由の解除では，解除の報告ポップアップが出ない
- 通知はロックの外で出す（`RunOnce` は排他を手放してから通知する）— ポップアップは最大 20 秒閉じないので，その間に緊急解除や次の回を待たせないため — 通知の表示より先に記録が進んでいる（通知の前に落ちると，その通知は出ない）
- `RunOnce` は前の回が動いていれば飛ばす（`SemaphoreSlim.Wait(0)`）．`RestoreAll`・`Startup` は最大 10 秒待つ — `Scheduler` は `System.Threading.Timer` で仕事の重なりを防がないため — 重なった回は 10 秒遅れる．`Job("limit")` は `quit` が立っていれば `RunOnce` しない（終了の `RestoreAll` の後に queue に残った回が再びかけるのを防ぐ）
- 判断できないとき（設定・宣言・計測が読めない，一覧の親の PID が取れない）は新しくかけず，かけたものを戻す．記録が掴まれていて読めない回は，何もしない（何を戻すか分からないため）．壊れた `limit.json` は `limit.json.bad` に名前を変えて 1 回知らせ，空で続ける — 設計書 §4.7・§4.9 — 掴まれた回は 10 秒遅れる
- `Startup`（起動時）は，見張りや計測を始める前に，記録の一時停止を再開する．`capped`・`announced`・`skipped` は空にし，`baseline`・`day`・`pending` は残す — 止めたまま Agent がいなくなると再開する者がいないため — 起動し直したときに予告・報告が 1 回ずつ出なおす
- 基準の使用率は `act` が 1 以上で `cpu` が 0 より大きい行の最大（無ければ `cpu` が 0 より大きい行の最大）— `act` が 1 以上でも `cpu` が 0.0 だけの行を基準にすると，上限が下限の 0.5% に張りつくため（設計書 §4.2 の「`cpu` 0.0 は待つ」に合わせた）— 基準が決まるまで上限をかけずに待つ
- `throttle` の段階 5 は予告しない（10% のままで変わらない）．段階 1〜4 の予告は各段階の 5 分前に 1 回，段階が狭くて重なるときは近い段階の分だけ — 設計書 §4.6 — 出しそびれた段階を後から出さない
- 乗務日が変わったときは，前の日の上限・一時停止を先に戻し（報告・`events.log` なし），`baseline`・`announced`・`skipped`・`pending` を捨てる — 昨日の通知を出さず，使い回しの PID を日が変わるときに再開しないため
- 通知・`agent.log`・`events.log` にアプリの名前を入れない（数だけ）．`--limit-once` は標準出力にだけ exe 名を出す（確かめ用）— 設計書 §4.6 — なし
- hook の印は，`UserPromptSubmit` が exit 0 で終わる道だけ足し，`Stop` で消す．書けなくても exit 2 にしない — Claude が動かない道では `Stop` が来ないため — 印が残っても 30 分で処理中でなくなる

## 最終レビューの直し（2026-11-23）

決めたこと — 理由 — 違っていた場合に起きること．

- 一時的に判断できない回（計測のサンプルが無い・30 秒より古い・親の PID が取れない，`state.json`・`shifts.json` が掴まれて開けない）は，何もせず飛ばし（判断・記録・通知なし），60 秒（`Limiter.UnknownGrace`）続いたら戻す側に倒す．判断できる回が来たら数え直し，前の回から 30 秒（`Interval` の 3 倍）を超えて空いたとき（スリープ・時計の飛び）も数え直す — スリープ復帰のたびに戻してかけ直し，予告・報告・`limit_release off` が誤って出るのを避けるため．設定が壊れている・宣言が無いなど確かな判断はすぐ戻す．`limit.json` が掴まれている回は元々何もしない（戻す記録が読めない）— 違っていたら，計測が止まっても 60 秒は上限・一時停止が残る（緊急解除は `state.json` が読めた回に今までどおり 10 秒以内に効く）
- 宣言に `limit_exclude`（外したゲーム）を写し，ゲームは「宣言の `limit_exclude` ∪ 今の `limit.exclude`」にあれば対象にしない — 宣言の後の `shl app limit include` が，同意の表示に載っていなかったゲームを対象に戻してしまうため．`limit_exclude` が無い古い宣言は今の設定だけ — 違っていたら，宣言の後に include したゲームにその場でかかる
- 出庫の時刻を過ぎて `TargetPlan` が次の乗務（か null）を返す回は，記録の日が今の乗務の日と違い効いているものがあれば，前の乗務の日・理由 `timeout` で戻して報告する．次の乗務のかけ始めは次の回 — 理由が `off`・日が次の乗務になるのを避けるため（効いているものが無い前の日の記録は，今までどおり日替わりの分岐で基準などを捨てるだけ）
- 緊急解除の `afterRelease`（排他を最大 10 秒待ちうる）は 2 回目の `AbortShutdown` の後 — シャットダウンの中止を遅らせないため
- 未処理の例外で落ちるときも `RestoreAll(null)` を呼ぶ（`AppDomain.UnhandledException`）．排他の待ちは 3 秒（`CrashRestore.Wait`）．画面のスレッドの例外も同じ受け口に流すため `Application.SetUnhandledExceptionMode(ThrowException)` にした（既定の画面のダイアログは出なくなる．`ThreadException` は使わない）— 設計書 §6.3 の「代わりの案」の要件 — 画面のスレッドの例外で，既定のダイアログで続けることはできなくなる
- 既定の動作（`enforce.action`）が `throttle`／`suspend` でも，設定メニュー（19）が落ちず，状態表示に「2h」を出さない

## 確かめたこと（2026-11-23）

- 自動テスト: `dotnet test native` は Core 852・Agent 221，pytest（WSL）338，`export_native.py --check` と `make_test_vectors.py --check` は一致．
- `docs/test-vectors/limit-stages.json` で Python（同意の表示）と C#（実行）の段階の時刻が一致する．
- hook の印は Git Bash 経由と WSL 経由の両方で，`UserPromptSubmit`・`Stop` とも終了コード 0（合成のフォルダ）．
- 合成のフォルダ（`SHIFTLOCK_DIR` を一時フォルダ，`SHIFTLOCK_GUARD_TASK=shiftlock-guard-test`）で `shiftlock-agent.exe --limit-once` を 1 回動かした（自分で起動した `burn` を `game_processes` で対象にした．宣言は `state.json` に手で書いた）．「すること」の JSON が出て，フォルダの中身は何も変わらなかった．就寝目安が 3 分後なら段階 1 の予告 1 件だけ，就寝目安を過ぎて基準の行があれば `cap`（段階 1 の 75% の上限）・`run`・次の段階（50%）の予告の順に出る．
- 下調べ（実機）の結果は上の「区切り 4：下調べ」．

## まだ確かめていないこと・先送り

- 上限をかけた後に，同じ名前でもう 1 つ起動したプロセスには上限がかからない（別の根で起動した同名のプロセスはジョブに入らない．安全側．次の回で入る可能性はあるが未確認）．
- （最終レビューで直した）計測が 30 秒以上止まる（スリープなど）と全部戻してかけ直し，予告・報告がもう一度出る件: 一時的に判断できない回は 60 秒飛ばすようにした（下の「最終レビューの直し」）．
- `limit.never`・`limit.protect` に拡張子なしで書いた名前（`claude` など）は `claude.exe` と一致しない（`system`・`vmmem` を保つため，`.exe` を補わない）．
- 管理者で起動したプロセスの開き方（`OpenProcess` が拒否される見込み．開けなければ見送り）．
- Steam が起動したゲームそのもの（入れ子のジョブは「ジョブに入っているプロセス」で確かめただけ）．
- 止めた・上限をかけたアプリの体感（文字入力が残るか・窓の「応答なし」の見え方・音・応答のしやすさ）．
- 実物の `WindowsLimitControl` を `Limiter` につないだ常駐の動き，`Scheduler` で計測の仕事と同時に走るときの挙動（テストは偽物の control）．
- （最終レビューで直した）`state.json` が一瞬読めないと宣言が無いものとして全部戻る件: 読めない（掴まれている）と読めて空を分け，読めない回は 60 秒飛ばすようにした．
- `Limiter.cs` は 581 行（400 行超．分けていない．分けるなら「ゲームの判定」と「記録の部品」）．
- 管理者の権限が要る `OBJ_PERMANENT` のジョブ，コントローラーをつないだままだと無操作秒数が 0 になる件（issue #8．基準は最大で取るので上限が低くなりすぎる方向には働かない）．

## 本人に残ること

- 本番への反映（`native\deploy.ps1`・`pc\deploy.ps1`）と Agent の切り替え．
- 本番での宣言（`shl enforce on throttle`／`shl enforce on suspend`）と，足すアプリの登録（`shl app limit add`）．
- 合成の対象での常駐の通し確認: 本番の Agent が固定名の Mutex（`Local\shiftlock-agent`）を持っていて試験の Agent を起動できないので，ここでは動かしていない．Agent を起動して，`limit.json` の `capped` が段階ごとに変わること・`hard_close` で止まること・`STOP.txt` を置くと 10 秒以内に戻ること・`events.log` に `limit_release	kaijo` が出ること・Agent を強制終了した後に止まったままのアプリが，起動し直すと再開されること・ゲームのフォルダに `EasyAntiCheat` という空のフォルダを作ると `limit-games.json` が `anticheat` になって対象から外れること．
- Steam のゲームでの入れ子のジョブ，管理者で起動したアプリ，体感（上の「まだ確かめていないこと」）．
- レポートの「強制モード」の欄に新しい 2 つの動作の名前が出ること．
- issue #9 のチェックリストの更新（対応したコミット・本人に残ること）．クローズは本番での確認の後．
