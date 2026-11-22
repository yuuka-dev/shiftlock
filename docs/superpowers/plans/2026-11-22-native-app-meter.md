# ネイティブ版 区切り 3：アプリの稼働計測 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** トレイ常駐の Agent が 10 秒ごとにプロセスを測り，5 分ごとに `log/app-YYYY-MM.jsonl` へ 1 行書く．`shl app` で exe を登録でき，レポートにカテゴリ（ゲーム／開発／Adobe）の帯と時間が出る（issue #6）．

**Architecture:** 計算（増分・ツリー合計・アクティブの判定・行の組み立て）は `shiftlock.Core` の `AppMeter` に純粋な形で置き，合成の計測の列でテストする．`shiftlock.Agent` はプロセスの一覧と前面の PID を取る側（インターフェースの裏）と，`Scheduler` に足す 10 秒の仕事 `Meter` だけを持つ．見張り（`Watcher`・`Tick.Run`）には触れない．登録とレポートは Python 版（`pc/shiftlock/`）に足す．

**Tech Stack:** C# / .NET 10，xUnit，System.Text.Json，P/Invoke（`ntdll`・`user32`・`kernel32`），Python 3.8+（標準ライブラリのみ）と pytest

**Spec:** [docs/superpowers/specs/2026-11-22-native-app-meter-design.md](../specs/2026-11-22-native-app-meter-design.md)（以下「設計書」）．実装者は両方読む．

## Global Constraints

- .NET 10．`native/Directory.Build.props`（Nullable・警告はエラー）に従う．`*.cs`・`*.json` は UTF-8・LF．コメントは日本語．`*.py` は UTF-8・LF・標準ライブラリのみ．
- **プロセスに手を加えない**．開くときの権限は `PROCESS_QUERY_LIMITED_INFORMATION`（0x1000）だけ．止める・優先度を変える・ジョブに入れる処理を書かない．
- **登録していないアプリの名前を，ログ・`agent.log`・レポート・テストの出力のどこにも残さない**（例外の文にプロセス名を入れない）．レポートにはアプリ名を出さない．
- `Tick.Run`・`Watcher`・`log/pc-*.jsonl` の形を変えない．`docs/test-vectors/tick.json` は書き出し直しても差分が出ないこと．
- 判断は `shiftlock.Core` に置く．Core は Windows API を呼ばない．`now` と値は引数で受ける．
- 文面を C#・Python のコードに直書きしない．`pc/shiftlock/i18n/ja.py` と `ja_kansai.py` の両方に同じ名前で足す（句読点・言い回しはそのファイルの既存の文に合わせる）．`inm.py` は触らない．
- **文面のキーは `APPS_` で始める**（設計書は `APP_` と書いているが，`APP_` は `export_native.py` がネイティブの画面用に書き出す接頭辞なので避ける．設計書の該当行は Task 8 で直す）．
- 計測の失敗で Agent を落とさない．見張り・通知を遅らせない．`agent.log` の文は英語固定で，同じ失敗は 1 日 1 回だけ書く．
- 本番の実行フォルダ `%USERPROFILE%\shift-lock` に向けて動かさない・編集しない．確認は `SHIFTLOCK_DIR` を合成データに向ける．`--install`・`deploy.ps1` を実行しない．個人データをコミットしない．実データの値を文書・テスト・コミット・報告に書き写さない．
- テストの日付は合成（2026-12）だけ．exe 名は `code.exe`・`photoshop.exe`・`eldenring.exe`・`explorer.exe`・`node.exe`・`notepad.exe` を使う．
- コミットは日本語で `<型>(native): タイトル (#6)`（Python 側は `(pc)`）．`Co-Authored-By` や「Generated with」の行は付けない．push しない．
- テスト: C# は Windows で `dotnet test native`．Python は WSL で `cd /mnt/c/Dev/repo/public/shiftlock && source .venv/bin/activate && cd pc && python3 -m pytest -q tests`．触った側を全部通してからコミット．

## Review Focus

設計書が明示しないが，使っていて当たりやすい入力．各行のテストは担当タスクに入れてある．

1. **`log/app-*.jsonl` の最後の行が未来・時計が戻った**（別の PC からコピーしたフォルダ，時刻合わせ）: 「同じ `t` を 2 回書かない」の決まりで永久に書けなくならない．起動時に最後の行が今より後なら無視する．（Task 2・4）
2. **手で書いた `apps` がおかしい**（`registered` が辞書でない，カテゴリが `dev`／`adobe` 以外，名前が大文字や `.exe` なし）: 使えない項目だけ無視し，名前は小文字・`.exe` つきにそろえて比べる．数値がおかしいときだけ計測を失敗にする．（Task 2・5）
3. **スリープをはさんで区切りを何本もまたぐ**: 行は 1 本だけ（`t` は前の区間の終わり），寝ていた時間は `span`・`act`・`cpu` に入らない．（Task 2）
4. **権限が上のプロセスが前面・登録の exe が管理者で動いている**（パスが取れない）: 登録の名前は効く．同じプロセスのパスを 10 秒ごとに取り直さない．落ちない．（Task 4）
5. **`log/app-*.jsonl` に壊れた行**（`span` が無い・数でない，`apps` が辞書でない，`act` が文字列）: レポートはその行・その項目を飛ばして出る．（Task 6）

## ファイルの地図

```
docs/data-format.md                       apps の節と log/app-*.jsonl を足す（Task 1．最初）
docs/test-vectors/app-log.jsonl           新規．合成の 4 行（Task 2 で作り，Task 6 が読む）
pc/shifts.example.json                    apps の例（Task 1）
pc/shiftlock/config.py                    DEFAULTS に apps（Task 1）
pc/shiftlock/apps.py                      新規．shl app の中身（Task 5）
pc/shiftlock/cli.py                       app サブコマンド（Task 5）
pc/shiftlock/report.py                    カテゴリの集計（Task 6）
pc/shiftlock/report_html.py               帯・列・凡例（Task 7）
pc/shiftlock/i18n/ja.py, ja_kansai.py     APPS_*（Task 5），REPORT_*（Task 7）
pc/tests/test_apps.py                     新規（Task 5）
pc/tests/test_report_apps.py              新規（Task 6・7）
native/shiftlock.Core/
  ShiftConfig.cs        DefaultsJson に apps，BadValue を internal に（Task 1）
  MeterSettings.cs      新規．apps の節の読み取りと名前のそろえ方（Task 2）
  AppMeter.cs           新規．計測の積み上げと行の組み立て（Task 2）
native/shiftlock.Core.Tests/
  MeterSettingsTests.cs, AppMeterTests.cs   新規（Task 2）
  shiftlock.Core.Tests.csproj               app-log.jsonl を vectors/ へ写す（Task 2）
native/shiftlock.Agent/
  Platform/IProcessSource.cs      新規．インターフェースと ProcessSnapshot（Task 3）
  Platform/NtProcessSource.cs     新規．NtQuerySystemInformation（Task 3）
  Platform/DiagnosticsProcessSource.cs  新規．System.Diagnostics.Process（Task 3）
  Platform/FallbackProcessSource.cs     新規．前者が失敗したら後者（Task 3）
  Platform/IMeterProbe.cs         新規．前面の PID と exe のパス（Task 3）
  Platform/WindowsProbe.cs        IMeterProbe を実装（Task 3）
  OncePerDay.cs                   新規．同じ文は 1 日 1 回（Task 3）
  Meter.cs                        新規．10 秒の仕事（Task 4）
  MeterOnce.cs                    新規．--meter-once（Task 4）
  Program.cs                      仕事 meter と --meter-once，終了時の Flush（Task 4）
native/shiftlock.Agent.Tests/     ProcessSourceTests.cs, MeterTests.cs（Task 3・4）
docs/pc-spec.md, native/README.md, docs/native-app-plan.md, README.md, docs/native-agent-worklog.md（Task 8）
```

---

### Task 1: データ形式の文書と設定の既定値

**Files:**
- Modify: `docs/data-format.md`（§1 の表），`pc/shifts.example.json`，`pc/shiftlock/config.py`（`DEFAULTS`），`native/shiftlock.Core/ShiftConfig.cs`（`DefaultsJson`，`BadValue`）
- Test: `pc/tests/test_apps.py`（新規），`native/shiftlock.Core.Tests/ConfigTests.cs`

**Interfaces:**
- Produces: Python `DEFAULTS["apps"] == {"registered": {}, "active_input_minutes": 5, "active_cpu_percent": 5}`．C# `cfg.Raw["apps"]` に同じ既定．`ShiftConfig.BadValue(string key, JsonNode? value)` が `internal`（Task 2 が使う）．

- [ ] **Step 1: `docs/data-format.md` §1 を直す**

`shifts.json` の行に「`apps` = 稼働計測の登録と数値（`{"registered": {"<exe名（小文字・.exe つき）>": "dev"｜"adobe"}, "active_input_minutes", "active_cpu_percent"}`）．書くのは `shl app`」を足す．`log/pc-YYYY-MM.jsonl` の行の下に次の行を足す:

| `log/app-YYYY-MM.jsonl` | ネイティブ版の Agent（10 秒ごとに測り，時計の 5 分の区切りで 1 行） | `{"t", "span": 秒, "apps": {"<exe名>": {"cat": "game"｜"dev"｜"adobe", "act": 秒, "cpu": %}}, "other": 秒}`．`t` = 区間の終わり（秒まで），`span` = 測れた秒数，`act` = アクティブ（前面 ＋ 入力か CPU）だった秒数，`cpu` = 子プロセスを含む平均使用率（PC 全体 = 100．小数 1 桁），`other` = 登録もゲームでもないアプリがアクティブだった秒数．名前が載るのは登録した exe と自動判定のゲームだけ．レポートは `t − span/2` の時刻の日・時に `act` を足す |

- [ ] **Step 2: 落ちるテストを書く**

`pc/tests/test_apps.py`:

```python
from shiftlock.config import DEFAULTS, load_config

def test_defaults_have_apps():
    assert DEFAULTS["apps"] == {"registered": {}, "active_input_minutes": 5, "active_cpu_percent": 5}

def test_load_config_merges_apps(tmp_path):
    p = tmp_path / "shifts.json"
    p.write_text('{"apps": {"registered": {"code.exe": "dev"}}}', encoding="utf-8")
    cfg = load_config(p)
    assert cfg["apps"] == {"registered": {"code.exe": "dev"}, "active_input_minutes": 5, "active_cpu_percent": 5}
```

`ConfigTests.cs` に（既存のテストの設定の作り方に合わせて）:

```csharp
[Fact]
public void Defaults_HaveApps()
{
    var cfg = Load("{}");   // このファイルの既存のヘルパーに合わせる
    var apps = cfg.Raw["apps"]!.AsObject();
    Assert.Empty(apps["registered"]!.AsObject());
    Assert.Equal(5, apps["active_input_minutes"]!.GetValue<double>());
    Assert.Equal(5, apps["active_cpu_percent"]!.GetValue<double>());
}
```

- [ ] **Step 3: 落ちることを確かめる**（pytest は `KeyError: 'apps'`，dotnet は null 参照）

- [ ] **Step 4: 既定値を足す**

`config.py` の `DEFAULTS` の末尾（`enforce` の後）と `ShiftConfig.DefaultsJson` に同じ値を足す．`load_config` の「辞書であること」の検査と C# の `DictKeys` には**足さない**（任意機能の設定ミスで見張りを止めない．形がおかしいときは Task 2 の `MeterSettings.Read` が計測だけを失敗にする）．`ShiftConfig.BadValue` を `private` から `internal` にする．`pc/shifts.example.json` に `"apps": {"registered": {"code.exe": "dev", "photoshop.exe": "adobe"}}` を足す．

- [ ] **Step 5: 通ることと，テストベクタが変わらないことを確かめる**

Run（WSL）: `python3 pc/tools/export_native.py && python3 pc/tools/make_test_vectors.py && git status --short docs/test-vectors native/shared`
Expected: 何も出ない（差分なし）．pytest 全部と `dotnet test native` が PASS．

- [ ] **Step 6: コミット** `docs(native): 稼働計測のログと apps の節をデータ形式に足す (#6)`

---

### Task 2: Core — 設定の読み取りと計測の積み上げ

**Files:**
- Create: `native/shiftlock.Core/MeterSettings.cs`，`native/shiftlock.Core/AppMeter.cs`，`docs/test-vectors/app-log.jsonl`
- Modify: `native/shiftlock.Core.Tests/shiftlock.Core.Tests.csproj`（`..\..\docs\test-vectors\*.jsonl` も `vectors\` へ写す）
- Test: `native/shiftlock.Core.Tests/MeterSettingsTests.cs`，`native/shiftlock.Core.Tests/AppMeterTests.cs`

**Interfaces:**
- Consumes: `ShiftConfig.Raw`，`ShiftConfig.BadValue`（Task 1），`Usage.GameIdleLimit(cfg)`．
- Produces（名前空間 `Shiftlock.Core`．全部 public）:

```csharp
public sealed record MeterSettings(IReadOnlyDictionary<string, string> Registered, TimeSpan ActiveInput, double ActiveCpuPercent, TimeSpan? GameIdle)
{
    public static MeterSettings Read(ShiftConfig cfg);          // 数値がおかしければ ConfigException
    public static string? NormalizeExe(string? name);           // "Code" → "code.exe"．使えなければ null
}

public readonly record struct ProcKey(int Pid, DateTime Start);

/// Name は小文字（NormalizeExe 済みでなくてよい．取れたままを小文字にしたもの）．Start はローカル時刻．Cpu はユーザー＋カーネル．
public sealed record ProcSample(int Pid, int ParentPid, DateTime Start, TimeSpan Cpu, string Name)
{
    public ProcKey Key => new(Pid, Start);
}

public sealed record MeterSample(DateTime Now, TimeSpan Mono, IReadOnlyList<ProcSample> Procs, int? ForegroundPid,
    double IdleSeconds, IReadOnlySet<ProcKey> Games, int Cores, bool HasParents = true);

/// T は行の時刻，Rec は t を除いた中身（Store.LogAppend("app", T, Rec) にそのまま渡せる）．
public sealed record MeterLine(DateTime T, JsonObject Rec);

public sealed class AppMeter(DateTime? lastWritten = null)
{
    public MeterLine? Add(MeterSample sample, MeterSettings settings);   // 区切りをまたいだら前の区間の行
    public MeterLine? Flush(DateTime now);                               // たまっている分（終了時）
    public void Reset();                                                 // 起点もたまった分も捨てる（設定が読めない間など）．最後に書いた t は残す
}
```

**決まり**（設計書 §4.2〜4.4 を，実装が 1 通りになるところまで決めたもの）:

1. `dt = sample.Mono − 前回の Mono`．前回が無い・`dt ≤ 0`・`dt ≥ 30 秒` のときは**起点の取り直し**: 増分もアクティブも足さず，各プロセスの CPU 時間を覚えるだけ．
2. それ以外は，まず増分を今の区間に足す: `span += dt`．各プロセスの増分 = 今回の `Cpu` − 前回の `Cpu`（同じ `ProcKey`．負なら 0）．前回に無かったプロセスは `Start > 前回の Now` なら `Cpu` 全部，そうでなければ 0．
3. 名前つきのアプリ = (a) 名前が `settings.Registered` にあるプロセスの名前（カテゴリは登録の値），(b) そうでなく `Games` に `Key` があるプロセスの名前（カテゴリ `game`）．比べる名前は `Name` そのまま（Agent が小文字・`.exe` つきで渡す）．
4. アプリのツリー = 根（(a) はその名前の全プロセス，(b) はその名前で `Games` にあるプロセス）とその子孫．親子は「`ParentPid` が一覧にあり，親の `Start ≤` 子の `Start`」のときだけ．同じプロセスは 1 つのアプリの中で 1 回だけ数える（別のアプリのツリーには重ねて入ってよい）．`HasParents` が偽なら根だけ．
5. アプリごとに，ツリーの増分の合計を区間の「CPU 時間」に足す．そのサンプルに根が 1 つでも在ったアプリは，区間の「動いていた」に入れる（起点の取り直しのサンプルでも入れる）．
6. アクティブ: `ForegroundPid` のプロセスが一覧に無ければ，どれもアクティブでない．在れば，その**プロセス自身の名前**で (a)(b) を見て，当たればそのアプリ，当たらなければ「その他」（ツリーは前面のプロセスとその子孫）．`GameIdle` が null でなく `IdleSeconds ≥ GameIdle` ならアクティブでない．そうでなく，`IdleSeconds < ActiveInput` か「ツリーの増分 ÷ (dt × Cores) × 100 ≥ ActiveCpuPercent」なら，そのアプリ（か `other`）のアクティブ秒に `dt` を足す．
7. 区切り: 区間の枠 = サンプルの `Now` を 5 分に切り下げた時刻．増分を足した**後で**，今回の枠が区間の枠より後なら行を作る．`t = 区間の枠 + 5 分`．その後，新しい区間を今回の枠で始める（各プロセスの CPU 時間の起点は持ち越す）．今回の枠が区間の枠より**前**（時計が戻った）なら，行は作らず，区間の枠を今回の枠に替えるだけ（たまった分は持ち越す）．
8. 行を出さない場合: `span` が 0（丸めて 0 を含む）→ 捨てて null．`t ≤ 最後に書いた t` → **出さずに持ち越す**（たまった分は次の区間に合算）．行を返したら「最後に書いた t」を更新する．コンストラクタの `lastWritten` は，最初の `Add`／`Flush` の時刻の 5 分の枠の終わりより後なら無視する（Review Focus 1．枠の終わりちょうどまでは有効: `SameT_IsNotWrittenTwice`）．
9. `Flush(now)`: `t = now`（秒未満は切り捨て）で 8 と同じ決まり．返した後は区間を空にする（起点は残す）．
10. 行の中身（キーの順）: `span`（秒．四捨五入の整数），`apps`（名前の順は `StringComparer.Ordinal`．各値は `cat`，`act`（四捨五入の整数），`cpu`），`other`（整数）．`cpu = CPU 時間の合計秒 ÷ (span の秒 × Cores) × 100` を小数 1 桁に四捨五入（`MidpointRounding.AwayFromZero`）し，**必ず小数 1 桁の字面**で書く（`1.0`・`0.0`．`JsonNode.Parse(値.ToString("0.0", CultureInfo.InvariantCulture))` で作ると字面が残る）．`Cores` は区間の最後のサンプルの値．カテゴリは行を作る時点の `settings`．
11. `MeterSettings.Read`: `cfg.Raw["apps"]` が辞書でなければ `ConfigException`（`cfg.BadValue("apps", 値)`）．`registered` が辞書でなければ空として扱う．各項目は `NormalizeExe(キー)` が null でなく値が `"dev"`／`"adobe"` のものだけ採る（ほかは無視）．`active_input_minutes`・`active_cpu_percent` は JSON の数だけ受け，数でない・負・非有限なら `ConfigException`（キーは `apps.active_input_minutes` など）．`GameIdle = Usage.GameIdleLimit(cfg)`．
12. `NormalizeExe`: 前後の空白を取り，小文字（`ToLowerInvariant`）にし，`.exe` で終わっていなければ足す．空・`\` `/` `:` を含むものは null．

- [ ] **Step 1: `MeterSettingsTests.cs` を書く（落ちる）**

```csharp
[Theory]
[InlineData("Code", "code.exe")]
[InlineData(" Code.EXE ", "code.exe")]
[InlineData("photoshop.exe", "photoshop.exe")]
[InlineData("", null)]
[InlineData(@"C:\x\code.exe", null)]
public void NormalizeExe_Cases(string input, string? expected) =>
    Assert.Equal(expected, MeterSettings.NormalizeExe(input));
```

`Read` について，`shifts.json` を一時フォルダに書いて `ShiftConfig.Load` → `MeterSettings.Read` で次を確かめる:

| テスト名 | `shifts.json` | 期待 |
|---|---|---|
| `Read_Defaults` | `{}` | `Registered` 空，`ActiveInput` 5 分，`ActiveCpuPercent` 5，`GameIdle` 180 分 |
| `Read_NormalizesAndDropsBadEntries` | `{"apps":{"registered":{"Code":"dev","photoshop.exe":"adobe","x.exe":"game","y.exe":5}}}` | `{"code.exe":"dev","photoshop.exe":"adobe"}` だけ |
| `Read_RegisteredNotDict_IsEmpty` | `{"apps":{"registered":[1]}}` | `Registered` 空（例外なし） |
| `Read_AppsNotDict_Throws` | `{"apps":5}` | `ConfigException` |
| `Read_BadNumber_Throws` | `{"apps":{"active_cpu_percent":"5"}}`，`{"apps":{"active_input_minutes":-1}}` | どちらも `ConfigException` |
| `Read_GameIdleZero_IsNull` | `{"usage":{"game_idle_minutes":0}}` | `GameIdle` が null |

- [ ] **Step 2: `MeterSettings.cs` を実装して通す**

- [ ] **Step 3: `AppMeterTests.cs` を書く（落ちる）**

テストの中に小さな道具を作る: 時刻 `21:00:00`（2026-12-07）から 10 秒おきのサンプルを作り，`Mono` は最初のサンプルからの経過．`Cores = 4`．既定の `settings` = `Registered {"code.exe":"dev","photoshop.exe":"adobe"}`，入力 5 分，CPU 5%，`GameIdle` 180 分．プロセスは「10 秒ごとに `Cpu` が決まった量だけ増える」形で書けるようにする（4 コアで +0.4 秒 = 1%，+4 秒 = 10%）．断りがなければ，プロセスの `Start` は 20:00:00，サンプルは 21:00:00（起点）〜21:05:00（30 回の増分．またぎ）で，返る行は `t = 2026-12-07T21:05:00`・`span = 300`．比べるのは `PyJson` で書いたときと同じ字面（`Store.LogAppend` で一時フォルダに書いて行を読むのが確実）．

| テスト名 | 入力 | 期待する行（`t` を除く） |
|---|---|---|
| `Foreground_WithInput_IsActive` | `code.exe`(pid 100) 前面，アイドル 10，+0.4 | `"span": 300, "apps": {"code.exe": {"cat": "dev", "act": 300, "cpu": 1.0}}, "other": 0` |
| `Foreground_NoInput_HighCpu_IsActive` | 同じでアイドル 600，+4 | `act` 300，`cpu` 10.0 |
| `Foreground_NoInput_LowCpu_IsNotActive` | アイドル 600，+0.4 | `act` 0，`cpu` 1.0，`other` 0 |
| `NotForeground_RecordsCpuOnly` | 前面は `explorer.exe`(200)，アイドル 10，`code.exe` +0.4 | `code.exe` は `act` 0・`cpu` 1.0，`other` 300 |
| `IdleBeyondGameIdle_IsNotActive` | 前面 `code.exe`，アイドル 10800，+4 | `act` 0，`cpu` 10.0 |
| `Game_IsRecordedUnderExeName` | `eldenring.exe`(300) が `Games` にあり前面，アイドル 600，+4 | `"apps": {"eldenring.exe": {"cat": "game", "act": 300, "cpu": 10.0}}` |
| `RegisteredWinsOverGame` | `code.exe` の `Key` が `Games` にもある | `cat` は `dev` |
| `NoRegistration_OnlyOther` | `Registered` 空，前面 `code.exe`，アイドル 10 | `"apps": {}, "other": 300` |
| `PidReuse_IsNotSameProcess` | 21:01:50 まで `code.exe`(100) 前面 +0.4．21:02:00 から pid 100 は `notepad.exe`（`Start` 21:01:55，`Cpu` 5 秒から）で前面，アイドル 10 | `code.exe` は `act` 110・`cpu` 0.4，`other` 190 |
| `Tree_SumsChildrenOnce` | `code.exe`(100)，子 `node.exe`(101，親 100，`Start` 20:00:01)，子 `code.exe`(103，親 100)，それぞれ +0.4．別に `notepad.exe`(102，親 100 だが `Start` 19:00:00)が +4 | `cpu` 3.0（102 は数えない．103 は 1 回だけ） |
| `NoParents_CountsSameNameOnly` | 上と同じで `HasParents: false` | `cpu` 2.0（100 と 103） |
| `ProcessStartedMidInterval_CountsAllItsCpu` | `code.exe` が 21:01:00 のサンプルで初めて現れる（`Start` 21:00:55，`Cpu` 2.0），以後 +0.4．前面は `explorer.exe` | `cpu` = (2.0 + 0.4×24) ÷ (300×4) × 100 = 1.0 |
| `Gap_DropsIncrement` | 21:02:00 の次のサンプルが 21:03:00（`Mono` も 60 秒進む．その間に `Cpu` は +100 秒），以後 10 秒おき | `span` 240，`act` 240，`cpu` 1.0 |
| `LongSleep_WritesOneLineForOldBucket` | 21:02:00 の次が 21:31:00 | そのサンプルで返る行は `t = 21:05:00`・`span` 120．次に返る行は 21:35:00 |
| `MonthBoundary` | 2026-12-31 23:55:00〜2027-01-01 00:00:00 | `T == 2027-01-01 00:00:00` |
| `SameT_IsNotWrittenTwice` | `new AppMeter(lastWritten: 21:05:00)` で 21:00:00〜21:10:00 | 21:05:00 のサンプルは null，21:10:00 で `t = 21:10:00`・`span` 600・`act` 600 |
| `LastWrittenInFuture_IsIgnored` | `new AppMeter(lastWritten: 22:00:00)` | 21:05:00 で行が返る |
| `ClockBack_DoesNotRepeatT` | 21:05:00 の行を出した後，サンプルの `Now` が 21:03:00 に戻り，10 秒おきに 21:05:00 を越える | 戻ったサンプルでも，2 回目に 21:05:00 を越えたサンプルでも null．次に返るのは 21:10:00 のサンプルで `t = 21:10:00`（戻ってからの分を全部含む） |
| `Flush_WritesPending` | 21:00:00〜21:01:00 の後 `Flush(21:01:03.500)` | `T == 21:01:03`，`span` 60．続けて `Flush` しても null |
| `NothingMeasured_NoLine` | サンプル 1 個だけで `Flush` | null |
| `RunningButIdleApp_IsListed` | `code.exe` が在るが増分 0，前面でない | `{"cat": "dev", "act": 0, "cpu": 0.0}` |
| `Reset_DropsBaseline` | 21:00:00〜21:01:00 の後 `Reset()`，21:01:10 から続ける | 21:01:10 は起点の取り直しになり，行の `span` は 230 |

- [ ] **Step 4: 共有の見本 `docs/test-vectors/app-log.jsonl` を作り，そのテストを足す**

ファイルの中身（この 4 行ちょうど．末尾は LF）:

```
{"t": "2026-12-07T21:05:00", "span": 300, "apps": {"code.exe": {"cat": "dev", "act": 300, "cpu": 1.0}}, "other": 0}
{"t": "2026-12-07T21:10:00", "span": 300, "apps": {"code.exe": {"cat": "dev", "act": 0, "cpu": 1.0}}, "other": 300}
{"t": "2026-12-07T21:15:00", "span": 300, "apps": {"eldenring.exe": {"cat": "game", "act": 300, "cpu": 10.0}}, "other": 0}
{"t": "2026-12-07T21:20:00", "span": 240, "apps": {"photoshop.exe": {"cat": "adobe", "act": 240, "cpu": 1.0}}, "other": 0}
```

テスト `SharedVector_ScenarioWritesTheseLines`: 次の 1 本のシナリオを `AppMeter` に通し，`Store.LogAppend("app", line.T, line.Rec)` で一時フォルダに書いた `log/app-2026-12.jsonl` が，`vectors/app-log.jsonl` とバイト単位で同じことを確かめる．

- 21:00:00〜21:05:00: `code.exe`(100) 前面，アイドル 10，+0.4．`explorer.exe`(200) は増分 0．
- 21:05:10〜21:10:00: 前面は `explorer.exe`，アイドル 10．`code.exe` は +0.4．
- 21:10:10〜21:15:00: `code.exe` は居ない．`eldenring.exe`(300．`Start` 21:10:05．最初の `Cpu` 4.0，以後 +4．`Games` にある）が前面，アイドル 600．
- 21:15:10〜21:17:30: `eldenring.exe` は居ない．`photoshop.exe`(400．`Start` 21:15:05．最初の `Cpu` 0.4，以後 +0.4）が前面，アイドル 10．
- 次のサンプルは 21:18:30（60 秒空く．その間の `Cpu` の伸びは数えない），以後 10 秒おきに 21:20:00 まで（増分は 15 回 + 9 回 = 240 秒）．

- [ ] **Step 5: `AppMeter.cs` を実装して全部通す**

Run: `dotnet test native --filter "FullyQualifiedName~AppMeter|FullyQualifiedName~MeterSettings"` → PASS．続けて `dotnet test native` 全部．

- [ ] **Step 6: コミット** `feat(native): 稼働計測の計算と行の組み立てを Core に足す (#6)`

---

### Task 3: Agent — プロセスの一覧と前面の PID を取る側

**Files:**
- Create: `native/shiftlock.Agent/Platform/IProcessSource.cs`，`NtProcessSource.cs`，`DiagnosticsProcessSource.cs`，`FallbackProcessSource.cs`，`IMeterProbe.cs`，`native/shiftlock.Agent/OncePerDay.cs`
- Modify: `native/shiftlock.Agent/Platform/WindowsProbe.cs`
- Test: `native/shiftlock.Agent.Tests/ProcessSourceTests.cs`

**Interfaces:**
- Consumes: `ProcSample`（Task 2）．
- Produces（名前空間 `Shiftlock.Agent`）:

```csharp
public sealed record ProcessSnapshot(IReadOnlyList<ProcSample> Procs, bool HasParents);

/// 全プロセスの一覧．取れなければ例外（呼ぶ側が握る）．
public interface IProcessSource { ProcessSnapshot Snapshot(); }

public sealed class NtProcessSource : IProcessSource;            // HasParents = true
public sealed class DiagnosticsProcessSource : IProcessSource;   // HasParents = false，ParentPid = 0
public sealed class FallbackProcessSource(IProcessSource primary, IProcessSource secondary, OncePerDay log, Func<DateTime> clock) : IProcessSource;

/// 計測が使う Windows 側の値．どれも例外を出さず，取れなければ null．
public interface IMeterProbe
{
    int? ForegroundPid();           // GetForegroundWindow → GetWindowThreadProcessId．窓が無い・pid 0 は null
    string? ImagePath(int pid);     // 既存の WindowsProbe.ImagePath を public のインスタンスメソッドにして使う
}

public sealed class OncePerDay(AgentLog log)
{
    public void Write(string key, string message, DateOnly today);   // 同じ key は同じ日に 1 回だけ log.Write
}
```

`WindowsProbe` は `ISystemProbe, IMeterProbe` を実装する（既存の `Processes()` は変えない）．

**取る値の決まり**:
- `ProcSample.Name`: exe のファイル名を `ToLowerInvariant` したもの．`NtProcessSource` は `ImageName` そのまま（`system`・`registry` のように `.exe` の無い名前はそのまま）．`DiagnosticsProcessSource` は `ProcessName + ".exe"`．名前が空のもの（pid 0）は入れない．
- `Start`: ローカル時刻（`DateTime.FromFileTime(CreateTime)`／`Process.StartTime`）．`Cpu`: ユーザー＋カーネル．
- `NtProcessSource`: `NtQuerySystemInformation(SystemProcessInformation = 5, …)`．`STATUS_INFO_LENGTH_MISMATCH`（0xC0000004）なら返ってきた長さより大きいバッファでやり直す（8 回まで）．ほかの失敗は `InvalidOperationException`（文は `NtQuerySystemInformation failed: 0x…`．プロセス名を入れない）．`SYSTEM_PROCESS_INFORMATION` から読むのは `NextEntryOffset`・`CreateTime`・`UserTime`・`KernelTime`・`ImageName`（`UNICODE_STRING`）・`UniqueProcessId`・`InheritedFromUniqueProcessId`．構造体の読み方は任せる（`IntPtr` の幅で 32/64 ビットの両方に合う形にする）．バッファは `finally` で必ず返す．
- `DiagnosticsProcessSource`: `Process.GetProcesses()`．`StartTime`・`TotalProcessorTime` が取れないプロセスは飛ばす．全部 `Dispose` する．`GetProcesses` 自体の失敗は外に出す．
- `FallbackProcessSource`: `primary` が例外なら `log.Write("process-fallback", "meter: process list fell back to System.Diagnostics.Process: <例外の型>: <文>", today)` して `secondary` を返す．`secondary` も失敗したらその例外を外に出す．次の回はまた `primary` から試す．

- [ ] **Step 1: テストを書く（落ちる）**

```csharp
[Theory]
[MemberData(nameof(RealSources))]   // new NtProcessSource(), new DiagnosticsProcessSource()
public void Snapshot_ContainsThisProcess(IProcessSource source)
{
    using var me = Process.GetCurrentProcess();
    var snap = source.Snapshot();
    var p = Assert.Single(snap.Procs, x => x.Pid == me.Id);
    Assert.Equal((me.ProcessName + ".exe").ToLowerInvariant(), p.Name);
    Assert.True((p.Start - me.StartTime).Duration() < TimeSpan.FromSeconds(1));
    Assert.True(p.Cpu > TimeSpan.Zero);
    Assert.All(snap.Procs, x => Assert.NotEqual("", x.Name));
}

[Fact]
public void Nt_HasParents_AndThisProcessHasAParentPid()
{
    var snap = new NtProcessSource().Snapshot();
    Assert.True(snap.HasParents);
    Assert.NotEqual(0, snap.Procs.Single(x => x.Pid == Environment.ProcessId).ParentPid);
}

[Fact]
public void Nt_CpuGrows_BetweenSnapshots()   // 自分で 200 ms ほど CPU を回してから取り直す
```

`FallbackProcessSource`（偽の `IProcessSource` で）: `Primary_Ok_UsesPrimary`／`Primary_Throws_UsesSecondary_AndLogsOncePerDay`（2 回呼んで `agent.log` に `process list fell back` が 1 行．日付を翌日にしてもう 1 回で 2 行）／`Both_Throw_Throws`．
`OncePerDay`: 同じ key・同じ日は 1 行，key が違えば別，日が変われば再び書く．
`WindowsProbe`: `ForegroundPid()` は例外を出さない（値は null でも数でもよい）．`ImagePath(Environment.ProcessId)` は `Environment.ProcessPath` と同じ（大文字小文字を区別しない）．`ImagePath(-1)` は null．

- [ ] **Step 2: 落ちることを確かめる → 実装 → `dotnet test native` が PASS**

- [ ] **Step 3: コミット** `feat(native): プロセスの一覧（NTAPI と代替）と前面の PID を取る側を足す (#6)`

---

### Task 4: Agent — 10 秒の仕事，終了時の書き出し，`--meter-once`

**Files:**
- Create: `native/shiftlock.Agent/Meter.cs`，`native/shiftlock.Agent/MeterOnce.cs`
- Modify: `native/shiftlock.Agent/Program.cs`
- Test: `native/shiftlock.Agent.Tests/MeterTests.cs`

**Interfaces:**
- Consumes: `AppMeter`・`MeterSettings`・`MeterSample`・`MeterLine`（Task 2），`IProcessSource`・`IMeterProbe`・`OncePerDay`（Task 3），`ISystemProbe`（`IdleSeconds`・`SteamCommonDirs`），`Watcher.LoadConfig(dir)`，`Games.Rules`・`Games.IsGame`，`Store.LogAppend`・`Store.LastLogTime("app")`．
- Produces:

```csharp
public sealed class Meter(string dir, IProcessSource source, IMeterProbe meterProbe, ISystemProbe probe,
    Func<DateTime> clock, Func<TimeSpan> mono, AgentLog log)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    public void RunOnce();   // 例外を外に出さない．前の回が動いていたら何もしない
    public void Flush();     // たまっている分を書く（終了時）．動いている回があれば 2 秒まで待つ
}

public static class MeterOnce
{
    /// 10 秒（wait）あけて 2 回測り，書くはずの行を JSON で出す．ファイルは書かない．成功 0，失敗 1．
    public static int Run(string dir, IProcessSource source, IMeterProbe meterProbe, ISystemProbe probe,
        TextWriter output, TextWriter error, Action<TimeSpan> wait);
}
```

**`Meter.RunOnce` の決まり**:
1. 設定を読むのは「まだ読めていないとき」と「行を返した直後の回」（＝ 5 分の区間の頭）だけ．読めていない間の読み直しも，時計の 5 分の枠が変わったときだけにする（10 秒ごとに読まない）．`Watcher.LoadConfig` が null（無い・壊れている）か `MeterSettings.Read` が `ConfigException` なら，`once.Write("meter-config", "meter: not measuring: <文>", today)` して `AppMeter.Reset()`，その回は終わり．設定が読めたら `Games.Rules(cfg, probe.SteamCommonDirs())` も作り直す．
2. `source.Snapshot()` が例外なら `once.Write("meter-source", "meter failed: <型>: <文>", today)` して終わり（次の回は普通に試す）．
3. ゲームの判定: プロセスごとに，exe のパスを `ProcKey` で覚えた入れ物から引き，無ければ `meterProbe.ImagePath(pid)` を 1 回だけ呼んで（null でも）覚える．`Games.IsGame(rules, 拡張子 .exe を取った名前, パス)` が真の `Key` を `Games` に入れる．入れ物は，今回の一覧に無い `Key` を毎回捨てる．
4. `new MeterSample(clock(), mono(), procs, meterProbe.ForegroundPid(), probe.IdleSeconds(), games, Environment.ProcessorCount, snapshot.HasParents)` を `AppMeter.Add` に渡す．行が返ったら `new Store(dir).LogAppend("app", line.T, line.Rec)`．`StateBusyException` は `once.Write("meter-write", …)` してその行は諦める（やり直さない）．
5. `AppMeter` は最初の回に `new AppMeter(new Store(dir).LastLogTime("app"))` で作る．
6. Python 版のタスク（`shiftlock-guard`）の状態は見ない．`Watcher` を呼ばない．
7. `Flush()`: `AppMeter.Flush(clock())` の行を 4 と同じに書く．

**`Program.cs`**:
- `RunAgent`: `var windowsProbe = new WindowsProbe();` を 1 つ作って `Watcher` と `Meter` の両方に渡す．`var once = new OncePerDay(log);` `var meter = new Meter(dir, new FallbackProcessSource(new NtProcessSource(), new DiagnosticsProcessSource(), once, clock), windowsProbe, windowsProbe, clock, () => Stopwatch.GetElapsedTime(0), log);`（`Meter` の中の `OncePerDay` も同じものを使えるよう，コンストラクタの形は実装で調整してよい）．
- `Scheduler` の一覧に `new Job("meter", Meter.Interval, Meter.Interval, meter.RunOnce)` を足す．`OnPower` は変えない（復帰で `meter` を Kick しない）．
- `finally` の中で，`quit.Cancel()` の後に `scheduler.Dispose()`（タイマーを止める．2 回呼んでも害は無い）→ `meter.Flush()` の順で呼ぶ．`agent stopped` のログより前．
- 引数 `--meter-once` → `OpenConsole(); return MeterOnce.Run(AgentPaths.DataDir, …実物…, Console.Out, Console.Error, Thread.Sleep);`．クラスの冒頭のコメントの一覧にも 1 行足す．

**`MeterOnce.Run`**: 設定が読めなければ `error` に文を書いて 1．`Snapshot` → `wait(10 秒)` → `Snapshot` を `AppMeter` に通し，`Flush` の行を出す．出力は 1 行の JSON: `{"parents": true|false, "procs": 数, "line": {"t": …, "span": …, "apps": {…}, "other": …}}`（行が無ければ `"line": null`）．本番のフォルダでも断らない（何も書かないので．読むのは `shifts.json` だけ）．

- [ ] **Step 1: `MeterTests.cs` を書く（落ちる）**

偽物: `FakeSource : IProcessSource`（次に返す一覧を差し替えられる．`Throw` を立てると例外），`FakeMeterProbe : IMeterProbe`（`Foreground`・`Paths`（pid → パス）・`ImagePathCalls`（pid ごとの回数）），既存の `FakeProbe`・`TempDir`．時計と `mono` はテストが進める．設定は `WatcherTests.Shifts` に `"apps":{"registered":{"code.exe":"dev"}}` を足したもの（`game_dirs` は `C:\Games`）．

| テスト名 | 確かめること |
|---|---|
| `FiveMinutes_WritesOneLine` | 21:00:00〜21:05:00 を 10 秒おきに `RunOnce`．`log/app-2026-12.jsonl` が 1 行で，`"code.exe": {"cat": "dev", "act": 300, …}` |
| `DoesNotTouchWatchLog` | 上の後，`log/pc-2026-12.jsonl`・`usage-pc.json`・`state.json` が無い |
| `GameByFolder_IsRecorded` | `Paths[300] = @"C:\Games\Elden\eldenring.exe"` で前面 → 行に `"eldenring.exe": {"cat": "game"` |
| `PathIsAskedOncePerProcess` | 5 分回して，`ImagePathCalls[100] == 1`．パスが null のプロセスも 1 回だけ |
| `UnregisteredNames_NeverAppear` | 前面が `notepad.exe` の 5 分．`log/app-*.jsonl` と `agent.log` のどちらにも `notepad` の字が無い |
| `SourceThrows_DoesNotThrow_LogsOnce` | `Throw` を立てて 3 回 `RunOnce`．例外なし．`agent.log` に `meter failed` が 1 行．戻すと次の回から測れる |
| `NoConfig_DoesNotMeasure_ThenRecovers` | `shifts.json` 無しで 21:00:00〜21:04:50（行なし・`agent.log` に `not measuring` 1 行）．21:05:00 の前に `shifts.json` を置くと，21:05:00 の回で読み直し，21:10:00 に行が出る |
| `BadAppsNumber_DoesNotMeasure` | `"apps":{"active_cpu_percent":"x"}` → 行なし・ログ 1 行・例外なし |
| `RegistrationChange_AppliesFromNextInterval` | 21:02:00 に `shifts.json` の登録を `{"notepad.exe":"dev"}` に書き換える．21:05:00 の行は `code.exe`，21:10:00 の行は `notepad.exe` |
| `Flush_WritesPending` | 21:00:00〜21:01:00 の後 `Flush()`（時計 21:01:03）→ `t` が `2026-12-07T21:01:03` の行 |
| `Restart_DoesNotRepeatT` | 既に `{"t": "2026-12-07T21:05:00", …}` の行がある状態で新しい `Meter` を 21:04:00〜21:10:00 → 21:05:00 の行は増えず，21:10:00 の行が 1 本足される |
| `LastLineInFuture_StillWrites` | 既にある行の `t` が `2026-12-08T00:00:00` → 21:05:00 の行が書かれる |
| `LogBusy_DoesNotThrow` | `log/app-2026-12.jsonl` を `FileShare.None` で開いたまま区切りをまたぐ → 例外なし，`agent.log` に 1 行 |
| `Busy_Skips` | `FakeSource.Snapshot` の中で止めておき，別スレッドから `RunOnce` → すぐ返る（`Snapshot` の呼び出しは 1 回） |

`MeterOnce`: `Run_PrintsLine_AndWritesNoFile`（`wait` は時計を 10 秒進める偽物．出力を `JsonNode.Parse` して `line.span == 10`，`parents == true`．フォルダに `log/` も `agent.log` も増えない）／`Run_NoConfig_Returns1`．

- [ ] **Step 2: 落ちることを確かめる → `Meter.cs`・`MeterOnce.cs`・`Program.cs` を実装 → `dotnet test native` が PASS**

- [ ] **Step 3: 合成データで動かして確かめる**

```powershell
$env:SHIFTLOCK_DIR = "$env:TEMP\shiftlock-meter-check"; $env:SHIFTLOCK_GUARD_TASK = 'shiftlock-guard-test'
New-Item -ItemType Directory -Force $env:SHIFTLOCK_DIR | Out-Null
Copy-Item pc\shifts.example.json "$env:SHIFTLOCK_DIR\shifts.json"
dotnet run --project native\shiftlock.Agent -- --meter-once
```

Expected: 10 秒ほどで `{"parents": true, "procs": <数百>, "line": {"t": "…", "span": 10, …}}` が 1 行出る．`$env:SHIFTLOCK_DIR` に `log\` が出来ていない．出力に登録していない exe 名が無い．（出力そのものは報告・コミットに貼らない．`parents` と `span` だけ報告する）

- [ ] **Step 4: コミット** `feat(native): 稼働計測を 10 秒の仕事として Agent に足す (#6)`

---

### Task 5: Python — `shl app add / remove / list`

**Files:**
- Create: `pc/shiftlock/apps.py`
- Modify: `pc/shiftlock/cli.py`，`pc/shiftlock/__init__.py`（ほかのモジュールと同じ形で公開），`pc/shiftlock/i18n/ja.py`，`pc/shiftlock/i18n/ja_kansai.py`
- Test: `pc/tests/test_apps.py`

**Interfaces:**
- Consumes: `config.load_raw`・`config.save_raw`（設定メニューと同じ道．省略したキーを書き足さない）．
- Produces:

```python
APP_CATEGORIES = ("dev", "adobe")
def normalize_exe(name: str) -> str | None      # C# の MeterSettings.NormalizeExe と同じ決まり
def app_add(cfg_path: Path, name: str, cat: str) -> tuple[int, str]     # (終了コード, 出す文)
def app_remove(cfg_path: Path, name: str) -> tuple[int, str]
def app_list(cfg_path: Path) -> tuple[int, str]
```

**決まり**:
- `cli.py`: サブコマンド `app`．`action`（`nargs="?"`，`choices=["add", "remove", "list"]`），`name`（`nargs="?"`），`cat`（`nargs="?"`）．`load_config` より前で `apply_lang(a.config)` してから処理し，文を `print` して終了コードを返す．`action` が無い・`add` で `name` か `cat` が無い・`remove` で `name` が無い → `APPS_USAGE` を出して 1．
- `add`: `cat` が `game` → `APPS_NO_GAME` で 1．`dev`／`adobe` 以外 → `APPS_BAD_CAT` で 1．`normalize_exe` が None → `APPS_BAD_NAME` で 1．`raw["apps"]` が在って辞書でない，または `raw["apps"]["registered"]` が在って辞書でない → `APPS_BAD_SECTION` で 1（書き換えない）．新規は `APPS_ADDED`，別カテゴリからの付け替えは `APPS_CHANGED`，同じカテゴリなら `APPS_ADDED`．どれも 0．
- `remove`: 名前をそろえて，`registered` のキーのうち `normalize_exe(キー)` が同じものを全部外す（手で大文字で書いたものも外れる）．無ければ `APPS_NOT_FOUND` で 1，ファイルは書かない．
- `list`: カテゴリごとに 1 行（`dev`，`adobe` の順．名前は辞書順）．登録が無ければ `APPS_LIST_EMPTY`．0．
- 書くのは `raw["apps"]["registered"]` だけ．`apps` の数値のキーやほかの節を書き足さない．

文面（`ja.py`．`ja_kansai.py` には同じ名前で関西弁を足す．句読点はそのファイルの既存の文に合わせる）:

| キー | `ja.py` | `ja_kansai.py` |
|---|---|---|
| `APPS_ADDED` | `{name} を {cat} で登録したよ` | `{name} を {cat} で登録したで` |
| `APPS_CHANGED` | `{name} を {old} から {cat} に付け替えたよ` | `{name} を {old} から {cat} に付け替えたで` |
| `APPS_REMOVED` | `{name} の登録を外したよ` | `{name} の登録を外したで` |
| `APPS_NOT_FOUND` | `{name} は登録されていないよ` | `{name} は登録されてへんで` |
| `APPS_NO_GAME` | `ゲームは登録しなくていいよ（自動で判定する。ゲームのフォルダは shl settings で）` | `ゲームは登録せんでええで（自動で判定する。ゲームのフォルダは shl settings で）` |
| `APPS_BAD_CAT` | `カテゴリは dev か adobe だよ（例: shl app add code.exe dev）` | `カテゴリは dev か adobe やで（例: shl app add code.exe dev）` |
| `APPS_BAD_NAME` | `exe の名前だけを書いてね（フォルダは書かない。例: code.exe）` | `exe の名前だけ書いてな（フォルダは書かへん。例: code.exe）` |
| `APPS_BAD_SECTION` | `shifts.json の apps の形がおかしいので書き換えないよ` | `shifts.json の apps の形がおかしいから書き換えへんで` |
| `APPS_USAGE` | `使い方: shl app add <exe名> <dev\|adobe> / shl app remove <exe名> / shl app list` | 同じ |
| `APPS_LIST_LINE` | `{cat}: {names}` | 同じ |
| `APPS_LIST_EMPTY` | `登録は無いよ（shl app add code.exe dev で足せる）` | `登録は無いで（shl app add code.exe dev で足せる）` |

`CLI_HELP`（両方）に 1 行足す: `  shl app add <exe> <dev|adobe> / remove <exe> / list   時間を測るアプリの登録（開発・Adobe。ゲームは自動）`（桁は前後の行に合わせる）．

- [ ] **Step 1: テストを書く（落ちる）** — `pc/tests/test_apps.py` に足す．`main(["app", …, "--config", str(p)])` と `capsys` で確かめる．

```python
def _cfg(tmp_path, body='{"lang": "ja", "start_target": "20:00"}'):
    p = tmp_path / "shifts.json"
    p.write_text(body, encoding="utf-8")
    return p

def test_add_normalizes_and_keeps_other_keys(tmp_path, capsys):
    p = _cfg(tmp_path)
    assert main(["app", "add", "Code", "dev", "--config", str(p)]) == 0
    assert json.loads(p.read_text(encoding="utf-8")) == {
        "lang": "ja", "start_target": "20:00", "apps": {"registered": {"code.exe": "dev"}}}
    assert "code.exe" in capsys.readouterr().out

def test_add_again_changes_category(tmp_path, capsys):      # dev → adobe．出力に APPS_CHANGED の文
def test_add_game_is_refused(tmp_path, capsys):             # 終了コード 1，ファイルはバイト単位で元のまま
def test_add_bad_category_is_refused(tmp_path):             # "video" → 1，元のまま
def test_add_path_is_refused(tmp_path):                     # r"C:\x\code.exe" → 1，元のまま
def test_add_bad_section_is_refused(tmp_path):              # {"apps": 5} と {"apps": {"registered": []}} → 1，元のまま
def test_remove(tmp_path):                                  # {"Code.EXE": "dev", "photoshop.exe": "adobe"} から remove code → photoshop だけ残る
def test_remove_missing_returns_1(tmp_path):                # ファイルは元のまま
def test_list_groups_by_category(tmp_path, capsys):         # 出力が "dev: code.exe, node.exe\nadobe: photoshop.exe\n"
def test_list_empty(tmp_path, capsys)
def test_missing_args_show_usage(tmp_path, capsys):         # ["app"]，["app", "add", "code"]，["app", "remove"] → 1
def test_help_mentions_app():                               # "shl app" in M.CLI_HELP（ja・ja_kansai の両方）
@pytest.mark.parametrize("raw,want", [("Code", "code.exe"), (" Code.EXE ", "code.exe"), ("", None), ("a/b", None), ("C:x", None)])
def test_normalize_exe(raw, want)
```

- [ ] **Step 2: 落ちることを確かめる → 実装 → pytest 全部が PASS**（`test_i18n_lang.py`・`test_package.py`・`test_export_native.py` が新しいキーとモジュールで落ちないこと．`APPS_` はネイティブ向けの書き出しの対象にならないので `native/shared/messages.*.json` は変わらない: `python3 pc/tools/export_native.py && git status --short native/shared` が空）

- [ ] **Step 3: コミット** `feat(pc): shl app で計測するアプリを登録する (#6)`

---

### Task 6: Python — レポートの集計

**Files:**
- Modify: `pc/shiftlock/report.py`（`build_report`）
- Test: `pc/tests/test_report_apps.py`（新規）

**Interfaces:**
- Consumes: `log_read(store, "app", start, end)`，`docs/test-vectors/app-log.jsonl`（Task 2）．
- Produces: `build_report` の各行に `"dev": [24 個の分]`，`"adobe": [24 個の分]`，`"dev_total"`，`"adobe_total"`．既存の `"game"`・`"game_total"` は，計測のあるマスだけ計測の値に置き換わる．ほかのキーは変えない．

**決まり**:
- 読む範囲は `log_read(store, "app", t0 - timedelta(days=1), t1)`．
- 行ごとに: `span` が数（`bool` を除く `int`／`float`）で 0 以上でなければ行を飛ばす．`mid = _t − span/2 秒`．`mid` の日・時のマスを「計測あり」にする．
- `mid` が外出区間（`away`）の中なら，マスは「計測あり」にするが秒は足さない．
- `apps` が辞書なら，各値が辞書で `cat` が `game`／`dev`／`adobe`，`act` が数（`bool` を除く）で 0 以上のものだけ，そのカテゴリの秒に足す．ほかは飛ばす．
- 分 = `min(60, int(秒 + 30) // 60)`（四捨五入）．
- 「計測あり」のマス: `game`・`dev`・`adobe` とも計測の値．「計測なし」のマス: `game` は今までどおり `log/pc-*.jsonl` から，`dev`・`adobe` は 0．
- `pc`・`cov`・`claude`・`flags` の計算は変えない．

- [ ] **Step 1: テストを書く（落ちる）**

道具: 一時フォルダに `shifts.json`（`{"lang": "ja", "work_weekdays": []}`）を置き，`log/app-2026-12.jsonl` などを書いて `build_report(cfg, Store(p), datetime(2026, 12, 8, 12, 0), days=2)` を呼ぶ．`rows[0]` が 12/07．

```python
VECTOR = Path(__file__).resolve().parents[2] / "docs" / "test-vectors" / "app-log.jsonl"

def test_shared_vector_totals(tmp_path):
    r = _report(tmp_path, app=VECTOR.read_text(encoding="utf-8"))["rows"][0]
    assert (r["dev"][21], r["game"][21], r["adobe"][21]) == (5, 5, 4)
    assert (r["dev_total"], r["game_total"], r["adobe_total"]) == (5, 5, 4)
    assert sum(r["dev"]) == 5 and sum(r["adobe"]) == 4      # ほかの時は 0

def test_seconds_are_summed_not_lines(tmp_path):
    # 21:05 と 21:10 の 2 行，code.exe の act が 100 と 110 → 210 秒 → 4 分（3.5 の四捨五入）

def test_bucket_is_mid_of_span(tmp_path):
    # {"t": "2026-12-07T22:00:00", "span": 300, …act 300} → 21 時のマスに 5 分．22 時は 0

def test_hour_is_capped_at_60(tmp_path)                     # act の合計が 4000 秒 → 60

def test_cells_without_meter_keep_pc_game(tmp_path):
    # pc ログ: 20:05〜20:30 に {"idle": 10, "game": 1} を 6 行，21:05〜21:20 にも 4 行．app ログ: 共有の見本
    # → game[20] == 30（pc から），game[21] == 5（計測から．pc の 20 分は足さない）

def test_no_app_log_is_unchanged(tmp_path):
    # app ログ無し → game は pc から，dev・adobe は全部 0，dev_total == adobe_total == 0

def test_away_lines_are_not_counted(tmp_path):
    # work_weekdays に月曜を入れ，12/07（月）を「行ってきます」済み（state.json の worked ＋ events.log の shuppatsu 行．
    # 既存の test_shiftlock.py の away のテストと同じ作り方）にして，外出中の時刻の行 → dev は 0

def test_broken_lines_are_skipped(tmp_path):
    # 行: span 無し／"span": "x"／"apps": []／{"code.exe": {"cat": "dev", "act": "9"}}／{"x.exe": {"cat": "video", "act": 300}}／
    #     JSON でない行／正しい 1 行（dev 300 秒） → 例外なし，dev[21] == 5，game・adobe は 0
```

- [ ] **Step 2: 落ちることを確かめる → `build_report` に足す → pytest 全部が PASS**

- [ ] **Step 3: コミット** `feat(pc): レポートの集計にカテゴリ別の時間を足す (#6)`

---

### Task 7: Python — レポートの表示

**Files:**
- Modify: `pc/shiftlock/report_html.py`，`pc/shiftlock/i18n/ja.py`，`pc/shiftlock/i18n/ja_kansai.py`
- Test: `pc/tests/test_report_apps.py`

**Interfaces:**
- Consumes: 行の `dev`・`adobe`・`dev_total`・`adobe_total`（Task 6）．行にこれらが無い古い形のデータでも落ちないこと（`r.get("dev") or [0] * 24`）．

**決まり**:
- マスの帯: いまの `<i class="g">`（ゲーム 15 分以上）を，入れ物 `<i class="bd">` の中に並べる形に変える．中身は 15 分以上のカテゴリだけを**ゲーム → 開発 → Adobe の順**で `<i class="g">`・`<i class="dv">`・`<i class="ad">`．1 つも無ければ入れ物ごと出さない．`i.bd` はいまの `i.g` の位置と高さ（下 2px・高さ 4px）を引き継ぎ，`display:flex; gap:1px`，中の帯は `flex:1`（等分）．
- 色（CSS 変数を明るい・暗いの両方に足す）: `--dev: #2f7d5b`／暗い `#4fb389`，`--adobe: #8a4fd0`／暗い `#b08af0`．`--game` は変えない．
- マスのヒント: `REPORT_TIP_GAME` の後に，0 でなければ `REPORT_TIP_DEV`・`REPORT_TIP_ADOBE` を足す．
- 日ごとの列: 「ゲーム(h)」の列の右に `REPORT_COL_DEV`・`REPORT_COL_ADOBE`（値は `_h1(dev_total)`・`_h1(adobe_total)`）．見出しと行の列の数を合わせる．
- 凡例: ゲームの項目の後に 2 つ足す（入れ物つきの帯で）．
- アプリ名はどこにも出さない（HTML に `apps` の中身を渡さない）．

文面（`ja.py`・`ja_kansai.py` とも同じ）: `REPORT_TIP_DEV = " / 開発 {dev}分"`，`REPORT_TIP_ADOBE = " / Adobe {adobe}分"`，`REPORT_LEGEND_DEV = "開発15分以上"`，`REPORT_LEGEND_ADOBE = "Adobe15分以上"`，`REPORT_COL_DEV = "開発(h)"`，`REPORT_COL_ADOBE = "Adobe(h)"`．

- [ ] **Step 1: テストを書く（落ちる）**

```python
def _row(html, label):          # その日の <tr>…</tr> を取り出す小さな道具

def test_bands_for_categories_over_15_min(tmp_path):
    # 21 時に game 20 分・dev 16 分・adobe 14 分になる app ログ → 12/07 の行に
    # '<i class="bd"><i class="g"></i><i class="dv"></i></i>' があり，'class="ad"' は無い

def test_no_band_container_when_nothing_over_15(tmp_path)   # 行に 'class="bd"' が無い

def test_tip_has_category_minutes(tmp_path)                 # " / 開発 16分" が data-tip にある．0 分のカテゴリは出ない

def test_day_columns(tmp_path):
    # 見出しに "開発(h)" と "Adobe(h)" がこの順で "ゲーム(h)" の後にあり，
    # 12/07 の行の <td> の数 == 見出しの <th> の数 − 1（先頭の日付の <th> のぶん）

def test_legend_has_two_more(tmp_path)                      # "開発15分以上"・"Adobe15分以上"

def test_app_names_never_appear(tmp_path)                   # 共有の見本で作った HTML に "code.exe"・"eldenring"・"photoshop" が無い

def test_old_rows_without_dev_render(tmp_path):
    # build_report の結果の各行から dev・adobe・dev_total・adobe_total を del して render_report → 例外なし

def test_css_has_both_themes():                             # "--dev:" と "--adobe:" がそれぞれ 2 回ずつ（明るい・暗い）
```

- [ ] **Step 2: 落ちることを確かめる → 実装 → pytest 全部が PASS**

- [ ] **Step 3: 目で見る**（合成データ）: `python3 pc/tools/make_sample_report.py` が今までどおり動くこと（この道具の出力に帯を足すのは任意．足すなら合成の app 行を数本）．

- [ ] **Step 4: コミット** `feat(pc): レポートにカテゴリの帯と開発・Adobe の列を足す (#6)`

---

### Task 8: 文書と，合成データでの通し確認

**Files:**
- Modify: `docs/pc-spec.md`，`native/README.md`，`docs/native-app-plan.md`，`README.md`，`docs/native-agent-worklog.md`，`docs/superpowers/specs/2026-11-22-native-app-meter-design.md`
- 変えない: `CLAUDE.md`

- [ ] **Step 1: 文書を直す**

- `docs/pc-spec.md`: 「アプリの稼働計測」の節を足す（何を測るか・アクティブの決まり・行の形は `data-format.md` を指す・`shl app`）．レポートの節に「計測のあるマスは 3 カテゴリとも計測から，無いマスのゲームは見張りのログから」「帯と列」を足す．`shl` の一覧に `shl app`．
- `native/README.md`: `--meter-once` の使い方（合成データのフォルダで）と，計測が見張りと別の仕事であること．
- `docs/native-app-plan.md`: 区切りの表の区切り 3 を「済み」に．
- `README.md`: 機能の一覧に 1 行．
- 設計書: §4.1 の「`APP_` で始まる名前」を「`APPS_` で始まる名前（`APP_` はネイティブの画面用に書き出されるため）」に直す．
- `docs/native-agent-worklog.md`: 区切り 3 の節を足す．**この計画で決めたこと**（区切りをまたいだサンプルの増分は前の区間に入れる／`t ≤ 最後の t` は持ち越す／外出中の行もマスを「計測あり」にする／分は四捨五入／書けなかった行はやり直さない／`--meter-once` は本番のフォルダでも断らない），レビューで送ったもの，**まだ確かめていないこと**（本番の PC で 10 分動かした結果・`NtQuerySystemInformation` が代替に落ちていないか）．

- [ ] **Step 2: 合成データで Agent を 10 分動かす**

Task 4 Step 3 と同じ環境変数で（`SHIFTLOCK_DIR` は合成データ，`SHIFTLOCK_GUARD_TASK=shiftlock-guard-test`），`pc\shifts.example.json` を `shifts.json` として置き，`dotnet run --project native\shiftlock.Agent` を 11 分動かしてトレイの「終了」で終わらせる（`--install` はしない）．

Expected: `log\app-2026-11.jsonl`（その日の月）に，5 分の区切りの行が 2 本と，終了時の行が 1 本．`agent.log` に `meter failed`・`fell back` が無い．行に登録していない exe 名が無い．続けて WSL で `python3 pc/shiftlock.py report --days 2 --config <そのフォルダ>/shifts.json` が `FILE\t…` を出す．
報告には行の本数と `span` だけを書く（中身は貼らない）．

- [ ] **Step 3: 全部のテスト** — `dotnet test native` と pytest が全部 PASS．`python3 pc/tools/export_native.py && python3 pc/tools/make_test_vectors.py && git status --short docs/test-vectors native/shared` が空．

- [ ] **Step 4: コミット** `docs(native): 区切り 3 の文書と作業記録 (#6)`

---

## 本人がやること（この計画の外）

- 本番への反映: `native\deploy.ps1`（Agent の置き直し）と `pc\deploy.ps1`（Python 版）．Claude Code からは実行しない．
- 本番で `shl app add …` で登録し，10 分後に `log\app-*.jsonl` に行が出ること，`shl report` で帯が出ることを見る．
- PR #5 を dev に入れた後，このブランチを dev 向けの PR にする（push は確認してから）．
