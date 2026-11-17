# PC 版リファクタ（モジュール分割と i18n）実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `pc/shiftlock.py`（3085 行）を役割ごとの 24 モジュールに分け，ユーザー向けの文面を `i18n/ja.py` に集め，機能・CLI・hook の入出力・ファイル形式・既存テストを一切変えない．

**Architecture:** エントリ `pc/shiftlock.py` は残して数行にし，実体はパッケージ `pc/shiftlock/`（同名ならパッケージが優先される）．`__init__.py` が全公開名を再公開するので `import shiftlock as sl` のテストは無変更で通る．分割は「移動だけ」を AST 比較で，i18n は「文面が同じ」を旧版との出力比較で証明する．

**Tech Stack:** Python 3.8+ 標準ライブラリのみ，pytest（WSL の `.venv`），PowerShell 5.1（`deploy.ps1` は UTF-8 BOM + CRLF）．

**Spec:** [docs/superpowers/specs/2026-11-04-pc-refactor-modules-design.md](../specs/2026-11-04-pc-refactor-modules-design.md)

## Global Constraints

- ベースは `fix/v11-review`．ブランチ `refactor/split-modules`，worktree `../worktrees/refactor/split-modules`．
- 関数名・定数名・引数・戻り値・ロジックは変えない．変えてよいのは設計書 §6 の 5 箇所だけ．
- hook は失敗時に exit 2 を返さない（CLAUDE.md）．
- `*.py` は UTF-8・LF・標準ライブラリのみ．`*.ps1` は UTF-8 BOM・CRLF．
- テストは WSL で `cd pc && ../.venv/bin/python -m pytest -q tests`．コミット前に全通過．
- 本番フォルダ・`~/.claude/settings.json`・タスクには触らない．push・PR・issue は本人が起きてから．
- コミットは日本語 `<型>(スコープ): タイトル`．`Co-Authored-By` 等の行は付けない．

## Review Focus

1. 古い hook ディレクトリ（パッケージ無し）に新しいエントリだけがある状態で hook が呼ばれる → `ImportError` を exit 1 で返し，プロンプトは通る（exit 2 にしない）→ Task 2 `test_entry_without_package_exits_1`．
2. `install()` を hook ディレクトリ内のエントリから実行（コピー元 == コピー先）→ 自分のパッケージを `rmtree` で消さない → Task 2 `test_install_from_hooks_dir_keeps_package`．
3. `install()` を 2 回実行したとき，前回あって今回は無いモジュール（stale）が hook ディレクトリに残らない → Task 2 `test_install_removes_stale_modules`．
4. i18n の定数にプレースホルダの綴り違いがあると `KeyError` で hook が落ちる → Task 4 `test_i18n_placeholders_all_resolve`．
5. 各モジュールを単独で import したときに循環 import で落ちる（`hook` だけ通っても `settings_menu` 単独で落ちる等）→ Task 2 `test_each_module_imports_alone`．

---

### Task 1: パッケージへの分割（移動のみ）

**Files:**
- Create: `pc/shiftlock/__init__.py`，`pc/shiftlock/i18n/__init__.py`（空），`pc/shiftlock/i18n/ja.py`（空），設計書 §4 の 24 モジュール
- Modify: `pc/shiftlock.py`（エントリに置き換え），`pc/tests/test_shiftlock.py:878,881,886`（`sl.windows_confirm` → `sl.enforce.windows_confirm`）
- Test: `pc/tests/test_shiftlock.py`（既存 81 件）

**Interfaces:**
- Produces: パッケージ `shiftlock`．`shiftlock.__all__` に全公開名．`shiftlock.enforce.ENFORCE_ACTIONS: tuple[str, ...]`．`shiftlock.install.install()` はパッケージもコピーする．

- [x] **Step 1: 割り当て表（設計書 §4）どおりに各モジュールを書く．** 関数本体は元のまま．各モジュールの先頭は `from __future__ import annotations` と必要な標準ライブラリ，相対 import（`from .store import _read, _write` のように名前を列挙）．`from .x import *` は使わない．（実際は手で転記せず，行範囲と割り当て表から切り出すスクリプトで生成した．直前のコメント行・デコレータも一緒に運ぶ．import 行は各定義が参照する名前から機械的に作った）
- [x] **Step 2: `__init__.py` を書く．** 各モジュールから公開名を `from .config import (...)` の形で列挙し，`__all__` に並べる．`import shutil` も書く．（156 名）
- [x] **Step 3: `shiftlock.py` をエントリにする．** `try: from shiftlock.cli import main / except ImportError: stderr に案内して sys.exit(1)`．`if __name__ == "__main__": sys.exit(main())`．
- [x] **Step 4: AST 比較（`install()` を直す前に）** Run: 一時フォルダの `equiv_ast.py <旧 shiftlock.py> pc/shiftlock/` Expected: 179 定義すべて一致．欠け・重複・差異なし → **結果: old=179 new=179 missing=0 extra=0 differ=0**
- [x] **Step 5: `install()` を直す（設計書 §6-1）．**
- [x] **Step 6: テストの 3 行を直す（設計書 §6-3）．**
- [x] **Step 7: テスト実行** Run: WSL で `cd pc && ../.venv/bin/python -m pytest -q tests` Expected: `81 passed` → **結果: 81 passed**（Step 5 の前は `test_cli_shell_roundtrip` だけ落ちた．パッケージ未コピーのため．想定どおり）
- [ ] **Step 8: Commit** `refactor(pc): shiftlock.py を役割ごとのモジュールに分割`

### Task 2: パッケージ構成の整合テスト

**Files:**
- Create: `pc/tests/test_package.py`

**Interfaces:**
- Consumes: Task 1 のパッケージ，`shiftlock.install.install(cfg_path, settings_path, hooks_dir)`．

- [x] **Step 1: テストを書く**
  - `test_all_public_names_reexported`: 各サブモジュール（`i18n` 以外）の `_` で始まらないモジュールレベルの関数・クラス・定数（**AST で定義名を絞る**．他から import しただけの名前は数えない）が `shiftlock.__all__` に入っていて `getattr(shiftlock, name)` が同一オブジェクト．モジュールは `importlib.import_module` で取る（`install` `settings_menu` は同名の関数に隠れるため）．
  - `test_no_import_cycles`: 各モジュールの相対 import を AST で集めて DFS．循環があれば経路を出して落とす．
  - `test_each_module_imports_alone`: 各モジュール名について `subprocess.run([sys.executable, "-c", "import shiftlock.<m>"], cwd=ROOT)` が returncode 0．
  - `test_install_copies_package_and_removes_stale`: 一時 `hooks/` に `shiftlock/stale.py` と `__pycache__` を置いてから `install()` → `hooks/shiftlock/stale.py` と `__pycache__` が無く，`hooks/shiftlock/cli.py` と `i18n/ja.py` がある．コピー先の `shiftlock.py status` が動く．
  - `test_install_from_hooks_dir_keeps_package`: `install()` 後，コピー先の `shiftlock.py` を `subprocess` で `install-hook --config … --settings … --hooks-dir <同じ hooks>` 実行 → returncode 0 でパッケージが残っている．
  - `test_entry_without_package_exits_1`: 一時フォルダに `shiftlock.py` だけコピーして `subprocess` で `hook` を実行 → returncode 1，stderr に `shiftlock/`，stdout 空．
- [x] **Step 2: 実行して失敗を確認** → 分割直後は `test_cli_shell_roundtrip` が落ち，`install()` 修正後に通った
- [x] **Step 3: 実装を直す**（Task 1 の Step 3・5 で済み）
- [x] **Step 4: テスト実行** Expected: 全通過（81 + 5）→ **結果: 86 passed**
- [x] **Step 5: Commit** `test(pc): パッケージ構成の整合テストを追加`
- [x] **Step 6（レビュー後に追加）:** i18n の検査 3 本（`test_i18n_templates_use_named_placeholders_only` `test_i18n_format_calls_match_templates` `test_i18n_constants_are_all_used`）と循環検査を足して Task 4 のコミットに含める

### Task 3: `deploy.ps1` でパッケージのフォルダも配る

**Files:**
- Modify: `pc/deploy.ps1:15-28`

- [x] **Step 1: `$dirs = @('shiftlock')` を足し，`Get-ChildItem -Recurse -File` で `__pycache__` を除いてコピー**（`-DryRun` は `copy shiftlock/<相対パス>` を表示）．コピー先に無くなったモジュールが残らないよう，コピー前に `Join-Path $Target 'shiftlock'` を消す（`shifts.json` 等のデータは別の場所なので触らない）．
- [x] **Step 2: 構文チェック** Run: `powershell -NoProfile -Command "[System.Management.Automation.Language.Parser]::ParseFile('pc/deploy.ps1', [ref]$null, [ref]$e); $e.Count"` Expected: `0` → **0**
- [x] **Step 3: DryRun** Run: `powershell -ExecutionPolicy Bypass -File pc\deploy.ps1 -DryRun -Target <一時フォルダ>` Expected: `copy shiftlock.py` と `copy shiftlock/cli.py` 等が出て，`__pycache__` が出ない → **27 ファイル列挙，`__pycache__` なし，Target は空のまま**
- [x] **Step 4: BOM と CRLF を確認** Run: `file pc/deploy.ps1` Expected: `UTF-8 Unicode (with BOM) text, with CRLF line terminators` → **OK**
- [x] **Step 5: Commit** `chore(pc): deploy.ps1 でパッケージのフォルダも配る`
- [x] **Step 6（レビュー後に追加）:** パッケージを `$files` より先にコピー，`$Target` が `pc/` 自身なら throw，`-SkipHook` を追加，`install-task.ps1` の存在確認に `__init__.py`．`-SkipHook -Target <一時フォルダ>` で本実行 → **18 + 28 ファイル，`__pycache__` 0，2 回目で `stale.py` が消える，コピー先で `status` が動く，`pc/` 自身は throw**．Task 4 のコミットに含める

### Task 4: メッセージの分離（i18n）

**Files:**
- Modify: `pc/shiftlock/i18n/ja.py`，`pc/shiftlock/i18n/__init__.py`，文面を持つ全モジュール
- Test: `pc/tests/test_package.py`（`test_i18n_placeholders_all_resolve` を追加）

**Interfaces:**
- Produces: `shiftlock.i18n.PREFIX = "[shiftlock] "` と設計書 §5 の命名の定数．

- [x] **Step 1: 旧版との出力比較スクリプトを一時フォルダに用意**（旧 `shiftlock.py` を `importlib` で別名ロード．旧版・新版を**別々の**一時環境（同じ初期内容）・同じ `now` で呼び，文字列を比較）．対象: `run_hook`（キーワード 15 種・話し言葉・聞き返し・ハード中・lastcall/soft・事後報告・出発），`run_tick`（lastcall/soft/hard/nag/confirm/auto_okita/enforce 予告），`status_text`，`kakunin_text`，`schedule_text`，`late_report` の失敗系，`enforce_terms` `enforce_status_text` `enforce_declare`（FakeIO），`render_report`（mood あり／なし，unconfirmed，enforce），`settings_menu`（FakeIO で全メニューを 1 周），`HELP_TEXT`，`sync_calendar` の文，`cleanup` の表示，`main(["learn", …])` 等の print．レビュー後に足したもの: 各シナリオ後のデータファイル（json/log/csv/html/txt）の比較，`main` の `datetime.now()` の固定，`sys.settrace` で「関数の中の `M.` 参照を含む文」の実行カバレッジ．
- [x] **Step 2: 分割直後の版で比較スクリプトを走らせ，全一致を確認**（スクリプト自体の妥当性）→ **134/134 一致**
- [x] **Step 3: `ja.py` を書き，モジュールごとに文面を置き換える**（領域の順: plan → records → confirm → enforce → commands → hook → tick → phone → autowake → days → calendar_sync → maintenance → console → settings_menu → report_html → cli → install．timeutil の `fmt_dur` と config の設定エラー文も）．`ENFORCE_ACTIONS` `CAUTION_ACTIONS` を足して判定を差し替え，`schedule_text` のメモの出所判定を `is_workday` の結果に，同じ文は `COMMON_` に集約．
- [x] **Step 4: i18n の検査を書く**: `test_i18n_templates_use_named_placeholders_only`（`string.Formatter().parse` で名前付きだけ・書式指定なし），`test_i18n_format_calls_match_templates`（AST で全 `M.X.format(...)` の引数名の集合＝テンプレの名前の集合），`test_i18n_constants_are_all_used`（`COMMON_` 以外は `M.X` でどこかから参照される）．Probe 方式（`format_map` + `__missing__`）は書式指定で落ちるので採らなかった．
- [x] **Step 5: テスト実行** Expected: 全通過 → **90 passed**
- [x] **Step 6: 出力比較を再実行** Expected: 全一致（違いがあれば文面を直す．旧版が正）→ **全一致**（シナリオ数と未実行行は作業記録）．残った日本語リテラル 139 件を AST で棚卸しし，全部が設計書 §5 の対象外に入ることを確認
- [x] **Step 7: Commit** `refactor(pc): ユーザー向けの文面を i18n/ja.py の定数に分離`（`install()` のコピー順・エントリの `sys.path`・テスト追加も同じコミット．`deploy.ps1` / `install-task.ps1` の分は `chore(pc): deploy.ps1 はパッケージを先に配り、pc/ 自身への上書きを拒否、-SkipHook を追加` で別コミット）

### Task 5: 文書

**Files:**
- Modify: `docs/pc-spec.md`（冒頭の「対象コード」表をモジュール一覧に），`pc/README.md`（セットアップ 1 と「動作要件」: `shiftlock/` フォルダも一緒に置く），`CLAUDE.md`（「ファイルの決まり」に文面は `i18n/ja.py` を 1 行）
- Create: `docs/pc-refactor-worklog.md`（作業記録: 結論・判断・検証結果・起きてからやること・PR と issue の下書き・気づいたこと）

- [x] **Step 1: 書く**（`pc-spec.md` は冒頭の表・§5.1・§5.6・§14・§16，`pc/README.md` はセットアップ 1 と未検証，`CLAUDE.md` はファイルの決まりに 1 行）
- [x] **Step 2: Commit** `docs(pc): モジュール構成とリファクタの作業記録`

### Task 6: レビューと最終確認

- [x] **Step 1: サブエージェント（sonnet）に差分レビュー**（同等性・import 構造・i18n の取りこぼし・命名）→ 指摘を直して commit（実装前の設計レビューは opus で済み．15 件を反映）→ **12 件．`deploy.ps1` のループの入れ子（私のパッチの貼り付けミス）など 10 件を直し，モジュール名の変更と `guard.ps1` の stderr は見送り（作業記録）．レビュー側が確認した「問題なし」: 分割の旧版一致，i18n の引数の対応 0 件取り違え，文面の逐語一致，保存値の漏れなし**
- [x] **Step 2: hook の経路確認**（設計書 §7）: 一時 hooks に `install()` → 生成された `command` を WSL の `sh -c` と Git Bash の `sh -c`（wsl.exe 経由）で実行 → **閉店: exit 2＋状況表示，x: exit 0＋JSON，Stop: exit 0，壊れた設定: exit 1，エントリだけ: exit 1（案内文），本体なし: exit 0**
- [x] **Step 3: 全テストと出力比較を最後にもう一度**（AST 比較は分割コミット時点で固定済み．i18n 後は使わない）→ HEAD（docs コミットまで）で **90 passed，185/185 一致，未実行 2 行**
- [x] **Step 4: 作業記録に結果を追記して commit** → `docs(pc): 最終レビューの結果と対応を作業記録に追記`（最終状態: 93 passed，185/185 一致）
