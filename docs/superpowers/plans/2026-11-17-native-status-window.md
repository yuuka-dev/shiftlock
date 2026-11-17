# ネイティブ版 区切り 1：状態と記録の画面 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 今の状態が見えて「寝る」「起きた」「行ってきます」「帰ってきた」をボタンで記録できる WinUI 3 のウィンドウを，Python 版と同じファイルを読み書きする形で作る（issue #38）．

**Architecture:** 判断は全部 `shiftlock.Core`（C#，UI も Windows API も呼ばない）に置き，`shiftlock.App`（WinUI 3）は表示とボタンだけにする．Python 版に合成データを通した結果を `docs/test-vectors/*.json` に書き出し，C# のテストがそれを読んで一致を確かめる．文面も Python の言語ファイルから JSON に書き出して C# が読む．

**Tech Stack:** C# / .NET 10，xUnit 2.9，System.Text.Json，WinUI 3（Windows App SDK，非パッケージ），Python 3.8+（書き出しと pytest）

**Spec:** [docs/superpowers/specs/2026-11-17-native-status-window-design.md](../specs/2026-11-17-native-status-window-design.md)（以下「設計書」）．実装者は両方読む．移植元は `pc/shiftlock/` の `config.py`・`timeutil.py`・`plan.py`・`store.py`・`records.py`・`usage.py`・`days.py`・`commands.py`（`status_text`）．

## Global Constraints

- ブランチは `feature/native-status-window`（dev から）．コミットは `<型>(スコープ): タイトル (#38)`，日本語．`Co-Authored-By` 行や生成元の記載は付けない．
- `shiftlock.Core` は NuGet パッケージを足さない（.NET の標準ライブラリだけ）．Windows API・UI を呼ばない．現在時刻を自分で取らず，`now` を引数で受ける．
- 時刻は全部ローカル時刻．タイムゾーンを付けない．ファイルに書く時刻は分まで（`yyyy-MM-ddTHH:mm`）．
- 書くファイルは `state.json` と `events.log` だけ．`state.json` は知らないキーを残す．書き方は `<名前>.tmp` に書いてから置き換え，UTF-8（BOM なし）・LF・インデント 1・日本語はエスケープしない．`events.log` はタブ区切り・LF の追記．
- 文面は C# に直書きしない．`pc/shiftlock/i18n/ja.py` と `ja_kansai.py` の両方に同じ名前で足し，書き出した JSON で読む．`inm.py` は触らない．
- 強制モードの宣言・解除・設定を画面に置かない．強制を促す表示を出さない．
- 個人データ・実データの値をテスト・テストベクタ・文書に入れない．合成データの日付は 2026-12-01 以降を使う．
- 本番の実行フォルダ（`%USERPROFILE%\shift-lock`）に向けてアプリやテストを実行しない．確かめるときはコピーに `SHIFTLOCK_DIR` を向ける．
- `*.py` は UTF-8・LF・標準ライブラリのみ．`*.cs`・`*.xaml`・`*.json` は UTF-8・LF．コメントは日本語．
- `Directory.Build.props` の `TreatWarningsAsErrors` を保つ（WinUI の生成コードの警告で通らないときだけ，その警告番号を `shiftlock.App` で `NoWarn` に入れ，理由をコメントに書く）．
- テストの実行:
  - Python: WSL で `source .venv/bin/activate && cd pc && python3 -m pytest -q tests`（全部通してからコミット）
  - C#: Windows 側で `dotnet test native/shiftlock.Core.Tests`
  - アプリのビルド: Windows 側で `dotnet build native/shiftlock.App -r win-x64`

## Review Focus

設計書が黙っているが，使う人が踏みそうなもの．それぞれ下のタスクにテストを入れてある．

1. **`state.json` が在るのに読めない**（壊れている・他のプロセスが掴んでいる）: 空として扱って書き戻すと記録が消える．記録を断り，「もう一度押して」と出す．何も書かない．（Task 6）
2. **置き換えに失敗する**（別のプロセスが開いている）: アプリは落ちず，記録できなかったと出す．短い間隔で 3 回までやり直す．`.tmp` を残さない．（Task 6）
3. **設定の値の型がゆるい**（`"prep_minutes": "90"`，`"start_target": "8:00"`，`5.0`）: Python 版は受け付けるので同じ結果を出す．（Task 4・5）
4. **`no_sleep` の項目が壊れている**（`from`／`to` が無い）: アプリは落ちず，設定のエラーとして出す．（Task 4）
5. **ボタンの二度押し**: 処理が終わるまで 4 つとも押せなくし，同じ記録を 2 行書かない．（Task 9）

---

## ファイルの地図

```text
pc/tools/export_native.py                 文面とテストベクタの書き出し（新規）
pc/tests/test_export_native.py            書き出しがコミット済みの生成物と同じか（新規）
pc/shiftlock/records.py                   do_kitaku / do_neru に via 引数（既定 "claude"）
pc/shiftlock/i18n/ja.py, ja_kansai.py     APP_* の文面を足す
native/shared/messages.ja.json, messages.ja_kansai.json   生成物
docs/test-vectors/{plan,phase,records,snapshot}.json      生成物
native/shiftlock.Core/
  Messages.cs      文面の読み込みと {name} の置き換え
  TimeUtil.cs      時刻の整形
  JsonFiles.cs     JSON の読み書き（Python 版と同じ書き方）
  ShiftConfig.cs   設定の型と読み込み・検証・例外日のマージ
  Planner.cs       乗務日・逆算・フェーズ・対象の乗務
  Store.cs         state.json / events.log / usage-pc.json / STOP.txt
  Records.cs       4 つの記録と取り消し
  Snapshot.cs      画面に出す値をまとめる入口
native/shiftlock.Core.Tests/
  Vectors.cs       テストベクタの読み込みと一時フォルダへの展開
  MessagesTests.cs, JsonFilesTests.cs, ConfigTests.cs, PlannerTests.cs, RecordsTests.cs, SnapshotTests.cs
native/shiftlock.App/                     WinUI 3（新規）
  shiftlock.App.csproj, app.manifest, Program.cs, App.xaml(.cs), MainWindow.xaml(.cs), Refresher.cs
```

合成データの土台（テストベクタで共通に使う設定）:

```json
{"lang": "ja", "work_weekdays": ["mon", "tue", "wed", "thu", "fri"],
 "start_target": "17:00", "start_latest": "17:30", "prep_minutes": 60,
 "sleep_target_hours": 8, "sleep_min_hours": 6, "lastcall_minutes": 40}
```

この設定で 2026-12-01（火）の乗務は: 就寝準備 07:20，就寝目安 08:00，最低ライン 10:30，出発 16:00／16:30，出庫 17:00／17:30．

---

### Task 1: 骨組みを dev に載せる

**Files:**
- Create: `native/Directory.Build.props`，`native/README.md`，`native/shiftlock.slnx`，`native/shiftlock.Core/shiftlock.Core.csproj`，`native/shiftlock.Core.Tests/shiftlock.Core.Tests.csproj`，`native/shiftlock.Core.Tests/SkeletonTests.cs`，`native/shl/shl.csproj`，`native/shl/Program.cs`
- Modify: `.gitignore`（末尾に .NET の節）

- [x] **Step 1:** `git switch -c feature/native-status-window dev`．手元の未追跡の `native/`（ビルドの出力だけ）を消す．
- [x] **Step 2:** `git checkout chore/native-skeleton -- native` で上の 8 ファイルを取り出す．`docs/proposal-discord-kill.md` は取り出さない．
- [x] **Step 3:** `.gitignore` に次を足す（`chore/native-skeleton` の `.gitignore` 全体は古いので取り出さず，この節だけ書く）．

```gitignore
# ---- .NET（native/） ----
native/**/bin/
native/**/obj/
native/.vs/
native/**/*.user
```

- [x] **Step 4:** `dotnet test native/shiftlock.Core.Tests` → `Passed: 1`．`git status` に `bin/`・`obj/` が出ないこと．
- [x] **Step 5:** コミット `chore(native): .NET 版の骨組みを載せる (#38)`

---

### Task 2: 文面の書き出しと画面用の文

**Files:**
- Create: `pc/tools/export_native.py`，`pc/tests/test_export_native.py`，`native/shared/messages.ja.json`，`native/shared/messages.ja_kansai.json`
- Modify: `pc/shiftlock/i18n/ja.py`，`pc/shiftlock/i18n/ja_kansai.py`

**Interfaces:**
- Produces（Python）: `export_native.build_messages(lang: str) -> dict`，`export_native.outputs() -> dict[Path, str]`（書き出す全ファイルの パス → 中身），`export_native.render(obj) -> str`（`json.dumps(obj, ensure_ascii=False, indent=1) + "\n"`）．実行は `python3 pc/tools/export_native.py`（書き出す），`--check`（コミット済みと違えば終了コード 1）．
- Produces（JSON）: `messages.<lang>.json` は `{キー: 文字列 または {キー: 文字列}}`．プレースホルダは `{name}`．

- [x] **Step 1: 画面用の文を `ja.py` に足す．** `ja_kansai.py` にも同じ名前・同じプレースホルダで，既存の関西弁に合わせて足す．

```python
# ---- ネイティブ版（native/）の画面 ----
APP_PHASE = {"free": "いまは自由時間", "lastcall": "就寝準備の時間", "soft": "就寝目安を過ぎているよ",
             "hard": "最低ラインを過ぎているよ", "go": "そろそろ出発", "go_last": "出発の最終が近いよ"}
APP_COUNTDOWN = {"lastcall": "就寝目安まで あと {remain}", "soft": "最低ラインまで あと {remain}",
                 "hard": "出発の最終まで あと {remain}", "go": "出発目安まで あと {remain}",
                 "go_late": "出発の最終まで あと {remain}", "go_last": "出庫の最終まで あと {remain}"}
APP_NEXT_SHIFT = "次の乗務  {day}"
APP_NO_SHIFT = "14 日以内に乗務はないよ"
APP_TIMES_SLEEP = "就寝準備 {lastcall} → 就寝目安 {soft} → 最低ライン {hard}"
APP_TIMES_DEPART = "出発 {target}（遅くとも {latest}）"
APP_TIMES_START = "出庫 {target}（遅くとも {latest}）"
APP_RECORD = "記録: {text}"
APP_RECORD_NONE = "まだなし"
APP_RECORD_STATUS = {"okita": "起きた", "worked": "出発済み", "yasumi": "休み"}
APP_RECORD_SLEEP_AT = "{when} 就寝"
APP_RECORD_KITAKU = "{when} 帰宅"
APP_RECORD_SEP = " / "
APP_BTN_NERU = "寝る"
APP_BTN_OKITA = "起きた"
APP_BTN_SHUPPATSU = "行ってきます"
APP_BTN_KITAKU = "帰ってきた"
APP_BTN_UNDO = "取り消す"
APP_SCHEDULE = "予定"
APP_DAY_WORK = "乗務"
APP_DAY_OFF = "休み"
APP_NO_CONFIG = "まだ設定がないよ（{path} が見つからない）。"
APP_BAD_CONFIG = "設定が読めないよ: {err}"
APP_STATE_BUSY = "いま記録のファイルを読み書きできなかったよ。もう一度押してね。"
```

- [x] **Step 2: 失敗するテストを書く**（`pc/tests/test_export_native.py`）．

```python
def test_messages_have_app_keys_in_both_langs():
    ja, kansai = ex.build_messages("ja"), ex.build_messages("ja_kansai")
    assert set(ja) == set(kansai)
    assert ja["APP_BTN_KITAKU"] == "帰ってきた"
    assert set(ja["APP_PHASE"]) == {"free", "lastcall", "soft", "hard", "go", "go_last"}
    assert ja["RECORDS_KITAKU"].startswith("[shiftlock] ")

def test_placeholders_match_between_langs():
    # 言語が違ってもプレースホルダの名前の集合は同じ（辞書の値も 1 つずつ比べる）

def test_format_spec_is_rejected():
    # 対象のキーに "{x:02d}" や "{x!r}" があれば build_messages が ValueError

def test_outputs_match_committed():
    for path, text in ex.outputs().items():
        assert path.read_text(encoding="utf-8") == text, f"{path} が古い。python3 pc/tools/export_native.py を実行する"

def test_lang_is_restored():
    # outputs() を呼んだあと i18n.LANG が呼ぶ前と同じ（ほかのテストの言語を変えない）
```

- [x] **Step 3:** pytest → `export_native` が無くて失敗することを確かめる．
- [x] **Step 4: `build_messages`・`render`・`outputs`・`main` を実装する．** 書き出すキーは次の接頭辞か名前に合うもの: `PREFIX`，`TIME_DUR_`，`INTENT_LABEL`（`INTENT_LABEL` と `INTENT_LABEL_SEP` だけ），`RECORDS_KITAKU`，`RECORDS_NERU`，`RECORDS_OKITA`，`RECORDS_SHUPPATSU`，`RECORDS_UNDO_`，`CONFIG_`，`DAYS_NOTE`，`DAYS_EXCEPTION_MARK`，`APP_`．`i18n.set_lang(lang)` で選んでから `vars(i18n)` を読み，終わったら元の言語に戻す．キーは名前順に並べる．`CONFIG_NOT_DICT` の `{{...}}` は Python の書式のエスケープなので，書き出すときに `{...}` へ直さず**そのまま**残す（C# 側が `{{`→`{`，`}}`→`}` を解釈する）．
- [x] **Step 5:** `python3 pc/tools/export_native.py` で 2 つの JSON を書き出し，pytest が全部通ることを確かめる（既存の 156 件を含む）．
- [x] **Step 6:** コミット `feat(native): 文面を JSON に書き出す仕組みと画面用の文を追加 (#38)`

---

### Task 3: Core の土台（文面・時刻・JSON）

**Files:**
- Create: `native/shiftlock.Core/Messages.cs`，`TimeUtil.cs`，`JsonFiles.cs`，`native/shiftlock.Core.Tests/MessagesTests.cs`，`JsonFilesTests.cs`
- Modify: `native/shiftlock.Core/shiftlock.Core.csproj`
- Delete: `native/shiftlock.Core.Tests/SkeletonTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed class Messages {
    public static string ResolveLang(string? lang);            // "ja" / "ja_kansai"．それ以外・null は "ja"
    public static Messages Load(string? lang);                 // 埋め込みの JSON を読む
    public string Lang { get; }
    public string this[string key] { get; }                    // 無いキーは KeyNotFoundException
    public IReadOnlyDictionary<string, string> Map(string key);
    public string Format(string key, params (string Name, object? Value)[] args);
    public static string Fill(string template, params (string Name, object? Value)[] args);
}
public static class TimeUtil {
    public static DateTime At(DateOnly d, string hm);          // "8:00" も "08:00" も受ける
    public static string Fmt(DateTime t);                      // "HH:mm"（0 埋め．Python の %H:%M）
    public static string FmtD(DateTime t);                     // "MM/dd HH:mm"
    public static string FmtDur(TimeSpan d, Messages m);       // 60 分以上は TIME_DUR_HM，未満は TIME_DUR_M．分は切り捨て
    public static string Iso(DateTime t);                      // "yyyy-MM-ddTHH:mm"
    public const string WdJa = "月火水木金土日";               // 月曜が 0
}
public static class JsonFiles {
    public static JsonObject ReadObjectOrEmpty(string path);   // 無い・壊れている・オブジェクトでない → 空（Python の _read）
    public static bool TryReadObject(string path, out JsonObject obj);   // 無い → true で空．在るのに読めない → false
    public static void Write(string path, JsonNode node);      // .tmp → 置き換え
}
```

- [x] **Step 1:** csproj に埋め込みを足す．`messages.ja.json` の `ja` が文化名と見なされて別アセンブリに回るのを避けるため，`WithCulture="false"` と `LogicalName` を必ず付ける．

```xml
<ItemGroup>
  <EmbeddedResource Include="..\shared\messages.ja.json" WithCulture="false" LogicalName="messages.ja.json" />
  <EmbeddedResource Include="..\shared\messages.ja_kansai.json" WithCulture="false" LogicalName="messages.ja_kansai.json" />
</ItemGroup>
```

- [x] **Step 2: 失敗するテストを書く．**

```csharp
// MessagesTests
[Theory] [InlineData("ja", "ja")] [InlineData("ja_kansai", "ja_kansai")] [InlineData("inm", "ja")] [InlineData(null, "ja")] [InlineData("xx", "ja")]
public void ResolveLang(string? input, string expected)
[Fact] public void Load_ReadsBothLanguages()        // Load("ja")["APP_BTN_KITAKU"] == "帰ってきた"，Load("ja_kansai") も読める
[Fact] public void Format_FillsNamedPlaceholders()  // Fill("{a}と{b}", ("a",1),("b","x")) == "1とx"
[Fact] public void Fill_UnescapesDoubledBraces()    // Fill("{key} は {{...}}", ("key","usage")) == "usage は {...}"
[Fact] public void Fill_MissingArgumentThrows()     // 引数に無い名前があれば ArgumentException
[Fact] public void FmtDur()                         // 30分→"30分"，150分→"2時間30分"，59分59秒→"59分"
[Fact] public void Fmt_IsZeroPadded()               // 7:05 → "07:05"

// JsonFilesTests（一時フォルダ）
[Fact] public void Write_MatchesPythonFormat()
// {"shifts":{"2026-12-01":{"status":"okita","at":"2026-12-01T14:30"}},"メモ":"あ","n":5.0,"e":{},"l":[]} を書くと，バイト列が
// "{\n \"shifts\": {\n  \"2026-12-01\": {\n   \"status\": \"okita\",\n   \"at\": \"2026-12-01T14:30\"\n  }\n },\n \"メモ\": \"あ\",\n \"n\": 5.0,\n \"e\": {},\n \"l\": []\n}"
// （末尾に改行なし．BOM なし．Python の json.dump(indent=1, ensure_ascii=False) と同じ）
[Fact] public void Write_KeepsUnknownKeysAndOrder()   // 読んで 1 か所変えて書くと，ほかのキーと順が変わらない
[Fact] public void Write_LeavesNoTmpFile()
[Fact] public void ReadObjectOrEmpty_MissingOrBroken_ReturnsEmpty()
[Fact] public void TryReadObject_BrokenFile_ReturnsFalse()   // 中身が "{" のファイル → false．無いファイル → true で空
```

- [x] **Step 3:** `dotnet test native/shiftlock.Core.Tests` → コンパイルエラーで失敗．
- [x] **Step 4:** 実装する．`JsonFiles.Write` は `Utf8JsonWriter`（`Indented = true`，`IndentSize = 1`，`IndentCharacter = ' '`，`NewLine = "\n"`，`Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping`）．数値は読んだときの表記のまま書く（`5.0` を `5` にしない）．置き換えは `File.Move(tmp, path, overwrite: true)`．
- [x] **Step 5:** `dotnet test` → 全部通る．
- [x] **Step 6:** コミット `feat(native): Core の土台（文面・時刻・JSON の読み書き）(#38)`

---

### Task 4: 設定と乗務日

**Files:**
- Create: `native/shiftlock.Core/ShiftConfig.cs`，`Planner.cs`（`IsWorkday` まで），`native/shiftlock.Core.Tests/Vectors.cs`，`ConfigTests.cs`，`docs/test-vectors/plan.json`（この時点では `workday` と `config_error` の節だけ）
- Modify: `pc/tools/export_native.py`，`pc/tests/test_export_native.py`，`native/shiftlock.Core.Tests/shiftlock.Core.Tests.csproj`

**Interfaces:**
- Consumes: `JsonFiles`，`Messages`，`TimeUtil`（Task 3）
- Produces:

```csharp
public sealed class ConfigException(string message) : Exception(message);   // 検証に落ちた（Python の ValueError など）
public sealed record NoSleepRule(string? Date, string? Weekday, string From, string To);
public sealed record DayTimes(string? StartTarget, string? StartLatest, double? SleepTargetHours, double? SleepMinHours);
public sealed class ShiftConfig {
    public static ShiftConfig Load(string shiftsJsonPath);   // 無ければ FileNotFoundException，読めない・検証に落ちれば ConfigException
    public string Dir { get; }
    public string Lang { get; }                              // Messages.ResolveLang 済み
    public IReadOnlyList<string> WorkWeekdays { get; }
    public string StartTarget { get; }  public string StartLatest { get; }
    public double PrepMinutes { get; }  public double LastcallMinutes { get; }
    public double SleepTargetHours { get; }  public double SleepMinHours { get; }
    public string SleepMinHoursText { get; }                 // 文面に入れる表記（Python の str()．5 → "5"，5.0 → "5.0"）
    public string AutoWakeHoursText { get; }                 // auto_wake_hours が null・0 なら SleepMinHoursText
    public IReadOnlyList<NoSleepRule> NoSleep { get; }
    public IReadOnlyDictionary<string, string> DaysOff { get; }    // 日 → 理由（手動がカレンダーより優先）
    public IReadOnlyDictionary<string, string> DaysWork { get; }
    public DayTimes TimesFor(string day);                    // days.json の times と旧形式 shifts の合成（plan.day_times）
    public bool IsLegacyShiftDay(string day);
}
public static partial class Planner {
    public static readonly string[] Weekdays = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];
    public static bool IsWorkday(ShiftConfig cfg, DateOnly d);
}
```

- テストベクタの入れ物（`Vectors.cs`）:

```csharp
public sealed record VectorCase(string Name, JsonObject Files, JsonObject Input, JsonObject Expect);
public static class Vectors {
    public static IEnumerable<object[]> Cases(string file, string section);   // xUnit の MemberData 用（Name を表示に使う）
    public static string Materialize(JsonObject files);   // 一時フォルダを作り，"shifts.json" などを書いてフォルダのパスを返す．値が true のキーは空ファイル（STOP.txt）
}
```

- ベクタの形（全ファイル共通）: `{"<節>": [{"name": "...", "files": {"shifts.json": {...}, "days.json": {...}, ...}, "input": {...}, "expect": {...}}]}`

- [x] **Step 1:** csproj でベクタを出力へ写す．

```xml
<ItemGroup>
  <Content Include="..\..\docs\test-vectors\*.json" Link="vectors\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [x] **Step 2: Python 側でベクタを書き出す．** `export_native.py` に `build_vectors() -> dict[str, dict]`（ファイル名 → 中身）を足し，`outputs()` に含める．各ケースは一時フォルダに `files` を書いて Python 版の関数を呼び，結果を `expect` に入れる．`plan.json` の節:
  - `workday`（`input: {"day"}` → `expect: {"work": bool}`．`config.load_config` → `plan.is_workday`）: 平日／土曜／`days.json` の off（理由つき）／`days.json` の work（土曜）／`calendar.json` 旧形式の off／新形式 `sources` 2 本で同じ日が off と work（上のソースが勝つ）／手動 work がカレンダー off に勝つ／旧形式 `shifts` に文字列の日付／`shifts` に `{"date": ...}`
  - `config_error`（`expect: {"error": "<ja の文>"}` か `{"ok": true}`）: `work_weekdays` に `"holiday"`／`usage` が文字列／`calendar.sources` が辞書／`start_target` が `"25:99"`／`notify.sound` がリスト（→ ok）／`"prep_minutes": "90"` と `"start_target": "8:00"`（→ ok）．`no_sleep` の項目に `from`／`to` が無い設定はベクタに入れない（Python 版は読み込みでは通し，逆算で落ちる．C# は読み込みで設定のエラーにする．Step 4 の単独のテストで扱う）
- [x] **Step 3:** `test_export_native.py` に足す．

```python
def test_vectors_use_only_synthetic_dates():
    # build_vectors() の全文に "2026-12-" より前の日付（正規表現 20\d\d-\d\d-\d\d）が無い
```

- [x] **Step 4: 失敗するテストを書く**（`ConfigTests.cs`）．

```csharp
[Theory] [MemberData(nameof(WorkdayCases))] public void IsWorkday_MatchesPython(VectorCase c)
[Theory] [MemberData(nameof(ConfigErrorCases))] public void Load_ValidatesLikePython(VectorCase c)   // error なら ConfigException.Message が一致
[Fact] public void Load_MissingFile_ThrowsFileNotFound()
[Fact] public void Load_BrokenJson_ThrowsConfigException()
[Fact] public void Load_NoSleepRuleWithoutFromTo_ThrowsConfigException()   // Review Focus 4: 落ちずに設定のエラーにする
[Fact] public void Load_LangMissing_IsJa()          // lang が無い → "ja"（Python 版の既定とは違う．設計書 4.6）
[Fact] public void SleepMinHoursText_KeepsJsonNotation()   // 5 → "5"，5.0 → "5.0"，4.5 → "4.5"，既定 → "4"
```

- [x] **Step 5:** `dotnet test` → 失敗．
- [x] **Step 6:** 実装する．既定値は `config.DEFAULTS` のうち上の型に出てくるキーだけ持つ（`work_weekdays` は月〜土，`start_target` 20:00，`start_latest` 22:30，`prep_minutes` 90，`sleep_target_hours` 5，`sleep_min_hours` 4，`lastcall_minutes` 40）．検証は `config.load_config` と同じ順・同じ文（`CONFIG_*`）．数値は JSON の数でも数字の文字列でも受ける（Python の `float()`）．
- [x] **Step 7:** `dotnet test` と pytest が全部通る．
- [x] **Step 8:** コミット `feat(native): 設定の読み込みと乗務日の判定を移す (#38)`

---

### Task 5: 時刻の逆算とフェーズ

**Files:**
- Modify: `native/shiftlock.Core/Planner.cs`，`pc/tools/export_native.py`，`docs/test-vectors/plan.json`
- Create: `native/shiftlock.Core.Tests/PlannerTests.cs`，`docs/test-vectors/phase.json`

**Interfaces:**
- Consumes: `ShiftConfig`，`Planner.IsWorkday`，`Vectors`（Task 4）
- Produces:

```csharp
public sealed record Plan(string Day, DateTime StartTarget, DateTime StartLatest, DateTime DepartTarget,
                          DateTime DepartLatest, DateTime SoftClose, DateTime HardClose, DateTime Lastcall);
public static partial class Planner {
    public static Plan MakePlan(ShiftConfig cfg, DateOnly d);
    public static IReadOnlyList<Plan> Plans(ShiftConfig cfg, DateOnly around, int back = 40, int fwd = 14);
    public static (string Phase, Plan? Plan) Current(ShiftConfig cfg, JsonObject shiftStatus, DateTime now);  // "free" "lastcall" "soft" "hard" "go" "go_last"
    public static Plan? TargetPlan(ShiftConfig cfg, DateTime now);
}
```

- [x] **Step 1: ベクタを書き出す．**
  - `plan.json` の `make_plan`（`input: {"day"}` → `expect`: 7 つの時刻を `yyyy-MM-ddTHH:mm` で）: 土台の設定で 2026-12-01／`start_latest` が `start_target` より早い（→ 同じ時刻に揃う）／`days.json` の `times` で `start_target` と `sleep_target_hours` を上書き／旧形式 `shifts` の辞書で上書き／`no_sleep` が睡眠の途中に 1 つ（曜日指定）／`no_sleep` が日をまたぐ（`from` 23:00 `to` 02:00）／`no_sleep` が 2 つ（日付指定と曜日指定）／`no_sleep` が起きる時刻をまたぐ／`"prep_minutes": "90"` と `"start_target": "8:00"`
  - `phase.json` の `current`（`files` に `state.json`，`input: {"now"}` → `expect: {"phase", "day"}`）: 土台の設定の 2026-12-01 で 07:19（free）／07:20（lastcall）／07:59／08:00（soft）／10:29／10:30（hard）／17:29（hard）／17:30（free）／`okita` で 12:00（free）・15:44（free）・15:45（go）・16:14（go）・16:15（go_last）／`yasumi` で 09:00（free）／`worked` で 16:20（free）／土曜 12:00（free）
  - `phase.json` の `target_plan`（`input: {"now"}` → `expect: {"day"}`，無ければ `null`）: 月曜 2026-12-07 17:29（→ 12-07）／12-07 17:30（→ 12-08）／12-08 17:29（→ 12-08）／金曜 12-11 17:30（→ null．次は月曜で 24 時間より先）／土曜 12-12 18:00（→ null）
- [x] **Step 2: 失敗するテストを書く**（`PlannerTests.cs`）: `MakePlan_MatchesPython`・`Current_MatchesPython`・`TargetPlan_MatchesPython`（どれも `[Theory]` でベクタの全ケース）．
- [x] **Step 3:** `dotnet test` → 失敗．
- [x] **Step 4:** `plan.py` の `no_sleep_intervals`・`latest_bedtime`・`make_plan`・`plans`・`current`・`target_plan` を移す．`latest_bedtime` の区間の並べ方と引き算は Python 版の順のまま移す（終わりが遅い区間から見る）．
- [x] **Step 5:** `dotnet test` と pytest が全部通る．
- [x] **Step 6:** コミット `feat(native): 時刻の逆算とフェーズの判定を移す (#38)`

---

### Task 6: 記録と取り消し

**Files:**
- Create: `native/shiftlock.Core/Store.cs`，`Records.cs`，`native/shiftlock.Core.Tests/RecordsTests.cs`，`docs/test-vectors/records.json`
- Modify: `pc/shiftlock/records.py`，`pc/tools/export_native.py`

**Interfaces:**
- Consumes: `ShiftConfig`，`Planner.TargetPlan`，`Plan`，`Messages`，`JsonFiles`，`TimeUtil`
- Produces:

```csharp
public sealed class StateBusyException : Exception;   // state.json が在るのに読めない／書けない
public sealed class Store(string dir) {
    public string Dir { get; }
    public string P(string name);
    public JsonObject ShiftStatus();                                  // state.json の shifts（無い・壊れている → 空）．表示用
    public JsonObject ReadStateForWrite();                            // 在るのに読めなければ StateBusyException
    public void WriteState(JsonObject state);                         // 3 回までやり直し，だめなら StateBusyException．.tmp を残さない
    public void SetShiftStatus(string day, string? status, DateTime now, string note = "");
    public void LogEvent(string day, string kind, DateTime now, string note = "");
    public void SetMark(string day, string key, DateTime? t, DateTime now, string note = "");
    public bool StopActive { get; }                                   // STOP.txt がある
    public bool? RecentLongGap(DateTime now, double minHours = 2.5, double withinHours = 4);   // usage-pc.json．記録なしは null
}
public sealed record UndoToken(string At, string What);               // state.json の undo.at と undo.what[0]
public sealed record RecordResult(string Message, bool Recorded, UndoToken? Undo);   // Message は PREFIX を外した文
public static class Records {
    public const string Via = "app";
    public static RecordResult Neru(ShiftConfig cfg, Store store, Messages m, DateTime now);
    public static RecordResult Okita(ShiftConfig cfg, Store store, Messages m, DateTime now);
    public static RecordResult Shuppatsu(ShiftConfig cfg, Store store, Messages m, DateTime now);
    public static RecordResult Kitaku(ShiftConfig cfg, Store store, Messages m, DateTime now);
    public static bool CanUndo(Store store, UndoToken token, DateTime now);
    public static RecordResult Undo(Store store, Messages m, DateTime now);
}
```

- `StateBusyException` のとき，4 つの記録と `Undo` は例外を投げずに `RecordResult(m["APP_STATE_BUSY"], false, null)` を返す．
- `Recorded` は「`events.log` に行を足したか」．断ったとき（早すぎる出発，対象なしの出発，強制中の起きた）は `false` で `state.json` に触らない．
- `undo` の `what` は `neru`／`okita`／`kitaku`／`worked`．`day` は対象の乗務日，対象が無ければ `-`．

- [ ] **Step 1: Python 版に出所の引数を足す．** `records.do_kitaku` と `do_neru` の末尾に `via: str = "claude"` を足し，`set_mark` の出所に渡す．ほかの呼び出しは変えない（既存の pytest がそのまま通ること）．
- [ ] **Step 2: ベクタを書き出す．** `export_native.py` に，アプリと同じ手順を Python 版の部品で組む関数を置く．

```python
def app_record(kind: str, cfg: dict, store, now) -> dict:
    """kind: neru / okita / shuppatsu / kitaku。Python 版の do_* を出所 "app" で呼ぶ。
    events.log に行が足されたら記録したとみなし、shuppatsu 以外は undo を apply_intents と同じ形で state.json に残す
    （prev は呼ぶ前の shifts[乗務日]。what は ["neru"] など。shuppatsu は do_shuppatsu 自身が ["worked"] で残す）。
    返り値 {"message": PREFIX を外した文, "recorded": bool}"""
```

  `records.json` の節 `steps`．`input: {"steps": [{"op": "neru|okita|shuppatsu|kitaku|undo", "now": "..."}]}`，`expect: {"results": [{"message", "recorded"}], "state": <終わりの state.json>, "events": [<足された行>]}`．`undo` は `records.undo_fuzzy` を呼ぶ．ケース（日付は土台の設定の 2026-12-01）:
  1. 寝る 08:10
  2. 寝る 08:10 → 起きた 14:30（6 時間 20 分．メモ `neru 08:10-14:30`）
  3. 寝る 10:00 → 起きた 14:00（最低ラインに届かない文が付く）
  4. 起きた 14:00，`usage-pc.json` なし（メモ空）
  5. 起きた 14:00，`gaps` に 09:00〜13:00（メモ空）
  6. 起きた 14:00，`gaps` が空（メモ `no-gap`，確かめる文が付く）
  7. 休みの乗務（`status: yasumi`）で 寝る 08:10 → 起きた 14:30（`status` は `yasumi` のまま．`events.log` に `okita` 行）
  8. 出発済みの乗務で 起きた 14:00（寝た記録なし）
  9. 強制中（`enforce.consented_at` あり，`gaps` が空，寝た記録なし）で 起きた 11:00 → 記録しない．**先に入れておいた `undo` がそのまま残る**
  10. 9 と同じで `STOP.txt` あり → 記録する
  11. 9 と同じで `enforce.released_at` あり → 記録する
  12. 行ってきます 13:59 → 断る
  13. 行ってきます 14:00 → 出発済み（メモ `shuppatsu`）
  14. 土曜 2026-12-05 18:00 に 行ってきます → 断る（`events.log` は増えない）
  15. 土曜 18:00 に 寝る・起きた・帰ってきた → それぞれ `free-day` の行
  16. 2026-12-02 07:30 に 帰ってきた（対象は 12-02 の乗務．メモ `app 2026-12-02T07:30`）
  17. 寝る 08:10 → 取り消す 08:20（`sleep_at` が消え，`undo` 行）
  18. 寝る 08:10 → 取り消す 09:11（61 分．取り消せない文．何も変わらない）
  19. 強制の宣言がある乗務で 行ってきます 14:00 → 取り消す 14:05（`enforce` はそのまま残る）
  20. 土曜の 寝る → 取り消す（`undo` 行だけ足され，`free-day` の行は残る）
  21. `state.json` に知らないキー（最上位に `pending` と `confirm_since`，乗務日に `x`）がある状態で 寝る → 全部残る
- [ ] **Step 3: 失敗するテストを書く**（`RecordsTests.cs`）．

```csharp
[Theory] [MemberData(nameof(StepCases))] public void Steps_MatchPython(VectorCase c)
// 各 step の Message と Recorded が一致．終わりの state.json が JSON として等しい（キーの順は問わない）．events.log の行が順も含めて一致
[Fact] public void CanUndo_OnlyForOwnLatestRecord()
// 寝る → token で true．そのあと state.json の undo を {"at": 同じ, "what": ["kitaku"]} に書き換えると false．61 分後は false．days_work_added が付いていれば false
[Fact] public void BrokenStateFile_RefusesAndWritesNothing()      // Review Focus 1: state.json の中身が "{" → Message == APP_STATE_BUSY，ファイルは "{" のまま，events.log は増えない
[Fact] public void LockedStateFile_RefusesAndLeavesNoTmp()        // Review Focus 2: state.json を FileShare.None で開いたまま 寝る → APP_STATE_BUSY，state.json.tmp が無い，例外が外に出ない
[Fact] public void Message_HasNoPrefix()                          // "[shiftlock] " で始まらない
```

- [ ] **Step 4:** `dotnet test` → 失敗．
- [ ] **Step 5:** 実装する．`Store` は `store.py` の `set_shift_status`・`log_event`・`set_mark` と同じ書き方（有給の印 `paid` の扱いを含む）．`Records` は `records.py` の `do_*` と `undo_fuzzy` の分岐をそのまま移す．書く順は「`events.log` に足す前に `state.json` を書く」（Python 版と同じ）で，`state.json` が書けなければ `events.log` にも書かない．やり直しの間隔は 50 ミリ秒．
- [ ] **Step 6:** `dotnet test` と pytest が全部通る．
- [ ] **Step 7:** コミット `feat(native): 記録（寝る・起きた・行ってきます・帰ってきた）と取り消しを移す (#38)`

---

### Task 7: 画面に出す値

**Files:**
- Create: `native/shiftlock.Core/Snapshot.cs`，`native/shiftlock.Core.Tests/SnapshotTests.cs`，`docs/test-vectors/snapshot.json`
- Modify: `pc/tools/export_native.py`

**Interfaces:**
- Consumes: `Planner`，`Store`，`ShiftConfig`，`Messages`
- Produces:

```csharp
public sealed record DayCell(DateOnly Date, bool Work, string? Why, bool IsException);
public sealed record StatusSnapshot(
    DateTime Now, string Phase, string? CountdownKey, DateTime? CountdownTo,
    Plan? NextPlan, string? RecordStatus, DateTime? SleepAt, DateTime? Kitaku,
    IReadOnlyList<DayCell> Schedule);
public static class Snapshot {
    public static StatusSnapshot Build(ShiftConfig cfg, Store store, DateTime now);
    public static IReadOnlyList<string> Lines(StatusSnapshot s, Messages m);   // 画面の上から順の文（時刻・フェーズ・残り・次の乗務・3 行の時刻・記録）
    public static string DayLabel(DayCell c, Messages m);                      // "12/01(火) 乗務"．理由があれば DAYS_NOTE，例外日で理由が無ければ DAYS_EXCEPTION_MARK
}
```

- `NextPlan` の選び方（`commands.status_text` と同じ）: `Current` の乗務 → `TargetPlan` → `Plans(今日, back: 0)` のうち `StartLatest > now` の最初．無ければ `null`．
- `CountdownKey`／`CountdownTo`: `lastcall` → `SoftClose`／`soft` → `HardClose`／`hard` → `DepartLatest`／`go` → `now < DepartTarget` なら `go` と `DepartTarget`，そうでなければ `go_late` と `DepartLatest`／`go_last` → `StartLatest`／`free` → どちらも `null`．相手の時刻を過ぎているときも `null`（負の残り時間を出さない）．
- `RecordStatus`・`SleepAt`・`Kitaku` は `state.json` の `shifts[NextPlan.Day]`．
- `Schedule` は今日から 14 日．`Why` は乗務なら `DaysWork`，休みなら `DaysOff` の理由（空文字は `null`）．`IsException` はその日がどちらかの例外に入っているか．

- [ ] **Step 1: ベクタを書き出す．** `snapshot.json` の節 `build`（`input: {"now"}` → `expect: {"phase", "countdown_key", "countdown_to", "next_day", "record_status", "sleep_at", "kitaku", "schedule": [{"date", "work", "why", "exception"}]}`）．期待値は `export_native.py` の中で Python 版の `current`・`target_plan`・`plans`・`is_workday`・`shift_status` を使って組む．ケース: 12-01 07:30（lastcall）／09:00（soft）／12:00（hard）／17:00（hard．出発の最終を過ぎているので残り時間なし）／`okita` で 15:50（go）／`okita` で 16:05（go_late）／`okita` で 16:20（go_last）／12-01 18:00（free．次は 12-02）／金曜 12-04 18:00（free．次は月曜で 24 時間より先）／`work_weekdays` が空（次の乗務なし）／`sleep_at` と `kitaku` がある乗務／`days.json` に理由つきの休みと，`calendar.json` に理由なしの出勤（土曜）
- [ ] **Step 2: 失敗するテストを書く**（`SnapshotTests.cs`）．

```csharp
[Theory] [MemberData(nameof(BuildCases))] public void Build_MatchesPython(VectorCase c)
[Fact] public void Lines_UsesMessages()
// 土台の設定・12-01 07:30・ja: Lines に "就寝準備の時間"，"就寝目安まで あと 30分"，"次の乗務  12/01(火)"，
// "就寝準備 07:20 → 就寝目安 08:00 → 最低ライン 10:30"，"出発 16:00（遅くとも 16:30）"，"出庫 17:00（遅くとも 17:30）"，"記録: まだなし" が順に入る
[Fact] public void Lines_NoShift()             // work_weekdays が空 → "14 日以内に乗務はないよ"
[Fact] public void Lines_RecordCombines()      // okita と sleep_at 08:10 → "記録: 起きた / 08:10 就寝"
[Fact] public void DayLabel_Variants()         // 理由つき → "12/03(木) 休み（用事）"，理由なしの例外 → "12/05(土) 乗務*"，例外でない日 → "12/01(火) 乗務"
```

- [ ] **Step 3:** `dotnet test` → 失敗．
- [ ] **Step 4:** 実装する．
- [ ] **Step 5:** `dotnet test` と pytest が全部通る．
- [ ] **Step 6:** コミット `feat(native): 画面に出す値をまとめる入口を追加 (#38)`

---

### Task 8: アプリの殻（WinUI 3）

**Files:**
- Create（テンプレートが作る）: `native/shiftlock.App/shiftlock.App.csproj`，`app.manifest`，`App.xaml`，`App.xaml.cs`，`MainWindow.xaml`，`MainWindow.xaml.cs`，`Properties/launchSettings.json`，`Properties/PublishProfiles/win-x64.pubxml`・`win-arm64.pubxml`
- Create（自分で書く）: `native/shiftlock.App/Program.cs`，`AppPaths.cs`
- Modify: `native/shiftlock.slnx`（`shiftlock.App` を足す），`native/README.md`

**Interfaces:**
- Consumes: `Shiftlock.Core`（プロジェクト参照）
- Produces: `Shiftlock.App.AppPaths.DataDir`（`SHIFTLOCK_DIR` があればそれ，無ければ `%USERPROFILE%\shift-lock`）．`MainWindow`（中身は Task 9）．

- [ ] **Step 1:** Microsoft 公式のテンプレートで作る（手書きしない．2026-11-17 に `dotnet new install Microsoft.WindowsAppSDK.WinUI.CSharp.Templates` でこの PC に入れてある．別の PC では先にこれを実行する）．

```powershell
dotnet new winui -n shiftlock.App -o native/shiftlock.App --packaging Unpackaged -tpmv 10.0.17763.0 -tfm net10.0
```

  作ったあとに直すところ:
  - csproj: `RootNamespace` を `Shiftlock.App`，`AssemblyName` を `shiftlock` にする．`DefineConstants` に `DISABLE_XAML_GENERATED_MAIN` を足す（Step 2 の `Main` を使うため）．`shiftlock.Core` へのプロジェクト参照を足す．テンプレートが固定した `Microsoft.WindowsAppSDK` の版はそのまま使い，`native/README.md` に書く．
  - 生成された各ファイルの名前空間を `Shiftlock.App` に揃える．
  - `MainPage.xaml`・`MainPage.xaml.cs` は消し，画面は `MainWindow` に直接置く（ページの移動は無いので）．`MainWindow` から `MainPage` への参照も外す．
  - `Properties/PublishProfiles/win-x86.pubxml` は消す（x86 は出さない）．
  - `native/Directory.Build.props` の `TreatWarningsAsErrors` で生成コードが通らないときの扱いは Global Constraints のとおり．
- [ ] **Step 2:** `Program.cs` に `Main` を書く．`AppInstance.FindOrRegisterForKey("shiftlock")` で 2 つ目の起動なら `RedirectActivationToAsync` して終わる．1 つ目は `Activated` を受けたらウィンドウを前に出す．
- [ ] **Step 3:** `MainWindow` はタイトル `shiftlock`，幅 520・高さ 560 程度，中身は空の `StackPanel`．
- [ ] **Step 4:** `dotnet build native/shiftlock.App -r win-x64` が警告なしで通る．`native/shiftlock.App/bin/Debug/net10.0-windows10.0.19041.0/win-x64/shiftlock.exe` を起動して空のウィンドウが出ること，もう一度起動しても 2 つ目が出ず 1 つ目が前に来ることを確かめる．`SHIFTLOCK_DIR` は空の一時フォルダに向ける．
- [ ] **Step 5:** `dotnet test native/shiftlock.Core.Tests` がそのまま通る．`native/README.md` の表の `shiftlock.App` の行と，ビルド・起動のコマンドを直す．
- [ ] **Step 6:** コミット `feat(native): WinUI 3 のアプリの殻を追加 (#38)`

---

### Task 9: 画面

**Files:**
- Modify: `native/shiftlock.App/MainWindow.xaml`，`MainWindow.xaml.cs`
- Create: `native/shiftlock.App/Refresher.cs`

**Interfaces:**
- Consumes: `ShiftConfig.Load`，`Store`，`Messages.Load`，`Snapshot.Build`・`Lines`・`DayLabel`，`Records.*`，`RecordResult`，`UndoToken`，`AppPaths.DataDir`
- Produces: `Refresher`（`event Action Changed`．1 分ごとのタイマーと，`DataDir` の `shifts.json`・`state.json`・`days.json`・`calendar.json`・`STOP.txt` の変更を 300 ミリ秒まとめて 1 回にして知らせる．UI スレッドで呼ぶ）

- [ ] **Step 1: 表示を作る．** 並びは設計書 §4 のスケッチのとおり: `Snapshot.Lines` の文，ボタン 4 つ（`APP_BTN_*`），結果の行と「取り消す」，`APP_SCHEDULE` の見出しと `DayLabel` を 7 日ずつ 2 行．色と余白は WinUI の標準のテーマ資源を使い，明るい配色と暗い配色の両方で読めること．時刻の表記は `Snapshot.Lines` の返す文のまま（0 埋め）．
- [ ] **Step 2: 読み直し．** `Refresher.Changed` とウィンドウが前に来たときに `ShiftConfig.Load` → `Snapshot.Build` をやり直す．
  - `FileNotFoundException` → `APP_NO_CONFIG`（`path` は `shifts.json` のフルパス）を出し，ボタン 4 つを押せなくする．文面は `Messages.Load(null)`．
  - `ConfigException` → `APP_BAD_CONFIG`（`err` は例外の文）を出し，ボタン 4 つを押せなくする．
  - それ以外の例外（読み込みの途中でファイルが変わった，など）→ 前の表示を残す．次の機会にやり直す．アプリは落とさない．
- [ ] **Step 3: ボタン．** 押したら 4 つとも押せなくし，`Records.*` を呼び，`RecordResult.Message` を結果の行に出し，読み直してから押せるように戻す（Review Focus 5）．`Recorded` が真で `Undo` があればそれを覚える．
- [ ] **Step 4: 取り消す．** 「取り消す」は，覚えている `UndoToken` に対して `Records.CanUndo` が真の間だけ出す（読み直しのたびに確かめる）．押したら `Records.Undo` を呼び，結果を出し，覚えていた `UndoToken` を捨てる．
- [ ] **Step 5: 動かして確かめる．** 一時フォルダに土台の設定（`lang` を `ja` にした合成の `shifts.json`）を置き，`SHIFTLOCK_DIR` を向けて起動する．次を順に確かめ，画面の写しを 1 枚 `native/README.md` 用に撮る（合成データであること）．
  1. フェーズ・次の乗務・予定が出る
  2. 4 つのボタンを押すと結果が出て，`state.json` と `events.log` が変わる（`events.log` のメモに `app`）
  3. 「取り消す」で戻る．61 分は待てないので，`state.json` の `undo.at` を手で 2 時間前に書き換えると「取り消す」が消えること
  4. 開いたまま `state.json` を別のエディタで書き換えると，1 秒ほどで表示が変わる
  5. `shifts.json` を消すと「まだ設定がない」になりボタンが押せない．戻すと元に戻る
  6. `shifts.json` の `work_weekdays` に `"holiday"` を入れると設定のエラーが出る
  7. ボタンを素早く 2 回押しても `events.log` に 1 行しか増えない
  8. `lang` を `ja_kansai` に変えると文面が関西弁になる
  9. Windows の配色を暗くしても読める
- [ ] **Step 6:** `dotnet build native/shiftlock.App -r win-x64` が警告なし，`dotnet test native/shiftlock.Core.Tests` と pytest が全部通る．
- [ ] **Step 7:** コミット `feat(native): 状態と記録の画面を作る (#38)`

---

### Task 10: 本番のコピーでの確認と文書

**Files:**
- Modify: `native/README.md`，`docs/data-format.md`，`docs/native-app-plan.md`，`CLAUDE.md`，`README.md`

- [ ] **Step 1: 本番のコピーで見比べる**（設計書 §6 の 3〜5）．`%USERPROFILE%\shift-lock` を scratchpad にコピーし（`log/` は不要），`SHIFTLOCK_DIR` を向けてアプリを起動する．同じコピーに対して Python 版の `status` を実行し（`python3 pc/shiftlock.py status --config <コピー>/shifts.json`），フェーズ・次の乗務の時刻・記録・予定が同じことを確かめる．コピーの上でボタン 4 つと取り消しを押し，Python 版の `status` が結果を読めること，アプリで記録したものを Python 版の「今のなし」（`shl` の取り消し）で戻せることを確かめる．**値を文書・テスト・コミットに書き写さない．確かめ終わったらコピーを消す．**
- [ ] **Step 2: 文書を直す．**
  - `docs/data-format.md` §1: `events.log` のメモの出所に `app`（ネイティブ版の画面）を足す．`state.json` の `undo` をネイティブ版も書くことを 1 行足す．
  - `docs/native-app-plan.md` §8: 順番を入れ替えたこと（画面ごとに移す 5 つの区切り）を表の下に 2〜3 行で書き，設計書へリンクする．もとの表は残す．
  - `CLAUDE.md`: 「テスト」に C# のテストのコマンドと「Python 版の判断・対象の文面を変えたら `python3 pc/tools/export_native.py` で書き出し直す」を足す．「ファイルの決まり」に `native/` の文面は書き出した JSON で読む（C# に直書きしない）ことを 1 行足す．
  - `README.md`: フォルダの表に `native/`（Windows のネイティブ版．状態と記録の画面まで）を足す．
  - `native/README.md`: 今できること，起動のしかた，`SHIFTLOCK_DIR`，画面の写し．
- [ ] **Step 3:** pytest と `dotnet test` が全部通る．
- [ ] **Step 4:** コミット `docs(native): 区切り 1 の文書を直す (#38)`．push して dev 向けの PR を作る（ロジックの変更を含むので Copilot のレビューを 1 回依頼する）．
- [ ] **Step 5:** マージ後，issue #38 のチェックリストを更新し，PR 番号とコミットを書いてクローズする．区切り 2 の issue を作る．公開リポジトリへ出すのはユーザーに確認してから（`tools/publish-public.sh`）．
