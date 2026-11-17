# v11 作業記録（2026-11-03 未明，自動実行）

寝ている間に何をしたかのまとめ．設計は [pc-v11-design.md](pc-v11-design.md)，計画は [superpowers/plans/2026-11-03-pc-v11.md](superpowers/plans/2026-11-03-pc-v11.md)，現行仕様は [pc-spec.md](pc-spec.md)（§17 に v11 の動きを追記済み）．

## 結論

- 5 機能（E shl ヘルプ，D カレンダー複数，C 通知音，A 事後報告，B 強制モード）を実装し，レビュー指摘を直して，テスト **79 件** が通る状態（WSL の `.venv`）．
- PR は **積み上げ式** で 7 本．**マージ・デプロイ・実機確認はしていない**（本人がやる）．自動モードでは「レビューなしのマージ」が止められるので，マージは起きてからお願いする形にした．
- Copilot のレビュー依頼は API が 200 を返すのに reviewer に載らない（このリポジトリで Copilot レビューが有効でない様子）．#8 #10 #11 で 1 回ずつ試して止めた．
- 設計書に書いた 2 点の判断（`おきた` は案 b，既定 lock・2h）はそのまま実装．追加で，同意の確認を「端末の `y` ＋ Windows のダイアログ」にした（下の「安全面」）．

## PR とマージの順番

| 順 | PR | ブランチ | base | 内容 |
|---|---|---|---|---|
| 1 | #6 | `docs/pc-v11-design` | `dev` | 仕様書・設計書・ネイティブ化計画・実装計画，CLAUDE.md 改訂 |
| 2 | #7 | `update/shl-help` | `docs/pc-v11-design` | E: `shl` だけで日本語の一覧（issue #1） |
| 3 | #8 | `feature/calendar-sources` | `update/shl-help` | D: カレンダー複数（issue #2） |
| 4 | #9 | `feature/notify-sound` | `feature/calendar-sources` | C: 通知音（issue #3） |
| 5 | #10 | `feature/late-report` | `feature/notify-sound` | A: 事後報告・未確認・出発（issue #4） |
| 6 | #11 | `feature/enforce` | `feature/late-report` | B: 強制モード（issue #5） |
| 7 | #12 | `docs/v11-worklog` | `feature/enforce` | pc-spec の v11 追記，この作業記録 |
| 8 | #13 | `fix/v11-review` | `docs/v11-worklog` | レビュー指摘の修正（下の「レビュー」）．テスト 79 件 |

マージの仕方: GitHub で #6 を「Merge」してブランチを削除 → #7 の base が自動で `dev` に付け替わる → 順に #13 まで．`dev` を `main` に出すのは別途（release ブランチの運用どおり）．

## 各機能の要点と，実装で決めたこと

- **E**: `shiftlock.py help`（`--config` 任意）．`shl`／`shl help`／`-h`／`?`，引数なしの `shiftlock.cmd` で出る．状態は `shl status`．
- **D**: `calendar.sources`（上が優先）．旧 `ics_url` は先頭ソースとして読み，設定メニュー 11 で保存すると `sources` に移行．`calendar.json` は新形式（ソースごと）で，旧形式も読む．失敗したソースは前回分で続行．`閉店` にソースごとの最終同期とエラー．
- **C**: `notify.sound`（種類ごと／`default`），`sound_volume`，`sound_max_seconds`．同じ tick で鳴るのは強い 1 件だけ．無いファイルは 1 日 1 回 `guard.log` に．`guard.ps1 -TestNotify -Sound <path>`．送信履歴を `_once()` にまとめた．
- **A**: `休んだ`／`昨日休んだ`／`11/2 出た`，`行ってきます`（出発済み＝`status: worked`），出庫最終のあとの `今日休む` は今日の乗務．未確認の乗務は翌朝以降に 1 回だけ聞く（tick のトースト／hook の文脈）．**起きた記録があれば出たとみなして聞かない**（`confirm.okita_means_worked`，既定 true．毎日聞かれるのを避けるため）．レポートは「出?」．`出た` に続けて文があると普通のプロンプト（誤爆防止）．月日だけの日付は事後報告では「未来なら去年」に解釈．
- **B**: 宣言は `shl enforce on [lock|sleep|shutdown] [2h]` だけ．内容を表示 → 端末で `y` → **Windows のダイアログで「はい」** → 有効．hook に強制のキーワード・文脈は無し（`強制` は普通のプロンプト）．予告は「ハードで PC を触っている最初の tick」から 15 分，戻ったら 5 分の予告のあと再実行，最初の実行から 2h で終了．`STOP.txt`・起動 15 分以内・uptime 不明・離席中・リモート・6 回・休み／出発済みでは発動しない．`kaijo.cmd`／`shl kaijo`（`guard.ps1 -Release`）と `STOP.txt` で解除．`shl enforce off`（ハード前は取り消し，後は解除扱い），`shl enforce resume`（STOP.txt を消す．宣言はやり直し）．強制中の `おきた` は寝た記録があるときだけ．

## 安全面（自動レビューの指摘と対応）

- コミット後の自動セキュリティレビューが「`isatty` だけでは擬似端末で回避できる」と指摘．→ 端末の `y` に加えて **Windows のダイアログ（`WScript.Shell.Popup`，60 秒）で「はい」** が無いと宣言しないようにした．画面の前にいる人しか押せない．WSL から `powershell.exe -EncodedCommand` で呼ぶ経路は実機で動作確認済み（タイムアウトで False が返る）．
- 残る限界: 同じユーザー権限で動くプロセスが `state.json` に `consented_at` を直接書けば偽造できる．防ぐのは CLAUDE.md の禁止（Claude Code は強制を操作しない）と，将来の権限分離（[native-app-plan.md](native-app-plan.md) §5.2）．設計書 B.3 に明記した．
- Parsec（リモート）の検出は未実装．`skip_if_remote` は検出値が来ないので効かず，代わりに「予告から猶予」で安全側に倒している．スリープ／シャットダウンは遠隔から戻せないので，Parsec を使うなら `lock` のまま．

## テスト

- Python: `source .venv/bin/activate && cd pc && python3 -m pytest -q tests` → 67 passed（E +1，D +4，C +3，A +9（既存 2 件を未確認の扱いに更新），B +8）．
- PowerShell: `guard.ps1` `shiftlock-profile.ps1` `deploy.ps1` ほかは Parser で構文チェック．一時フォルダ（`%LOCALAPPDATA%\Temp\shiftlock-dryrun`，本番フォルダは触っていない）で `guard.ps1 -DryRun` と `-Release` の経路を確認．**ロック／スリープ／シャットダウンの実行，音の再生，トーストは未確認**．
- サブエージェントのレビュー（sonnet，D+C+A で 1 体，B で 1 体）: 結果と対応は末尾の「レビュー」に追記．

## 起きてからやること（順番）

1. PR を #6 → #13 の順にマージ（上の表）．終わったら `git checkout dev && git pull`．
2. 反映: `cd C:\dev\repo\private\shiftlock\pc` → `powershell -ExecutionPolicy Bypass -File .\deploy.ps1 -Full`（`kaijo.cmd` が増え，`shl` の既定と `itte` `kakunin` `kaijo` が増えたので profile も入れ直す．タスクの登録内容は変わらないが `-Full` で再登録しても害はない）．Claude Code は再起動．
3. 設定（`settings.cmd`）: `11` カレンダー（旧 `ics_url` があれば保存で `sources` に移行），`17` 通知音（任意），`18` 乗務の確認（既定のままで可），`19` 強制の既定値（既定のままで可）．
4. 実機確認（チェックリスト）:
   - [ ] E: `shl` で一覧，`shl status` で状態，`shl ?`
   - [ ] D: `shl sync` で全ソースの行が出る．`閉店` にソースごとの最終同期
   - [ ] C: `guard.ps1 -TestNotify -Sound "C:\...\x.mp3"` で音→トースト→読み上げ．WAV と MP3，長い曲が `sound_max_seconds` で止まる，無いファイルで通知だけ出る
   - [ ] A: `shl itte` で出発済み（`閉店` に worked），翌朝のトースト／Claude の一言「出た？休んだ？」が 1 回だけ，`出た` で記録．`shl kakunin`
   - [ ] B: pwsh で `shl enforce on` → 端末 `y` → ダイアログ「はい」で宣言できる（**できなければ** `enforce_declare` の `isatty` 判定か `windows_confirm` を疑う）．`shl enforce` で状態．ハードの時間帯に `guard.ps1 -DryRun` で `[enforce] lock (DryRun…)` が出る．実機で lock → ログイン → 5 分予告 → 再ロック → `kaijo.cmd` で止まる → `shl enforce resume`．shutdown は 60 秒以内の `kaijo.cmd` で中止できる
5. issue #1〜#5 はマージ後にコメントしてクローズ（#3 と #5 は実機確認が済んでから）．

## 触っていないもの

- `docs/proposal-discord-kill.md`（別セッションの提案）は読んだだけ．今回の設計・実装には入れていない．
- 本番フォルダ `%USERPROFILE%\shift-lock`，タスクスケジューラ，`~/.claude/settings.json`（hook）．
- `dev`／`main` へのマージ．

## レビュー（サブエージェント）と対応（PR #13）

2 体（sonnet）に差分を読ませた．指摘はどちらも再現スクリプト付きで妥当なものが多く，次のように直した（テストも追加．合計 79 件）．

### D・C・A のレビュー

| 重要度 | 指摘 | 対応 |
|---|---|---|
| HIGH | iPhone の `yasumi`/`deta` が冪等でない（訂正が巻き戻る，同じ日の 2 件を毎回適用，日付なしが古い乗務へ波及） | 適用済みの記録を `state.json` の `phone_applied` に残して **1 件 1 回** だけ適用．日付なしは「記録した時点で直近に終わった乗務」に固定 |
| HIGH | 取り込みの窓が `target_plan` 依存で，日曜など 24h 以内に乗務が無いと事後報告が入らない | `yasumi`/`deta` は `target_plan` から切り離し，`now − lookback_days` の窓で処理 |
| HIGH | `行ってきます` に時刻の条件が無く，昼間の常用句で夜の乗務の保護が外れる．出庫最終後は翌日に付く．undo が効かない | 出発目安の **2 時間前から** だけ受け付け，undo も残す．`やっぱ出る` の文言も「出発済みを取り消した」に |
| MEDIUM | 日付なしの返事が古い `confirm_asked` を無期限に優先 | 36 時間以内かつ lookback 内だけ |
| MEDIUM | 未確認が溜まると tick（5 分）・プロンプトごとに 1 件ずつ聞き続ける | **1 日 1 件**（新しい乗務から，「ほか N 件は shl kakunin」） |
| MEDIUM | `yasumi_late` が記録時刻で数えられて期間外・二重で増える．okita→worked で起きた時刻が消える | 乗務日ベースで「その日の最後の yasumi イベントが late」だけ数える．`okita_at` を残す |
| MEDIUM | 壊れた mp3 で MediaPlayer が黙り，無音で 23 秒止まる．失敗しても既定音が消える | 長さが取れなければ諦めてログ，WAV は SoundPlayer に．鳴らせたときだけトーストを無音に．最長秒の上限は 60 |
| MEDIUM | `notify.sound` や `confirm` の型違いで tick/hook が落ちる | `attach_sounds` と音量を try/except，`load_config` で型を検査して分かる文で exit 1 |
| MEDIUM | profile の `yasumi`/`deru` と `.cmd` が引数を捨てる | `@args` / `%*` を通す |
| MEDIUM | 『休んだ ので…』『行った』がプロンプトを食う | メモは日付付きの『休んだ』だけ．『行った』は日付必須 |
| MEDIUM | 休みの日の『出た』で足した出勤の例外が undo で戻らない．未来の月日が去年扱い．同じ報告の二重 | undo で `days.json` も戻す．去年扱いは 1〜2 月に 11〜12 月を言ったときだけで，未来は拒否．同じ状態なら何もしない |
| LOW | `read_phone_marks` の sort で同時刻の日付あり／なしが TypeError | sort key を付けた |
| LOW | 全ソースがスキップでも毎 tick `calendar.json` を書き直す | 変化が無ければ書かない |
| LOW | v11 導入前の乗務が全部「出?」になる | `confirm_since`（初回実行の前日）より前は従来どおり「出」 |
| LOW | 設計書・README の表現（`target_plan` と worked，音の優先順） | 直した |
| 見送り | 同じ tick の他のトーストは Windows の既定音のまま | 意図どおり．README に明記 |
| 見送り | WSL から見えないパス（UNC）の音源 | ドライブレター／POSIX 以外は存在確認を `guard.ps1` に任せる |

### B（強制モード）のレビュー

| 重要度 | 指摘 | 対応 |
|---|---|---|
| HIGH | 離席をまたいだ古い `warned_at`/`rewarned_at` で，戻った最初の tick に **予告なしで即実行** | active の tick ごとに `seen_at` を残し，12 分以上空いたら予告からやり直す．「あと5分」の記録キーに `warned_at` を含めて 2 周目も出す |
| HIGH | hook の『今のなし』が `shifts[日]` を丸ごと戻し，取り消した宣言の復活・`released_at` の消去・`runs` の巻き戻しが起きる | undo は `enforce` に触らない（現在の `enforce` を保持） |
| MEDIUM | `-Release` で `STOP.txt` の書き込みが失敗すると `shutdown /a` に届かない | `shutdown /a` を最初に，各手順を独立の try/catch に |
| MEDIUM | `Invoke-Enforce` の `STOP.txt` 確認が音（最大 20 秒）の前 | 音のあと，実行の直前にもう一度確認 |
| MEDIUM | 同意した内容のうち猶予・回数などが実行時の設定を読む．設定メニュー 19 が非対話で変えられる | 宣言に `grace_minutes` `relock_minutes` `max_runs` `boot_grace_minutes` を固定して保存．メニュー 19 は端末だけ |
| MEDIUM | 手で置いた `STOP.txt` → `resume` で古い同意が復活．解除中も『おきた』が拒否される | `resume` は未解除の宣言に `released_at` を付ける（宣言はやり直し）．`STOP.txt` がある間は『おきた』のゲートを外す |
| MEDIUM | `-DryRun` でも Python 側が `warned_at`/`runs` を進める | tick に `--dry-run` を渡し，強制の状態を進めない |
| MEDIUM | ロック直後（最後の入力から 20 分未満）に無人のロック画面で再予告・再実行し，回数を消費 | 実行より後に入力があったときだけ再予告（idle を渡す） |
| LOW | `uptime` 0 / NaN が発動側に倒れる | 不明扱い（発動しない） |
| LOW | ダイアログの既定ボタンが「はい」，判定が緩い | 既定「いいえ」＋最前面．最終行が `6` のときだけ |
| LOW | `enforce_suffix` が go / go_last にも付く．`enforce_due` の例外で tick が落ちる | lastcall / soft / hard だけ．例外は握って警告に |
| LOW | kaijo の表示が毎回「もう解除中」．宣言が無い kaijo が記録されない | 直した |
| LOW | 古い文言（guard.ps1 / install-task.ps1 /「Claude Code からは一切操作できへん」） | 直した（解除方向は端末なしでも効く，と README に明記） |
| 要確認 | 高速スタートアップ（この PC は有効）だと「シャットダウン → 電源オン」で `LastBootUpTime` が進まず，起動直後の 15 分ガードが効かない可能性 | README に注意として記載．実機で確認（再予告 5 分は残る） |
| 未対応 | Parsec（リモート）の検出 | 設計どおり未実装．同意の文面と README に「遠隔操作中でも発動する」と明記 |

レビュアーが挙げた「テストの穴」のうち，上の修正に関わるものはテストを足した．残り（CLI 配線の自動テスト，`-TestEnforce` スイッチ，境界値の網羅）は次の機会に．
