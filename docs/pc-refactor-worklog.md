# PC 版リファクタ作業記録（2026-11-04 深夜，自動実行）

寝ている間に何をしたかのまとめ．設計は [superpowers/specs/2026-11-04-pc-refactor-modules-design.md](superpowers/specs/2026-11-04-pc-refactor-modules-design.md)，計画は [superpowers/plans/2026-11-04-pc-refactor-modules.md](superpowers/plans/2026-11-04-pc-refactor-modules.md)．仕様書 [pc-spec.md](pc-spec.md) は冒頭の表・§5.1・§5.6・§14・§16 を直した．

## 結論

- `pc/shiftlock.py`（1 ファイル 3085 行）を，**エントリ（十数行）＋パッケージ `pc/shiftlock/`（24 モジュール＋ `i18n/ja.py`）** に分けた．ユーザー向け・Claude 向けの文面は `i18n/ja.py` の定数 306 個に集めた．
- **機能・CLI・hook と tick の入出力・ファイル形式・関数名は変えていない**．証拠は下の「検証」．既存テスト 81 件は 3 行（`sl.windows_confirm` の差し替え）以外無変更で全通過．構成と i18n の決まりのテストを 12 件足して 93 件．
- ブランチ `refactor/split-modules`（worktree `../worktrees/refactor/split-modules`，**base は `fix/v11-review`**），コミット 8 本．**push・PR・issue・deploy・install-hook・本番フォルダ・`~/.claude/settings.json` は触っていない**．メインの作業ツリー（`chore/native-skeleton` の未コミット分）も触っていない．

## 判断したこと（起きたら確認してほしい）

1. **ベースを `dev` ではなく `fix/v11-review` にした**．本番の `shift-lock\shiftlock.py` と WSL の `~/.claude/hooks/shiftlock.py` はどちらも `fix/v11-review` の版（162,447 バイト）で，`dev` は v10（105,528 バイト）．v11 の積み上げ PR（#6〜#13）が未マージなので，`dev` を分割すると v11 の 1141 行分が 1 ファイルへの差分のまま戻せなくなる．`fix/v11-review` の子孫にしておけば #13 のマージ後にそのまま続けてマージできる（PR の base も `fix/v11-review`，積み上げの 9 本目）．
2. パッケージ名をエントリと同名の **`shiftlock`** にした（同名ならパッケージ優先．Windows / WSL の 3.12 で実測）．
3. hook のフォルダ `~/.claude/hooks/` に **パッケージごとコピー**する（`install-hook` が `shiftlock/` を丸ごと入れ替えてからエントリを置く）．
4. 既存テストは **3 行だけ**変えた（`sl.windows_confirm = …` → `sl.enforce.windows_confirm = …`．パッケージの属性を差し替えても関数側に効かないため）．
5. i18n の範囲は「表示する文」だけ．入力の語彙（`KW_*`・正規表現・`今日休む` など）・ファイルに書く値（`事後報告`・ソース名「カレンダー」・`events.log` のメモ）・設定の既定値・日付の表記（`%m/%d`・曜日）・`FILE\t` 行は対象外．残った日本語リテラル 139 件を AST で棚卸しして，全部この対象外に入ることを確認した．
6. 判定に使っていたキー集合を i18n から切り離した: `ENFORCE_ACTIONS`・`CAUTION_ACTIONS`（enforce），`schedule_text` のメモの出所は `is_workday` の結果で分岐．集合も結果も同じ．
7. 同じ文（「24時間以内に乗務の予定はないで。」が 3 箇所など）は `COMMON_` に 1 つ置き，領域ごとの名前から参照する．
8. `deploy.ps1` に `-SkipHook`（hook の登録を飛ばして一時フォルダへ本実行できる）と「`-Target` が `pc/` 自身なら throw」を足した．コピー順はパッケージ → エントリ．
9. エントリ `shiftlock.py` は自分のフォルダを `sys.path` に明示的に入れ，パッケージが読めへんときは案内文を出して exit 1（exit 2 にしない）．
10. 最終レビュー（下）のあとで: `install()` のパッケージ入れ替えを `.new` にコピー → rename → `.old` を削除の順にし（途中で止まってもどちらかが残る），判定・保存・順番に使うキーの集合（`SOUND_KIND_KEYS` `LEARNED_KINDS`）を `ja.py` から外してコード側に置いた．

設計書は実装前に opus のサブエージェントに「実装者の目」でレビューさせ，15 件の指摘のうち設計を変えるもの（コピー順・`-SkipHook`・`CAUTION_ACTIONS`・`COMMON_`・プレースホルダの検査方法・出力比較の作り方・循環検査の方法・対象外の列挙・事実誤り 3 点）を取り込んだ．

## コミット（`fix/v11-review` → `refactor/split-modules`）

| 順 | コミット | 内容 |
|---|---|---|
| 1 | `refactor(pc): shiftlock.py を役割ごとのモジュールに分割` | 行範囲と割り当て表から機械的に切り出し（転記ミスをゼロに）．AST で 179 定義すべて一致．`__init__.py` が 156 名を再公開．設計書・計画書 |
| 2 | `test(pc): パッケージ構成の整合テストを追加` | 5 件 |
| 3 | `chore(pc): deploy.ps1 でパッケージのフォルダも配る` | `shiftlock/` の再帰コピー |
| 4 | `refactor(pc): ユーザー向けの文面を i18n/ja.py の定数に分離` | 306 定数．`install()` のコピー順，エントリの `sys.path`，テスト +4 |
| 5 | `chore(pc): deploy.ps1 はパッケージを先に配り、pc/ 自身への上書きを拒否、-SkipHook を追加` | `install-task.ps1` の存在確認も |
| 6 | `docs(pc): モジュール構成とリファクタの作業記録` | `pc-spec.md`・`pc/README.md`・`CLAUDE.md`・この記録 |
| 7 | `fix(pc): 最終レビューの指摘を修正（deploy.ps1 の入れ子、install の入れ替え、i18n のキー分離、テスト強化）` | 下の「最終レビュー」．テスト +3（計 93） |
| 8 | `docs(pc): 最終レビューの結果と対応を作業記録に追記` | この記録・設計書 §6・計画書 |

## モジュール構成（どこに何があるか）

| 層 | モジュール |
|---|---|
| 基盤 | `config`（設定・例外日のマージ・Windows パス変換）`timeutil`（時刻の整形）`store`（ファイルの読み書き・`Store`・ログ）`keywords`（hook の固定キーワードと正規表現）`console`（対話入力の部品）`i18n/ja.py`（文面） |
| 判断 | `plan`（乗務日と時刻の逆算・フェーズ）`usage`（連続稼働・離席）`autowake`（自動『起きた』）`intents`（話し言葉・聞き返し・学習）`confirm`（出た？休んだ？）`enforce`（強制モード）`notify`（送信履歴・通知音）`days`（例外日・2 週間表） |
| 記録と応答 | `records`（帰宅・就寝・起床・出発・事後報告・取り消し）`commands`（キーワード応答・状況）`phone`（mood.csv） |
| 入口 | `hook` `tick` `cli` |
| 出力・保守 | `report`（集計）`report_html`（HTML）`calendar_sync` `maintenance` `settings_menu` `install` |

依存は上から下へ一方向（循環なし．テストで AST 検査）．`import shiftlock as sl` → `sl.関数名` は全部そのまま使える．

## 検証（同等性の証拠）

| 何を | 結果 |
|---|---|
| 既存テスト `tests/test_shiftlock.py` | 81 件，3 行の差し替え以外無変更で **全通過**（WSL の `.venv`） |
| 追加テスト `tests/test_package.py` | 12 件 **全通過**（合計 93）: 公開名の再公開，関数内のグローバル名が解決できる（`symtable`），import グラフに循環なし，単独 import，`-P` 起動，`install()` のコピー・stale 削除・`.old`/`.new` を残さない・hooks からの再実行・パッケージ無しで exit 1，i18n（名前付きプレースホルダだけ，`M.X` の存在とプレースホルダ付きは `.format` 経由，format の引数名＝テンプレの名前，定数が全部使われている） |
| 分割が「移動だけ」 | 旧 `shiftlock.py` と新パッケージの top-level 定義 179 個を AST で比較（docstring 除く）→ **欠け 0・重複 0・差異 0**（分割コミットの時点．`install()` を直す前） |
| i18n が文面を変えていない | 旧版（`fix/v11-review`）と新版を別々の一時フォルダ・同じ入力で動かし，**185 シナリオ**（hook のキーワード 30 種・話し言葉・聞き返し・ハード中・フェーズ文・連続稼働・自動起きた・iPhone・未確認・強制モードの宣言〜解除・tick の通知と予告と実行・音の警告・壊れた設定・状況・予定・学習・カレンダー同期・整理・設定メニュー 2 周・レポート 4 種・CLI 全サブコマンドと `--help`・install-hook）の**戻り値と，各シナリオ後のデータファイル（state.json / events.log / days.json / calendar.json / learned.json / notified.json / shifts.json / mood.csv / log / reports）が全一致** |
| 出力比較の網羅 | 新版の実行行を `sys.settrace` で集め，関数の中で `M.` を参照する文 272 のうち **270 を通した**．残り 2 は到達困難な防御分岐（`console._list_edit` の変換失敗，`tick` の `attach_sounds` 例外）で，文面は定数化済み |
| 残った日本語リテラル | docstring 以外に 139 件．内訳: `keywords` 57・`intents` 32（語彙と正規表現），`cli` 5・`commands` 7・`hook` 1・`console` 8・`timeutil` 9（入力語・曜日・日付語），`config` 7（設定既定値・ソース名），`phone` 9（`PHONE_MARKS`・全角ー），`records` 1（`事後報告`），`settings_menu` 3（ソース名）．全部設計書 §5 の対象外 |
| hook の実経路 | 一時フォルダに `install()` → 生成された `settings.json` の `command` をそのまま **WSL の `sh -c`** と **Git Bash の `sh -c`（`/mnt/c` が無いので `wsl.exe` 経由の分岐）** で実行 → `閉店`: exit 2＋状況表示，`x`: exit 0＋JSON，`Stop`: exit 0，壊れた設定: exit 1，エントリだけでパッケージ無し: exit 1（「shiftlock/ フォルダが読めへん」），本体無し: exit 0（コマンド側の `\|\| exit 0`）．**exit 2 は意図したときだけ** |
| `install()` | パッケージをコピー → 古いモジュールと `__pycache__` を消す → コピー先の `shiftlock.py status` が動く → コピー先から `install-hook` を実行しても自分を消さない（テスト） |
| `deploy.ps1` | 構文エラー 0．`-DryRun` で 46 行（ファイル 18 ＋ パッケージ 28）列挙・`__pycache__` 無し・Target は空のまま．`-SkipHook -Target <一時フォルダ>` で本実行 → 18 + 28 ファイル，2 回目で置いた `stale.py` が消える，コピー先で `python3 shiftlock.py status` が動く．`-Target` に `pc/` 自身 → throw．BOM・CRLF 維持（最終レビューで見つかったループの入れ子を直したあとに再確認） |
| Windows の python でも | `import shiftlock` と各モジュールの単独 import，`python shiftlock.py help` が動く（テストは WSL 前提） |

## 起きてからやること（順番）

1. この記録と設計書 §9 の「判断」を見て，違うところがあれば言ってな．
2. v11 の PR #6 → #13 を順にマージ（[pc-v11-worklog.md](pc-v11-worklog.md)）．そのあと **本ブランチの PR を作る**（base `fix/v11-review`．#13 がマージ済みなら GitHub が base を付け替える）．下書きは下．issue も発行する（refactor は chore やないので要る）．
3. 反映: `cd C:\dev\repo\private\shiftlock\pc` → `powershell -ExecutionPolicy Bypass -File .\deploy.ps1`（`-Full` は要らん．`install-hook` も走って `~/.claude/hooks/shiftlock/` ができる）→ Claude Code を再起動．
4. 実機確認:
   - [ ] `shl status`，`shl`（ヘルプ），`settings.cmd` を開いて `q`
   - [ ] Claude Code（WSL 版と Windows 版）で `閉店` → 状況が出る．普通の文 → 通る
   - [ ] `powershell -ExecutionPolicy Bypass -File .\guard.ps1 -DryRun`（tick がパッケージを読めるか）
   - [ ] `report.cmd 7`
5. 不要になったら worktree を消す: `git worktree remove ../worktrees/refactor/split-modules`（ブランチは残る）．

## PR 本文の下書き

```
refactor(pc): shiftlock.py を役割ごとのモジュールに分割し、文面を i18n/ja.py に分離

## 何を
- `pc/shiftlock.py`（3085 行）→ エントリ（十数行）＋ `pc/shiftlock/`（24 モジュール＋ `i18n/ja.py`）
- ユーザー向け・Claude 向けの文面 306 個を `i18n/ja.py` の定数に。入力の語彙・保存する値・設定既定値・日付表記は対象外
- 機能・CLI・hook と tick の入出力・ファイル形式・関数名は変えていない

## 証拠
- 既存テスト 81 件: 3 行（`sl.windows_confirm` の差し替え先）以外無変更で全通過。構成と i18n の決まりのテスト 12 件を追加（計 93）
- 分割時点の AST 比較: 179 定義すべて一致
- 旧版との出力比較: 185 シナリオ（戻り値とデータファイル）全一致。`M.` 参照を含む文 272 のうち 270 を通した
- hook の実経路（WSL と Git Bash → wsl.exe）で exit code を確認。壊れた設定・パッケージ無しは exit 1、exit 2 は意図したときだけ

## 動作が変わりうる点（意図したもの）
- `install-hook` が `~/.claude/hooks/shiftlock/` にパッケージもコピーする（古いモジュールは入れ替え）
- `deploy.ps1` はパッケージを先に配る。`-SkipHook` 追加。`-Target` に `pc/` 自身は不可
- エントリはパッケージが読めへんとき案内文を出して exit 1

## 確認手順
1. `deploy.ps1` → Claude Code 再起動 → `閉店` / `shl status` / `guard.ps1 -DryRun`

設計: docs/superpowers/specs/2026-11-04-pc-refactor-modules-design.md
記録: docs/pc-refactor-worklog.md
```

## issue の下書き

```
refactor(pc): shiftlock.py のモジュール分割と文面の i18n 分離

`pc/shiftlock.py` が 1 ファイル 3085 行になっていて保守しづらい。

- [ ] 役割ごとにモジュールへ分割（機能・CLI・hook/tick の入出力・ファイル形式は変えない）
- [ ] ユーザー向け・Claude 向けの文面を専用の定数（i18n）に分離
- [ ] 既存テスト全通過、同等性の証拠（AST 比較・出力比較）
- [ ] deploy / install-hook がパッケージを配る
- [ ] 仕様書・README の更新
- [ ] 実機で deploy → hook（WSL / Windows 版）→ guard -DryRun の確認
```

## 最終レビュー（sonnet のサブエージェント）と対応

コミット 6 本の時点の差分（base `fix/v11-review`）を読ませて 12 件．

| 重要度 | 指摘 | 対応 |
|---|---|---|
| 中 | `deploy.ps1` で `$files` のループが `$dirs` の内側に入っていた（コピー順を入れ替えたパッチの貼り付けミス．`-DryRun` の一覧がパッケージの 28 行だけになる．通常実行は `$dirs` が 1 要素なので偶然動いていた） | 直して `-DryRun` 46 行と `-SkipHook` の本実行を再確認 |
| 中 | 関数の中で使う名前が解決できるかのテストが無い（機械的な分割で残りやすい import 漏れを，通らへん分岐では保証できない） | `symtable` で全関数・クラスのグローバル参照がモジュールか builtins にあることを検査するテストを追加（レビュー側の grep でも未解決名は 0 だった） |
| 低 | `test_no_import_cycles` に常に真の assert が 2 つ，絶対 import を見ていない | 直した（`cli` の辺の存在と `timeutil` `store` の辺で検査） |
| 低 | i18n 検査の穴: `M.X` が `ja.py` に実在するか，プレースホルダ付きの文を `.format` 無しで使っていないか | テスト追加（現状どちらも 0 件） |
| 低 | `-P` / `PYTHONSAFEPATH` 起動のテストが無い．`list[str]` 注釈が 3.8 で落ちる | `-P` のテストを追加（3.11+ でだけ走る），`from __future__ import annotations` |
| 低 | `install()` の同一判定が文字列比較（`/mnt/c` の綴り違いで実行中のパッケージを消しうる），`rmtree` → `copytree` が非原子的 | `samefile` で判定．`.new` にコピー → rename → `.old` を削除 |
| 低 | `deploy.ps1` のガードがジャンクション・8.3 名・相対指定をすり抜ける | `Remove-Item` の直前に `Resolve-Path` で同一なら throw |
| 低 | Windows ダイアログの題名を PowerShell の単一引用符に生で埋めている | `'` をエスケープ |
| 低 | 判定・保存・順番に使うキーの集合が `ja.py` に残っていた（`MENU_SOUND_KINDS` `INTENT_LEARNED_KINDS` `MENU_DAYS_KIND`） | キーはコード側（`SOUND_KIND_KEYS` `LEARNED_KINDS`，days の種別は `COMMON_DAY_KIND_LABEL` をデータ値で引く）．`ja.py` の dict はキー → 表示だけ |
| 低 | `RECORDS_LATE_KIND[kind]` は旧版の条件式と等価でない（`yasumi` `worked` 以外で KeyError．今の呼び出し元では起きない） | 旧版と同じ条件式に戻した |
| 低 | コメント・docstring の接頭辞一覧・定義順（`MENU_19` が `MENU_18` より前）・区切り文字がコードに残る・E302（25 本） | 直した |
| 見送り | `install` / `settings_menu` はモジュール名と公開した関数名が同じ（`import shiftlock.install as m` は関数を返す．`mock.patch("shiftlock.install.shutil")` が効かない）．ファイル名を `hook_install.py` / `menu.py` に変えれば消える | 文書に波及するので起きてから決めてほしい（公開名 `sl.install` `sl.settings_menu` は変わらない．設計書 §4 に `importlib.import_module` で取ると明記済み） |
| 見送り | `guard.ps1` は tick の stdout しか `guard.log` に残さないので，エントリが ImportError で出す案内文（stderr）が `tick failed (1):` だけになる | `guard.ps1` は今回の範囲外．次の PR で `2>&1` を足す |

レビュー側が「問題なし」と確認したこと: 分割コミットは旧版と一致（非空行の多重集合，top-level 183 ブロック），i18n の全 hunk で引数の取り違え 0（`when`/`sleep_at`，`target_h`/`min_h`，`soft`/`hard`，`depart`/`latest`），書式付き式 13 個の保持，`ja.py` の文面の逐語一致（`HELP_TEXT` 完全一致），保存値の漏れなし，未使用 import 0，exit 2 になる経路なし，コミットメッセージに禁止トレーラなし．

## PR #15 の Copilot レビュー（2026-11-04）と対応

指摘は 3 件で，いずれもリファクタ本体ではなく，このブランチに積んだ `fix(phone)`・`fix(report)` のコミットに対するもの．3 件とも再現できた（再現はそのまま回帰テストにした．計 96 件）．

| 指摘 | 再現 | 原因 | 対応 |
|---|---|---|---|
| iPhone の記録をまとめて読むと二度寝後の『おきた』を取りこぼす | 07:05 ねる／09:30 おきた／10:30 ねる／15:00 おきた を 15:05 に初同期 → 通知が「07:05〜09:30」のまま，再同期でも直らない | `_phone_okita_pending` が `at`（取り込み時刻 `now`）と `sleep_at` を比べていた | iPhone 経由の `okita` は **`at` と `events.log` の時刻を iPhone の記録時刻 `t`** にする（`set_shift_status(..., t, ...)`）．レポートの起床列も実時刻になる |
| 『今のなし』で別の記録を戻すと出発時刻がレポートから消える | 21:59『行ってきます』→『11/3 休んだ』→『今のなし』→ state は worked/21:59 に戻るが外出は 18:30 開始 | `events.log` の「最後の worked のメモ」を見ていて，後続の `yasumi` と `undo` 行で消していた | `(乗務日, at)` が `events.log` の `worked\tshuppatsu` 行と一致するかで判定（後続の行に左右されない） |
| 同じ乗務日に『ただいま』を 2 回言うと前日の外出が 2 回目まで伸びる | 11/4 10:07 と 15:00 に『ただいま』→ 11/3 の外出が 15:00 終了，10〜14 時の在宅が消える | `set_mark` が乗務日ごとに `kitaku` を 1 つしか持たない | 帰宅の候補を `events.log` の `kitaku` 行（＋state）から集める．`set_mark` のメモを `出所 時刻` にして phone 経由でも時刻を残す（古い行は行の時刻で代用．`free-day` 行も候補に入るようになった） |

文書: `pc-spec.md` §6.4 と §（レポート）の外出区間，`data-format.md` の `events.log`．

## 気づいたこと（直していない）

- `install` / `settings_menu` のモジュール名（上のレビュー「見送り」）．
- `guard.ps1` が tick の stderr を `guard.log` に残さない（同上）．
- `pc/README.md` の見出しが `# shift-lock v10` のまま（v11 の PR でも残っている）．
- 設定メニューの表示順が `17 → 19 → 18`（元のまま．番号で選ぶので動作は同じ）．
- `tests/test_shiftlock.py` のモジュール別分割は今回やっていない（無変更で通すことを同等性の証拠にしたため）．
- 到達困難な防御分岐 2 つ（上の表）は出力比較で通せていない．
- 本番の `~/.claude/hooks/shiftlock.py`（旧 1 ファイル版 162KB）は次の `install-hook` で上書きされ，`shiftlock/` が増える．旧版の `__pycache__` は Python が自動で作り直す．

## 触っていないもの

- 本番フォルダ `%USERPROFILE%\shift-lock`，`~/.claude/settings.json`（WSL・Windows），タスクスケジューラ，`dev` / `main`，push，PR，issue．
- メインの作業ツリー `C:\Dev\repo\private\shiftlock`（`chore/native-skeleton` の未コミット分: `.gitignore`・`native/`・`docs/proposal-discord-kill.md`）．

## 使った一時ファイル（リポジトリ外，scratchpad）

`split.py`（切り出し），`equiv_ast.py`（AST 比較），`snapshot.py`（出力比較），`hooktest.sh` / `hooktest-win.sh`（hook 経路），`patch_deploy*.py`（ps1 の BOM・CRLF を保つ置換）．リポジトリには入れていない．必要なら言ってな．
