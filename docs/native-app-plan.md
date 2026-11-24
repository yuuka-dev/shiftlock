# ネイティブアプリ化の計画：言語・構成・権限

PC 版（Python + PowerShell + WSL）と同等の機能をネイティブアプリとして作り直す場合の，開発言語と構成の検討．スマホ版の前提は [android-plan.md](android-plan.md)，現機能は [pc-spec.md](pc-spec.md)，v11 で足す機能は [pc-v11-design.md](pc-v11-design.md)．

## 1. 想定する利用者と求めるもの

| 利用者 | 環境 | 求めるもの | 権限 |
|---|---|---|---|
| 本人 | Windows 11 ＋ Claude Code ＋ iPhone／サブ Android．乗務中の休憩に MacBook Pro から Parsec で Windows を触ることがある | 現機能の全部．強制モード（目的は「寝る前に止める」．ロック中心．スリープ／シャットダウンは遠隔から戻せないので注意）．自分で閉じられない常駐でもよい | タスクスケジューラ・（必要なら）サービスなど強い権限を使ってよい．ただし **緊急解除** は必ず残す |
| 一般の人 | Windows 10 1809 以降／11．WSL も Python も無い | 乗務（夜勤）前の就寝管理・通知・レポート．Claude Code 連携は無くてよい | 標準ユーザーで動く．強制系は明示的な同意の後だけ |
| スマホ | Android（サブ端末）→ iOS（Mac が来てから） | 通知・ワンタップ記録・レポート閲覧（[android-plan.md](android-plan.md)） | OS の制約内（強制は不可能，通知だけ） |

## 2. 現機能を OS の機能に分解する

ネイティブ化で言語を決める材料は「どの OS 機能に，どれだけ深く触るか」．

| 機能 | 必要な OS 機能（Windows） | 深さ |
|---|---|---|
| 5 分おきの見張り（アイドル・ゲーム判定） | `GetLastInputInfo`，プロセス列挙とパス取得，レジストリ（Steam） | Win32 直叩き |
| 通知（トースト・ポップアップ・読み上げ・任意音声） | WinRT `ToastNotification`／`AppNotification`，`MessageBox`，`System.Speech`，`MediaPlayer` | WinRT＋.NET |
| 強制モード | `LockWorkStation`，`SetSuspendState`，`InitiateSystemShutdownEx`／`shutdown.exe` | Win32 |
| 常駐と再起動耐性 | タスクスケジューラ（COM `ITaskService`）か スタートアップ登録，トレイアイコン | COM／シェル |
| Claude Code hook | **数十 ms で起動する CLI**（プロンプトごとにプロセス起動，exit 2 と stdout JSON） | プロセス起動速度 |
| 設定 UI | ウィンドウ・フォーム | UI フレームワーク |
| レポート | HTML 生成，PDF 化（Edge ヘッドレス／WebView2 印刷／PDF ライブラリ） | ライブラリ |
| カレンダー同期 | HTTPS GET，iCal 解析 | 言語標準 |
| iPhone／Android 連携 | ファイル監視（iCloud・Google ドライブ） | ファイル I/O |
| リモート操作（Parsec）の検出 | Parsec host のログ／API（要調査．入力だけでは区別できない） | 外部依存 |
| ロジック（逆算・フェーズ・意図判定・睡眠推定） | なし | 純粋ロジック |

**強制モード・通知音・タスクスケジューラ・アイドル取得が Windows 固有で深い**．ロジックは純粋で，どの言語にも移せる．

## 3. 候補の比較

| 候補 | Windows 固有 API | スマホと共有 | 常駐の軽さ | hook CLI の起動 | 一般配布 | 一人開発＋Claude Code |
|---|---|---|---|---|---|---|
| **C# / .NET 10 ＋ WinUI 3** | ◎ 全部一級（WinRT・Win32・COM・`TaskScheduler` ライブラリ） | ✕ ロジックは別実装（Kotlin） | ○ 50〜100 MB | ◎ NativeAOT で数十 ms | ◎ MSIX／winget／署名の流れが整っている | ◎ 資料が最も多い |
| C# / .NET 10 ＋ WPF | ◎ 同上（トーストは Community Toolkit の `ToastNotificationManagerCompat`） | ✕ | ○ | ◎ | ○ MSI／MSIX どちらも | ◎ 枯れている．Hot reload も安定 |
| Kotlin Multiplatform ＋ Compose Desktop | △ JNA で Win32（`GetLastInputInfo`，`LockWorkStation`）は可．**WinRT トースト・COM は実質 PowerShell 呼び出し**になる | ◎ `shared` を Android／iOS／Windows で 1 本 | △ JVM 同梱で 150 MB 超，常駐メモリ 200 MB 級 | △ JVM 起動 0.5〜1 秒（GraalVM native-image で改善できるが手間） | △ jpackage で MSI は作れるが大きい | ○ |
| Flutter（Dart） | ○ `win32` パッケージ（FFI）で Win32 は直接呼べる．トーストは `windows_notification` 等，WinRT 直叩きは FFI が要る | ◎ Windows／Android／iOS を 1 コードベース．ホーム画面ウィジェットは `home_widget` | ○ 30〜50 MB | ○ AOT exe で速い | ○ 自前インストーラ（MSIX も可） | ○ ただし [android-plan.md](android-plan.md) の KMP 方針を捨てることになる |
| Tauri 2（Rust ＋ Web UI） | ◎ `windows` クレートで Win32／WinRT／COM 全部 | △ モバイルは Tauri 2 で可だが，ウィジェット・バックグラウンドは Kotlin／Swift のプラグインが要る | ◎ 10 MB 級，WebView2 利用 | ◎ | ○ MSI／NSIS | △ Rust の学習コスト |
| Python 継続（Windows ネイティブ Python ＋ PyInstaller ＋ 薄い GUI） | △ 今の PowerShell 分担のまま | ✕ | ○ | △ Python 起動 0.1〜0.3 秒（今と同じ） | △ PyInstaller の SmartScreen 問題，WSL 依存を外す作業が要る | ◎ 今のコードを活かせる |

### 「タスクスケジューラを操作するなら WinUI 3」について

半分正しい．タスクスケジューラの操作は **UI フレームワークとは無関係**で，.NET なら WinUI 3 でも WPF でも同じライブラリ（`Microsoft.Win32.TaskScheduler`）で書ける．WinUI 3 を勧められた本当の理由は「.NET で Windows を深く触るなら今の標準 UI が WinUI 3 だから」だと思う．WinUI 3 の利点は，トースト通知のボタン（「昨日の乗務，出た？ [出た][休んだ]」のような確認．強制を促すボタンは作らない）を Windows App SDK の `AppNotificationManager` で扱いやすいこと，Fluent の見た目，MSIX 配布．欠点は資料が WPF より少なく，Hot reload やウィンドウ周りに癖があること．**C# を選ぶなら UI は WinUI 3 を第一候補，詰まったら WPF**．どちらもロジック層（クラスライブラリ）は共通なので乗り換えは UI だけで済む．

## 4. 推奨

### 4.1 結論

**Windows は C# / .NET 10（UI は WinUI 3，予備に WPF），スマホは [android-plan.md](android-plan.md) どおり Kotlin Multiplatform．ロジックの同一性は「仕様書＋共通テストベクタ」で担保する．**（2026-11-03 決定）

理由:

1. v11 で足す機能（強制モード・通知音・タスクスケジューラ・未確認乗務の聞き返しトースト）は全部 Windows API 直結で，C# なら PowerShell を挟まずに書ける．KMP Desktop や Flutter だと結局 PowerShell／C++ を呼ぶことになり，今の `guard.ps1` と構造が変わらない．
2. 一般配布（コード署名・MSIX／winget・SmartScreen・自動更新）は .NET が最も整っている．
3. スマホ側は「計算は PC が正，スマホは `schedule.json` を読んで通知と記録をする」と決めてあり，**共有したいロジックが実は少ない**．意図判定の正規表現表のような小さいものは，JSON のデータとして両方から読めば二重実装にならない．
4. Claude Code hook は NativeAOT の CLI にすれば WSL 版・Windows 版どちらの Claude Code からも数十 ms で動く（WSL からは `/mnt/c/.../shl.exe` を直接実行できる）．

一つの言語に揃えることを最優先にするなら **Flutter** が唯一「Windows＋Android＋iOS を 1 コードベースで，Win32 も FFI で直接呼べる」選択肢．その場合は android-plan.md を Flutter に書き換える．KMP Desktop は「常駐が重い・WinRT が遠い」ので Windows 側には勧めない．

### 4.2 構成（Windows）

```text
shiftlock.Core        クラスライブラリ．逆算・フェーズ・乗務日・意図判定・睡眠推定・レポート生成・設定の読み書き
                      依存なし（Windows API を呼ばない）．テストはここに集中
shiftlock.Agent       常駐（トレイ）．5 分おきの見張り・通知・強制・カレンダー同期・整理・Drive／iCloud の取り込み
                      タスクスケジューラのログオン時タスクで起動．落ちたら 1 分後に再起動（タスクの設定）
shiftlock.App         設定 UI（WinUI 3）．Agent とは別プロセス．閉じても Agent は動く
shl.exe               CLI（NativeAOT）．Claude Code hook・コマンド・ダブルクリック用．Agent が止まっていても動く（ファイルを直接読む）
```

- データは今と同じ `%USERPROFILE%\shift-lock`．**ファイル形式は [data-format.md](data-format.md) をそのまま使う**（Python 版と同じファイルを読み書きできれば，移行期間に両方を並走させられる）．
- Agent と shl.exe は同じ Core を使い，同じファイルを読む．競合は今と同じく「書き手ごとにファイルを分ける＋一時ファイル→置換」で避ける．
- Python 版の `tick`／`hook` の判断ロジックを Core に移す．PowerShell でやっていた OS 操作は Agent に移る．

### 4.3 ロジックの同一性（Windows ⇄ スマホ）

- 仕様は [pc-spec.md](pc-spec.md) を正とする．
- `docs/test-vectors/*.json` に「設定・状態・now → 期待する Plan／フェーズ／意図」のテストベクタを置く．Python 版のテストから生成する（Python 版が最初の「正」）．
  - C#（`shiftlock.Core`）は **全部** 読む（Plan・フェーズ・意図・睡眠推定）．
  - Kotlin（`shared`）が読むのは **意図判定だけ**（Android v1 で移植する範囲）．スマホは Plan・フェーズを計算せず `schedule.json` を読むだけなので，そのベクタは使わない．スマホ側で計算するようになったら範囲を広げる．
- 意図判定の正規表現表と学習済みフレーズは JSON（`intents.json`）にして両方から読む．正規表現の方言差（.NET／Kotlin(JVM)／Python）に注意して，`\p{..}` や lookbehind を避けた書き方に揃える．

## 5. 権限と緊急解除

### 5.1 標準権限で足りる範囲（段階 1，まずこれ）

| やりたいこと | 必要な権限 |
|---|---|
| ログオン時に自動起動，5 分おき，落ちたら再起動 | 標準ユーザー（自分のタスクは自分で登録できる．管理者不要） |
| トースト・ポップアップ・音・読み上げ | 標準ユーザー |
| ロック・スリープ・シャットダウン | 標準ユーザー（`SeShutdownPrivilege` は通常ユーザーにある） |
| アイドル取得・プロセス列挙 | 標準ユーザー（他ユーザーのプロセスは見えなくてよい） |

**本人がタスクマネージャーで Agent を止められる**．これが段階 1 の緊急解除でもある．加えて `STOP.txt`（[pc-v11-design.md](pc-v11-design.md) B.4）はそのまま残す．

### 5.2 「自分で止められない」常駐（段階 2，必要になったら）

ADHD の自縛（self-binding）として「衝動で Agent を殺せない」ことに価値があるなら，サービスを足す:

```text
shiftlock.Watchdog（Windows サービス，LocalSystem）
  └ ユーザーセッションに Agent が居なければ起動し直す（CreateProcessAsUser か，ログオン時タスクの起動）
     自分では通知もロックもしない（セッション 0 からは LockWorkStation もトーストも効かない）
shiftlock.Agent（ユーザーセッション）
  └ 今までどおり通知・強制を行う
```

- 止めるにはサービスの停止（**UAC の管理者確認**）が要る．これが段階 2 の「摩擦のある緊急解除」．
- それでも `STOP.txt` は Agent が見るので，エクスプローラーでファイルを置けば強制だけは止まる（通知は続く）．**UAC が通らない状況でも最低限の逃げ道が残る**．
- 一般の人には段階 2 を提供しない（インストールに管理者権限が要り，誤解されやすい）．
- サービスは「Agent を生かす」以外の権限を使わない（設計で縛る）．

### 5.3 緊急解除の一覧（段階 1／2 共通）

| 経路 | 効く範囲 | 必要なもの |
|---|---|---|
| `STOP.txt` を置く | 強制だけ止まる．通知は続く | エクスプローラー |
| トレイメニュー「緊急解除」／`shl kaijo` | 同上＋予約シャットダウンの中止 | ログインできること |
| Agent を終了（段階 1） | 全部止まる | トレイメニュー「終了」（確認なし）か，タスクマネージャー |
| サービス停止（段階 2） | 全部止まる | 管理者（UAC） |
| セーフモード起動 | 全部止まる | 物理アクセス |

解除は全部 `events.log` に残し，レポートに回数を出す．

区切り 2 で作ったのは段階 1 の分: トレイの「緊急解除」は `guard.ps1 -Release` と同じこと（予約済みシャットダウンの中止 → `STOP.txt` → 宣言に `released_at` と `events.log`．理由は `tray`）をする．トレイに置くのは解除だけで，宣言・再開（`shl enforce resume`）・設定は置かない．

## 6. 一般の人向けに足すもの

- **初回設定ウィザード**（出庫時刻・準備時間・睡眠目標・曜日）．設定メニューの番号選択は本人向けのまま残す．
- 強制モードは **本人が明示的に操作（`shl.exe enforce on` か，アプリ内の宣言ボタン）して内容に同意した乗務だけ**．促す通知・トーストのボタンは出さない（[pc-v11-design.md](pc-v11-design.md) B の原則と同じ）．強制が続く時間は設定で変えられる．既定は `lock` だけ．`shutdown` は「詳細設定」の奥．解除方法を同じ画面に書く．
- Claude Code hook は「開発者向け」の項目として任意．一般化するなら「任意のコマンドの前に判定を挟む」仕組みにしない（責任範囲が広がる）．
- データは端末内のみ．外部送信はカレンダー取得だけ．健康っぽいデータを扱うので，レポートに「本人が作った記録であり診断ではない」注記を残す．
- 配布: MSIX（署名は Azure Trusted Signing か EV 証明書）＋ winget．自動更新は MSIX のアップデート機構を使う．
- 対応 OS: Windows 10 1809 以降（Windows App SDK の下限）．

## 7. スマホ側（Android → iOS）

[android-plan.md](android-plan.md) のまま．補足:

- iOS は Compose Multiplatform の iOS ターゲットで同じ `shared` を使う（Compose Multiplatform は 1.8 で iOS が stable）．Mac と Apple Developer Program が要る．
- iOS のバックグラウンドは制限が強い．通知は **`schedule.json` を読んだ時点でローカル通知を 14 日ぶん予約**する方式（Android と同じ）で足りる．画面利用の記録は iOS では取れない（Screen Time API は自アプリ外の利用を出さない）ので，iOS は「手動記録＋通知」に割り切る．
- スマホからの強制（ロック）は OS が許さない．スマホは声かけ専用．

## 8. 段階計画

| 段階 | 内容 | 目的 |
|---|---|---|
| v11（Python 版） | [pc-v11-design.md](pc-v11-design.md) の 5 項目を今の構成で実装 | 機能と数値（猶予・回数・閾値）を本人の生活で固める |
| v11.5（Python 版） | `schedule.json` の書き出しと `events-android-*.jsonl` の取り込み（[data-format.md](data-format.md) §3）を Python 版に足す | Android v0 の前提．v12 の Core にもそのまま移す |
| v12-α | `shiftlock.Core`（C#）と `shl.exe`．Python 版と同じファイルを読み書きし，テストベクタで一致を確認．hook を `shl.exe` に切り替え | ロジック移植の検証．WSL 依存を外す |
| v12-β | `shiftlock.Agent`（トレイ常駐）．`guard.ps1` とタスクを置き換え | PowerShell を無くす |
| v12 | `shiftlock.App`（WinUI 3 設定画面）．MSIX 化 | 本人の運用を全面移行 |
| v13 | 一般配布（ウィザード・署名・winget） | 一般の人向け |
| 並行 | Android v0 → v1，iOS | スマホ |

Python 版は v12 が安定するまで残す（同じデータ形式なので並走できる）．

**2026-11-17 に順番を入れ替えた．** 上の表では GUI が最後（v12）だが，画面ごとに「その画面に要るロジックだけ」を C# に移し，区切りごとに動くものを出す: 1 状態と記録の画面 → 2 トレイ常駐と通知 → 3 設定画面と初回ウィザード → 4 レポート → 5 配布．hook（`shl.exe`）と強制モードはこの 5 つとは別に扱う．区切り 1（状態と記録の画面）は実装済みで，設計は [superpowers/specs/2026-11-17-native-status-window-design.md](superpowers/specs/2026-11-17-native-status-window-design.md)．上の表は構成（Core／Agent／App／`shl`）の説明として残す．

| 区切り | 内容 | 状態 |
|---|---|---|
| 1 | 状態と記録の画面（`shiftlock.exe`） | 実装済み |
| 2 | トレイ常駐と見張り（`shiftlock-agent.exe`．`tick` の判断を全部 C# に移し，`guard.ps1` とタスク `shiftlock-guard` を置き換える） | 実装済み．設計は [superpowers/specs/2026-11-17-native-agent-design.md](superpowers/specs/2026-11-17-native-agent-design.md)．本番への切り替えは本人が行う（[native/README.md](../native/README.md)） |
| 2 の続き ② | アプリごとの稼働計測（同じ Agent に載せる．`log/app-*.jsonl`・`shl app`・レポートのカテゴリの帯と列） | 実装済み（issue #6）．設計は [superpowers/specs/2026-11-22-native-app-meter-design.md](superpowers/specs/2026-11-22-native-app-meter-design.md)，作業記録は [native-agent-worklog.md](native-agent-worklog.md) の区切り 3 の節．本番への反映は本人が行う |
| 2 の続き ③ | 減速・一時停止（同じ Agent に載せる．強制モードの動作 `throttle`／`suspend`） | 実装済み（issue #9．合成の対象での通し確認と本番での確認は本人に残る）．設計は [superpowers/specs/2026-11-23-native-app-limit-design.md](superpowers/specs/2026-11-23-native-app-limit-design.md)，作業記録は [native-agent-worklog.md](native-agent-worklog.md) の区切り 4 の節 |
| 3 | 設定画面と初回ウィザード | 未着手 |
| 4 | レポート | 未着手 |
| 5 | 配布 | 未着手 |

計画の具体化の範囲（判断）: α・β・v12 は作業単位まで分解する．v13（一般配布）と Android／iOS はマイルストーンだけにし，それぞれ着手時に別の計画を書く．α は v11 の数値（猶予・回数・閾値）が本人の運用で固まってから始める．v13 を見据えて α の時点でやっておくのは **文言の外出し**（関西弁の直書きをやめる）だけ．

## 9. 未決事項

1. ~~言語を「C#（Windows）＋ Kotlin（スマホ）」の 2 本にするか，Flutter 1 本にするか~~ → **2 本で決定**（2026-11-03）．
2. 段階 2（サービス化）を v12 に入れるか，v11 の運用で「Agent を自分で止めてしまう」ことが実際に起きてから決めるか．**推奨は後者**．
3. レポートの PDF 化を Edge ヘッドレス（今と同じ）にするか，WebView2 の印刷か，PDF ライブラリ（QuestPDF 等）にするか．移行期は Edge のままでよい．
4. Claude Code hook を一般向けにも出すか（出さない想定）．
