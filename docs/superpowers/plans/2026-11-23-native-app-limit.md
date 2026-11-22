# ネイティブ版 区切り 4：アプリの減速・一時停止・解除 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 本人が `shl enforce on throttle|suspend` で宣言した乗務にだけ，Agent が対象のアプリの CPU 上限を就寝目安から 4 段階で下げ，`suspend` なら最低ラインで一時停止し，出発・「今日休む」・緊急解除・出庫の時刻で元に戻す（issue #9）．

**Architecture:** 段階の計算・対象の決定・「すること」の判断は `shiftlock.Core` に純粋な形で置き，合成の入力でテストする．`shiftlock.Agent` はジョブと一時停止の操作・ゲームのフォルダの一覧（どちらもインターフェースの裏）と，`Scheduler` に足す 10 秒の仕事 `Limiter` だけを持つ．宣言・対象の登録・hook の印は Python 版（`pc/shiftlock/`）．見張り（`Tick`）は，新しい 2 つの動作に対して何も出さないようにするだけ．

**Tech Stack:** C# / .NET 10，xUnit，System.Text.Json，P/Invoke（`kernel32` のジョブ API，`ntdll` の `NtSuspendProcess`／`NtResumeProcess`），Python 3.8+（標準ライブラリのみ）と pytest

**Spec:** [docs/superpowers/specs/2026-11-23-native-app-limit-design.md](../specs/2026-11-23-native-app-limit-design.md)（以下「設計書」）．実装者は両方読む．

## Global Constraints

- .NET 10．`native/Directory.Build.props`（Nullable・警告はエラー）に従う．`*.cs`・`*.json` は UTF-8・LF．コメントは日本語．`*.py` は UTF-8・LF・標準ライブラリのみ．
- **プロセスを終了させない**（`TerminateProcess`・`Process.Kill`・`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` を書かない）．シャットダウン・再起動・ログオフを足さない．
- **宣言の入口を C#・画面・トレイ・hook に作らない**．非対話で同意を省く入口（`--yes` など）を作らない．C# が `state.json` の `enforce` に書いてよいのは `runs`・`first_run_at`・`released_at` だけ．
- **Claude Code から宣言・解除・設定変更のコマンドを実行しない**（`shl enforce on/off/kaijo/resume`，`shl app limit add/include`）．テストは関数を直接呼ぶ（`io` を差し替える）．
- 本番の実行フォルダ `%USERPROFILE%\shift-lock` に向けて動かさない・編集しない．確認は `SHIFTLOCK_DIR` を合成データ，`SHIFTLOCK_GUARD_TASK` を `shiftlock-guard-test` にする．`--install`・`deploy.ps1` を実行しない．個人データをコミットしない．実データの値を文書・テスト・コミット・報告に書き写さない．
- 実機で手を加えてよいプロセスは，**そのテストや下調べが自分で起動したものだけ**．本人のアプリ・ゲームに上限や一時停止をかけない．
- 判断は `shiftlock.Core` に置く．Core は Windows API を呼ばない．`now` と値は引数で受ける．
- 文面を C#・Python のコードに直書きしない．`pc/shiftlock/i18n/ja.py` と `ja_kansai.py` の両方に同じ名前で足す（言い回しは今の `ENFORCE_*` に合わせる）．`inm.py` は触らない．Agent が出す文のキーは `AGENT_LIMIT_` で始める（`AGENT_` は `export_native.py` が書き出す接頭辞）．`APP_` で始めない．
- **通知・`agent.log`・`events.log`・例外の文にアプリの名前を入れない**（`agent.log` に書いてよい名前は対象になったアプリのものだけ．通知は数だけ）．
- hook は **exit 2 を新しく返さない**．印の読み書きが失敗しても今までどおり動く．
- 減速の失敗で Agent を落とさない．見張り・計測・通知を遅らせない．`agent.log` の文は英語固定で，同じ失敗は 1 日 1 回（`OncePerDay`）．
- 判断できないとき（設定・宣言・計測が読めない）は，新しくかけず，既にかけたものを戻す．
- テストの日付は合成（2026-12）だけ．exe 名は `eldenring.exe`・`apex.exe`・`photoshop.exe`・`code.exe`・`node.exe`・`claude.exe`・`steam.exe`・`notepad.exe` を使う．
- コミットは日本語で `<型>(native): タイトル (#9)`（Python 側は `(pc)`）．`Co-Authored-By` や「Generated with」の行は付けない．push しない．
- テスト: C# は Windows で `dotnet test native`．Python は WSL で `cd /mnt/c/Dev/repo/public/shiftlock && source .venv/bin/activate && cd pc && python3 -m pytest -q tests`．触った側を全部通してからコミット．文面や `enforce_due` を変えたタスクは `python3 pc/tools/export_native.py` と `python3 pc/tools/make_test_vectors.py` で書き出し直して一緒にコミットする．

## Review Focus

設計書が明示しないが，使っていて当たりやすい入力．各行のテストは担当タスクに入れてある．

1. **`limit.json` が壊れている・途中まで書かれている**（Agent が書いている最中に電源が落ちた）: 止めたプロセスの記録が読めないと再開できない．起動時に読めなければ `limit.json.bad` に名前を変え，「記録が読めないので，止まったままのアプリがあれば終了して起動し直す」と 1 回知らせる．落ちない．（Task 6・9）
2. **一時停止の記録の PID が別のプロセスに使い回されている**（PC を再起動した後など）: 開始時刻が違えば触らず，記録から消す．（Task 7・9）
3. **段階の途中で宣言した・Agent を起動した・スリープから戻った**（S を過ぎている，H を過ぎている）: 過ぎた段階の予告は出さず，今の段階の上限をすぐかける．`runs` には今の段階だけ足す．（Task 7）
4. **`limit.steps`・`limit.apps` を手で壊した**（文字列，3 個，昇順，`null`）: 宣言は断る．Agent は「判断できない」として戻す側に倒す．既に宣言に写した `steps` が壊れているときも同じ．（Task 2・6）
5. **`claude-busy.json` に `Stop` が来なかった印が残る・時計が戻って未来の印がある**: 30 分より古い印と，今より後の印は「処理中でない」と読む．通知が永久に待たされない．（Task 6）

## ファイルの地図

```
docs/data-format.md                               Task 2（最初に直す）
docs/test-vectors/limit-stages.json               Task 2（新規．make_test_vectors.py が書く）
docs/test-vectors/tick.json                       Task 2（シナリオを足して書き出し直し）
pc/shiftlock/limit.py                             Task 2（新規．段階の計算・limit の設定の読み方・limit-games.json の読み方）
pc/shiftlock/config.py                            Task 2（DEFAULTS["limit"]・検証）
pc/shiftlock/enforce.py                           Task 2（enforce_due・suffix），Task 3（宣言・同意の表示・状態表示）
pc/shiftlock/apps.py・cli.py                      Task 4（shl app limit）
pc/shiftlock/hook.py                              Task 5（claude-busy.json）
pc/shiftlock/i18n/ja.py・ja_kansai.py             Task 2〜5・9（文）
pc/tools/export_native.py・make_test_vectors.py   Task 2
native/shiftlock.Core/Enforce.cs                  Task 2（Due・Suffix），Task 7（AppendLimitRun）
native/shiftlock.Core/LimitStages.cs              Task 2（新規）
native/shiftlock.Core/LimitSettings.cs            Task 6（新規）
native/shiftlock.Core/LimitFiles.cs               Task 6（新規．limit.json・limit-games.json・claude-busy.json）
native/shiftlock.Core/ProcTree.cs                 Task 6（新規．AppMeter から取り出す）
native/shiftlock.Core/AntiCheat.cs                Task 6（新規）
native/shiftlock.Core/LimitTargets.cs             Task 6（新規）
native/shiftlock.Core/LimitPolicy.cs              Task 7（新規）
native/shiftlock.Agent/Platform/ILimitControl.cs・WindowsLimitControl.cs   Task 8（新規）
native/shiftlock.Agent/Platform/IFolderLister.cs・WindowsFolderLister.cs   Task 8（新規）
native/shiftlock.Agent/Limiter.cs・LimitOnce.cs   Task 9（新規）
native/shiftlock.Agent/Meter.cs・ReleaseFlow.cs・Program.cs   Task 9
CLAUDE.md・docs/pc-spec.md・docs/pc-v11-design.md・docs/native-app-plan.md・docs/native-agent-worklog.md・native/README.md・README.md・pc/shifts.example.json・.gitignore・tools/publish-public.sh   Task 10（worklog は Task 1 でも）
```

---

### Task 1: 実機の下調べ（設計書 §6.3）

捨てる試しのコード．リポジトリには入れない（スクラッチ用のフォルダに置く）．結果だけを文書に書く．

**Files:**
- Modify: `docs/native-agent-worklog.md`（末尾に「区切り 4」の節），`docs/superpowers/specs/2026-11-23-native-app-limit-design.md` §6.3（結果を各項目の下に書き足す）

**Interfaces:**
- Produces: Task 8 が使う事実（ジョブを名前で開き直せるか／入れ子のジョブに上限をかけられるか／`CpuRate` の単位と最小値／一時停止と再開の動き／開けないプロセスのエラーコード）

- [ ] **Step 1: 試しのプログラムを 2 つ作る**（スクラッチのフォルダに `dotnet new console`．.NET 10）
  - `burn`: 引数のスレッド数だけ空ループで CPU を回し続ける．
  - `probe`: 引数で動作を選ぶ．`cap <pid> <jobName> <rate>`（`CreateJobObjectW(名前つき)` → `AssignProcessToJobObject` → `SetInformationJobObject(JobObjectCpuRateControlInformation, ENABLE|HARD_CAP, CpuRate)` → Enter を待つ），`uncap <jobName>`（`OpenJobObjectW` → `ControlFlags = 0`），`suspend <pid>`／`resume <pid>`（`NtSuspendProcess`／`NtResumeProcess`）．各 API の戻り値と `GetLastError` を出す．
- [ ] **Step 2: 5 項目を確かめて，結果を書き留める**（対象は自分で起動した `burn` と `notepad.exe` だけ）
  1. `burn 8` を起動 → `probe cap` で上限 → **`probe` をタスク マネージャー相当（`Stop-Process -Force`）で落とす** → 別の `probe uncap` が名前で開けるか．開けないときの `GetLastError`．開けない場合，`burn` の使用率が上限のままか．
  2. `probe` で外側のジョブを作り `burn` を入れる → 別の名前の内側のジョブに同じ `burn` を入れて上限をかけられるか（Steam が起動したゲームの代わり）．
  3. `burn` を論理コア数ぶんのスレッドで回し，`CpuRate` を 5000・2500・1000・50 にして，`shiftlock-agent.exe --meter-once`（合成データのフォルダで．`burn.exe` を `shl` を使わず `shifts.json` の `apps.registered` に手で書く）の `cpu` と比べる．
  4. `notepad.exe` を起動 → 文字を打つ → `probe suspend` → 窓を触る → `probe resume` → 打った文字が残り，続けて打てるか．`suspend` を 2 回 → `resume` 1 回で動くか（動かないはず）．
  5. 管理者で起動したプロセス（本人に 1 つ起動してもらうか，飛ばして「未確認」と書く）を `OpenProcess(PROCESS_SET_QUOTA | PROCESS_TERMINATE | PROCESS_SUSPEND_RESUME)` で開いたときのエラー．
- [ ] **Step 3: 結果を `docs/native-agent-worklog.md` の「区切り 4：下調べ」と設計書 §6.3 に書く**（項目ごとに「確かめたこと・結果・設計への影響」．実データの値は書かない）
- [ ] **Step 4: 1 で「開けない」だったら，ここで止まって本人に伝える**（Agent が落ちたときの戻し方が §6.3 の「代わりの案」になる．承諾をもらってから Task 2 へ）
- [ ] **Step 5: Commit** `docs(native): 区切り 4 の下調べの結果を書く (#9)`

---

### Task 2: 設定・段階の計算・見張りが新しい動作に何も出さない（Python と C# を揃える）

**Files:**
- Modify: `docs/data-format.md`（設計書 §5 の全部: `limit` の節，宣言の形，`limit.json`・`limit-games.json`・`claude-busy.json`，`events.log` の `limit_release` と `enforce_run` のメモ）
- Create: `pc/shiftlock/limit.py`，`native/shiftlock.Core/LimitStages.cs`，`docs/test-vectors/limit-stages.json`
- Modify: `pc/shiftlock/config.py`，`pc/shiftlock/enforce.py`，`pc/shiftlock/i18n/ja.py`，`ja_kansai.py`，`pc/tools/make_test_vectors.py`，`pc/tools/export_native.py`，`native/shiftlock.Core/Enforce.cs`，`native/shiftlock.Core/ShiftConfig.cs`（既定値の `limit`）
- Test: `pc/tests/test_limit.py`（新規），`pc/tests/test_vectors.py`，`native/shiftlock.Core.Tests/LimitStagesTests.cs`（新規），`native/shiftlock.Core.Tests/EnforceReleaseTests.cs`

**Interfaces:**
- Produces（Python）:
  - `limit.LIMIT_ACTIONS = ("throttle", "suspend")`
  - `limit.check_steps(steps) -> list[int] | None`（4 個ちょうど・整数・1 以上 100 未満・大きい順でなければ `None`．`bool` は整数と見ない）
  - `limit.limit_stages(soft: datetime, hard: datetime, steps: list[int], action: str) -> list[dict]`（`{"step": 1..5, "at": datetime, "percent": int | None}`．`percent` が `None` は一時停止）
  - `config.DEFAULTS["limit"]`（下の値）
- Produces（C#）:
  - `public sealed record LimitStage(int Step, DateTime At, int? Percent);`
  - `public static class LimitStages { public const string Throttle = "throttle"; public const string Suspend = "suspend"; public static bool IsLimitAction(string? action); public static IReadOnlyList<int>? CheckSteps(JsonNode? steps); public static IReadOnlyList<LimitStage> Of(DateTime soft, DateTime hard, IReadOnlyList<int> steps, string action); }`

**既定値**（Python の `DEFAULTS` と C# の既定値に同じ値）:

```python
"limit": {"steps": [75, 50, 25, 10], "baseline_minutes": 30, "apps": [], "exclude": [],
          "anticheat_markers": ["EasyAntiCheat*", "BattlEye*", "BEService*", "BEClient*", "GameGuard*", "*.des", "EAAntiCheat*",
                                "xigncode*", "*.xem", "mhyprot*", "ACE-*", "AntiCheatExpert*", "PunkBuster*", "pb*.dll", "vgc*",
                                "FACEIT*", "EQU8*", "nProtect*", "Denuvo*Anti*Cheat*"],
          "never": ["system", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "smss.exe", "svchost.exe",
                    "dwm.exe", "explorer.exe", "fontdrvhost.exe", "sihost.exe", "taskmgr.exe",
                    "shiftlock-agent.exe", "shiftlock.exe", "shl.exe", "python.exe", "pythonw.exe", "python3.exe",
                    "claude.exe", "node.exe", "wsl.exe", "wslhost.exe", "vmmemwsl", "vmmem", "windowsterminal.exe", "conhost.exe",
                    "openconsole.exe", "powershell.exe", "pwsh.exe", "cmd.exe", "bash.exe",
                    "discord.exe", "steam.exe", "steamwebhelper.exe", "steamservice.exe"],
          "protect": ["claude.exe", "node.exe", "wsl.exe", "wslhost.exe", "discord.exe",
                      "shiftlock-agent.exe", "shiftlock.exe", "shl.exe"]},
```

**段階の決まり**（設計書 §4.1）: `D = (hard - soft) / 4`．`hard > soft` なら 1〜4 が `soft + (k-1)*D`（`percent = steps[k-1]`），5 が `hard`（`throttle` は `steps[3]`，`suspend` は `None`）．`hard <= soft` なら 5 だけ．時刻は秒より下を切り捨てる．

- [ ] **Step 1: `docs/data-format.md` を直す**（コードより先）
- [ ] **Step 2: 失敗するテストを書く**（`pc/tests/test_limit.py`）

```python
S, H = datetime(2026, 12, 7, 13, 30), datetime(2026, 12, 7, 17, 0)

def test_stages_throttle():
    st = limit_stages(S, H, [75, 50, 25, 10], "throttle")
    assert [(x["step"], x["at"].strftime("%H:%M:%S"), x["percent"]) for x in st] == [
        (1, "13:30:00", 75), (2, "14:22:30", 50), (3, "15:15:00", 25), (4, "16:07:30", 10), (5, "17:00:00", 10)]

def test_stages_suspend_last_is_none():
    assert limit_stages(S, H, [75, 50, 25, 10], "suspend")[-1] == {"step": 5, "at": H, "percent": None}

def test_stages_hard_not_after_soft():
    assert [x["step"] for x in limit_stages(H, H, [75, 50, 25, 10], "suspend")] == [5]

@pytest.mark.parametrize("bad", [None, "75", [75, 50, 25], [10, 25, 50, 75], [75, 50, 25, 0], [100, 50, 25, 10], [75, 50, 25, True], [75.5, 50, 25, 10]])
def test_check_steps_rejects(bad):
    assert check_steps(bad) is None

def test_config_rejects_limit_not_dict(tmp_path):   # load_config が ValueError（CONFIG_NOT_DICT）

def test_enforce_due_returns_nothing_for_limit_actions(tmp_path):
    # hard フェーズ・active・uptime 十分・宣言 {"action": "suspend", "consented_at": ...} → enforce_due(...) == []，state.json の enforce に seen_at・warned_at が付かない

def test_enforce_suffix_for_limit_action(tmp_path):
    # ENFORCE_SUFFIX_LIMIT.format(label=ENFORCE_LABEL["suspend"], stop="") が返る（「N 時間」を含まない）
```

- [ ] **Step 3: 落ちるのを確かめる** `python3 -m pytest -q tests/test_limit.py` → ImportError
- [ ] **Step 4: Python を実装する**
  - `limit.py`: 上の 3 つ．`enforce.py` から `plan`・`store` を import しない（基盤の側に置く）．
  - `config.py`: `DEFAULTS["limit"]`，検証の一覧（`"usage", "notify", ...`）に `"limit"` を足す．
  - `enforce.py`: `ENFORCE_ACTIONS` に `throttle`・`suspend` を足す．`enforce_due` は，宣言の `action` が `LIMIT_ACTIONS` なら**最初に** `[]` を返す（`seen_at` も書かない）．`enforce_suffix` は `LIMIT_ACTIONS` のとき `M.ENFORCE_SUFFIX_LIMIT.format(label=..., stop=...)`．
  - 文: `ENFORCE_LABEL`・`ENFORCE_TERMS_WHAT` に 2 つ，`ENFORCE_SUFFIX_LIMIT`（`{label}`・`{stop}`）．両方の言語．
  - `export_native.py` の `EXACT` に `ENFORCE_SUFFIX_LIMIT`．
- [ ] **Step 5: 共有の見本を足す**（`make_test_vectors.py`）
  - `FILES` に `"limit-stages.json": build_limit_stages`．形は `{"stages": [{"name", "input": {"soft", "hard", "steps", "action"}, "expect": [{"step", "at", "percent"}]}]}`（時刻は秒までの ISO）．入れる例: 上の `throttle`／同じ `suspend`／`hard == soft`／`hard < soft`／幅 10 分（`soft` 16:50）／幅 7 分（割り切れない．秒の切り捨てを確かめる）／日またぎ（`soft` 23:30・`hard` 翌 01:30）．
  - `tick.json`: 今の作り方（`export_native.py` 側）に，`suspend` と `throttle` の宣言がある hard フェーズのシナリオを 1 つずつ足す（期待: `actions` に `enforce` が無い）．
  - `pc/tests/test_vectors.py` に `limit-stages.json` を読んで `limit_stages` と突き合わせるテストを足す．
- [ ] **Step 6: C# のテストを書く**
  - `LimitStagesTests`: `limit-stages.json` の全例で `LimitStages.Of` が一致（`Vectors.cs` の読み方に合わせる）．`CheckSteps` は Step 2 の `bad` と同じ入力で `null`，`[75,50,25,10]` はその並び．`IsLimitAction("throttle")`・`("suspend")` は真，`("lock")`・`(null)` は偽．
  - `EnforceReleaseTests`: `Enforce.Due` が `action` = `suspend`／`throttle` の宣言で空を返し，`state.json` が書き換わらない．`Enforce.Suffix` が `ENFORCE_SUFFIX_LIMIT` の文を返す．
- [ ] **Step 7: C# を実装する**（`LimitStages.cs`，`Enforce.Due` の先頭の判定，`Enforce.Suffix` の分岐，`ShiftConfig` の既定値）
- [ ] **Step 8: 書き出し直して全部通す** `python3 pc/tools/export_native.py && python3 pc/tools/make_test_vectors.py`，pytest，`dotnet test native`．`tick.json` の差分は足したシナリオだけであること．
- [ ] **Step 9: Commit** `feat(pc): 強制モードの動作 throttle／suspend の設定と段階の計算を足す (#9)`（C# も同じコミット．見本がずれないように）

---

### Task 3: 宣言と同意の表示（Python）

**Files:**
- Modify: `pc/shiftlock/enforce.py`，`pc/shiftlock/limit.py`，`pc/shiftlock/cli.py`（時間の引数の渡し方は変えない），`pc/shiftlock/i18n/ja.py`，`ja_kansai.py`，`pc/shiftlock/report_html.py`（動作のラベルの引き方が `ENFORCE_LABEL` でなければ足す）
- Test: `pc/tests/test_limit.py`

**Interfaces:**
- Consumes: `limit.LIMIT_ACTIONS`，`limit.check_steps`，`limit.limit_stages`（Task 2）
- Produces:
  - `limit.read_games(store, now) -> dict[str, str]`（`limit-games.json` を読む．exe 名 → `"target"｜"anticheat"｜"unknown"`．`seen` が 30 日より古い・形がおかしい項目は捨てる．ファイルが無い・壊れている → `{}`）
  - `limit.agent_seems_running(store, now) -> bool`（`log/app-*.jsonl` の最後の行の `t` が 15 分以内）
  - 宣言の形（`state.json`）: `{"action", "declared_at", "consented_at", "via": "shl", "runs": [], "steps": [..4 個..], "limit_apps": [...]}`（`duration_hours`・`grace_minutes`・`relock_minutes`・`max_runs`・`boot_grace_minutes` は**入れない**）

- [ ] **Step 1: 失敗するテストを書く**（`io` を差し替えて `enforce_declare` を直接呼ぶ．Windows のダイアログは `StdIO` のときだけ出るので通らない）

```python
def test_declare_suspend_fixes_steps_and_apps(tmp_path):
    # cfg["limit"]["apps"] = ["photoshop.exe"] ／ io が "y" を返す
    # → state.json の enforce == {"action": "suspend", "via": "shl", "runs": [], "steps": [75, 50, 25, 10], "limit_apps": ["photoshop.exe"], declared_at, consented_at}
    # → "duration_hours" not in e
def test_declare_limit_rejects_hours(tmp_path):        # enforce_declare(cfg, store, now, "throttle", "3h", io) == M.ENFORCE_LIMIT_NO_HOURS，state.json は変わらない
def test_declare_limit_rejects_bad_steps(tmp_path):    # cfg["limit"]["steps"] = [10, 25, 50, 75] → M.ENFORCE_LIMIT_BAD_STEPS
def test_declare_limit_needs_yes(tmp_path):            # io が "n"・EOF → M.ENFORCE_DECLINED，宣言なし
def test_terms_limit_lists_stages_and_games(tmp_path):
    # limit-games.json = {"eldenring.exe": target, "apex.exe": anticheat}，limit.apps = ["photoshop.exe"]
    # enforce_terms(...) に: 5 つの段階の時刻（13:30・14:22・15:15・16:07・17:00）と 75・50・25・10，"eldenring.exe"，"apex.exe"，"photoshop.exe"，
    #   M.ENFORCE_TERMS_LIMIT_ANTICHEAT_NOTE（見逃しがありうる），戻る時刻（start_latest），解除の方法（shl kaijo・STOP.txt）が入る
def test_terms_suspend_has_unsaved_warning_throttle_does_not(tmp_path)
def test_terms_warns_when_agent_not_running(tmp_path):  # app ログが無い → M.ENFORCE_LIMIT_NO_AGENT を含む．5 分前の行がある → 含まない
def test_status_text_limit_has_no_hours(tmp_path):     # enforce_status_text の該当行に "時間" の数値が出ない（ENFORCE_STATUS_LINE_LIMIT）
def test_off_before_first_run_cancels_after_run_releases(tmp_path):
    # runs == [] かつ now < hard → 宣言が消える．runs == [{"at", "action": "throttle", "step": 1}] → released_at が付き STOP.txt ができる
def test_read_games_ignores_old_and_broken(tmp_path)
```

- [ ] **Step 2: 落ちるのを確かめる**
- [ ] **Step 3: 実装する**
  - `enforce_declare`: `action in LIMIT_ACTIONS` のとき — `hours is not None` なら `M.ENFORCE_LIMIT_NO_HOURS`／`check_steps(cfg["limit"]["steps"])` が `None` なら `M.ENFORCE_LIMIT_BAD_STEPS`／`hours` の検証と `duration_hours` の固定を飛ばす／宣言は上の形（`limit_apps` は `cfg["limit"]["apps"]` を `normalize_exe` でそろえて並べ替えたもの）．対話・TTY・Windows のダイアログの手順は今のまま．`events.log` のメモは `throttle`／`suspend` だけ．
  - `enforce_terms(cfg, p, action, hours)` に `store`・`now` を任意の引数で足し，`LIMIT_ACTIONS` のときは別の組み立て（`M.ENFORCE_TERMS_LIMIT` ＋ 段階の行 `M.ENFORCE_TERMS_LIMIT_STAGE` ＋ ゲームの一覧 ＋ 足したアプリ ＋ 注意）．`suspend` だけ `M.ENFORCE_TERMS_LIMIT_SUSPEND_NOTE`．どちらの動作も `M.ENFORCE_TERMS_CAUTION_LINE` を付ける（`CAUTION_ACTIONS` に足す）．
  - `enforce_off`: 今の条件 `now < p.hard_close and not e.get("runs")` のままで足りる（`runs` は Agent が段階ごとに足す）．変えない．テストだけ足す．
  - `enforce_status_text`: `LIMIT_ACTIONS` の行は `M.ENFORCE_STATUS_LINE_LIMIT`（`{hours}` なし）．
- [ ] **Step 4: 全部通す**（pytest．`export_native.py --check` が通ること．`ENFORCE_LABEL` を変えていれば書き出し直す）
- [ ] **Step 5: Commit** `feat(pc): shl enforce on throttle／suspend の宣言と同意の表示を足す (#9)`

---

### Task 4: `shl app limit`（Python）

**Files:**
- Modify: `pc/shiftlock/apps.py`，`pc/shiftlock/cli.py`，`pc/shiftlock/i18n/ja.py`，`ja_kansai.py`（`APPS_LIMIT_*` と `shl help` の文）
- Test: `pc/tests/test_apps.py`

**Interfaces:**
- Consumes: `apps.normalize_exe`，`config.load_raw`／`save_raw`，`limit.read_games`（Task 3）
- Produces: `apps.limit_add(cfg_path, name, interactive: bool) -> tuple[int, str]`，`limit_remove(cfg_path, name)`，`limit_exclude(cfg_path, name)`，`limit_include(cfg_path, name, interactive: bool)`，`limit_list(cfg_path, store, now)`（どれも `(終了コード, 文)`）

- [ ] **Step 1: 失敗するテストを書く**

```python
def test_limit_add_requires_registered(tmp_path):      # 登録に無い → (1, APPS_LIMIT_NOT_REGISTERED)
def test_limit_add_rejects_never_names(tmp_path):      # "node.exe" を dev で登録してあっても → (1, APPS_LIMIT_NEVER)
def test_limit_add_needs_interactive(tmp_path):        # interactive=False → (1, APPS_LIMIT_NEED_TTY)，shifts.json は変わらない
def test_limit_add_writes_and_warns(tmp_path):
    # "Photoshop" → limit.apps == ["photoshop.exe"]，文に APPS_LIMIT_ANTICHEAT_WARN と APPS_LIMIT_PROTECT_NOTE を含む．2 回目も 1 件のまま
def test_limit_add_does_not_write_omitted_keys(tmp_path):   # shifts.json に limit が無かった → 書いた後の limit のキーは "apps" だけ
def test_limit_remove(tmp_path)                        # 無い → (1, APPS_LIMIT_NOT_FOUND)．interactive は要らない
def test_limit_exclude_include(tmp_path):              # exclude はどこからでも・2 回やっても 1 件．include は interactive=False で断る
def test_limit_exclude_accepts_unknown_game(tmp_path): # 見たことの無い名前でも外せる（先回りして外せるように）
def test_limit_list_shows_three_groups(tmp_path):      # 足したアプリ／外したゲーム／最近見たゲームの判定（target・anticheat・unknown）
def test_limit_bad_section(tmp_path):                  # "limit": [] → (1, APPS_BAD_SECTION)
```

- [ ] **Step 2: 落ちるのを確かめる**
- [ ] **Step 3: 実装する**．`cli.py` は `shl app limit <add|remove|exclude|include|list> [名前]` を受け，`interactive` に `sys.stdin.isatty()` を渡す（`enforce_declare` の判定と同じ）．引数が足りなければ使い方（`APPS_LIMIT_USAGE`）と終了コード 1．
- [ ] **Step 4: 全部通す**
- [ ] **Step 5: Commit** `feat(pc): shl app limit で減速の対象を足す・外す (#9)`

---

### Task 5: hook の「処理中」の印（Python）

**Files:**
- Modify: `pc/shiftlock/hook.py`
- Test: `pc/tests/test_shiftlock.py`（hook のテストがある場所に足す）

**Interfaces:**
- Produces: `claude-busy.json` = `{"<session_id>": "<ISO 秒まで>"}`（Task 6 の `ClaudeBusy.IsBusy` が読む）

- [ ] **Step 1: 失敗するテストを書く**（`run_hook(cfg_path, stdin_text, now)` を直接呼ぶ）

```python
def test_busy_mark_set_on_prompt_and_cleared_on_stop(tmp_path):
    # UserPromptSubmit（session_id "s1"，普通の文）→ exit 0，claude-busy.json == {"s1": "2026-12-07T10:00:00"}
    # Stop（"s1"）→ {}（ファイルは残ってよい）
def test_busy_mark_two_sessions(tmp_path):             # s1・s2 を足して s1 の Stop → {"s2": ...}
def test_busy_mark_not_set_on_keyword(tmp_path):       # 「ねる」→ exit 2，claude-busy.json は無いか {}
def test_busy_mark_not_set_on_hard_block(tmp_path):    # hard フェーズで block_claude_on_hard → exit 2，印なし
def test_busy_mark_without_session_id(tmp_path):       # session_id なし → 印なし，戻り値は今までどおり
def test_busy_mark_write_failure_does_not_change_exit(tmp_path, monkeypatch):
    # claude-busy.json を書く関数が OSError → UserPromptSubmit は exit 0（か今までと同じ出力），Stop は exit 0
def test_busy_mark_broken_file_is_replaced(tmp_path):  # 中身が "[" → 次の UserPromptSubmit で {"s1": ...} に置き換わる
def test_busy_mark_drops_entries_older_than_a_day(tmp_path)
```

- [ ] **Step 2: 落ちるのを確かめる**
- [ ] **Step 3: 実装する**．`run_hook` の中で，戻り値を作った後に印を足す／消す小さな関数を呼ぶ（`try/except Exception: pass` で包む）．足すのは「`event == "UserPromptSubmit"` かつ戻り値の終了コードが 0」のときだけ．設定が壊れていて 1 を返す道では足さない．
- [ ] **Step 4: 全部通す**．加えて，合成データのフォルダで hook を実際に 2 通りで呼び，終了コードが 2 にならないことを見る（Git Bash: `echo '{"hook_event_name":"Stop","session_id":"t"}' | python pc/shiftlock.py hook --config <合成>/shifts.json; echo $?`．WSL でも同じ．入口の引数は `pc/shiftlock.cmd`・`docs/pc-spec.md` §5 で確かめる）
- [ ] **Step 5: Commit** `feat(pc): hook が Claude Code の処理中の印を claude-busy.json に残す (#9)`

---

### Task 6: Core の部品（設定・記録の読み書き・ツリー・アンチチート・対象の決定）

**Files:**
- Create: `native/shiftlock.Core/LimitSettings.cs`，`LimitFiles.cs`，`ProcTree.cs`，`AntiCheat.cs`，`LimitTargets.cs`
- Modify: `native/shiftlock.Core/AppMeter.cs`（ツリーの組み立てを `ProcTree` に移す．動きは変えない），`native/shiftlock.Core/Games.cs`（`Like` を `internal` にする）
- Test: `native/shiftlock.Core.Tests/LimitSettingsTests.cs`，`LimitFilesTests.cs`，`ProcTreeTests.cs`，`AntiCheatTests.cs`，`LimitTargetsTests.cs`（どれも新規）

**Interfaces:**
- Consumes: `LimitStages.CheckSteps`（Task 2），`ProcSample`・`ProcKey`・`MeterSample`・`MeterSettings.NormalizeExe`，`GameRules`，`Store.ReadJson`／`WriteJson`／`P`
- Produces:

```csharp
public sealed record LimitSettings(IReadOnlyList<int> Steps, TimeSpan BaselineWindow, IReadOnlySet<string> Apps, IReadOnlySet<string> Exclude,
    IReadOnlyList<string> AntiCheatMarkers, IReadOnlySet<string> Never, IReadOnlySet<string> Protect)
{ public static LimitSettings Read(ShiftConfig cfg); }   // 形がおかしければ ConfigException．名前は NormalizeExe でそろえる（"system"・"vmmem" のように .exe の無い名前は小文字のまま持つ）

public sealed record CappedApp(string Job, double Percent);
public sealed record SuspendedProc(int Pid, DateTime Start, string Exe) { public ProcKey Key => new(Pid, Start); }
public sealed record LimitState(string? Day, IReadOnlyDictionary<string, double> Baseline, IReadOnlyDictionary<string, CappedApp> Capped,
    IReadOnlyList<SuspendedProc> Suspended, IReadOnlySet<string> Skipped, IReadOnlySet<string> Announced, IReadOnlyDictionary<string, DateTime> Pending)
{ public static LimitState Empty { get; } public bool HasEffects { get; } /* Capped か Suspended が空でない */ }

public static class LimitFiles
{
    public static LimitState ReadState(Store store);            // 無い → Empty．壊れている → InvalidDataException
    public static void WriteState(Store store, LimitState s);   // limit.json．.tmp → 置き換え（Store.WriteJson）
    public static void QuarantineState(Store store);            // limit.json → limit.json.bad（上書き）
    public static IReadOnlyDictionary<string, string> ReadGames(Store store, DateTime now);
    public static void UpdateGames(Store store, IReadOnlyDictionary<string, string> seen, DateTime now);   // 変わったときだけ書く．30 日見ないものを消す
    public static bool ClaudeBusy(Store store, DateTime now);   // 例外を出さない
}

public static class ProcTree
{   // root を含む子孫．親の開始時刻が子より後なら親子とみなさない．同じプロセスは 1 回
    public static IReadOnlyList<ProcSample> OfName(IReadOnlyList<ProcSample> procs, string exeName);
}

public static class AntiCheat
{
    public sealed record Root(string Dir, int Depth);   // Depth 3 = 根の下を 3 階層，0 = そのフォルダの直下だけ
    public static Root? RootOf(GameRules rules, string? exePath);                       // 設計書 §4.4 の「根のフォルダの決め方」．パスが無い → null
    public static bool Matches(string fileOrDirName, IReadOnlyList<string> markers);    // Games.Like と同じ一致（大文字小文字を区別しない）
}

public enum LimitVerdict { Target, AntiCheat, Unknown, Excluded, NotAdded, Protected }
public sealed record LimitApp(string Exe, bool IsGame, IReadOnlyList<ProcSample> Tree, LimitVerdict Verdict);
public static class LimitTargets
{
    /// games は「exe 名 → target｜anticheat｜unknown」（Agent がフォルダを見て決めた結果．無い名前は unknown）．
    /// declaredApps は宣言に固定した limit_apps．
    public static IReadOnlyList<LimitApp> Resolve(MeterSample sample, IReadOnlyDictionary<string, string> registered,
        LimitSettings settings, IReadOnlySet<string> declaredApps, IReadOnlyDictionary<string, string> games);
}
```

  - `limit.json` には設計書 §5.3 の形に `"announced": ["1", "2"]` と `"pending": {"<キー>": "<時刻>"}` を足す（予告を出した段階と，処理中で待たせている通知）．**設計書 §5.3 と `data-format.md` の表にこの 2 つを書き足す**．
  - `Resolve` の決まり（設計書 §4.3．上から順に当てる）: ゲーム（`sample.Games` に 1 つでも当たる exe 名）で `settings.Exclude` にある → `Excluded`／ゲームで `games` が `anticheat` → `AntiCheat`／`unknown`・無い → `Unknown`／登録アプリで（`settings.Apps` ∩ `declaredApps`）に無い → `NotAdded`／exe 名が `settings.Never` にある → `Protected`／ツリーの**子孫**（根の名前のプロセス自身を除く）に `settings.Protect` の名前がある → `Protected`／それ以外 → `Target`．登録とゲームの両方に当たる exe は登録として扱う（区切り 3 と同じ）．登録にもゲームにも無い exe は結果に入れない．

- [ ] **Step 1: 失敗するテストを書く**（主なもの．名前と確かめること）
  - `LimitSettingsTests`: 既定値（Task 2 の値）が読める／`steps` が壊れている 4 通りで `ConfigException`／`apps`・`exclude` の `"Photoshop"` が `photoshop.exe` になる／`limit` が辞書でない → `ConfigException`／`baseline_minutes` が 0・負・文字列 → `ConfigException`．
  - `LimitFilesTests`: 書いて読んで同じ／ファイル無し → `Empty`／`"{"`・`"[]"`・`suspended` の `pid` が文字列 → `InvalidDataException`（Review Focus 1）／`QuarantineState` で `limit.json.bad` ができ `limit.json` が無くなる／`ReadGames` が 31 日前を捨てる／`UpdateGames` が同じ中身なら書かない（更新時刻が変わらない）／`ClaudeBusy`: 29 分前 → 真，31 分前 → 偽，**5 分後（未来）→ 偽**，壊れたファイル → 偽，無い → 偽（Review Focus 5）．
  - `ProcTreeTests`: 子・孫を含む／親の開始時刻が子より後（PID の使い回し）なら含まない／同じ名前が 2 本あれば両方のツリー／重複なし．加えて **`AppMeter` の既存のテストが全部そのまま通る**．
  - `AntiCheatTests`: `C:\Steam\steamapps\common\Game\bin\x64\game.exe` → `Root("C:\Steam\steamapps\common\Game\", 3)`／`C:\Games\foo.exe`（`C:\Games\` がゲームのフォルダ）→ `Root("C:\Games\", 0)`／ゲームのフォルダの外（`game_processes` の名前だけ）→ exe のフォルダ・3／`null` → `null`／`Matches("EasyAntiCheat_EOS", …)`・`("battleye", …)`・`("game.des", …)` は真，`("data.pak", …)` は偽．
  - `LimitTargetsTests`: 上の決まりを 1 行 1 テストで（`eldenring.exe` target／`apex.exe` anticheat／判定なしは `Unknown`／`Excluded`／`photoshop.exe` が `settings.Apps` にあるが `declaredApps` に無い → `NotAdded`（後から足しても効かない）／`declaredApps` にあるが `settings.Apps` から外した → `NotAdded`（外すのはすぐ効く）／`code.exe` の孫に `claude.exe` → `Protected`／`eldenring.exe` の**親**が `steam.exe` → `Target`（祖先は見ない）／`conhost.exe` を子に持つだけ → `Target`／登録した名前が `never` にある → `Protected`）．
- [ ] **Step 2: 落ちるのを確かめる** `dotnet test native --filter "FullyQualifiedName~Limit|FullyQualifiedName~ProcTree|FullyQualifiedName~AntiCheat"`
- [ ] **Step 3: 実装する**．`AppMeter` のツリーの部分は `ProcTree` を呼ぶ形に置き換える（結果が同じであること）．
- [ ] **Step 4: 全部通す** `dotnet test native`
- [ ] **Step 5: Commit** `feat(native): 減速の設定・記録・対象の決定を Core に足す (#9)`

---

### Task 7: Core の判断（`LimitPolicy`）

**Files:**
- Create: `native/shiftlock.Core/LimitPolicy.cs`
- Modify: `native/shiftlock.Core/Enforce.cs`（`AppendLimitRun`）
- Test: `native/shiftlock.Core.Tests/LimitPolicyTests.cs`（新規），`EnforceReleaseTests.cs`

**Interfaces:**
- Consumes: Task 2・6 の型，`Plan`（`SoftClose`・`HardClose`・`StartLatest`・`Day`．`Planner.cs` の実際の名前に合わせる），`Messages`
- Produces:

```csharp
public sealed record LimitInput(
    DateTime Now,
    Plan? Plan,                      // 見張りと同じ決め方で選んだ乗務（Planner.TargetPlan）．無ければ null
    JsonObject Declaration,          // その乗務の enforce（無ければ空）
    string? ShiftStatus,             // その乗務の status（"worked"・"yasumi"・"okita"・null）
    bool StopActive,
    LimitState State,
    IReadOnlyList<LimitApp>? Apps,   // null = 判断できない（設定・計測が読めない，親の PID が取れない経路）
    IReadOnlyList<(DateTime T, JsonObject Rec)> RecentLines,   // log/app の直近の行（基準の計算用）
    TimeSpan BaselineWindow,
    bool ClaudeBusy,
    string PhaseMethod, string HardMethod);   // notify.phase・notify.hard

public abstract record LimitOp;
public sealed record CapOp(string Exe, string Job, IReadOnlyList<ProcKey> Procs, double Percent) : LimitOp;   // ジョブに入れて上限（既に入っていれば上限だけ変える）
public sealed record UncapOp(string Exe, string Job) : LimitOp;
public sealed record SuspendOp(SuspendedProc Proc) : LimitOp;
public sealed record ResumeOp(SuspendedProc Proc) : LimitOp;
public sealed record NoticeOp(string Method, string Text) : LimitOp;
public sealed record RunOp(string Day, string Action, int Step) : LimitOp;        // state.json の runs に足す
public sealed record ReleaseEventOp(string Day, string Reason) : LimitOp;         // events.log の limit_release

/// <summary>StateBeforeOps は，操作の前に書く記録（止めるプロセスを含む）．StateAfterOps は，全部成功したときの記録．</summary>
public sealed record LimitDecision(IReadOnlyList<LimitOp> Ops, LimitState StateBeforeOps, LimitState StateAfterOps);

public static class LimitPolicy
{
    public const double MinPercent = 0.5;
    public static readonly TimeSpan NoticeLead = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan BusyWaitMax = TimeSpan.FromMinutes(10);
    public static string JobName(string exe);   // "shiftlock-limit-" + exe（Task 1 の結果で名前空間の接頭辞が要れば足す）
    public static LimitDecision Decide(LimitInput input, Messages m);
    public static LimitDecision RestoreAll(LimitState state, string? day, string? reason, Messages m, string hardMethod);   // 終了時・緊急解除用（reason が null なら events.log に書かない）
}
// Enforce.cs
public static void AppendLimitRun(Store store, string day, string action, int step, DateTime now);
// runs に {"at", "action", "step"} を足し（first_run_at が無ければ入れる），events.log に enforce_run（メモ "throttle 1" の形）．宣言が無い・released_at がある → 何もしない
```

**`Decide` の順番**（設計書 §4.5〜4.7・§4.9）:

1. **戻す条件**を上から見る．当たれば `RestoreAll` と同じ結果（理由つき）を返す:
   `Apps is null` か宣言の `steps` が `CheckSteps` を通らない →（理由なし）／`Plan is null`・宣言が空・`consented_at` が無い → `off`／宣言の `action` が `throttle`／`suspend` でない →（理由なし）／`StopActive` か `released_at` → `kaijo`／`ShiftStatus == "worked"` → `depart`／`"yasumi"` → `yasumi`／`Now >= Plan.StartLatest` → `timeout`．
   `RestoreAll` は: `Capped` ごとに `UncapOp`，`Suspended` ごとに `ResumeOp`，`State.HasEffects` か `Announced` が空でないときだけ `ReleaseEventOp`（理由があるとき）と解除の報告（`AGENT_LIMIT_RELEASED`．`hardMethod`）．`StateAfterOps` は `Baseline`・`Day`・`Pending` だけ残す．**戻すものが何も無ければ `Ops` は空**（10 秒ごとに報告を出さない）．
2. `State.Day != Plan.Day` なら `Baseline`・`Announced`・`Skipped` を空にしてから続ける（`Capped`・`Suspended` が残っていれば先に戻す）．
3. `stages = LimitStages.Of(Plan.SoftClose, Plan.HardClose, steps, action)`．今の段階 = `At <= Now` の最後のもの（無ければ「まだ」）．
4. **記録の掃除**: `Suspended` のうち，`Apps` のどのツリーにも同じ `ProcKey` が無いものは記録から消す（プロセスが終わった・PID の使い回し．Review Focus 2）．`Capped` のうち，その exe が `Apps` に無いものも消す（アプリが終わった）．
5. **予告**（次の段階があり，`next.At - NoticeLead <= Now < next.At`，`Announced` に無く，`Verdict == Target` のアプリが 1 つ以上）: `Announced` に足し，文（`AGENT_LIMIT_NOTICE`: 時刻・割合・対象の数／一時停止なら `AGENT_LIMIT_NOTICE_SUSPEND`）を通知の列に入れる．方法は一時停止の予告が `HardMethod`，ほかは `PhaseMethod`．過ぎた段階の予告は出さない（Review Focus 3）．
6. **今の段階があるとき**，`Target` のアプリごとに:
   - 基準が無ければ決める: `RecentLines` のうち `[窓の終わり − BaselineWindow, 窓の終わり]` でその exe が載っている行．窓の終わりは，`T <= SoftClose` の行にその exe があれば `SoftClose`，無ければ `Now`．`act >= 1` の行があればその `cpu` の最大，無ければ `cpu > 0` の行の最大，それも無ければ**このアプリは今回何もしない**．
   - `percent` があれば: 上限 = `max(MinPercent, 基準 × percent / 100)`（小数 2 桁に丸める）．`CapOp`（ツリーの全 `ProcKey`）を出すのは「`Capped` にその exe が無い」か「`Capped[exe].Percent` が違う」ときだけ．同じなら出さない（上限をかけた後に増えた子プロセスは，ジョブが自動で引き継ぐ）．
   - `percent` が `null`（一時停止）: ツリーのうち `Suspended` に無いプロセスごとに `SuspendOp`．上限は段階 4 のまま残す．
   - その段階で初めて何かの `CapOp`／`SuspendOp` を出すとき（宣言の `runs` にその `step` が無い）: `RunOp(Plan.Day, action, step)` を 1 つ．
   - 一時停止を出した回に，報告（`AGENT_LIMIT_SUSPENDED`．`HardMethod`）を 1 回（`Announced` に `"suspended"`）．
7. `Verdict == Protected` のアプリは，`Skipped` に無ければ足して `AGENT_LIMIT_SKIPPED` を 1 回（`PhaseMethod`）．`Capped`・`Suspended` に載っていたら戻す（`UncapOp`・`ResumeOp`）．`Excluded`・`AntiCheat`・`Unknown`・`NotAdded` に変わったアプリも同じく戻す（知らせない）．
8. **通知を出すか待つか**: 通知の列（`Pending` に残っていたもの ＋ 今回のもの）は，`ClaudeBusy` が偽ならすぐ `NoticeOp`．真なら `Pending` に（キー → 最初に待ち始めた時刻．文は作り直せるようキーから決まるものにする）入れ，待ち始めから `BusyWaitMax` を過ぎたものだけ出す．

- [ ] **Step 1: 失敗するテストを書く**（`LimitPolicyTests`．合成の `LimitInput` を作る小さな組み立て関数を用意し，設計書 §7 の 1 の箇条を 1 行 1 テストで．値は S = 12/7 13:30，H = 17:00，`StartLatest` = 22:30，`steps` = 75・50・25・10，基準 40.0）

```csharp
[Fact] public void Before_soft_no_ops()                         // 13:20 → Ops 空
[Fact] public void Notice_five_minutes_before_stage_1()         // 13:25 → NoticeOp 1 つ（"13:30" と "75" を含み，"eldenring" を含まない），Announced に "1"
[Fact] public void Notice_not_repeated()                        // Announced に "1" → 13:26 は空
[Fact] public void No_notice_when_no_target_running()
[Fact] public void Stage_1_caps_at_75_percent_of_baseline()     // 13:30 → CapOp("eldenring.exe", …, 30.0)，RunOp(day, "suspend", 1)
[Fact] public void Stage_boundaries_exact()                     // 14:22:30 → 20.0，15:15 → 10.0，16:07:30 → 4.0
[Fact] public void Cap_floor_is_half_percent()                  // 基準 2.0・段階 4 → 0.5
[Fact] public void Throttle_keeps_10_percent_after_hard()       // action throttle・17:30 → CapOp 4.0（か既に 4.0 なら空），SuspendOp なし
[Fact] public void Suspend_at_hard_suspends_whole_tree()        // 17:00 → ツリー 3 個に SuspendOp 3 つ，StateBeforeOps.Suspended に 3 件，報告 1 つ
[Fact] public void Already_suspended_not_suspended_again()
[Fact] public void App_started_after_hard_is_suspended()
[Fact] public void Hard_not_after_soft_only_stage_5()
[Fact] public void Baseline_is_max_of_active_lines_in_window()  // act≥1 の行が 12.0・40.0・25.0 → 40.0．31 分前の 90.0 は入らない
[Fact] public void Baseline_falls_back_to_lines_with_cpu_when_never_active()
[Fact] public void Baseline_waits_when_no_line()                // 行なし → その回は CapOp なし，Baseline に入らない
[Fact] public void Baseline_not_recomputed()                    // State.Baseline 40.0・直近の行が 5.0 → 40.0 のまま
[Fact] public void Baseline_uses_lines_before_soft_when_state_lost()   // 15:00・State 空・S 以前の行 40.0，S 以後の行 8.0 → 40.0
[Fact] public void Started_mid_stage_applies_current_stage_only()      // 15:20 に初めて見る → CapOp 10.0，RunOp は step 3 だけ，予告は出さない（Review Focus 3）
[Fact] public void Run_op_once_per_step()                       // 宣言の runs に step 1 がある → RunOp なし
[Theory] /* worked→depart, yasumi→yasumi, StopActive→kaijo, released_at→kaijo, 宣言なし→off, 22:30→timeout */
         public void Release_conditions_restore_everything(…)   // UncapOp・ResumeOp・ReleaseEventOp(reason)・報告 1 つ．StateAfterOps は Baseline だけ残る
[Fact] public void Release_with_nothing_applied_is_silent()     // State 空で worked → Ops 空
[Fact] public void Lock_declaration_restores_without_event()    // action lock・Capped あり → UncapOp，ReleaseEventOp なし
[Fact] public void Unknown_apps_restores_and_adds_nothing()     // Apps null・Capped と Suspended あり → 戻すだけ
[Fact] public void Broken_declared_steps_restores()             // Declaration["steps"] = "x"（Review Focus 4）
[Fact] public void Protected_app_skipped_and_announced_once()
[Fact] public void App_becoming_protected_is_restored()         // 上限をかけた後で子に claude.exe → UncapOp
[Fact] public void Excluded_after_cap_is_restored_silently()
[Fact] public void Suspended_record_of_reused_pid_is_dropped_without_resume()   // 同じ pid・違う Start → ResumeOp なし，記録から消える（Review Focus 2）
[Fact] public void Notice_waits_while_claude_busy()             // ClaudeBusy → NoticeOp なし，Pending にキー．CapOp は出る
[Fact] public void Pending_notice_delivered_when_idle()
[Fact] public void Pending_notice_delivered_after_ten_minutes()
[Fact] public void Narrow_stages_only_nearest_notice()          // 幅 10 分（D = 2.5 分）で S の 4 分前 → 予告は段階 1 の 1 つだけ
[Fact] public void New_day_clears_baseline()
```
  - `EnforceReleaseTests`: `AppendLimitRun` が `runs` に `{"at","action":"suspend","step":2}` を足し，`first_run_at` を 1 回だけ入れ，`events.log` に `enforce_run\tsuspend 2`．宣言が無い・`released_at` がある → 何も書かない．`duration_hours` などを足さない．
- [ ] **Step 2: 落ちるのを確かめる**
- [ ] **Step 3: 実装する**（上の順番のとおり．文は `m.Format("AGENT_LIMIT_…", …)`．キーは Task 9 で i18n に足すので，このタスクのテストは `Messages` を合成の辞書から作る — `MessagesTests` の作り方に合わせる）
- [ ] **Step 4: 全部通す** `dotnet test native`
- [ ] **Step 5: Commit** `feat(native): 減速・一時停止・解除の判断を Core に足す (#9)`

---

### Task 8: Agent の Windows 側（ジョブ・一時停止・フォルダの一覧）

**Files:**
- Create: `native/shiftlock.Agent/Platform/ILimitControl.cs`，`WindowsLimitControl.cs`，`IFolderLister.cs`，`WindowsFolderLister.cs`
- Test: `native/shiftlock.Agent.Tests/WindowsLimitControlTests.cs`，`WindowsFolderListerTests.cs`（新規）

**Interfaces:**
- Consumes: `ProcKey`，Task 1 の結果（ジョブを名前で開けるか・入れ子・`CpuRate` の単位）
- Produces:

```csharp
/// <summary>プロセスに手を加える唯一の場所．どれも例外を出さず，できなければ false．開始時刻が違うプロセスには触らない．</summary>
public interface ILimitControl
{
    /// <summary>procs をジョブ jobName に入れ（既に入っているものはそのまま），上限 percent（PC 全体 = 100）をかける．1 つでも入り上限がかかれば true．</summary>
    bool Cap(string jobName, IReadOnlyList<ProcKey> procs, double percent);
    /// <summary>ジョブの上限を外す．ジョブを持っていない・開けない → false．</summary>
    bool Uncap(string jobName);
    bool Suspend(ProcKey proc);
    bool Resume(ProcKey proc);
    /// <summary>同じ pid・同じ開始時刻のプロセスが今も在るか．</summary>
    bool Exists(ProcKey proc);
}

public interface IFolderLister
{
    /// <summary>dir の下を depth 階層まで見て，ファイルとフォルダの名前（パスでなく名前だけ）を返す．読めなければ null．maxEntries を超えたら打ち切って，それまでの分を返す．</summary>
    IReadOnlyList<string>? Names(string dir, int depth, int maxEntries = 20000);
}
```

- [ ] **Step 1: 失敗するテストを書く**（実機のテスト．対象は**テストが自分で起動した子プロセスだけ**．`cmd.exe /c pause` 相当か，テスト用に `ping -n 60 127.0.0.1` を `Process.Start` で起動し，`finally` で `Kill` する — テストの後片付けの `Kill` は製品コードではないので許される）
  - `Cap_then_uncap_own_child`: `Cap` が真 → `Uncap` が真．子は生きている．
  - `Cap_twice_changes_rate`: 同じジョブに 50.0 → 10.0 で両方真．
  - `Suspend_then_resume_own_child`: `Suspend` が真 → 子のスレッドが全部 `Suspended`（`Process.Threads` の `WaitReason`）→ `Resume` が真 → 動いている．
  - `Wrong_start_time_is_not_touched`: `new ProcKey(pid, 本当の開始時刻 + 1 分)` で `Suspend`・`Cap`・`Exists` が偽．子は止まっていない．
  - `Missing_pid_returns_false`: 終わった子の `ProcKey` で全部偽．例外なし．
  - `Uncap_unknown_job_returns_false`．
  - `WindowsFolderListerTests`: 一時フォルダに 4 階層作り，`depth: 3` で 3 階層目まで・4 階層目は入らない／`depth: 0` は直下だけ／無いフォルダ → `null`／`maxEntries: 2` で 2 件．
- [ ] **Step 2: 落ちるのを確かめる**
- [ ] **Step 3: 実装する**
  - `WindowsLimitControl`: 開くときの権限は `PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA | PROCESS_TERMINATE`（ジョブに入れる側．`AssignProcessToJobObject` が求める最小）と `PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SUSPEND_RESUME`（止める側）．**開いた後に `GetProcessTimes` で開始時刻を確かめ，違えば何もしない**（比べ方は `NtProcessSource` が `ProcSample.Start` を作るときと同じ変換）．ジョブのハンドルは名前 → ハンドルの辞書で Agent が生きている間持つ．`Uncap` は辞書に無ければ Task 1 の結果に従う（名前で開けるなら `OpenJobObjectW`，開けないなら false）．`CpuRate` = `percent × 100` を 1〜10000 に収める．`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` などほかの制限は付けない．`Dispose` でハンドルを閉じる（上限は外さない．外すのは呼ぶ側）．
  - `WindowsFolderLister`: `Directory.EnumerateFileSystemEntries` を階層ごとに．`UnauthorizedAccessException`・`IOException` は，根で起きたら `null`，奥で起きたらそのフォルダだけ飛ばす．リンク（再解析ポイント）はたどらない．
- [ ] **Step 4: 全部通す** `dotnet test native`
- [ ] **Step 5: Commit** `feat(native): ジョブの CPU 上限と一時停止の操作を Agent に足す (#9)`

---

### Task 9: Agent の 10 秒の仕事・起動時の再開・終了時と緊急解除・`--limit-once`

**Files:**
- Create: `native/shiftlock.Agent/Limiter.cs`，`native/shiftlock.Agent/LimitOnce.cs`
- Modify: `native/shiftlock.Agent/Meter.cs`（直近のサンプルを外から読めるようにする），`ReleaseFlow.cs`，`Program.cs`，`pc/shiftlock/i18n/ja.py`，`ja_kansai.py`（`AGENT_LIMIT_*`），`native/shared/messages.*.json`（書き出し直し）
- Test: `native/shiftlock.Agent.Tests/LimiterTests.cs`，`LimitOnceTests.cs`（新規），`MeterTests.cs`，`ReleaseFlowTests.cs`

**Interfaces:**
- Consumes: Task 6〜8 の全部，`ActionRunner.Run(TickResult, CancellationToken)`（通知を出す道），`Watcher.LoadConfig`，`Planner.TargetPlan`，`Store.LogRead("app", …)`，`OncePerDay`
- Produces:

```csharp
// Meter.cs に足す
public sealed record MeterSnapshot(MeterSample Sample, MeterSettings Settings, GameRules Rules, IReadOnlyDictionary<ProcKey, string?> Paths);
public MeterSnapshot? Latest { get; }   // 最後に取れた 1 回分．取れなかった回は null に戻す

public sealed class Limiter(string dir, Func<MeterSnapshot?> latest, ILimitControl control, IFolderLister folders,
    Action<IReadOnlyList<TickAction>> notify, Func<DateTime> clock, OncePerDay once)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan StaleSample = TimeSpan.FromSeconds(30);
    public void Startup();              // 起動時に 1 回．記録の一時停止を再開する
    public void RunOnce();              // 10 秒の仕事
    public void RestoreAll(string? reason);   // 終了時（null）・トレイの緊急解除（"kaijo"）
}
// ReleaseFlow: コンストラクタの最後に Action? afterRelease = null を足し，Record の後に呼ぶ（例外は握ってログ）
// LimitOnce.Run(string dir, …, TextWriter stdout, TextWriter stderr) -> int   // --limit-once
```

**文**（両方の言語．`{}` の名前はこのとおり）: `AGENT_LIMIT_NOTICE`（`{at}`・`{percent}`・`{n}`），`AGENT_LIMIT_NOTICE_SUSPEND`（`{at}`・`{n}`），`AGENT_LIMIT_SUSPENDED`（`{n}`），`AGENT_LIMIT_RELEASED`，`AGENT_LIMIT_SKIPPED`，`AGENT_LIMIT_FAILED`，`AGENT_LIMIT_BAD_RECORD`，`AGENT_LIMIT_STALE_CAP`（Task 1 で「名前で開けない」だったときだけ使う）．どの文にも解除の方法（`shl kaijo`・トレイ）を入れるのは予告の 2 つだけ．アプリの名前の入る `{}` は作らない．Task 7 のテストで使った仮の文と `{}` の名前が合っていること．

**`RunOnce` の流れ**（全体を 1 つのロックで包む．`RestoreAll`・`Startup` も同じロック）:

1. `Watcher.LoadConfig(dir)` → 読めなければ `Apps = null` で `Decide`（戻す側）．
2. `latest()` が `null`，`Sample.Now` が `StaleSample` より古い，`Sample.HasParents` が偽 → `Apps = null`．
3. ゲームの判定: `Sample.Games` の各プロセスについて，`AntiCheat.RootOf(Rules, Paths[key])` → `null` なら `unknown`．根ごとに `folders.Names(root.Dir, root.Depth)` を**1 回だけ**呼んで結果を覚える（Agent が生きている間．`null` → `unknown`）．名前のどれかが `AntiCheat.Matches` → `anticheat`．ツリー（`ProcTree.OfName`）のプロセス名が当たっても `anticheat`．どちらでもなければ `target`．exe 名ごとにまとめ（1 つでも `anticheat` なら `anticheat`），`LimitFiles.UpdateGames` に渡す．
4. `LimitFiles.ReadState` → `InvalidDataException` なら `QuarantineState` して `AGENT_LIMIT_BAD_RECORD` を 1 回通知し，`Empty` で続ける（Review Focus 1）．
5. `LimitTargets.Resolve` → `LimitPolicy.Decide`．
6. **`StateBeforeOps` を書く → 書けたら `Ops` を順に実行**．書けなければ `SuspendOp`・`CapOp` は実行しない（`UncapOp`・`ResumeOp`・`NoticeOp` は実行する）．
7. 実行: `CapOp` → `control.Cap`，`UncapOp` → `control.Uncap`，`SuspendOp` → `control.Suspend`，`ResumeOp` → `control.Resume`（`Exists` が偽なら成功と同じ扱いで記録から消す），`RunOp` → `Enforce.AppendLimitRun`，`ReleaseEventOp` → `store.AppendEvent(now, day, "limit_release", reason)`，`NoticeOp` → まとめて `notify`．
8. 失敗した操作を記録に戻す: `Suspend` が偽 → そのプロセスを `Suspended` から外す．`Cap` が偽 → `Capped` から外す．`Resume` が偽で `Exists` が真 → `Suspended` に残す（次の回にやり直す）．失敗は `once.Write`（`"limit-cap"`・`"limit-suspend"`・`"limit-resume"`）と，`AGENT_LIMIT_FAILED` を乗務ごとに 1 回（`Announced` に `"failed"`）．最後に記録を書く．
9. どこかで例外 → `once.Write("limit-run", "limit failed: " + 型の名前)`．外に出さない．

**`Startup`**: `ReadState`（壊れていれば 4 と同じ）→ `Suspended` の全部に `control.Resume`（`Exists` が偽なら記録から消すだけ）→ `Capped` の全部に `control.Uncap`（偽のものは，Task 1 の結果が「開けない」なら `AGENT_LIMIT_STALE_CAP` を 1 回）→ `Capped`・`Suspended`・`Announced`・`Skipped` を空にして書く（`Baseline`・`Day` は残す）．その後の `RunOnce` が，宣言が有効なら今の段階からかけ直す．

- [ ] **Step 1: 失敗するテストを書く**（`ILimitControl`・`IFolderLister` を記録する偽物に差し替え，合成データのフォルダ（一時フォルダ）に `shifts.json`・`state.json`・`log/app-*.jsonl` を置く）
  - `Startup_resumes_recorded_suspended`／`Startup_skips_reused_pid`（`Exists` 偽 → `Resume` を呼ばず記録から消す）／`Startup_quarantines_broken_record`（`limit.json` が `"{"` → `.bad` ができ，通知 1 つ，落ちない）／`Startup_keeps_baseline`．
  - `RunOnce_writes_record_before_suspending`（偽物の `Suspend` が呼ばれた時点で `limit.json` にその pid が載っている）／`RunOnce_does_not_suspend_when_record_cannot_be_written`（`limit.json` を別ハンドルで掴んでおく → `Suspend`・`Cap` が呼ばれない）．
  - `RunOnce_failed_suspend_is_removed_from_record`／`RunOnce_failed_cap_continues_with_other_apps`（1 つ目の `Cap` が偽でも 2 つ目が呼ばれる）／`RunOnce_control_throws_does_not_escape`．
  - `RunOnce_stale_sample_restores`（35 秒前のサンプル・`Capped` あり → `Uncap`）／`RunOnce_no_parents_restores`／`RunOnce_bad_config_restores`．
  - `RunOnce_scans_game_folder_once`（2 回呼んでも `Names` は根ごとに 1 回）／`RunOnce_marks_anticheat_game_and_writes_limit_games`（`Names` が `["EasyAntiCheat"]` → `Cap` が呼ばれず，`limit-games.json` に `anticheat`）．
  - `RunOnce_appends_run_and_event`（`state.json` の `runs` と `events.log`）．
  - `RestoreAll_uncaps_resumes_and_clears`／`RestoreAll_null_reason_writes_no_event`．
  - `ReleaseFlowTests`: `afterRelease` が `Record` の後に 1 回呼ばれる／`afterRelease` が例外でも `RunDetailed` は結果を返す．
  - `MeterTests`: `Latest` が取れた回の後に非 null，設定が読めない回の後に null．**既存のテストが変わらず通る**．`Limiter` が例外を出しても `Meter.RunOnce` の結果（書かれる行）が変わらない（別の仕事なので，`Limiter` を作らずに確かめられる）．
  - `LimitOnceTests`: 本番のフォルダ（`AgentPaths` の既定）を断って終了コード 2（`TickOnce` の断り方に合わせる）／合成のフォルダで「すること」の JSON が出て，偽物の `control` が 1 回も呼ばれない．
- [ ] **Step 2: 落ちるのを確かめる**
- [ ] **Step 3: 実装する**
  - `Program.RunAgent`: `var control = new WindowsLimitControl();` → `var limiter = new Limiter(dir, () => meter.Latest, control, new WindowsFolderLister(), actions => runner.Run(new TickResult(actions, [], 音量, 秒数), quit.Token), clock, once);`（音量と秒数は `TestNotify` と同じ読み方．通知音は付けない）→ `limiter.Startup()` を `scheduler.Start()` の前に → `Scheduler` に `new Job("limit", Limiter.Interval, Limiter.Interval, limiter.RunOnce)` → `ReleaseFlow` に `afterRelease: () => limiter.RestoreAll("kaijo")` → `finally` で `scheduler.Dispose()` の後・`meter.Flush()` の前に `limiter.RestoreAll(null)`．
  - `Main` の `switch` に `"--limit-once" => RunLimitOnce()`．クラスの先頭のコメントの引数の一覧にも足す．
  - 文を両方の言語に足し，`python3 pc/tools/export_native.py` で書き出し直す．
- [ ] **Step 4: 全部通す** `dotnet test native`，pytest（`test_export_native.py`・`test_i18n_lang.py`）
- [ ] **Step 5: Commit** `feat(native): Agent が宣言した乗務で対象のアプリを減速・一時停止し，解除で戻す (#9)`

---

### Task 10: 文書・個人データの一覧・合成の対象での通し確認

**Files:**
- Modify: `CLAUDE.md`（設計書 §8 のとおり「絶対」を書き換える・個人データの一覧に 3 ファイルと `limit.json.bad`），`.gitignore`，`tools/publish-public.sh`（個人データの名前の確認の対象），`docs/pc-spec.md`，`docs/pc-v11-design.md`，`docs/native-app-plan.md`（区切りの表），`docs/native-agent-worklog.md`（区切り 4 の節），`native/README.md`（`--limit-once`），`README.md`，`pc/shifts.example.json`（`limit` は `steps`・`baseline_minutes`・`apps`・`exclude` だけ例に書く）

- [ ] **Step 1: 文書を直す**（`pc-spec.md`: 強制モードの節に 2 つの動作と段階の表，hook の処理順に印，`shl app limit`，`shl help` の一覧）
- [ ] **Step 2: 通し確認（合成のフォルダ・合成の対象）**
  1. 一時フォルダに合成の `shifts.json`（`game_processes: ["burn"]`，`steam_libraries: false`），`state.json`（今日の乗務に `{"action": "suspend", "consented_at": …, "steps": [75,50,25,10], "limit_apps": [], "runs": []}` を手で書く．**`shl enforce on` は実行しない**）を置く．`soft_close`〜`hard_close` が「今から 3 分後〜11 分後」になるよう時刻の設定を合わせる．
  2. `SHIFTLOCK_DIR` をそのフォルダ，`SHIFTLOCK_GUARD_TASK=shiftlock-guard-test` にする．本番の Agent が動いていると固定名の Mutex で試験の Agent が起動できない（区切り 3 で分かっている）．**そのときは本人に本番の Agent を一時的に止めてもらうか，この確認を本人に回す**（Claude Code から `--stop` を実行しない）．
  3. Task 1 の `burn` を起動 → `--limit-once` で「すること」を見る → Agent を起動して，`limit.json` の `capped` が段階ごとに変わること・`hard_close` で `burn` が止まること・`STOP.txt` を置くと 10 秒以内に戻ること・`events.log` に `limit_release\tkaijo` が出ることを見る．
  4. もう一度かけ直した状態で Agent を強制終了 → `burn` が止まったままなのを見る → Agent を起動 → 再開されることを見る．
  5. `burn` は exe のパスがゲームのフォルダの外なので，`game_processes` の名前でゲームになり，根は exe のフォルダ．そこに `EasyAntiCheat` という空のフォルダを作ると，対象から外れて `limit-games.json` が `anticheat` になることを見る（Agent を起動し直してから）．
- [ ] **Step 3: 結果を `docs/native-agent-worklog.md` に書く**（確かめたこと・確かめられなかったこと・本人に残ること: 本番への反映，本番での宣言，Steam のゲームでの入れ子のジョブ，レポートの表示）
- [ ] **Step 4: 全部通す**（`dotnet test native`，pytest，`python3 pc/tools/export_native.py --check`）
- [ ] **Step 5: Commit** `docs: 区切り 4（減速・一時停止）の仕様と決まりを文書に反映する (#9)`
- [ ] **Step 6: issue #9 のチェックリストを更新するコメントを書く**（対応したコミット，本人に残ること）．クローズはしない（本番での確認が残る）．
