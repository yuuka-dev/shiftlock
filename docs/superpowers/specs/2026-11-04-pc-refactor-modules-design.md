# PC 版リファクタ設計：`shiftlock.py` の役割別分割とメッセージの分離

依頼（2026-11-04 深夜，本人は就寝中）: 「`.py` がゴッドモジュールになっていて保守性が非常に悪い．各役割ごとにモジュールを分割して，現在実装している機能と同等の状態を維持してリファクタリングしてほしい．メッセージは専用の const（i18n）を用いて分離させる」．

この文書は寝ている間に設計側で決めたこと．**「（判断）」と書いた箇所は依頼に無かったので設計側で決めたもの．違ったら直す．**

## 0. 現状

- `pc/shiftlock.py` は **3085 行**（v10 で 2192 行，v11 の 5 機能で +1141 行）．hook・tick・CLI・レポート・設定メニュー・カレンダー同期・事後報告・強制モードが 1 ファイルに入っている．
- 定義は 179 個（関数 134・クラス 3・モジュール定数 42）．日本語の文字列リテラルは 706 箇所．
- テストは `pc/tests/test_shiftlock.py` の 81 件（`fix/v11-review` 時点，WSL の `.venv` で全通過）．すべて `import shiftlock as sl` → `sl.関数名` の形で呼んでいる．

## 1. ベースにするブランチ（判断）

**`fix/v11-review` から `refactor/split-modules` を切る．**

- 本番フォルダ `%USERPROFILE%\shift-lock\shiftlock.py` と WSL の `~/.claude/hooks/shiftlock.py` は，どちらも `fix/v11-review` の版（162,447 バイト）と一致している．「現在実装している機能」= この版．
- `dev` は v10（105,528 バイト）で，v11 の積み上げ式 PR（#6〜#13）はまだマージされていない．`dev` を分割すると，その上に積まれた 5 本分の差分（1141 行）が 1 ファイルへの差分のまま取り残されて戻せなくなる．
- `fix/v11-review` の子孫にしておけば，#6〜#13 を順にマージした後に本ブランチの PR（base = `fix/v11-review`）をそのままマージできる．

## 2. 変えないもの（互換の約束）

| 対象 | 扱い |
|---|---|
| `pc/shiftlock.py` の名前と場所 | 残す．hook コマンド・`*.cmd`・`shiftlock-profile.ps1`・`guard.ps1`・`make-report.ps1`・`deploy.ps1`・`install-task.ps1`・`MARKER` が全部この名前を指している |
| CLI のサブコマンドと引数 | 変えない |
| hook の入出力（exit 0/1/2，stdout JSON，stderr） | 変えない．失敗時に exit 2 にならない決まりもそのまま |
| tick の JSON（`actions` / `warnings` / `sound`） | 変えない |
| ファイル形式（[data-format.md](../../data-format.md)，`state.json`，`events.log` …） | 変えない |
| 関数名・定数名・引数・戻り値 | **変えない**．改名はしない（改名したくなったら別 PR） |
| ロジック | **変えない**．移動だけ．直したいバグを見つけても直さず記録に残す |
| `import shiftlock as sl` → `sl.X` | 動く（§4） |
| テスト `pc/tests/test_shiftlock.py` | **無変更で全通過させる**．例外は §6 の 3 行だけ |

## 3. 配置

```
pc/
  shiftlock.py                 エントリ（数行．from shiftlock.cli import main）
  shiftlock/                   パッケージ
    __init__.py                公開 API の再公開（__all__ に全部列挙）
    i18n/__init__.py           言語の選択（今は ja だけ．from .ja import *）
    i18n/ja.py                 関西弁のメッセージ定数
    config.py  timeutil.py  store.py  keywords.py  console.py          … 基盤
    plan.py  usage.py  autowake.py  intents.py  confirm.py  enforce.py  notify.py  days.py   … 判断
    records.py  commands.py  phone.py                                   … 記録と応答
    hook.py  tick.py  cli.py                                            … 入口
    report.py  report_html.py  calendar_sync.py  maintenance.py  settings_menu.py  install.py  … 出力・保守
  tests/test_shiftlock.py      既存（無変更）
  tests/test_package.py        追加（構成の整合）
```

- 同じディレクトリに `shiftlock.py` と `shiftlock/` が同居するが，Python は **同名ならパッケージを優先**する（Windows の 3.12 と WSL の 3.12 で実測済み）．`python3 shiftlock.py …` で起動すると `sys.path[0]` がそのディレクトリになり，中の `import shiftlock` はパッケージを取る．
- （判断）パッケージ名を `shiftlock` にした理由: 本番フォルダ `shift-lock\` に `shiftlock\` があるのが一番分かりやすい．`shiftlock_core` のような別名は，エントリとの関係が名前から読めない．

## 4. モジュール一覧

依存は上から下へ一方向（循環なし．割り当てを AST の参照関係で機械検査済み）．

| 層 | モジュール | 役割 | 主な関数・定数 | import する相手 |
|---|---|---|---|---|
| 基盤 | `config` | 設定の既定値と読み書き，例外日のマージ，Windows パスの変換 | `WEEKDAYS` `DEFAULTS` `deep_merge` `load_raw` `save_raw` `calendar_sources` `calendar_days` `merged_days` `load_config` `to_local_path` | store |
| | `timeutil` | 時刻の整形・日付の解釈・区間の引き算 | `at` `fmt` `fmt_d` `fmt_dur` `WD_JA` `parse_day` `_clamp_day` `_subtract` | – |
| | `store` | JSON の読み書き，`Store`，記録ログ | `_read` `_write` `Store` `set_mark` `TICK_MIN` `log_append` `log_read` `first_log_date` | – |
| | `keywords` | hook の固定キーワードと日付コマンドの正規表現（入力の語彙） | `KW_*` `RE_DAY` `RE_PAST` `PAST_YASUMI` `PAST_WORKED` | – |
| | `console` | 対話入力の部品（設定メニューと強制モードの同意で共用） | `WD_IN` `WD_OUT` `_wd_str` `_ask` `_hm_conv` `_num_conv` `_wd_conv` `StdIO` `_list_edit` | config |
| | `i18n` | ユーザー向け・Claude 向けの文面（§5） | `ja.py` の定数 | – |
| 判断 | `plan` | 乗務日と時刻の逆算，フェーズ判定 | `no_sleep_intervals` `latest_bedtime` `Plan` `is_workday` `day_times` `make_plan` `plans` `current` `target_plan` `last_past_plan` `phase_messages` | config timeutil |
| | `usage` | 連続稼働と離席区間 | `touch_usage` `due_nag` `recent_long_gap` `pc_gaps` | store |
| | `autowake` | 自動『起きた』 | `slept_before` `maybe_auto_okita` | plan store timeutil usage |
| | `intents` | 話し言葉の判定・聞き返し・学習 | `LABEL` `ASK_LABEL` `norm_text` `norm_kw` `phrase_core` `INTENT_PATTERNS` `NEGATE` `WEAK` `REQUEST` `AFFIRM` `DENY` `load_learned` `learn` `unlearn` `learned_text` `detect_intents` `weak_intent` `can_ask` `set_pending` `take_pending` `clear_pending` | store |
| | `confirm` | 終わった乗務の「出た？休んだ？」 | `last_asked_unconfirmed` `unconfirmed_shifts` `mark_confirm_asked` `can_ask_confirm` `confirm_since` `pick_unconfirmed` `home_activity_minutes` `confirm_text` `kakunin_text` | plan store timeutil |
| | `enforce` | 強制モード（宣言・同意・発動・解除） | `STOP_FILE` `ENFORCE_ACTIONS` `stop_active` `enforce_state` `set_enforce` `enforce_terms` `windows_confirm` `enforce_declare` `_declared` `enforce_off` `enforce_release` `enforce_resume` `enforce_suffix` `enforce_status_text` `enforce_due` | console notify plan store timeutil |
| | `notify` | 通知の送信履歴と通知音 | `NOTIFY_EVERY` `SOUND_PRIORITY` `_once` `resolve_sound` `attach_sounds` | config store |
| | `days` | 手動の例外日と 2 週間表 | `days_set` `schedule_text` | plan store timeutil |
| 記録と応答 | `records` | 自己申告の記録（帰宅・就寝・起床・出発・事後報告）と取り消し | `answer_pending` `apply_intents` `undo_fuzzy` `do_kitaku` `do_neru` `do_okita` `do_shuppatsu` `resolve_past_day` `_late_report` `late_report` `cmd_past` `month_yasumi_count` | confirm days enforce intents keywords plan store timeutil usage |
| | `commands` | キーワード応答と状況表示 | `cmd_day` `cmd_keyword` `status_text` | config confirm days enforce keywords plan records store timeutil |
| | `phone` | iPhone の記録（mood.csv）の読み取りと取り込み | `PHONE_MARKS` `read_phone_marks` `import_phone_marks` `MOOD_LABEL` `MOOD_GLYPH` `mood_str` `read_mood` | config plan records store timeutil |
| 入口 | `hook` | Claude Code の hook（処理順は [pc-spec.md](../../pc-spec.md) §5.3） | `run_hook` | 上の全部 |
| | `tick` | 5 分おきの見張り | `run_tick` | 上の全部 + maintenance |
| | `cli` | サブコマンドの配線 | `HELP_TEXT` `main` | 全部 |
| 出力・保守 | `report` | レポートの集計と締め期間 | `_gaps_from_ticks` `closing_period` `build_report` `report_name` `write_period_reports` `cleanup_reports` | confirm phone plan report_html store timeutil |
| | `report_html` | レポートの HTML | `_hm` `_h1` `render_report` | phone |
| | `calendar_sync` | iCal の解析と同期 | `_ics_unfold` `_ics_dt` `parse_ics` `_http_get` `sync_calendar` | config store |
| | `maintenance` | 日次の整理 | `cleanup` `daily_maintenance` | calendar_sync report store |
| | `settings_menu` | 設定メニュー | `settings_menu` `games_menu` `calendar_menu` `enforce_menu` `SOUND_KINDS` `sound_menu` `learned_menu` `days_menu` | calendar_sync config console days enforce intents store timeutil |
| | `install` | hook の登録 | `MARKER` `HOOK_EVENTS` `hook_command` `merge_hooks` `install` | – |

元のファイルでの置き場所から動かしたもの（役割に合わせた）: `first_log_date`（report → store．`confirm` が使うため），`to_local_path`（report → config），`_subtract`（report → timeutil），`closing_period` と `_clamp_day`（report と timeutil），`norm_kw`（hook の近く → intents），`learned_text` `unlearn`（設定メニューの近く → intents），`WD_IN` `WD_OUT`（設定メニュー → console．`_wd_conv` が使うため），`last_past_plan`（records → plan）．

表に無い名前の置き場所: `to_hira` `ASK_COOLDOWN` `_EDGE` `_P`（intents），`_phone_okita_pending`（phone），`_log_free`（records）．`ENFORCE_LABEL` `ENFORCE_CAUTION`（enforce），`LABEL` `ASK_LABEL`（intents），`MOOD_LABEL` `MOOD_GLYPH`（phone），`SOUND_KINDS`（settings_menu），`HELP_TEXT`（cli）は**名前を元のモジュールに残し，値だけ i18n から取る**（`ENFORCE_LABEL = M.ENFORCE_LABEL` の形．`sl.ENFORCE_LABEL` などの公開名は変わらない）．

サブモジュールと同名の関数（`install`，`settings_menu`）があるので，`__init__` が再公開したあとの `shiftlock.install` は関数．モジュールが要るときは `importlib.import_module("shiftlock.install")` で取る（`shiftlock.enforce` のように同名の関数が無いものは属性でモジュールが取れる）．

`__init__.py` は上の全部の公開名（`_` で始まらないもの）と，テストが使う `_` 付きの名前は無い，を `__all__` に列挙して再公開する．`import shutil` も `__init__` に書く（テストが `sl.shutil.which` を差し替えるため．`shutil` はモジュールが共有されるので差し替えは全モジュールに効く）．

## 5. メッセージの分離（i18n）

- 置き場所: `pc/shiftlock/i18n/ja.py`．`i18n/__init__.py` は `from .ja import *` だけ（言語を足すときはここで切り替える．いまは関西弁 1 つ）．`i18n` は他のモジュールを import しない（`config` `timeutil` からも使うため）．
- 使い方: 各モジュールで `from . import i18n as M` → `M.PHASE_SOFT_USER.format(soft=fmt(p.soft_close), target_h=cfg["sleep_target_hours"], hard=fmt(p.hard_close))`．f-string は `str.format` の **名前付き**プレースホルダに置き換える．
- **プレースホルダは `{名前}` だけ**．書式指定（`{hours:g}` `{d:%m/%d}`）と属性参照（`{p.start_latest}`）はテンプレートに書かず，呼ぶ側で文字列にしてから渡す（`hours=f"{hours:g}"`）．`str.format` は余分な引数を黙って捨てるので，**全部の `M.X.format(...)` について「渡す名前の集合＝テンプレートの名前の集合」を AST で検査する**（`tests/test_package.py`）．
- 命名: `<領域>_<意味>[_USER|_CTX]`．領域はモジュール名に対応させる（`PHASE_` `HOOK_` `TICK_` `RECORDS_` `CONFIRM_` `ENFORCE_` `CMD_` `STATUS_` `MENU_` `REPORT_` `CLI_` `CAL_` `DAYS_` `INTENT_` `INSTALL_` `PHONE_` `AUTOWAKE_` `CONFIG_` `CONSOLE_` `NOTIFY_` `TIME_`）．`_USER` はユーザーに見せる文，`_CTX` は Claude に渡す文脈．同じ文が複数の領域にある（「24時間以内に乗務の予定はないで。」が records・enforce・commands，「日付が読めへん」「ええ？ (y/N)」「曜日ルールどおり」「（なし）」）ものは **`COMMON_` に 1 つ置き，領域ごとの名前から参照する**（`RECORDS_SHUPPATSU_NO_PLAN = COMMON_NO_PLAN`．片方だけ変えたくなったらそこで分ける）．
- `[shiftlock] ` の接頭辞は `M.PREFIX`．文面の中で `.replace("[shiftlock] ", "")` している 2 箇所（`apply_intents`，`import_phone_marks`）も `M.PREFIX` を使う．
- **対象**: ユーザーに表示する文（hook の stderr／`systemMessage`，tick の通知文と警告，CLI の `print`，argparse の `help`），Claude に渡す `additionalContext`，設定メニューの表示と入力プロンプト，レポート HTML の文言（タイトル・タイル・凡例・列見出し・注記・ツールチップ．タイルの値の `<small>` は文と一緒に持つ），`HELP_TEXT`，エラー文，`ENFORCE_LABEL` `ENFORCE_CAUTION` `LABEL` `ASK_LABEL` `MOOD_LABEL` `MOOD_GLYPH` `SOUND_KINDS` のラベル部分，`fmt_dur` の「n時間m分」，Windows ダイアログのタイトル．
- **対象外**（判断）:
  - 入力の語彙と正規表現: `KW_*` `RE_*` `PAST_*` `INTENT_PATTERNS` `NEGATE` `WEAK` `REQUEST` `AFFIRM` `DENY`，`PHONE_MARKS` のキー，`WD_IN` のキー，`parse_day` の `今日/明日/昨日/一昨日`．関数の中にある入力語も同じ: `main()` の `{"yasumi": "今日休む", …}`（サブコマンド → hook と同じキーワード），`cmd_keyword` の `"今日休む"` と `late_report(…, "今日")`，`run_hook` の `"行った"`，`_wd_conv` の `("なし", "-")`，`cmd_day` の `{"休み": "off", …}`，設定メニューの番号・記号の対応表．
  - **ファイルに書き込む値**: `"事後報告"`（`days.json` の出勤メモ．`schedule_text` が「（事後報告）」と表示もする），`events.log` のメモ（`shuppatsu` `phone` `late claude` `auto HH:MM-HH:MM` など．全部 ASCII），カレンダーのソース名 `"カレンダー"` と `f"カレンダー{n}"`（`shifts.json` / `calendar.json` のキー），`calendar.json` の `error` 文字列，`via`．
  - 設定の既定値（`off_keywords` の `休み` など），HTML/CSS/JS の構造，日付・時刻の表記（`%m/%d` `HH:MM` `WD_JA`．timeutil 側），`"—"` のような記号，`"shiftlock"` という製品名（通知のタイトル）．
  - 機械が読む出力: `FILE\t<パス>`（`make-report.ps1` が解析する）．
  - エントリ `shiftlock.py` の案内文（パッケージが読めへんときに出すので i18n に置けない．例外の内容 `{e}` を添える）．
  これらは「メッセージ」ではなく判定と保存の仕様なので，動かすとデータ互換や判定が変わる．残った日本語リテラル（docstring 以外）139 件が全部この一覧に入ることを AST で棚卸しした（作業記録）．
- 判定に使っていたキー集合は i18n 辞書から切り離す（§2 の「ロジックを変えない」の例外．集合は同じ）:
  - `ENFORCE_LABEL` のキーで action の妥当性を見ていた 2 箇所（`enforce_declare`，`main`）→ `enforce.ENFORCE_ACTIONS = ("lock", "sleep", "shutdown")`．
  - `ENFORCE_CAUTION` のキーで注意を出すか決めていた 2 箇所（`enforce_terms`，`enforce_menu`）→ `enforce.CAUTION_ACTIONS = ("sleep", "shutdown")`．
  - `schedule_text` の `mark == "休"`（メモの出所の判定に表示の文字を使っていた）→ `is_workday` の結果で分岐．

## 6. 移動以外に変える箇所（全部で 7 つ）

1. `install()`（`install.py`）: 今は `shutil.copy2(__file__, hooks_dir/shiftlock.py)` で 1 ファイルをコピーしている．パッケージになるので，**`shiftlock/` を `hooks_dir/shiftlock/` へコピーしてから，エントリ `shiftlock.py` を置く**（この順番．途中で止まっても古い 1 ファイル版のエントリはパッケージを読まないので動く）．パッケージの入れ替えは `shiftlock.new` に `copytree`（`__pycache__` と `.pyc` は除く）→ 既存を `shiftlock.old` に rename → `.new` を本名に rename → `.old` を削除，の順（途中で止まっても本名か `.old` のどちらかが残る．古いモジュールは残らない）．エントリの場所は `Path(__file__).resolve().parent.parent / "shiftlock.py"`（`install.py` の親の親）．hook ディレクトリから直接実行した場合（コピー元 == コピー先）は何もしない，は今と同じ．同一かどうかは `samefile` で見る（`/mnt/c` は大文字小文字を区別しないので文字列比較では綴り違いを別物と判断してしまう）．
2. `pc/deploy.ps1`: `shiftlock` フォルダを **`$files` より先に** `__pycache__` と `.pyc` を除いて再帰コピーする（サブフォルダは作ってからコピー）．コピー先の `shiftlock/` は無くなったモジュールが残らないよう丸ごと入れ替える（データは `$Target` 直下なので触らない）．`-DryRun` では削除も書き込みもせず一覧だけ表示．`$Target` がリポジトリの `pc/` 自身なら throw（自分を消さない）．**`-SkipHook`** を足して，`install-hook`（本物の `~/.claude/settings.json` を書く）を飛ばして一時フォルダへ本実行できるようにする．UTF-8 BOM・CRLF のまま．`pc/install-task.ps1` の存在確認に `shiftlock\__init__.py` を足す．
3. `pc/tests/test_shiftlock.py`: `sl.windows_confirm = …` の差し替え 3 行（878・881・886 行）を `sl.enforce.windows_confirm = …` に．理由: `enforce_declare` は自分のモジュールの `windows_confirm` を呼ぶので，パッケージの属性を差し替えても効かない．**これ以外は無変更**．
4. `ENFORCE_ACTIONS` と `CAUTION_ACTIONS`（§5）．
5. `schedule_text` のメモの出所判定を `is_workday` の結果で行う（§5）．
6. エントリ `shiftlock.py`: 自分のフォルダを `sys.path` の先頭に明示的に入れる（`python -P` や `PYTHONSAFEPATH` で起動されても同名パッケージを読めるように）．パッケージが見つからないとき（古い hook ディレクトリに新しいエントリだけコピーされた等）は `ImportError` を捕まえて stderr に「`shiftlock/` フォルダが読めへん（例外の内容）．`deploy.ps1` / `install-hook` をやり直して」と出して **exit 1**（exit 2 にしない．CLAUDE.md の決まり）．
7. `__init__.py` に新しい公開名 `ENFORCE_ACTIONS` `CAUTION_ACTIONS` を足す．

## 7. 検証（同等性の証拠）

| 何を | どうやって |
|---|---|
| 既存テスト 81 件 | WSL の `.venv` で全通過（`cd pc && ../.venv/bin/python -m pytest -q tests`） |
| 分割が「移動だけ」であること | 旧 `shiftlock.py` と新パッケージ全モジュールの top-level 定義を AST で比較（docstring 除く）．**分割のコミット時点（i18n の前，`install()` を直す前）で全一致**を確認して固定する．i18n 後は関数本体が `M.X.format(…)` に変わるので AST 比較は使わない．スクリプトは一時フォルダ（リポジトリに入れない），結果は作業記録に書く |
| i18n が文面を変えていないこと | 旧版（`git show fix/v11-review:pc/shiftlock.py`）を別名で読み込み，**旧版と新版を別々の一時フォルダ（同じ初期内容）で**同じ入力で動かし，`run_hook` `run_tick` `status_text` `render_report` `settings_menu`（FakeIO で全メニューを 1 周）`enforce_*` `kakunin_text` `schedule_text` `late_report` `sync_calendar` `cleanup` `main`（全サブコマンドと `--help`）の戻り値に加えて，**各シナリオ後のデータファイル（`state.json` `events.log` `days.json` `calendar.json` `learned.json` `notified.json` `shifts.json` `mood.csv` `log/*.jsonl` `reports/*.html`）の中身も比較**する（保存値に i18n が漏れていない証拠）．`main` の `datetime.now()` は両方とも固定時刻に差し替える．新版の実行行を `sys.settrace` で集め，**関数の中で `M.` を参照する行のうち 1 回も通っていない行を列挙**する（網羅の目安．残れば比較のシナリオを足す） |
| hook の経路 | 一時フォルダに `install()` し，生成された `settings.json` の `command` 文字列を**そのまま** WSL の `sh -c` と Git Bash の `sh -c`（`/mnt/c` が無いので `wsl.exe` 経由の分岐）で実行して exit code と出力を確認．壊れた設定で exit 1，パッケージを消した状態で exit 1，エントリも無ければコマンド側の `|| exit 0`．**本番の `shifts.json` と `~/.claude/settings.json` は触らない** |
| `install-hook` | テストの一時フォルダで `install()` を呼び，パッケージがコピーされ，古いモジュールと `__pycache__` が消え，コピー先の `shiftlock.py` が `subprocess` で動き，コピー先から `install-hook` を実行しても自分を消さない |
| `deploy.ps1` | `-DryRun` で一覧表示だけ（`__pycache__` が出ない），**`-SkipHook -Target <一時フォルダ>` で本実行**（パッケージ 28 ファイルがコピーされる，2 回目で置いた `stale.py` が消える，コピー先で `python3 shiftlock.py status` が動く），`-Target` に `pc/` 自身を渡すと throw．構文は PowerShell の Parser で確認．BOM・CRLF は `file` で確認 |
| 追加テスト `tests/test_package.py` | `__all__` と各モジュールの定義名（AST で絞る）の整合，import グラフに循環が無い（AST），各モジュールの単独 import，`install()` のコピー・stale 削除・hooks からの再実行・パッケージ無しで exit 1，i18n（名前付きプレースホルダだけ，`M.X.format` の引数名＝テンプレの名前，定数が全部どこかから使われている） |

## 8. やらないこと

- 関数名・定数名の改名，ロジックの改善，バグ修正（見つけたら作業記録の「気づいたこと」に書く）
- `tests/test_shiftlock.py` のモジュール別分割（既存テストを無変更で通すことが同等性の一番強い証拠なので，今回は触らない）
- `dev` / `main` へのマージ，push，PR の作成，issue の発行（本人が起きてから．PR 本文と issue の下書きは作業記録に書く）
- `deploy.ps1` の本実行，`install-hook` の本実行，本番フォルダ・`~/.claude/settings.json`・タスクスケジューラへの変更

## 9. 判断した点（起きたら確認してほしいもの）

1. ベースを `dev` ではなく **`fix/v11-review`** にした（§1）．PR の base も `fix/v11-review`（積み上げの 9 本目）．
2. パッケージ名を **`shiftlock`**（エントリと同名）にした（§3）．
3. hook のディレクトリ `~/.claude/hooks/` に **パッケージごとコピー**する方式にした（§6-1）．代案は「hook 側は薄いランチャーにして本番フォルダのパッケージを `sys.path` 経由で読む」だったが，hook が本番フォルダの状態に依存するようになり，今の「hook 本体は `~/.claude/hooks` で完結」から動きが変わるので採らなかった．
4. 既存テストの **3 行**だけ変えた（§6-3）．
5. i18n の範囲を「表示する文」に限り，入力語彙・保存する値・設定既定値・日付の表記は対象外にした（§5）．
6. `ENFORCE_ACTIONS` と `CAUTION_ACTIONS` を足し，`schedule_text` のメモの出所判定を `is_workday` の結果に変えた（§5．判定が i18n に依存しないように）．
7. 同じ文は `COMMON_` に 1 つ置いて領域ごとの名前から参照する形にした（§5）．
8. `deploy.ps1` に `-SkipHook` と「`pc/` 自身への上書き拒否」を足した（§6-2）．
9. `install()` と `deploy.ps1` のコピー順をパッケージ → エントリにした（§6-1）．

設計書は実装前に opus のサブエージェントに実装者の目でレビューさせ，15 件の指摘のうち設計を変えるもの（コピー順，`-SkipHook`，`CAUTION_ACTIONS`，`COMMON_`，プレースホルダの検査方法，出力比較の作り方，循環検査の方法，対象外の列挙，事実誤り）を取り込んだ．
