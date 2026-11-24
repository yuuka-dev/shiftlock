# PC 版 v11 設計書：ADHD 特性向けの追加機能

現行の動きは [pc-spec.md](pc-spec.md)，使い方は [pc/README.md](../pc/README.md)．この文書は **v11 で足す 5 項目の設計**で，実装はこの文書の承認後に機能ごとの issue／ブランチで進める．

| # | 項目 | 規模 | 種別 |
|---|---|---|---|
| A | 突発休の事後報告 | 中 | feat |
| B | 本人の要求があるときだけ PC をロック／スリープ／シャットダウンする（強制モード） | 大 | feat |
| C | 任意の音声ファイルで通知 | 中 | feat |
| D | Google カレンダー複数対応 | 中 | feat |
| E | `shl` だけ打ったら日本語のコマンド説明を出す | 小 | update |

「（判断）」と書いた箇所は依頼に無かったので設計側で決めたもの．違ったら直す．

---

## 0. 背景と設計原則

v10 までは「声かけだけ．PC は止めない．記録はその場で打つ」を前提にしていた．ADHD の特性を考えるとこの前提が崩れる場面がある:

| 困りごと | v10 の穴 | v11 の手当 |
|---|---|---|
| その場で記録できない（寝落ち・しんどくて PC を開けない） | `今日休む` は「これから出る乗務」にしか効かず，過去の乗務は自動で「出た」扱いになる | A: 後から `休んだ` `出た` と言える．言わない乗務は「未確認」として残し，勝手に「出た」にしない |
| 「あと 5 分」が効かず，惰性で続けてしまう | 通知は無視できる．Claude Code は止まるがゲーム・ブラウザは止まらない | B: 元気なときに `shl enforce on` で自分に予約しておけば，ハード以降に PC がロックされる．予約を促す通知は出さない |
| 通知音に慣れて気づかなくなる／フルスクリーン中は見えない | トーストの既定音だけ | C: 自分に効く音を鳴らす |
| 予定が複数のカレンダーに散っている | iCal 1 本だけ | D: 複数本をマージ |
| コマンドを忘れる | `shl` は状態表示．一覧は README にしかない | E: `shl` でコマンド一覧 |
| 乗務中の休憩に MacBook Pro から Parsec で PC を触ることがある（可能性） | 入力があれば「PC の前にいる」とみなし，リモートを区別しない | B: 強制は「寝る前」にだけ効かせ，猶予は **PC を触っている状態で出した警告から** 数える（Parsec で入った直後に黙って落ちない）．スリープ・シャットダウンは遠隔から戻せないので既定にしない |

B（強制モード）の目的は **寝る前に止めること**（ハードフェーズ）．乗務中・休憩中の開発（Parsec 経由を含む）を止めるためのものではない．

原則（v10 から引き継ぐものと，変えるもの）:

1. **判定は控えめ側に倒す**（誤記録より記録漏れの方がマシ）．ただし「漏れ」を「出た」と断定しない．不明は不明と表示する（A）．
2. **強制は本人が `shl enforce on` を自分で実行し，内容（何を・いつから・何時間・解除方法）を見て同意した乗務だけ．既定はオフ．宣言の入口はこのコマンドだけ**（Claude Code のキーワード・通知のボタン・設定の自動有効化は作らない）．**強制を促す通知・質問・Claude への文脈は入れない**（出してよいのは宣言済みの強制の予告と，実行・解除の報告だけ）．**Claude Code（hook・会話・Bash）からは強制の宣言・解除・設定変更をしない，できないようにする**．強制が続く時間は設定で変えられる．緊急解除の経路を必ず 2 つ以上残す（B）．CLAUDE.md の「絶対」項目はこの内容に改訂済み（2026-11-03 承認）．
3. 実行主体は分けたまま: 判断は Python（tick／hook），OS 操作は PowerShell（guard.ps1）．hook は exit 2 を増やさない．
4. 互換: 旧 `shifts.json`・旧 `calendar.json`・旧 `state.json` はそのまま読める．形式を変えるものは先に [data-format.md](data-format.md) を直す．

---

## A. 突発休の事後報告

### A.1 要件

- 出庫最終（既定 22:30）が過ぎた乗務について，後から「休んだ」「出た」を記録できる．
- 記録が無い過去の乗務は「未確認」として扱い，レポートで「出」と区別する．
- 乗務の翌日に PC を触ったとき，1 回だけ「昨日の乗務，出た？休んだ？」と聞く．
- `今日休む` を出庫最終の後（例 23:00）に打ったとき，翌日ではなく今日の乗務を休みにする（v10 は `target_plan` が翌日を指すので誤る）．

### A.2 用語

| 語 | 意味 |
|---|---|
| 過去の乗務 | `Plan.start_latest <= now` の乗務日 |
| 未確認 | 過去の乗務で `state.json` の `status` が無いもの（記録開始日 `since` 以降に限る） |
| 事後報告 | 過去の乗務への `休んだ` / `出た` の申告 |

### A.3 キーワード（Claude Code の hook，exit 2 で応答）

既存の `RE_DAY`（予定の休み／出勤）とは別に `RE_PAST` を足す:

```text
RE_PAST = ^(11/3 | 2026-11-03 | 今日 | 昨日 | 一昨日 | おととい)?  [は|の乗務]?
          (休んだ | 休んでた | 休みました | 出た | 出てた | 出勤した | 行った)  [メモ]?$
```

| 入力 | 対象 | 動作 |
|---|---|---|
| `休んだ` `昨日休んだ` | 日付なし → 直近 `confirm.lookback_days`（既定 7）日以内に終わった最後の乗務．`昨日` → 昨日が乗務日ならそれ | `status = yasumi`，note `late`，`events.log` に `yasumi\tlate ...`．返事に日付を必ず出す（「11/3(火) を休みにしたで」） |
| `11/3 休んだ` | その日 | 同上．乗務日でなければ「11/3 はもともと休みやで」 |
| `出た` `11/3 出た` | 同上 | `status = worked`．乗務日でない日なら `days.json` の `work` にも入れる（休みの日に出た） |
| `出発` `出発した` `行ってきます` `いってきます`（採用済み） | 今日の乗務（`target_plan`） | `status = worked`（出発済み）．以後その乗務はフェーズ free（通知・強制・Claude の拒否が止まる）．A.5 の確認も不要になる．Parsec で乗務中に触っても「寝る前」と誤認しない |
| `今日休む`（既存） | **今日が乗務日で出庫最終が過ぎていれば今日**（事後報告扱い，note `late`）．それ以外は従来どおり `target_plan` | （判断）`休む` だけのときは従来どおり未来の乗務 |
| `今のなし` | 直前の事後報告を取り消す | 既存の `undo` 機構に乗せる（1 時間以内） |

- 日付なしの `休んだ`／`出た` は，**直近に聞いた未確認の乗務（`confirm_asked` あり）があればそれを優先**し，無ければ直近の過去の乗務（判断）．返事に日付を出すので，違えば `11/3 休んだ` と日付付きで打ち直せる．
- 未来の乗務（`start_latest > now`）には `RE_PAST` は効かない（「まだ終わってへんで．休むなら『今日休む』」）．
- 対象が見つからないときは「直近 7 日に終わった乗務はないで」．

CLI／ダブルクリック:

| 入口 | 追加 |
|---|---|
| `shl yasumi [日付]` / `shl deru [日付]` | 日付付きなら過去の乗務の事後報告．`deru` は `status` を `worked` に（日付なしは従来どおり「やっぱ出る」＝ `yasumi` の取り消し．出発の意味には使わない） |
| `shl itte`（行ってきます） | 今日の乗務を出発済み（`worked`）に |
| `shl kakunin`（確認） | 未確認の乗務一覧を表示 |
| iPhone `mood.csv` | 2 列目に `yasumi` / `deta`（任意で `yasumi:2026-11-03` のように日付付き）．日付なしは「直近の過去の乗務」 |
| Android `events-android-*.jsonl` | `{"type": "yasumi", "date": "2026-11-03"}` の `date` を任意追加，`{"type": "deta", ...}` を新設（[data-format.md](data-format.md) §3.2 を更新）．**取り込み処理そのものは v11 の対象外**（Android 連携の issue で足す．[native-app-plan.md](native-app-plan.md) §8 の v11.5） |

### A.4 状態（`state.json`）

`shifts[日付].status` に **`worked`** を追加（`okita` `yasumi` `worked`）．

- `current()` は `worked` を `yasumi` と同じく読み飛ばす（出発済み＝その乗務についてもう言うことは無い）．`target_plan()` はそのまま（`worked` の乗務も対象．「行ってきます → 今日休む」が同じ乗務に効くように）．`行ってきます` は出発目安の 2 時間前から（昼間の常用句で保護が外れないように）．
- `confirm_asked`（ISO 時刻）を乗務日に追加．聞いたら記録し，2 回聞かない．
- `events.log` の種類に `worked` を追加（`yasumi` は note に `late` / `confirm`）．

### A.5 聞き返し（`confirm.mode`，既定 `ask`）

`unconfirmed_shifts(cfg, store, now)`: `since` 以降，`start_latest + (翌朝 shift_end_earliest)` を過ぎた乗務日で `status` が無く，`confirm_asked` も無いもの．直近 `lookback_days` に限る．

（判断）`okita` の乗務は **出たとみなして聞かない**（`confirm.okita_means_worked`，既定 true）．起きた記録があるのに出なかった日（起きたけど休んだ）は本人が `休んだ` と言う前提．理由: 毎日の乗務でいちいち「出た？」と聞かれるのは煩わしく，ADHD で危ないのは「起きた記録も PC の操作も無いまま乗務が過ぎた日」の方．`false` にすれば `okita` の乗務も聞く．

| 入口 | 動作 |
|---|---|
| tick（active のとき） | 最も古い 1 件について通知 1 回: 「昨日（11/3）の乗務，出た？ Claude Code か shl で『出た』『休んだ』を打ってな」．`confirm_asked` を記録 |
| hook（UserPromptSubmit，通常プロンプト） | `confirm_asked` が無ければ `additionalContext` に「返事の最後に一言『11/3 の乗務は出た？休んだ？（出た／休んだ）』と聞くこと．記録は shiftlock がやる」を入れ，`confirm_asked` を記録 |
| `閉店`（status） | 「未確認: 11/3, 11/5」の行を出す |

- 自動では `yasumi` にしない（控えめ原則）．
- ヒント（判断）: 乗務の時間帯（`depart_target` 〜 翌朝 `shift_end_earliest`）に PC 操作が合計 `confirm.home_activity_minutes`（既定 60）以上あれば，聞くときに「PC の記録やと家におったっぽいで（Parsec なら気にせんでええ）」と添える．ゲーム起動も同じ扱い．**Parsec 経由の操作と区別できないので，ヒントは聞くときの一言にとどめ，判定や表示には使わない**．
- `confirm.mode = off` で聞かない（`閉店` の表示だけ残る）．

### A.6 レポート（`build_report`）

`shift` の値を増やす:

| 値 | 表示 | 条件 |
|---|---|---|
| `worked` | 出 | `status = worked`，または `status = okita` で `okita_means_worked` が true（起きた記録があれば出たとみなす．注記に書く） |
| `unconfirmed` | 出? | 過去の乗務で `status` 無し（`okita_means_worked` が false なら `okita` も含む） |
| `yasumi` | 休 | 既存 |
| `planned` | 予定 | 既存 |

- 外出除外（`away`）は `worked` と `unconfirmed` の両方に適用（従来どおり出た前提．Parsec で触った時間はセルに活動として出るので見れば分かる）．
- サマリ: 「乗務 n / 予定 m 回」の内訳に「うち未確認 k」，「休み n（うち事後報告 k）」．注記に「出? は本人の確認が取れていない日」．
- 既に作った「前の締め期間の確定版」は，事後報告があれば次回の自動生成で上書きされる（`write_period_reports` は毎回 2 本作り直すので追加実装なし）．

### A.7 設定

```json
"confirm": {"mode": "ask", "lookback_days": 7, "home_activity_minutes": 60, "okita_means_worked": true}
```

設定メニューに 18) 「乗務の確認」を追加（mode と日数）．

### A.8 テスト

- `今日休む` を 23:00 に打つと今日の乗務が `yasumi` になる／翌日分は触らない
- `休んだ` の対象選択（直近，昨日，日付指定，乗務日でない日，未来の乗務）
- `出た` で非乗務日が `work` 例外になる
- 未確認の検出・1 回だけ聞く・`閉店` の表示・`okita` の乗務は聞かない（`okita_means_worked`）・日付なしの返事が聞いた乗務に紐づく
- レポートの `unconfirmed` 表示とサマリ内訳，`出発` → `worked` でフェーズが free になること
- mood.csv の `yasumi:2026-11-03` 取り込み（冪等）

---

## B. 強制モード（ロック／スリープ／シャットダウン）

### B.1 要件

- 目的は **寝る前に止めること**．本人が **`shl enforce on` を自分で実行し，内容（何を・いつから・何時間・解除方法）を見て同意した乗務だけ**，ハードフェーズに入って猶予が過ぎたら PC を **ロック**（既定），**スリープ**，または **シャットダウン**する．乗務中・休憩中（Parsec で遠隔から触っているとき）を止めるためのものではない．
- 既定はオフ．**宣言の入口は `shl enforce on` だけ**．Claude Code のキーワード，通知のボタン，「乗務日は毎回」「lastcall で聞く」のような設定は作らない．
- **強制を促す仕組みは入れない**．通知・Claude への文脈・質問で「強制する？」と誘導しない．出してよいのは，宣言済みの強制の予告（15 分前など）と，実行・解除の報告だけ．
- **Claude Code からは強制を一切操作できない**．hook に宣言・解除・設定変更のキーワードを置かない．`shl enforce on` の同意は対話入力（`y/N`）だけで，`--yes` のような非対話の入口を作らない（Claude Code の Bash は stdin が無いので同意できない）．
- **強制が続く時間（`duration_hours`，例 2h／3h）は設定で変えられ，宣言のたびに表示して確認する**．時間を過ぎたら，まだハードでも何もしない．
- 緊急解除の経路を複数持つ．解除は可能だが記録に残る（禁止ではなく摩擦）．
- アプリを個別に遅くする・一時停止するのは，本人が宣言した乗務で動作が `throttle`／`suspend` のときだけ（2026-11-23 に CLAUDE.md の「絶対」を改訂．設計は [superpowers/specs/2026-11-23-native-app-limit-design.md](superpowers/specs/2026-11-23-native-app-limit-design.md)）．アプリを終了させる処理は入れない．
- 動作に `throttle`（アプリの CPU を段階的に下げる）と `suspend`（さらに最低ラインで一時停止する）を足した（2026-11-23．区切り 4，issue #9）．宣言・同意・解除の入口は他の動作と同じ（`shl enforce on throttle|suspend`）で，実行はネイティブ版の Agent だけが行う．仕様は [pc-spec.md](pc-spec.md) §17.B・§7.6．

### B.2 動作

```text
hard_close   PC を触っている最初の tick = warned_at        +grace(15分)      実行後に PC を触ったら
   │            │ 警告: popup+音「15分後にロック」          │ 実行(lock)      │ 警告「5分後にまたロック」→ +relock(5分) で実行 … 最初の実行から duration_hours(2h) が過ぎるか max_runs(6) まで
   ├────────────┼──────────────┬───────────────────────────┼─────────────────┤
                           +10分 「5分後」     ↑ 1 分前通知は tick 間隔(5分)上できない．直前の tick で「次でロック」と出す
```

猶予は **hard_close からではなく，ハードに入ってから最初に「PC を触っている状態で出した警告」から** 数える．離席中（出かけている・寝ている）は警告も実行も起きず，戻ってきた（または Parsec で入った）ときに必ず 15 分の予告から始まる．

| 項目 | 内容 |
|---|---|
| 発動条件（全部満たす） | その乗務の `enforce` が有効（宣言して同意済み）／フェーズが `hard`／`warned_at` が記録済みで `now >= warned_at + grace_minutes`／`STOP.txt` が無い／起動から `boot_grace_minutes` 以上／前回実行から `relock_minutes` 以上（再警告も active の tick で出してから）／`consented_at` がある（同意済み）／最初の実行から `duration_hours` 以内／実行回数 < `max_runs`／PC が active（離席中なら意味がない）／リモート操作中でない（検出できる場合） |
| 強制の時間 | 最初の実行 `first_run_at` から `duration_hours`（既定 2h，宣言ごとに上書き可）の間だけ再ロックする．過ぎたら以後は何もしない（ハードが続いていても）．`start_latest` を過ぎた場合も同様 |
| リモート操作（Parsec） | 入力だけでは区別できない．猶予を警告から数えるので黙って落ちることは無く，予告の間に `kaijo.cmd` を遠隔から実行できる．**`sleep`／`shutdown` は遠隔から戻せない**（Wake-on-LAN が無い限り）ので，Parsec を使う運用なら `lock` だけにする（設定メニューで `sleep`／`shutdown` を選ぶときに注意文を出す）．Parsec のセッション検出（host のログ `%APPDATA%\Parsec\log.txt` の接続イベントなど）は **要調査**．検出できれば `enforce.skip_if_remote` で発動を止める |
| 発動しない | `yasumi`・`okita`（フェーズが hard でなくなる）／`start_latest` を過ぎた／guard の `-DryRun` |
| action | `lock`: `rundll32 user32.dll,LockWorkStation`．`sleep`: `[Windows.Forms.Application]::SetSuspendState(Suspend, $false, $false)`．`shutdown`: `shutdown.exe /s /t 60 /c "shiftlock: ..."`（Windows 自身がカウントダウンを出す．`/t` > 0 は `/f` 扱いになり未保存の作業は失われる．README に明記） |
| 実行の記録 | `state.json` `shifts[日].enforce.runs` に時刻と action，`events.log` に `enforce_run\tlock`，`guard.log` |
| 判断と実行の分離 | tick が `{"method": "enforce", "action": "lock", "text": "..."}` を actions で返す．guard.ps1 は **実行直前に `STOP.txt` をもう一度確認**してから実行．判断の数値（猶予・回数）は Python 側だけが持つ |
| guard.ps1 → tick の追加入力 | `--uptime-seconds`（`Win32_OperatingSystem.LastBootUpTime` から）．起動後の猶予判定に使う |

宣言済みの乗務では，lastcall と soft の通知に「今日は強制モード（ロック 2h）やで．解除は kaijo.cmd」を添え，hard の警告は `notify.hard`（popup）＋音（C の `enforce` 種別）＋ `speak` 相当の読み上げを必ず付ける．これは **予約済みの内容を知らせる** ためで，宣言していない乗務ではこの種の文言は一切出さない．

### B.3 宣言（オプトイン）

強制は **本人が `shl enforce on` を実行し，内容を見て同意した乗務** にだけ効く．それ以外の入口は無い．

| 項目 | 内容 |
|---|---|
| 宣言のコマンド | `shl enforce on [lock\|sleep\|shutdown] [時間]`（例 `shl enforce on`，`shl enforce on lock 3h`）．action と時間を省略すると設定の既定値 |
| **同意の確認（必ず）** | コマンドは内容を表示して `y/N` を対話入力で聞き，さらに **Windows のダイアログ（はい／いいえ）** を出して本人に押してもらう．両方そろって有効化（`consented_at` を記録）．端末でない（パイプ・Claude Code の Bash）・EOF・ダイアログで「いいえ」や 60 秒無応答は無効のまま．**`--yes` 等の非対話フラグは作らない**．限界: 同じユーザー権限のプロセスが `state.json` を直接書けば偽造できる．それを防ぐのは CLAUDE.md の禁止と，将来の権限分離（[native-app-plan.md](native-app-plan.md) §5.2）|
| 表示する内容 | action／予告が出る条件（ハード 17:00 以降に PC を触ったら 15 分前に予告）／**強制が続く時間（2h など）と終わる見込み時刻**／解除方法（`kaijo.cmd`・`STOP.txt`）／`sleep`・`shutdown` なら「未保存の作業は消える」「遠隔（Parsec）から戻せない」の注意 |
| 作らないもの | Claude Code の `強制` キーワード，`kyousei.cmd`（ダブルクリック），通知のボタン，`enforce.mode = ask/always` のような設定，lastcall で「強制する？」と聞く文脈 |
| 状態の確認 | `shl enforce`（宣言の有無・action・時間・実行履歴・`STOP.txt` の有無）．`閉店`（status）にも 1 行出す（表示だけ） |
| 対象の乗務 | `target_plan`（24 時間以内の次の乗務）．翌日に持ち越さない |
| 時間の変更 | 同意後でも最初の実行前なら `shl enforce on lock 3h` で宣言し直せる（もう一度 `y/N`）．実行が始まってからの変更は B.4 の解除扱い |
| 宣言の取り消し | `shl enforce off`．**`hard_close` までは即時．hard に入ってからは B.4 の解除扱い**（記録に残る） |

（判断・要確認）`okita` の扱い: 強制中に `おきた` を打った場合，`sleep_at` の記録か PC の離席記録（`recent_long_gap`）があれば通常どおり解除．どちらも無ければ `okita` にはせず「強制中は寝た記録が要るで．ほんまに緊急なら kaijo.cmd」と返す．理由: 強制の意味が `おきた` 一言で消えるなら宣言した意味が無い．緊急解除は別経路で確保する．

### B.4 緊急解除

| 経路 | 動作 | 使える場面 |
|---|---|---|
| `STOP.txt` を実行フォルダに置く | 存在する間，tick は enforce を返さず guard.ps1 も実行しない．中身は自由（理由を書く）．**Claude Code・タスク・hook が全部壊れていても効く** | いつでも（エクスプローラーだけで可） |
| `kaijo.cmd` ／ `shl kaijo` | `STOP.txt` を作る → `shutdown.exe /a`（予約されたシャットダウンを中止）→ `shiftlock.py enforce release` で `events.log` に `enforce_release` | ロック後にログインし直して実行．シャットダウンの 60 秒猶予内なら中止できる．Parsec 越しでも予告の 15 分の間に実行できる |
| `shl enforce resume` | `STOP.txt` を消して再開 | 解除後 |
| 自動の安全装置 | 起動後 `boot_grace_minutes`（15）は発動しない／ログイン直後 `relock_minutes`（5）は発動しない／`max_runs`（3）で打ち止め／`yasumi` `okita` で止まる／tick が落ちていれば何も起きない | — |

- 解除は **禁止しない**．代わりに `events.log` に残し，レポートの要約に「強制: 宣言 n 回／実行 n 回／解除 n 回」を出す（本人と訪看さんが見える）．
- Claude Code からの解除経路は作らない（宣言と同じく，強制の操作は Claude Code の外で行う）．
- `STOP.txt` を置きっぱなしにすると強制が黙って効かなくなるので，`閉店` と lastcall 通知に「強制: 解除中（STOP.txt）」と出す．
- （判断）宣言から解除までの遅延（`unlock_delay_minutes`）は入れない．ロック画面からはコマンドを打てないため，遅延があると「本当の緊急」に対応できない．摩擦は「記録に残る」だけにする．

### B.5 設定

```json
"enforce": {
  "action": "lock",           // lock | sleep | shutdown
  "duration_hours": 2,        // 最初の実行からこの時間だけ強制が続く（宣言ごとに上書き可．例 2 / 3）
  "grace_minutes": 15,        // 最初の警告からの猶予
  "relock_minutes": 5,        // 実行後に戻ってきてから次の実行まで
  "max_runs": 6,              // 安全弁．duration_hours 内でもこれ以上は実行しない
  "boot_grace_minutes": 15,   // 起動直後は発動しない
  "skip_if_remote": true      // リモート（Parsec）を検出できたときだけ効く．検出方法は要調査
}
```

設定メニューに 19) 「強制モード」（action／時間／猶予の **既定値** だけ．ここでは宣言できない）．**`sleep`／`shutdown` を選ぶとき，時間を変えるときは説明文（上の「表示する内容」と同じ）を出して `y` を入力させる**．メニューの説明文に「宣言は shl enforce on，解除は kaijo.cmd か STOP.txt」を必ず出す．

### B.6 ファイル

- `state.json` `shifts[日].enforce = {"action", "duration_hours", "declared_at", "consented_at"?, "via", "declined"?, "warned_at"?, "first_run_at"?, "runs": [{"at", "action"}], "released_at"?}`
- `STOP.txt`（実行フォルダ．存在だけを見る）
- `events.log` 種類: `enforce_declare` `enforce_consent` `enforce_run` `enforce_release`

### B.7 権限

ロック・スリープ・シャットダウンはいずれも **標準ユーザー権限で可能**（`shutdown` に必要な `SeShutdownPrivilege` は通常ユーザーにある）．管理者権限・サービス化はしない．タスクは現状どおり `-LogonType Interactive`（セッション 0 のサービスからは `LockWorkStation` が効かない）．

### B.8 テスト

- Python: 発動条件の全組み合わせ（宣言の有無・**同意の有無**・mode・フェーズ・`warned_at` からの猶予・**`duration_hours` の終わり**・STOP・起動猶予・relock・max_runs・active）．同意の流れ（`y`／`n`／非対話 EOF で無効／時間の宣言し直し）．hook に強制系のキーワードが無いこと（`強制` と打っても普通のプロンプトとして通る）．宣言していない乗務の通知・文脈に強制の文言が無いこと．離席中は警告も実行も出ないこと，戻ってから 15 分の予告が入ること．`おきた` の扱い．`出発` でフェーズ free になり発動しないこと．宣言／取消／declined．レポート要約．
- 設定メニュー: `sleep`/`shutdown`・時間の変更で説明文と `y` の入力を求めること．メニューから宣言できないこと．
- PowerShell: `guard.ps1 -DryRun` で `[enforce] lock` と表示されるだけで実行しない．`-TestEnforce lock` で実際にロックを 1 回試す（スリープ・シャットダウンは手動確認）．`kaijo.cmd` → `STOP.txt` ができて `-DryRun` が enforce を出さなくなる．
- 実機: `lock` → ログイン → 5 分猶予 → 再ロック → `kaijo.cmd` の一連．`shutdown` は 60 秒以内の `shutdown /a` を確認．

---

## C. 任意の音声ファイルで通知

### C.1 要件

- 通知の種類ごとに WAV／MP3 などのファイルを指定でき，通知と同時に鳴る．
- 未指定なら今までどおり（トーストの既定音）．
- 離席中には鳴らさない（既存の「active のときだけ通知」に乗る）．

### C.2 設定

```json
"notify": {
  "phase": "toast", "hard": "popup", "nag": "toast", "speak": false,
  "sound": {
    "default": "",            // 全種類の既定
    "lastcall": "", "soft": "", "hard": "", "go": "", "go_last": "",
    "nag": "", "confirm": "", "enforce": "", "auto_okita": ""
  },
  "sound_volume": 0.8,        // 0〜1（MediaPlayer のみ有効）
  "sound_max_seconds": 20     // 長い曲でもここで打ち切る
}
```

- 解決順: 種類 → `default` → なし．パスは Windows 形式（`C:\...`）で書く．Python 側は `to_local_path()` で存在確認し，無ければ `guard.log` 用に `warn` を actions に添える（1 日 1 回）．
- 設定メニューに 17) 「通知音」（種類ごとにパス．エクスプローラーの「パスのコピー」をそのまま貼れる）．

### C.3 tick → guard.ps1

actions の各要素に `sound`（パス or 空）を足す．同じ tick に複数の通知があれば **音は 1 回だけ**（`enforce` > `hard` > `confirm` > `phase` > `nag` の順で強い方）．

### C.4 guard.ps1 の再生

| 拡張子 | 方法 | 備考 |
|---|---|---|
| `.wav` | `(New-Object Media.SoundPlayer $path).PlaySync()` | 追加の Add-Type 不要．音量は OS 任せ |
| `.mp3` `.m4a` `.wma` `.aac` | `Add-Type -AssemblyName PresentationCore` → `System.Windows.Media.MediaPlayer`（`Open` → `Volume` → `Play`，`NaturalDuration` が取れるまで待ってから `min(duration, sound_max_seconds)` 秒待つ） | `powershell.exe` は STA 既定なので動く想定．タスクの実行時間上限（2 分）に収める |
| それ以外 | 再生しない．`guard.log` に書く | — |

- 音を鳴らすときはトーストの XML に `<audio silent="true"/>` を入れて既定音と二重にならないようにする．
- 失敗したら `guard.log` に書いて通知自体は出す．
- `-TestNotify` で `sound.default`（無ければ引数 `-Sound <path>`）を鳴らして試せるようにする．
- 順番: 音 → トースト／ポップアップ → 読み上げ（`speak`）．ポップアップは 20 秒ブロックするので音を先に鳴らす．

### C.5 テスト

- Python: 解決順，存在しないパスの warn，同一 tick での 1 回制限，ASCII JSON に日本語パスが入っても壊れない．
- PowerShell: 実機で WAV と MP3，長い曲の打ち切り，存在しないファイル．

---

## D. Google カレンダー複数対応

### D.1 要件

- 複数の iCal（秘密のアドレス）を同時に読む．ソースごとに「休み」「出勤」のキーワードを変えられる．
- 1 本が失敗しても他は更新し，失敗したものは前回の内容で続ける．
- 旧設定（`calendar.ics_url` 1 本）はそのまま動く．

### D.2 設定

```json
"calendar": {
  "sources": [
    {"name": "シフト", "ics_url": "https://...", "off_keywords": ["休み", "公休", "有給"], "work_keywords": ["出勤"]},
    {"name": "通院",   "ics_url": "https://...", "off_keywords": ["通院", "受診"], "work_keywords": []}
  ],
  "off_keywords": ["休み", "公休", "有給"],   // ソースに無いときの既定
  "work_keywords": ["出勤"],
  "sync_minutes": 60
}
```

- `load_config()` で旧 `ics_url` があれば `sources` の先頭（`name: "カレンダー"`）に変換する．設定メニューで保存したときに新形式へ移行（`ics_url` は消す）．
- 優先順位: 手動（`days.json`）＞ `sources` の上から順（同じ日に off と work が衝突したら上のソース）．
- 設定メニュー 11 を一覧＋ `a) 追加 / e番号) 編集 / d番号) 削除 / s) 今すぐ全部同期` にする．

### D.3 `calendar.json`

```json
{
  "fetched": "2026-11-03T09:00:00",
  "sources": {
    "シフト": {"fetched": "...", "error": null, "off": {"2026-11-05": "公休"}, "work": {}, "recurring_skipped": 0},
    "通院":   {"fetched": "...", "error": "11/03 09:00 URLError: ...", "off": {...}, "work": {}, "recurring_skipped": 1}
  }
}
```

- `merged_days()` は `sources` を順にマージ（先勝ち）．旧形式（`off`／`work` が直下）も読める．
- `sync_calendar()` はソースごとに `sync_minutes` を判定し，失敗はそのソースの `error` に残して他を続ける．戻り値はソースごとに 1 行．
- `閉店` はソースごとに「最終同期／エラー」を 1 行ずつ．

### D.4 影響しないもの

- `plans()` 以降（マージ後の例外日を見るだけ）．Android 向け `schedule.json`．
- RRULE（繰り返し予定）の展開は今回も対象外（初回の日だけ．件数は `recurring_skipped` に残す）．

### D.5 テスト

- 旧 `ics_url` → `sources` 変換，保存時の移行．
- 2 本の衝突（上が勝つ）と手動の優先．
- 1 本失敗・1 本成功の `calendar.json`．旧 `calendar.json` の読み込み．

---

## E. `shl` だけで日本語のコマンド説明

### E.1 要件

- `shl`（引数なし）→ 日本語のコマンド一覧を出す．**引数があるときは出さない**（今までどおり実行）．
- 状態表示は `shl status`．`status.cmd` は変更しない．

### E.2 仕様

- `shiftlock.py` に `help` サブコマンドを追加（`--config` は任意）．`shl` / `shl help` / `shl -h` / `shl ?` と，引数なしの `shiftlock.cmd` がこれを呼ぶ．
- 出力（関西弁で，実行例つき）:

```text
shiftlock の使い方（どのディレクトリからでも）
  shl status                 いまのフェーズ・次の乗務・2 週間の予定
  shl neru / okita / yasumi / deru   寝る / 起きた / 今日休む / やっぱ出る
  shl yasumi 11/3 / shl deru 11/3    終わった乗務の事後報告（休んだ / 出た）
  shl itte                   行ってきます（今日の乗務を出発済みに）
  shl kakunin                未確認の乗務
  shl yotei                  2 週間の出／休
  shl day off 11/5 通院      予定の休み（欠勤にはならへん）/ day work 11/8 / day rm 11/5 / day list
  shl enforce [on [lock|sleep|shutdown] [2h]|off|resume]  強制モードの確認・宣言（同意確認あり）・解除後の再開
  shl kaijo                  強制の緊急解除（STOP.txt を置く）
  shl report [14]            訪看さん向けレポート（締め期間 / 直近 14 日）
  shl sync                   カレンダーを今すぐ同期
  shl settings               設定メニュー
  shl learned / learn / unlearn   覚えた言い方
  scan-games / show-games    ゲーム判定の確認
Claude Code のプロンプトに打つ言葉は README の「解除ワード」を見てな
```

- `argparse` の英語エラー（不明なサブコマンド）は対象外（判断）．

---

## 横断事項

### 変更するファイル（実装の目安）

| ファイル | A | B | C | D | E |
|---|---|---|---|---|---|
| `shiftlock.py` | `RE_PAST` `cmd_past` `unconfirmed_shifts` `status=worked` `build_report` 設定 18 | `enforce_due` `cmd_enforce`（対話 `y/N`）`tick` の actions 設定 19．hook には触らない | `resolve_sound` actions に `sound` 設定 17 | `sources` 変換 `sync_calendar` `merged_days` 設定 11 | `help` |
| `guard.ps1` | — | `--uptime-seconds`，`enforce` の実行，`STOP.txt` 再確認，`-Release` `-TestEnforce` | `Play-Sound`，toast の `silent`，`-TestNotify` | — | — |
| `shiftlock-profile.ps1` | `kakunin` | `enforce` `kaijo` | — | — | 引数なし → `help` |
| `*.cmd` | — | `kaijo.cmd`（ASCII のみ） | — | — | `shiftlock.cmd` 引数なし → help |
| `deploy.ps1` | — | 配布ファイルに追加 | — | — | — |
| `docs/data-format.md` | state.json・mood.csv・events の追記 | state.json・events・`STOP.txt` | — | calendar.json 新形式 | — |
| `pc/README.md` | 解除ワード表・レポートの説明 | 「強制モード」節（データ損失の注意・解除方法） | 「通知音」節 | カレンダー節 | コマンド表 |
| `CLAUDE.md` | — | 「絶対」項目は改訂済み（2026-11-03） | — | — | — |
| `docs/pc-spec.md` | 各節を更新 | | | | |

### 実装順序と issue 分割（1 機能 1 issue，1 ブランチ）

1. **E** `shl` ヘルプ（小．`update/shl-help`）
2. **D** カレンダー複数（互換処理が独立していて安全．`feature/calendar-sources`）
3. **C** 通知音（PowerShell の実機確認が要る．`feature/notify-sound`）
4. **A** 事後報告（data-format と レポートに触る．`feature/late-report`）
5. **B** 強制モード（CLAUDE.md 改訂・安全装置・実機確認．最後．`feature/enforce`）

B は A の後にする理由: 強制中の `おきた` の扱いが A の「記録の信頼度」の考え方に乗るため．

### テストの流儀

- Python は既存どおり `now` を引数で渡す．guard.ps1 は `-DryRun` に enforce／sound の表示を足し，実行系は `-Test*` スイッチで 1 回ずつ手動確認．
- 実機確認が済むまで `enforce.mode` の既定は `off` のまま（出荷時に変えない）．

---

## 決定事項と残る確認

決まったもの（2026-11-03）:

- CLAUDE.md の「絶対」項目を改訂する（済み）．
- `行ってきます`／`出発` で出発済みにする仕組みを入れる．
- 強制の宣言は `shl enforce on` だけ．促す通知・文脈は禁止．Claude Code から強制は操作しない．
- `shl` 単体はヘルプ，状態は `shl status`．
- ネイティブ化の言語は C#（Windows）＋ Kotlin（スマホ）（[native-app-plan.md](native-app-plan.md)）．

残る確認:

1. **強制中に `おきた` と打ったときの扱い**（B.3）．例: 17:00 ハード → 17:15 ロック → ログインし直して Claude Code に『おきた』と打つ．
   - 案 a: 一言で解除する（強制も通知も止まる．ロックを「おきた」で抜けられるので自縛としては弱い）．
   - 案 b（設計の既定）: 『ねる』の記録か，PC を 2.5 時間以上触っていない記録があるときだけ解除．無ければ「寝た記録が要るで」と返して強制は続く．本当の緊急は `kaijo.cmd`．
2. 既定 action は `lock`，既定の強制時間は 2h（`shl enforce on lock 3h` や設定で変更可）でよいか．
3. Parsec セッション検出の調査を B の実装前にやるか．やらなくても「警告から猶予」で安全側に倒れている．
