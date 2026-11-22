# ネイティブ版 区切り 2：トレイ常駐と見張り Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** トレイ常駐の `shiftlock-agent.exe` を作り，`guard.ps1` と Python の `tick` を置き換えて，見張りと通知に Python も PowerShell も要らない状態にする（issue #4）．

**Architecture:** 見張りの判断は全部 `shiftlock.Core`（Windows API を呼ばない）に移し，Python 版に合成データを通した結果 `docs/test-vectors/tick.json` と突き合わせる．`shiftlock.Agent`（WinUI なしの常駐）は Windows 側の値を取り，Core の `Tick.Run` を 5 分おきに呼び，返ってきた通知と強制の実行を出すだけにする．Windows に触る部分はインターフェースの裏に置いてテストで差し替える．

**Tech Stack:** C# / .NET 10（SDK 10.0.401），xUnit 2.9，System.Text.Json，Windows Forms（トレイ．別の方法でもよい），Python 3.8+（書き出しと pytest）

**Spec:** [docs/superpowers/specs/2026-11-17-native-agent-design.md](../specs/2026-11-17-native-agent-design.md)（以下「設計書」）．実装者は両方読む．移植元は `pc/shiftlock/` の `tick.py`・`usage.py`・`notify.py`・`plan.py`（`phase_messages`・`no_sleep_intervals`）・`autowake.py`・`phone.py`・`records.py`（`_late_report` まわり）・`attendance.py`・`days.py`・`confirm.py`・`enforce.py`・`calendar_sync.py`・`maintenance.py`・`report.py`（`cleanup_reports`）・`store.py`・`config.py`，それと `pc/guard.ps1`．

## Global Constraints

- .NET 10．`native/Directory.Build.props` の設定（Nullable・警告はエラー）に従う．`*.cs`・`*.json` は UTF-8・LF．コメントは日本語．
- Python 版が正．**Python の判断は変えない**（足してよいのは `pc/tools/` の道具と `i18n` の文だけ）．`*.py` は UTF-8・LF・標準ライブラリのみ．
- 文面を C# に直書きしない．`pc/shiftlock/i18n/ja.py` と `ja_kansai.py` の両方に同じ名前で足し，`python3 pc/tools/export_native.py` で書き出して一緒にコミットする．`inm.py` は触らない．
- 判断は `shiftlock.Core` に置く．Core は Windows API・UI を呼ばない．どの関数も `now` を引数で受ける．
- Python の `tick` と Agent を同時に見張らせない．`log/pc-*.jsonl` に 5 分より短い間隔で行を足さない．
- 強制を促す通知・質問を出さない．トレイに強制の宣言・設定・再開を置かない（解除だけ置く）．C# に宣言の入口（`enforce_declare` 相当）を作らない．
- 本番の実行フォルダ `%USERPROFILE%\shift-lock` に向けて動かさない・編集しない．確認は `SHIFTLOCK_DIR` を合成データかコピーに向ける．個人データ（`shifts.json`・`state.json`・`log/`・`events.log`・`mood.csv` など）をコミットしない．実データの値を文書・テスト・コミットに書き写さない．
- テストベクタの日付は合成（2026-12 のもの）だけ．
- 見張り 1 回の中の例外で Agent を落とさない．
- `*.ps1` を足すなら UTF-8 BOM・CRLF．`*.cmd` は ASCII・CRLF．
- コミットは日本語で `<型>(native): タイトル (#4)`．`Co-Authored-By` などの行は付けない．
- テスト: C# は Windows で `dotnet test native`．Python は WSL で `cd pc && python3 -m pytest -q tests`（`.venv` を有効にして）．両方通してからコミット．

## Review Focus

仕様が明示しないが，使っていて当たりやすい入力．各行のテストは担当タスクに入れてある．

1. **ほかの書き手がファイルを掴んでいる**（ウィンドウや hook が `state.json`・`events.log` を書いている最中）: その回の見張りは失敗をログに書いて終わり，次の回は普通に動く．Agent は落ちない．（Task 10）
2. **手で壊れたファイル**（`usage-pc.json` が空・JSON でない，`log/*.jsonl` や `mood.csv` に壊れた行，`notified.json` に読めない時刻）: 壊れた分を無いものとして進む．例外で止まらない．（Task 2・3・5）
3. **時計が戻った・ログの最後の行が未来**（時刻合わせ，別の PC からコピーしたフォルダ）: 「5 分たっていない」が永久に続かない．最後の行が今より後なら見張る．（Task 10）
4. **データのフォルダが無い・後から現れる**（初回起動，外したドライブ）: 設定エラーのアイコンで待ち，現れたら次の回から見張る．（Task 10・13）
5. **通知が長く居座る**（ポップアップ 20 秒＋通知音 20 秒が何件も）: 次の回と重ならず，「終了」を選んだら出し残しを捨ててすぐ終わる．（Task 12）

## ファイルの地図

```
pc/tools/export_native.py            文面のキーを足す．tick.json を作る（節を順に足す）
pc/tools/tick_once.py                新規．時刻を指定して tick を 1 回（突き合わせ用．Task 15）
pc/shiftlock/i18n/ja.py, ja_kansai.py  AGENT_* の文を足す
pc/tests/test_export_native.py       tick.json と AGENT_* の確認を足す
docs/test-vectors/tick.json          新規（生成物）
native/shared/messages.*.json        生成物
native/shiftlock.Core/
  ShiftConfig.cs        設定の節を足す（Task 1）
  Store.cs              ログ・印・履歴の読み書きを足す（Task 1）
  LocalPath.cs          /mnt/c/... を C:\... に（Task 1）
  Tick.cs               1 回の見張りの本体（Task 2 で作り，以後のタスクが段を足す）
  Usage.cs              触っているか・離席・声かけ（Task 2）
  NotifyLog.cs          通知の間引き・通知音（Task 3）
  PhaseText.cs          フェーズ通知の文（Task 3）
  AutoWake.cs           自動「起きた」（Task 4）
  Phone.cs              iPhone の記録（Task 5）
  LateReport.cs         事後報告と，それが使う当日欠勤の目安・例外日（Task 5）
  Confirm.cs            終わった乗務の確認（Task 6）
  Enforce.cs            強制の発動判定と解除（Task 7）
  CalendarSync.cs       iCal の解析と同期（Task 8）
  Maintenance.cs        1 日 1 回の整理（Task 8）
  Games.cs              ゲームの判定（Task 9）
native/shiftlock.Core.Tests/
  TickVectorTests.cs    tick.json の全節（Task 2）
  ほか各ファイルの単体テスト
native/shiftlock.Agent/              新規（Task 10〜14）
  Program.cs, AgentPaths.cs, AgentLog.cs, Watcher.cs, Platform/*.cs, ActionRunner.cs, Tray.cs, ReleaseFlow.cs, Setup.cs, TickOnce.cs
native/shiftlock.Agent.Tests/        新規
native/deploy.ps1                    新規．発行して置く（Task 14）
```

---

### Task 1: 文面の書き出しと Core の土台

**Files:**
- Modify: `pc/tools/export_native.py`（`PREFIXES`），`pc/tests/test_export_native.py`
- Modify: `native/shiftlock.Core/ShiftConfig.cs`，`native/shiftlock.Core/Store.cs`
- Create: `native/shiftlock.Core/LocalPath.cs`
- Test: `native/shiftlock.Core.Tests/ConfigTests.cs`（追記），`StoreLogTests.cs`，`LocalPathTests.cs`

**Interfaces:**
- Produces（文面）: 書き出すキーに次を足す — `PHASE_`，`AUTOWAKE_`，`PHONE_LATE_PREFIX`，`PHONE_OKITA`，`RECORDS_LATE_`，`RECORDS_PAID_`，`RECORDS_YASUMI_PAID`，`ATTENDANCE_`（`absence_note` が使うもの．名前は `attendance.py` で確かめる），`CONFIRM_HOME_HINT`・`CONFIRM_MORE`・`CONFIRM_USER`，`ENFORCE_LABEL`・`ENFORCE_SUFFIX`・`ENFORCE_SUFFIX_STOP`・`ENFORCE_WARN`・`ENFORCE_WARN_SOON`・`ENFORCE_REWARN`・`ENFORCE_RUN`・`ENFORCE_RELEASED`・`ENFORCE_RELEASE_ALREADY`，`NOTIFY_`，`TICK_`，`CAL_`．宣言・状態表示の `ENFORCE_*` と `*_CTX`（Claude 向け）は書き出さない．
- Produces（設定）: `ShiftConfig` に Python の `DEFAULTS` と同じ既定値で次を足す（持ち方は既存の項目に合わせる）: `usage`（`nag_after_minutes` 120，`nag_every_minutes` 30，`idle_reset_minutes` 20，`game_idle_minutes` 180），`notify`（`phase` "toast"，`hard` "popup"，`nag` "toast"，`speak` false，`sound` {}，`sound_volume` 0.8，`sound_max_seconds` 20），`retention`（60／180／365），`report.keep_months` 3，`confirm`（"ask"／7／60／true），`absence`（24／5），`enforce`（"lock"／2／15／5／6／15／`skip_if_remote` true），`calendar`（`ics_url`・`sources`・`off_keywords`・`work_keywords`・`sync_minutes` 60），`mood_file` ""，`shift_end_earliest` "07:00"，`home_by_latest` "09:00"，`auto_wake` true，`auto_wake_hours` null，`steam_libraries` true，`game_dirs`・`game_exclude_dirs`・`game_processes` []，`game_exclude`（既定 9 個）．`usage` などが辞書でなければ `ConfigException`（`CONFIG_NOT_DICT`），`calendar.sources` がリストでなければ `CONFIG_SOURCES_NOT_LIST`，`notify.sound` が辞書でなければ空にする（Python の `load_config` と同じ）．
- Produces（Store）:

```csharp
public void LogAppend(string kind, DateTime now, JsonObject rec);          // log/<kind>-yyyy-MM.jsonl に {"t": 秒まで, ...rec} を 1 行
public IReadOnlyList<(DateTime T, JsonObject Rec)> LogRead(string kind, DateTime start, DateTime end);  // start <= t < end，時刻順．壊れた行は飛ばす
public DateOnly? FirstLogDate();                                           // pc-*.jsonl の最初に読める行の日付
public DateTime? LastLogTime(string kind);                                 // 最新の月のファイルの最後に読める行の時刻（Agent の 5 分の判定用）
public (HashSet<(string Day, string At)> Shuppatsu, HashSet<string> Kitaku) Departures();
public void SetShiftStatus(string day, string? status, DateTime now, string note = "");
public void SetMark(string day, string key, DateTime? t, DateTime now, string note = "");
public void SetPaid(string day, bool on, DateTime now, string note = "");
public JsonObject ReadJson(string name);                                   // 無い・壊れている → 空
public void WriteJson(string name, JsonObject obj);                        // .tmp → 置き換え
```

  `SetShiftStatus` が区切り 1 の `Records` の中に同じ処理としてあれば，そちらを `Store` に移して `Records` から呼ぶ（動きは変えない）．
- Produces: `public static string LocalPath.ToLocal(string p)` — `/mnt/<文字>/...` を `<文字>:\...` に．それ以外はそのまま．

- [ ] **Step 1:** `test_export_native.py` に「上のキーが両方の言語の `messages.*.json` にある」「`ENFORCE_TERMS`・`ENFORCE_DECLARED`・`PHASE_SOFT_CTX` は無い」を足す → 失敗を確かめる
- [ ] **Step 2:** `PREFIXES`／`EXACT` を直し，`python3 pc/tools/export_native.py` で書き出す．書式指定つきの文があって弾かれたら，Python 側で値を文字列にしてから渡している形かを確かめ，文は変えずに書き出せる範囲だけにする（変える必要が出たら止まって報告）
- [ ] **Step 3:** C# の失敗するテストを書く: 既定値（空の `shifts.json` に近い設定で上の値になる），壊れた節の扱い 3 つ，`LogAppend`→`LogRead` の往復（月またぎ，範囲の端，壊れた行を飛ばす），`LastLogTime`（ファイル無し → null，最後の行が壊れていたらその前の行），`Departures`（`store.py` の docstring の 3 つの形），`SetMark`（メモ「出所 時刻」と `clear`），`SetPaid`，`ToLocal("/mnt/c/Users/x/a.csv") == @"C:\Users\x\a.csv"`
- [ ] **Step 4:** 実装して `dotnet test native` と pytest が通るのを確かめる
- [ ] **Step 5:** コミット `feat(native): 見張りの文面と Core の土台（設定の節・ログの読み書き） (#4)`

---

### Task 2: 見張りの本体の骨組みと，稼働・離席

**Files:**
- Modify: `pc/tools/export_native.py`（`tick.json` の作り方と節 `usage`），`pc/tests/test_export_native.py`
- Create: `native/shiftlock.Core/Tick.cs`，`Usage.cs`
- Modify: `native/shiftlock.Core.Tests/Vectors.cs`（下の 2 点）
- Test: `native/shiftlock.Core.Tests/TickVectorTests.cs`，`UsageTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record TickInput(double IdleSeconds, bool Game, double? UptimeSeconds = null, bool Remote = false, bool DryRun = false);
public sealed record TickAction(string Method, string Kind, string Title, string Text, bool Speak, string Sound = "", string? Action = null);
public sealed record TickResult(IReadOnlyList<TickAction> Actions, IReadOnlyList<string> Warnings, double SoundVolume, int SoundMaxSeconds);
public static class Tick
{
    public static TickResult Run(ShiftConfig cfg, Store store, Messages m, TickInput input, DateTime now, Func<string, string>? fetch = null);
}
public static class Usage
{
    public static bool PcActive(TimeSpan idle, bool game, TimeSpan reset, TimeSpan? gameIdle);
    public static TimeSpan? GameIdleLimit(ShiftConfig cfg);            // 0 以下 → null．壊れた値・inf・nan → 180 分
    public static bool ShiftAway(Store store, DateTime now);            // 24 時間
    public static TimeSpan? DueNag(JsonObject usage, DateTime now, ShiftConfig cfg);   // usage["last_nag"] を書く
    public static IReadOnlyList<(DateTime S, DateTime E)>? PcGaps(Store store, DateTime now);
}
```

  `Tick.Run` はこのタスクでは `run_tick` の「ログの追記 → `usage-pc.json` の更新」までを行い，`Actions` は空．以後のタスクが `run_tick` と同じ位置に段を足す．`Store.RecentLongGap`（既存）はそのまま使う．
- Produces（ベクタの形）: `tick.json` は `{節名: [ケース]}`．ケースは既存と同じ `name`・`files`・`input`・`expect`．

```
input:  {"steps": [{"now": "2026-12-01T09:00", "idle": 30, "game": 0, "uptime": 7200, "dry_run": false}, ...],
         "fetch": {"<URL>": "<ics の中身>" または null（失敗させる）},   // 無ければ取得しない設定のケース
         "watch": ["usage-pc.json", "log/pc-2026-12.jsonl", ...]}        // 最後に比べるファイル
expect: {"results": [{"actions": [...], "warnings": [...], "sound": {"volume": 0.8, "max_seconds": 20}}, ...],
         "files": {"<watch の名前>": 読み込んだ JSON（*.json）／行の配列（*.jsonl・*.log・*.txt）／null（無い）}}
```

  - `files` の名前に `/` があれば親フォルダを作る（`_put_files` と `Vectors.Materialize` の両方）．
  - `files` を置いた後，`shifts.json` の中の文字列 `{DIR}` を一時フォルダのパス（区切りは `/`）に置き換える（`mood_file`・通知音のパス用．両方の側で同じことをする）．結果の `text`・`warnings`・`sound` に一時フォルダのパスが出たら `{DIR}` に戻してから比べる．
  - `actions` の各要素は `method`・`kind`・`title`・`text`・`speak`・`sound` と，強制の実行だけ `action`．
  - **確認の段（Task 6）より前に作る節は，設定に `"confirm": {"mode": "off"}` を入れる**（Python の `tick` は毎回全部の段を通る．確認が動くと `state.json` に `confirm_since` を書くため）．同じ理由で，各節は自分の段が書くファイルだけを `watch` に入れる．
- Consumes: Task 1 の `Store`・`ShiftConfig`．

- [ ] **Step 1:** `export_native.py` に `_expect_tick(cfg, store, inp)`（`steps` を順に `sl.run_tick(cfg_path, idle, bool(game), now, uptime, None, dry_run)` に通す．`fetch` があれば `calendar_sync._http_get` を差し替える）と節 `usage` を足す．`usage` のケース（`BASE` の設定に `confirm.mode` off，乗務の無い土日 2026-12-05〜06 を使い，フェーズ通知が出ない時刻にする）:
  - 触っている 3 回（idle 30）→ `start` が 1 回目，`last` が 3 回目
  - idle 1500 で離席 → `gap_start` = now − idle．45 分後に戻る → `gaps` に 1 件，`start` = 戻った回の now − idle
  - 30 分未満の離席は `gaps` に入らない
  - `last` から 31 分空く（スリープ）→ `start` を切り直す
  - ゲーム中 idle 9000（150 分）→ 触っている．idle 10900 → 離席で `gap_start` = now − idle
  - `game_idle_minutes` 0 → 無制限
  - 外出中のゲーム（`events.log` に `worked`／`shuppatsu` の行と `state.json` の `worked`）→ ゲームを見ない
  - `gaps` が 21 件目で最初の 1 件が落ちる
- [ ] **Step 2:** 書き出して pytest（生成物が古くない・日付が合成・`tick.json` の形）を通す
- [ ] **Step 3:** `TickVectorTests` を書く: `[Theory][MemberData(nameof(Vectors.Cases), "tick.json", "usage")] void Usage(VectorCase c)` が共通の `RunCase(c)`（`files` を置く → `{DIR}` を置き換え → `steps` を順に `Tick.Run` → 各回の結果と `watch` のファイルを比べる）を呼ぶ．`fetch` は辞書から返し，`null` は例外を投げる．失敗を確かめる
- [ ] **Step 4:** `UsageTests` に Review Focus 2 を足す: `usage-pc.json` が空ファイル・`[]`・`{"gaps": "x"}`・`{"start": "こわれた"}` のとき `Tick.Run` が例外を出さず，次の回から普通の形で書く．`log/pc-2026-12.jsonl` に壊れた行があっても追記できる
- [ ] **Step 5:** `Usage` と `Tick.Run`（ここまでの段）を実装して通す
- [ ] **Step 6:** コミット `feat(native): 見張りの本体の骨組みと稼働・離席の記録 (#4)`

---

### Task 3: フェーズ通知・声かけ・通知音

**Files:**
- Modify: `pc/tools/export_native.py`（節 `notify`・`sound`），`native/shiftlock.Core/Tick.cs`
- Create: `native/shiftlock.Core/NotifyLog.cs`，`PhaseText.cs`
- Test: `TickVectorTests.cs`（節を足す），`NotifyLogTests.cs`

**Interfaces:**
- Produces:

```csharp
public static class NotifyLog
{
    public static int? Every(string phase);     // lastcall・go・go_last → null（1 回だけ），soft 30，hard 10
    public static bool Once(Store store, string key, DateTime now, int? everyMinutes = null);   // notified.json．3 日より古いキーを捨てる
    public static IReadOnlyList<string> AttachSounds(ShiftConfig cfg, Store store, Messages m, IList<TickAction> actions, DateTime now);  // 戻り値は警告．actions を sound 付きに置き換える
}
public static class PhaseText
{
    public static string User(string phase, Plan p, DateTime now, ShiftConfig cfg, Messages m);
}
```

  音の優先順は `enforce`，`hard`，`go_last`，`confirm`，`lastcall`，`soft`，`go`，`auto_okita`，`nag`．鳴らすのは 1 件．
- `Tick.Run` に足す段: 触っているときだけ「フェーズ通知（`kind` = フェーズ名．`hard`・`go_last` は `notify.hard`，ほかは `notify.phase`）→ 声かけ（`kind` "nag"，ゲーム中は `TICK_NAG_GAME`）」，最後に `AttachSounds` と音量・最長秒数（読めなければ 0.8／20 と `TICK_SOUND_NUMBER_ERROR`．`AttachSounds` が例外なら `TICK_SOUND_CONFIG_ERROR` で全部の `sound` を空に）．

- [ ] **Step 1:** 節 `notify` を足す（乗務日 2026-12-01．`confirm.mode` off）: lastcall は 1 回だけ／soft は 30 分おき（07:59 までは lastcall，08:00・08:05・08:30）／hard は 10 分おき／go・go_last は 1 回ずつ／離席中（idle 1500）は出さない／`notify.hard` を "toast" にした設定／声かけは連続 120 分で 1 回，その 30 分後にもう 1 回，離席から戻ると数え直し／ゲーム中の声かけの文／関西弁（`lang` "ja_kansai"）で 1 ケース
- [ ] **Step 2:** 節 `sound` を足す: 種類ごとの音（`{DIR}/hard.wav` を `files` に `true` で置く）／`default` だけ／設定なし → 空／無いファイル → 警告が 1 日 1 回（同じ日の 2 回目は警告なし）／同じ回に 2 件の通知 → 優先の高い 1 件だけ／`sound_volume` が "大" → 0.8 と警告
- [ ] **Step 3:** 書き出し，C# に節のテストを足して失敗を確かめる．`NotifyLogTests` に Review Focus 2（`notified.json` に読めない時刻があっても `Once` が true を返して書き直す）
- [ ] **Step 4:** 実装して通す
- [ ] **Step 5:** コミット `feat(native): フェーズ通知・声かけ・通知音の解決 (#4)`

---

### Task 4: 自動「起きた」

**Files:**
- Modify: `pc/tools/export_native.py`（節 `autowake`），`native/shiftlock.Core/Tick.cs`
- Create: `native/shiftlock.Core/AutoWake.cs`
- Test: `TickVectorTests.cs`

**Interfaces:**
- Produces: `public static class AutoWake { public static (DateTime S, DateTime E)? SleptBefore(ShiftConfig cfg, Store store, Plan p, DateTime now); public static string MaybeAutoOkita(ShiftConfig cfg, Store store, Messages m, DateTime now); }`．`no_sleep_intervals` と区間の引き算（`timeutil._subtract`）が Core に無ければこのタスクで足す（`Planner`／`TimeUtil` に）．
- `Tick.Run` に足す段: 触っているとき，フェーズ通知より前に `MaybeAutoOkita`（文があれば `kind` "auto_okita"，`notify.phase`）．Task 5 で「iPhone の取り込みが文を返したらそちらを使う」に変わる．

- [ ] **Step 1:** 節 `autowake`（`confirm.mode` off．`watch` は `state.json`・`events.log`）: 「ねる」あり＋6 時間の離席 → `okita`（メモ `auto HH:MM-HH:MM`）／離席だけ 6 時間（`auto_wake` 既定）→ `okita`／`auto_wake` "neru" で離席だけ → 何もしない／`auto_wake` false → 何もしない／5 時間 50 分 → 足りない／`auto_wake_hours` 3 → 3 時間で成立／`no_sleep` の時間帯で分断されて足りない／前日が乗務で，出発目安〜`home_by_latest` の離席は数えない／前日が乗務でも `yasumi` なら数える／`log/claude-2026-12.jsonl` のプロンプト（`{"t": ..., "k": "p"}`）で区切られる／もう `status` がある → 何もしない／乗務日の 0 時より前 → 何もしない
- [ ] **Step 2:** 書き出し，節のテストを足して失敗を確かめる
- [ ] **Step 3:** 実装して通す
- [ ] **Step 4:** コミット `feat(native): 自動「起きた」 (#4)`

---

### Task 5: iPhone の記録の取り込みと事後報告

**Files:**
- Modify: `pc/tools/export_native.py`（節 `phone`），`native/shiftlock.Core/Tick.cs`
- Create: `native/shiftlock.Core/Phone.cs`，`LateReport.cs`
- Test: `TickVectorTests.cs`，`PhoneTests.cs`

**Interfaces:**
- Produces:

```csharp
public static class Phone
{
    public static IReadOnlyList<(DateTime T, string Kind, string? Extra)> ReadMarks(ShiftConfig cfg);   // mood_file を LocalPath.ToLocal で読む．BOM 可
    public static string ImportMarks(ShiftConfig cfg, Store store, Messages m, DateTime now);
}
public static class LateReport
{
    // kind: "yasumi" / "worked"．(記録したか, 表示文)．Python の _late_report と同じ（paid は今回 null しか渡さないが，引数は持つ）
    public static (bool Ok, string Text) Report(ShiftConfig cfg, Store store, Messages m, DateTime now, string kind,
        string? dayText = null, DateTime? when = null, string via = "claude", bool? paid = null);
}
```

  `LateReport` が使う `parse_day`（`past=True`）・`days_set`（`days.json` に出勤の例外日を足す）・`month_yasumi_count`・`absence_note`・`last_asked_unconfirmed` は Python の同名の関数どおりに `LateReport.cs` の中（または既存の合うファイル）に置く．`Planner.LastPastPlan` は既存．
- `Tick.Run` の段: 触っているとき `ImportMarks`（例外は握って空文字）→ 文が空なら `MaybeAutoOkita`．

- [ ] **Step 1:** 節 `phone`（`files` に `mood.csv`，設定に `"mood_file": "{DIR}/mood.csv"`．`watch` は `state.json`・`events.log`・`days.json`）: `neru` → `sleep_at`（メモ `phone 時刻`）／`neru` の後の `okita` → `okita`（`at` は iPhone の時刻，メモ `phone HH:MM-HH:MM`）と通知／二度寝（okita → neru → okita）をまとめて取り込んでも最後の okita が効く／`kitaku`／日本語の印（`ねる`・`ただいま`・`休んだ`）／`yasumi:2026-11-30` → その日の事後報告，2 回目の見張りでは適用しない（`phone_applied`）／日付なしの `deta` → 直近に終わった乗務／休みの日（土曜）に `deta:2026-12-05` → `days.json` の `work` に足す／未来の時刻・`lookback_days` より前は無視／気分の行（`2026-12-01 09:00,1,メモ`）は無視／`mood_file` が無いファイル → 何も起きない
- [ ] **Step 2:** 書き出し，節のテストを足して失敗を確かめる．`PhoneTests` に Review Focus 2: BOM つき，CRLF，壊れた行（列が 1 つ・読めない日時），同じ行の重複，`/mnt/c/...` の書き方
- [ ] **Step 3:** 実装して通す
- [ ] **Step 4:** コミット `feat(native): iPhone の記録の取り込みと事後報告 (#4)`

---

### Task 6: 終わった乗務の確認

**Files:**
- Modify: `pc/tools/export_native.py`（節 `confirm`），`native/shiftlock.Core/Tick.cs`
- Create: `native/shiftlock.Core/Confirm.cs`
- Test: `TickVectorTests.cs`

**Interfaces:**
- Produces: `public static class Confirm { public static (Plan? Pick, int Others) PickUnconfirmed(ShiftConfig cfg, Store store, DateTime now); public static string UserText(ShiftConfig cfg, Store store, Messages m, Plan p, DateTime now, int others); public static void MarkAsked(Store store, string day, DateTime now); }`．`confirm_since`（無ければ `state.json` に前日を書く），`unconfirmed_shifts`，`home_activity_minutes` を含む．
- `Tick.Run` の段: フェーズ通知の後，声かけの前．`kind` "confirm"，`notify.phase`．出したら `MarkAsked`．

- [ ] **Step 1:** 節 `confirm`（ここからは `confirm.mode` の既定 "ask"．`files` に `log/pc-2026-11.jsonl` を置いて記録開始日を作り，`state.json` に `"confirm_since": "2026-11-20"`）: 昨日の乗務が未記録 → 聞く／同じ日の 2 回目は聞かない／2 件あれば新しいほうと「ほか 1 件」／`okita` があり `okita_means_worked` 既定 → 聞かない，false → 聞く／乗務の時間帯に PC を 60 分以上触っていた → `CONFIRM_HOME_HINT` 付き／翌朝 07:00（`shift_end_earliest`）より前は聞かない／記録開始日より前の乗務は聞かない／`confirm_since` が無い → 前日が書かれ，昨日より前は聞かない／`mode` "off"
- [ ] **Step 2:** 書き出し，節のテストを足して失敗を確かめる
- [ ] **Step 3:** 実装して通す
- [ ] **Step 4:** コミット `feat(native): 終わった乗務の確認 (#4)`

---

### Task 7: 強制の発動判定と緊急解除

**Files:**
- Modify: `pc/tools/export_native.py`（節 `enforce`・`release`），`native/shiftlock.Core/Tick.cs`
- Create: `native/shiftlock.Core/Enforce.cs`
- Test: `TickVectorTests.cs`，`EnforceReleaseTests.cs`

**Interfaces:**
- Produces:

```csharp
public static class Enforce
{
    public static JsonObject State(Store store, string day);
    public static IReadOnlyList<TickAction> Due(ShiftConfig cfg, Store store, Messages m, DateTime now, string phase, Plan? p,
        bool active, double? uptimeSeconds, bool remote, double idleSeconds, bool dryRun);
    public static string Suffix(Store store, Messages m, Plan p, string phase);
    public static string Release(Store store, Messages m, DateTime now, string reason);   // STOP.txt に追記 → released_at → events.log
}
```

  実行の要素は `Method` "enforce"，`Kind` "enforce"，`Action` が `lock`／`sleep`／`shutdown`，`Speak` true．予告は `notify.hard`，`Speak` true．**宣言・`enforce_off`・`enforce_resume`・状態表示は作らない．**
- `Tick.Run` の段: フェーズ通知の文に `Suffix` を足す．触っているとき最後に `Due`（例外は握って `TICK_ENFORCE_ERROR` を警告に）．

- [ ] **Step 1:** 節 `enforce`（`state.json` に同意済みの宣言を置く．値は `enforce_declare` が書く形: `action`・`duration_hours`・`declared_at`・`consented_at`・`via`・`runs: []`・`grace_minutes` 15・`relock_minutes` 5・`max_runs` 6・`boot_grace_minutes` 15．`uptime` は 7200．`watch` は `state.json`・`events.log`・`notified.json`）: 最低ラインに入った最初の回 → 予告，`warned_at`／10 分後 → 「あと 5 分」が 1 回／15 分後 → 実行（`runs`，`first_run_at`，`events.log` に `enforce_run`）／実行後，入力が実行より前 → 何もしない／戻ってきた（入力が実行より後）→ 再予告 → 5 分後に再実行／`max_runs` 2 の宣言 → 3 回目は無い／`duration_hours` 0.5 → 30 分で時間切れ／12 分以上空いた → 予告からやり直し／`STOP.txt` あり → 何もしない／`released_at` あり → 何もしない／`uptime` 600・0・無し → 発動しない／`dry_run` → 結果は出るが `state.json` が進まない／宣言なし → 通知に添え字なし，宣言あり → lastcall・soft・hard の文に添え字（`STOP.txt` があれば `ENFORCE_SUFFIX_STOP` 付き）／`action` "shutdown"／離席中 → 何もしない
- [ ] **Step 2:** 節 `release` は `tick` を通さないので別の期待関数で作る: `input` `{"now", "reason": "tray"}`，`expect` `{"message", "state", "events", "stop": 行の配列}`．ケース: 宣言 1 件あり／宣言 2 件（別の乗務日）／宣言なし（`events.log` の乗務日が `-`）／`STOP.txt` が既にあって宣言なし（`ENFORCE_RELEASE_ALREADY`）／解除済みの宣言には触らない
- [ ] **Step 3:** 書き出し，両方の節のテストを足して失敗を確かめる
- [ ] **Step 4:** 実装して通す．`rg -n "enforce_declare|Declare" native/` が何も返さないこと（宣言の入口が無い）を確かめる
- [ ] **Step 5:** コミット `feat(native): 強制の発動判定と緊急解除 (#4)`

---

### Task 8: カレンダー同期と 1 日 1 回の整理

**Files:**
- Modify: `pc/tools/export_native.py`（節 `calendar`・`maintenance`），`native/shiftlock.Core/Tick.cs`
- Create: `native/shiftlock.Core/CalendarSync.cs`，`Maintenance.cs`
- Test: `TickVectorTests.cs`，`IcsTests.cs`

**Interfaces:**
- Produces:

```csharp
public static class CalendarSync
{
    public static JsonObject ParseIcs(string text, IReadOnlyList<string> offKeywords, IReadOnlyList<string> workKeywords);   // {"off": {...}, "work": {...}, "recurring_skipped": n}
    public static string Sync(ShiftConfig cfg, Store store, Messages m, DateTime now, Func<string, string> fetch, bool force = false);
}
public static class Maintenance
{
    public static JsonObject Cleanup(ShiftConfig cfg, Store store, DateTime now);     // removed の件数
    public static void Daily(ShiftConfig cfg, Store store, Messages m, DateTime now, Func<string, string>? fetch);
}
```

  **`Tick.Run` に `fetch` が渡されなければ Core の既定（`HttpClient`，タイムアウト 10 秒，UTF-8 で読み，読めない文字は置き換え）を使う**．`Daily` の中の例外は握る（`maintenance.json` の `result` に `{"error": ...}`）．`ics` の `Z` つきの時刻はローカルの日付にする．
- `Tick.Run` の段: ログの追記の直後に `Daily`（`run_tick` と同じ位置）．

- [ ] **Step 1:** 節 `calendar`（`input.fetch` を使う．`watch` は `calendar.json`．見張りの後に乗務日の判定が変わることは，2 回目の見張りのフェーズ通知で確かめる）: 終日の「公休」で乗務日が休みになる／「出勤」で休みの日が乗務日になる／複数日の終日予定（`DTEND` は翌日）／`Z` つきの時刻／折り返した行／`STATUS:CANCELLED` は無視／`RRULE` は初回だけで `recurring_skipped` 1／`sync_minutes` 内の 2 回目は取りに行かない（`fetch` が呼ばれたら例外にして確かめる）／取得に失敗 → 前回分が残り `error` が入る／2 本のうち 1 本だけ失敗／旧形式の `calendar.json`（`fetched`・`off`・`work` が直下）を先頭のソースに引き継ぐ／同じ URL は 1 本／名前の重複に `'` が付く／ソースごとのキーワード
- [ ] **Step 2:** 節 `maintenance`（`watch` は `maintenance.json`・`days.json`・`state.json`・`events.log` と，残る・消えるファイルを `files` の一覧で）: 61 日前の `days.json`・`state.json` の日が消える／181 日より前の月の `log/*.jsonl` が消える（合成の古い月 `log/pc-2026-05.jsonl`）／366 日前の `events.log` の行が消える／`reports/report_2026-07-21_2026-08-20.html` が消え，新しいものは残る／同じ日の 2 回目は整理しない／翌日は整理する．`expect.files` で「無い」は `null`
- [ ] **Step 3:** 書き出し，節のテストを足して失敗を確かめる．壊れた入力は **Python と同じ扱い**にする: 空文字・`BEGIN:VEVENT` だけ → 空の結果（`IcsTests`），日付が読めない `DTSTART` → 例外になり `Sync` が失敗として前回分を残す（節 `calendar` に 1 ケース足す）
- [ ] **Step 4:** 実装して通す
- [ ] **Step 5:** コミット `feat(native): カレンダー同期と 1 日 1 回の整理 (#4)`

---

### Task 9: ゲームの判定

**Files:**
- Create: `native/shiftlock.Core/Games.cs`
- Test: `native/shiftlock.Core.Tests/GamesTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record GameRules(IReadOnlyList<string> GameDirs, IReadOnlyList<string> ExcludeDirs, IReadOnlyList<string> ExcludeNames, IReadOnlyList<string> ProcessNames);
public static class Games
{
    // steamCommonDirs: Agent が集めた steamapps\common の一覧（steam_libraries が false なら使わない）
    public static GameRules Rules(ShiftConfig cfg, IEnumerable<string> steamCommonDirs);
    public static bool IsGameExe(GameRules rules, string? exePath);                 // フォルダ配下 かつ 除外に当たらない
    public static bool IsGame(GameRules rules, string processName, string? exePath); // IsGameExe か，名前が ProcessNames にある
    public static IReadOnlyList<string> SteamLibraryPaths(string libraryFoldersVdf);  // "path" "..." の行を拾い \\ を \ に
}
```

  決まりは `pc/guard.ps1` の `Normalize-Dir`・`Test-GameExe`・`Get-RunningGames`（設計書 §5.1）: フォルダは絶対パスにして末尾 `\`，前方一致は大文字小文字を区別しない，除外名はワイルドカード（`*`・`?`）でファイル名・拡張子なしの名前・フルパスのどれかに当たれば除外，`game_exclude` が設定に無ければ既定の 9 個（空の配列を書いたら除外なし），プロセス名は完全一致（PowerShell の `-contains` と同じく大文字小文字を区別しない）．

- [ ] **Step 1:** 失敗するテストを書く: `C:\Games\Foo\bin\foo.exe` はゲーム（`game_dirs` `C:\Games`）／`c:\games\FOO\foo.exe` もゲーム／`C:\GamesX\a.exe` はゲームでない（末尾 `\` の効果）／`UnityCrashHandler64.exe`・`vc_redist.x64.exe`・`FooSetup.exe` は除外／除外フォルダの下は除外／`game_exclude: []` なら `UnityCrashHandler64.exe` もゲーム／パスが null でも `game_processes` の名前ならゲーム（`Notepad` と `notepad`）／`steam_libraries` false なら Steam のフォルダを使わない／`SteamLibraryPaths` が `"path"\t\t"D:\\SteamLibrary"` から `D:\SteamLibrary` を返す／壊れたパス（`"C:\\a<>|"`）のフォルダ設定は無視して落ちない
- [ ] **Step 2:** 実装して通す
- [ ] **Step 3:** コミット `feat(native): ゲームの判定 (#4)`

---

### Task 10: Agent の殻（常駐・周期・ログ）

**Files:**
- Create: `native/shiftlock.Agent/shiftlock.Agent.csproj`（`OutputType` WinExe，`TargetFramework` `net10.0-windows`，`AssemblyName` `shiftlock-agent`，`RootNamespace` `Shiftlock.Agent`，Core を参照），`Program.cs`，`AgentPaths.cs`，`AgentLog.cs`，`Watcher.cs`，`Platform/ISystemProbe.cs`
- Create: `native/shiftlock.Agent.Tests/shiftlock.Agent.Tests.csproj`（xUnit．Core.Tests と同じパッケージの版），`WatcherTests.cs`，`AgentLogTests.cs`
- Modify: `native/shiftlock.slnx`

**Interfaces:**
- Produces:

```csharp
public interface ISystemProbe
{
    double IdleSeconds();                 // 取れなければ 0
    double? UptimeSeconds();              // 取れなければ null
    IReadOnlyList<(string Name, string? Path)> Processes();
    IReadOnlyList<string> SteamCommonDirs();
}
public enum WatchOutcome { Ran, TooSoon, Busy, GuardTaskEnabled, NoConfig, BadConfig, Failed }
public sealed class Watcher(string dir, ISystemProbe probe, Func<DateTime> clock, Func<GuardTaskState> guardTask,
                            Action<TickResult, Messages> deliver, AgentLog log, Func<string, string>? fetch = null)
{
    public WatchOutcome LastOutcome { get; }
    public string? LastError { get; }                    // 設定エラーの文（APP_NO_CONFIG / APP_BAD_CONFIG）
    public WatchOutcome RunOnce(bool dryRun = false);    // 例外を外に出さない
}
public enum GuardTaskState { Missing, Disabled, Enabled, Unknown }
public sealed class AgentLog(string path, long maxBytes = 1_000_000)   // 超えたら <名前>.1 に回して作り直す
{
    public void Write(string message);    // "yyyy-MM-dd HH:mm:ss message"．書けなくても例外を出さない
}
public static class AgentPaths { public static string DataDir { get; } }   // SHIFTLOCK_DIR，無ければ %USERPROFILE%\shift-lock
```

  `RunOnce` の順: `guardTask()` が `Enabled` → `GuardTaskEnabled`（`Unknown` は見張る．警告は 1 日 1 回）→ 設定を読む（無い → `NoConfig`，壊れている → `BadConfig`）→ `store.LastLogTime("pc")` が `now` の 5 分前より新しく，かつ `now` 以前なら `TooSoon`（**最後の行が今より後なら見張る**）→ 前の回が動いていれば `Busy` → `Tick.Run`（`Game` は `Games.IsGame` で 1 つでも当たれば true）→ 警告をログ → `deliver` → `Ran`．途中の例外は `Failed` にしてログに書く．
  `Program` は名前付きミューテックス `Local\shiftlock-agent` で二重起動を防ぎ，起動 1 分後と以後 5 分おきに UI でないスレッドで `RunOnce` を呼ぶ．電源の復帰（`SystemEvents.PowerModeChanged` の `Resume`）でも 30 秒後に呼ぶ（`TooSoon` が間引く）．**周期の違う仕事を後から足せるよう，「名前・間隔・処理」を並べた一覧をタイマーが回す形にする**（今は 1 件）．
- Consumes: `Tick.Run`，`Games`，`Store.LastLogTime`，`ShiftConfig.Load`．

- [ ] **Step 1:** `WatcherTests` を書く（偽の `ISystemProbe`・時計・一時フォルダ．合成の `shifts.json`）: 1 回目 `Ran` で `log/pc-*.jsonl` が 1 行／4 分後は `TooSoon` で行が増えない／5 分後は `Ran`／**新しい `Watcher` を作り直しても 4 分後は `TooSoon`**／ログの最後の行が 1 時間先 → `Ran`（Review Focus 3）／`shifts.json` 無し → `NoConfig`，後から置くと次は `Ran`／フォルダごと無い → `NoConfig`（Review Focus 4）／`shifts.json` が `{` → `BadConfig`／`guardTask` が `Enabled` → `GuardTaskEnabled` で行が増えない，`Unknown` → `Ran`／`deliver` が例外 → `Failed`，次の回は `Ran`／`state.json` を別のハンドルで排他オープンしたまま → `Failed` か `Ran`（落ちない）で，離した次の回は `Ran`（Review Focus 1）／ゲームのプロセスあり → ログの行が `"game": 1`
- [ ] **Step 2:** `AgentLogTests`: 1 行の形／`maxBytes` を超えたら `.1` に回る／フォルダが無くても例外を出さない
- [ ] **Step 3:** 実装して通す．`dotnet build native` が警告 0 であること
- [ ] **Step 4:** コミット `feat(native): Agent の殻（常駐・5 分の周期・ログ） (#4)`

---

### Task 11: Windows 側の値

**Files:**
- Create: `native/shiftlock.Agent/Platform/WindowsProbe.cs`
- Test: `native/shiftlock.Agent.Tests/WindowsProbeTests.cs`

**Interfaces:**
- Produces: `public sealed class WindowsProbe : ISystemProbe`．アイドルは `GetLastInputInfo`（`unchecked((uint)Environment.TickCount - dwTime) / 1000.0`），起動からの秒数は `Environment.TickCount64 / 1000.0`，プロセスは名前と exe のパス（取れないものはパス null．1 つ失敗しても全体は続ける），Steam は `HKCU\Software\Valve\Steam` の `SteamPath` と `C:\Program Files (x86)\Steam` の `steamapps\libraryfolders.vdf` から `Games.SteamLibraryPaths` で拾い，各 `steamapps\common` のうち在るものを重複なしで返す．

- [ ] **Step 1:** テストを書く（実機の値に触るので形だけ）: `IdleSeconds() >= 0`／`UptimeSeconds() > 0`／`Processes()` に自分のプロセス名があり，そのパスが null でない／`SteamCommonDirs()` が例外を出さない
- [ ] **Step 2:** 実装して通す
- [ ] **Step 3:** コミット `feat(native): Agent の Windows 側の値（アイドル・起動時間・プロセス・Steam） (#4)`

---

### Task 12: 知らせる・止める

**Files:**
- Create: `native/shiftlock.Agent/Platform/INotifier.cs`，`IPowerControl.cs`，`WindowsNotifier.cs`，`WindowsPower.cs`，`native/shiftlock.Agent/ActionRunner.cs`
- Test: `native/shiftlock.Agent.Tests/ActionRunnerTests.cs`

**Interfaces:**
- Produces:

```csharp
public interface INotifier
{
    void Toast(string title, string text, bool silent);   // 失敗は例外
    void Popup(string title, string text);                // 最前面・警告アイコン・20 秒で閉じる
    void Speak(string text);
    bool PlaySound(string path, double volume, int maxSeconds, CancellationToken cancel);   // 鳴らせたら true
}
public interface IPowerControl { void Lock(); void Sleep(); void ScheduleShutdown(int seconds); void AbortShutdown(); }
public sealed class ActionRunner(INotifier notifier, IPowerControl power, Store store, AgentLog log)
{
    public void Run(TickResult result, CancellationToken cancel);
}
```

  `Run` の決まり（`guard.ps1` の `Send-Notice`・`Invoke-Enforce` と同じ）: 一覧の順に 1 件ずつ．通知は「音（`Sound` があれば）→ `toast` か `popup`（`toast` が例外なら `Popup`）→ `Speak`（`♡` を除く）」．`toast` の `silent` は音を鳴らせたとき true．強制の実行は「`STOP.txt` があればやめてログ → 音 → もう一度 `STOP.txt` を見る → `lock`／`sleep`／`shutdown`（60 秒）」．知らない `Action` はログだけ．各手順の例外はログに書いて次へ．`cancel` が立ったら残りを出さずに戻る．
- トーストは `shiftlock` の名前で出す．**最初に小さく試す**（設計書 §8）: 非パッケージの exe から出せる方法を 1 つ選び，選んだ方法と，必要な登録（Task 14 の `--install` が行う）を `native/README.md` に書く．どうしても出せなければ `Toast` は常に例外を投げる実装にして（= いつも `Popup` になる），その旨を報告する．
- 読み上げと通知音のライブラリは任せる（WAV／MP3／M4A／WMA が鳴ること．音量と最長秒数が効くこと）．

- [ ] **Step 1:** `ActionRunnerTests`（呼ばれた順を記録する偽物）: 音あり toast → `PlaySound`，`Toast(silent: true)`／音が鳴らせない → `Toast(silent: false)`／`Toast` が例外 → `Popup`／`speak` true → 最後に `Speak`，`♡` が除かれている／`enforce lock` → `Lock` が 1 回／`STOP.txt` あり → `Lock` が呼ばれずログに 1 行／音の最中に `STOP.txt` を置く（偽の `PlaySound` の中で作る）→ 呼ばれない／`shutdown` → `ScheduleShutdown(60)`／知らない action → 何も呼ばれない／2 件目の通知で例外 → 3 件目は出る／1 件目の後に `cancel` → 2 件目は出ない（Review Focus 5）
- [ ] **Step 2:** `ActionRunner` を実装して通す
- [ ] **Step 3:** `WindowsNotifier`・`WindowsPower` を実装する（`Lock` は `LockWorkStation`，`Sleep` は `SetSuspendState(false, false, false)`，`ScheduleShutdown` は `shutdown.exe /s /t 60 /c "shiftlock: kyousei mode. cancel = kaijo.cmd (60s)"`，`AbortShutdown` は `shutdown.exe /a`）
- [ ] **Step 4:** 合成データで手で確かめる（`SHIFTLOCK_DIR` を合成のフォルダに）: トースト・ポップアップ（20 秒で閉じる）・読み上げ・通知音（WAV と MP3）が出る．`Lock` を 1 回だけ実行して画面がロックされる．`sleep`・`shutdown` は実行しない（本人が確かめる項目として残す）
- [ ] **Step 5:** コミット `feat(native): Agent の通知と強制の実行 (#4)`

---

### Task 13: トレイと緊急解除

**Files:**
- Modify: `pc/shiftlock/i18n/ja.py`，`ja_kansai.py`（`AGENT_*`），`pc/tests/test_export_native.py`，`native/shiftlock.Agent/Program.cs`
- Create: `native/shiftlock.Agent/Tray.cs`，`ReleaseFlow.cs`，アイコン（`native/shiftlock.Agent/Assets/*.ico`）
- Test: `native/shiftlock.Agent.Tests/ReleaseFlowTests.cs`，`TrayStateTests.cs`

**Interfaces:**
- Produces（文．両方の言語に）: `AGENT_MENU_OPEN`，`AGENT_MENU_RELEASE`，`AGENT_MENU_EXIT`，`AGENT_RELEASE_ASK`（確認の本文），`AGENT_RELEASE_TITLE`，`AGENT_TIP_GUARD_TASK`（`shiftlock-guard` が有効なので見張りを止めている），`AGENT_TIP_FAILED`，`AGENT_OPEN_FAILED`（`shiftlock.exe` が見つからない）．設定エラーの文は既存の `APP_NO_CONFIG`・`APP_BAD_CONFIG`，ホバーの 1 行は `Snapshot.Lines` の残り時間の行を使う．
- Produces:

```csharp
public enum TrayLook { Normal, Caution, Warning, ConfigError, Paused }
public static class TrayState
{
    // Paused: GuardTaskEnabled ／ ConfigError: NoConfig・BadConfig ／ Warning: hard・go_last ／ Caution: lastcall・soft ／ ほかは Normal
    public static (TrayLook Look, string Tip) From(WatchOutcome outcome, string? error, StatusSnapshot? snapshot, Messages m);
}
public sealed class ReleaseFlow(IPowerControl power, Store store, Func<DateTime> clock, AgentLog log)
{
    public string Run(Messages m);   // AbortShutdown → Enforce.Release(store, m, now, "tray")．各手順は独立．戻り値は知らせる文
}
```

  トレイ: 左ダブルクリックと「状態を開く」は Agent と同じフォルダの `shiftlock.exe` を起動（無ければ `AGENT_OPEN_FAILED` を通知）．「緊急解除」は はい／いいえ（既定「いいえ」）→「はい」で `ReleaseFlow.Run` → 結果をトーストで 1 回．「終了」は確認なしで，出している途中の通知を打ち切って終わる．アイコンとホバーは見張りのたびと毎分に更新する（ホバーは 63 文字以内に切る）．メニューは常に 3 つとも有効．
- Consumes: `Enforce.Release`，`Snapshot.Build`／`Lines`（区切り 1），`Watcher.LastOutcome`．

- [ ] **Step 1:** `AGENT_*` を両方の言語に足し，pytest（両方にある・プレースホルダが揃う）を通して書き出す
- [ ] **Step 2:** `ReleaseFlowTests`: 宣言あり → `AbortShutdown` が先に呼ばれ，`STOP.txt` ができ，`released_at` が付き，`events.log` に `enforce_release\ttray`／`AbortShutdown` が例外 → それでも `STOP.txt` と記録ができる／`state.json` が読めない → `STOP.txt` はできる／宣言なし → `STOP.txt` と乗務日 `-` の行．`TrayStateTests`: 上の対応表の 5 通りと，ホバーが 63 文字を超えない
- [ ] **Step 3:** 実装して通す
- [ ] **Step 4:** 合成データで手で確かめる: アイコンが出る／フェーズを変えた合成データで見た目が変わる／「状態を開く」でウィンドウが出る（2 回目は前に出る）／宣言を置いた合成データで「緊急解除」→「いいえ」は何も変わらず，「はい」で `STOP.txt` と通知／`shifts.json` を消すと設定エラーの見た目，戻すと直る／2 つ目の Agent はすぐ終わる／「終了」で消える
- [ ] **Step 5:** コミット `feat(native): Agent のトレイと緊急解除 (#4)`

---

### Task 14: 切り替え（登録・戻す・置く）

**Files:**
- Create: `native/shiftlock.Agent/Setup.cs`，`native/shiftlock.Agent/Platform/GuardTask.cs`，`native/deploy.ps1`
- Modify: `native/shiftlock.Agent/Program.cs`（引数），`native/README.md`
- Test: `native/shiftlock.Agent.Tests/SetupTests.cs`

**Interfaces:**
- Produces: `shiftlock-agent.exe --install`（ログオン時の自動起動を登録 → トーストの名前を登録（Task 12 で要ると分かった場合）→ タスク `shiftlock-guard` を無効化 → Agent を起動）と `--uninstall`（動いている Agent を終わらせる → 登録を外す → `shiftlock-guard` を有効に戻す）．どちらも管理者権限なし・標準出力に結果を 1 行ずつ・`shiftlock-report` には触らない・タスクが無ければ飛ばす．終了コードは成功 0，失敗 1．
- Produces: `GuardTask.State()`（`GuardTaskState` を返す．確かめ方は任せる．失敗は `Unknown`）と `GuardTask.SetEnabled(bool)`．タスク名は定数 `shiftlock-guard`．
- Produces: `native/deploy.ps1`（UTF-8 BOM・CRLF）— `shiftlock.Agent` と `shiftlock.App` を Release・win-x64 で発行し，`%LOCALAPPDATA%\Programs\shiftlock\` に置く（動いている Agent があれば先に終わらせ，置いた後に起動し直す）．`-WhatIf` で何をするかだけ表示．
- 自動起動の登録先は任せる（設計書 §8）．`Setup` は登録先への書き込みをインターフェースの裏に置き，テストで差し替える．

- [ ] **Step 1:** `SetupTests`（偽の登録先・偽の `GuardTask`）: `--install` で登録され，タスクが無効になる／タスクが無い → 飛ばして成功／タスクの無効化に失敗 → 終了コード 1 で，自動起動の登録は残さない（半端に二重の見張りにならない）／`--uninstall` で登録が消え，タスクが有効に戻る／`--install` を 2 回しても登録は 1 つ
- [ ] **Step 2:** 実装して通す
- [ ] **Step 3:** 手で確かめる（本番のタスクには触らない）: 試験用のタスク名（`shiftlock-guard-test` を一時的に作る．名前を引数か環境変数で差し替えられるようにしておく）で `State`・`SetEnabled` が効く．試験用のタスクは消す．`deploy.ps1 -WhatIf` の表示
- [ ] **Step 4:** `native/README.md` に切り替えの手順（置く → `--install` → 戻すときは `--uninstall`．切り替え後に `shiftlock.py tick` を手で実行しない）を書く
- [ ] **Step 5:** コミット `feat(native): Agent への切り替え（登録・戻す・発行して置く） (#4)`

---

### Task 15: 突き合わせ用の入口

**Files:**
- Create: `pc/tools/tick_once.py`，`native/shiftlock.Agent/TickOnce.cs`
- Modify: `native/shiftlock.Agent/Program.cs`
- Test: `pc/tests/test_tick_once.py`，`native/shiftlock.Agent.Tests/TickOnceTests.cs`

**Interfaces:**
- Produces: どちらも同じ引数で，同じ形の JSON を標準出力に 1 つ出す（キーは名前順，日本語はそのまま，インデント 1）．通知は出さず，強制も実行しない．**渡したフォルダのファイルは書き換える**（コピーに向けて使う）．

```
python3 pc/tools/tick_once.py   --dir <フォルダ> --now 2026-12-01T09:00 --idle 30 --game 0 --uptime 7200 [--dry-run]
shiftlock-agent.exe --tick-once --dir <フォルダ> --now 2026-12-01T09:00 --idle 30 --game 0 --uptime 7200 [--dry-run]
→ {"actions": [...], "sound": {"max_seconds": 20, "volume": 0.8}, "warnings": [...]}
```

  `--dir` が `%USERPROFILE%\shift-lock`（本番）を指していたら，どちらも何もせず終了コード 2 で断る．

- [ ] **Step 1:** テストを書く: 両方とも，`tick.json` の節 `notify` の最初のケースの 1 回目と同じ結果を出す／本番のフォルダは断る（`USERPROFILE` を一時フォルダに差し替えて確かめる）／`--now` が読めなければ終了コード 2
- [ ] **Step 2:** 実装して通す
- [ ] **Step 3:** コミット `feat(native): Python 版と突き合わせるための 1 回だけの見張り (#4)`

---

### Task 16: 本番のコピーでの突き合わせと文書

**Files:**
- Modify: `docs/pc-spec.md`，`native/README.md`，`docs/native-app-plan.md`，`docs/data-format.md`，`CLAUDE.md`，`README.md`

- [ ] **Step 1:** 本番フォルダを 2 つコピーする（スクラッチの場所に `copy-py`・`copy-cs`．`log/`・`reports/` も含める）．**本人に一声かけてから**行う
- [ ] **Step 2:** 同じ入力の列（今の時刻から 5 分おきに 12 回．idle は 30 と 1500 を混ぜ，game は 0 と 1 を混ぜる．カレンダーを取りに行かないよう，両方のコピーの `calendar.json` の `fetched` が `sync_minutes` 内になる時刻から始めるか，ネットワークありで同じ結果になることを確かめる）を `tick_once.py`（`copy-py`）と `--tick-once`（`copy-cs`）に与え，各回の JSON と，最後のファイル（`usage-pc.json`・`notified.json`・`state.json`・`events.log`・`log/pc-*.jsonl` の末尾・`maintenance.json`・`calendar.json`）が一致するのを確かめる．違いが出たら原因を直してベクタに足す．結果は「一致した／違った箇所の種類」だけ記録し，値は書かない
- [ ] **Step 3:** コピーを消す
- [ ] **Step 4:** 文書を直す（設計書 §6.3）: `docs/pc-spec.md` §7 に「ネイティブ版では Agent が行う．違いは 5 分の判定がログの最後の行で行われること，通知の名前が `shiftlock` になること」，`docs/data-format.md` に `enforce_release` の理由 `tray` と `agent.log`，`docs/native-app-plan.md` §5.3 と §8 の表，`CLAUDE.md` の決まり（「画面に強制モードの宣言・設定を置かない．解除は置いてよい」，Agent の確かめ方），`native/README.md`（構成の表・今できること・前提・コマンド），`README.md`
- [ ] **Step 5:** `dotnet test native`，pytest，`python3 pc/tools/export_native.py --check`，`python3 pc/tools/make_test_vectors.py --check`，`dotnet build native`（警告 0）を通す
- [ ] **Step 6:** コミット `docs(native): 区切り 2 の文書を直す (#4)`

本番への切り替え（`deploy.ps1` → `--install`）は本人が行う．この計画には含めない．
