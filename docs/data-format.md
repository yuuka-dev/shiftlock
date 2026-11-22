# データ形式の決まり（PC 版とスマホ版の共通ルール）

PC 版（`pc/`）とスマホ版（`app/`）は**このファイルに書いた形式だけ**でやりとりする。形式を変えるときは、まずここを直してから両方の実装を直す。

- 時刻はすべて**ローカル時刻（JST）、タイムゾーン表記なし**の ISO 8601（`2026-11-04T07:10` または `2026-11-04T07:10:00`）
- 日付は `YYYY-MM-DD`
- 文字コードは UTF-8（BOM なし。読む側は BOM 付きも受け付ける）
- JSONL は 1 行 1 イベント、**追記のみ**（書き換え・削除をしない）。壊れた行は読む側が無視する

---

## 1. PC 版の実行フォルダ（`%USERPROFILE%\shift-lock\`）

リポジトリ外。個人データなのでコミットしない。

| ファイル | 書く人 | 中身 |
|---|---|---|
| `shifts.json` | 設定メニュー | 設定（形式は `pc/shifts.example.json`。省略したキーは `shiftlock.py` の `DEFAULTS`）。`apps` = 稼働計測の登録と数値（`{"registered": {"<exe名（小文字・.exe つき）>": "dev"｜"adobe"}, "active_input_minutes", "active_cpu_percent"}`）．書くのは `shl app` |
| `days.json` | 設定メニュー／`11/5 休み` | 手動の例外日 `{"off": {"2026-11-05": "通院"}, "work": {...}, "times": {"2026-11-06": {"start_target": "21:00"}}}` |
| `calendar.json` | カレンダー同期 | Google カレンダー（iCal）から拾った例外日。`{"fetched", "sources": {"<名前>": {"fetched", "error", "off", "work", "recurring_skipped"}}}`（ソースの順に先勝ち）。旧形式 `{"fetched", "off", "work", ...}` も読める |
| `state.json` | hook／CLI | 乗務日ごとの状態 `{"shifts": {"2026-11-04": {"status": "okita"｜"yasumi"｜"worked", "at", "sleep_at"?, "kitaku"?, "paid"?（休みを有給に振り替えた印）, "confirm_asked"?}}, "undo"?, "pending"?, "last_ask"?}`。`worked` = 出発済み／出た（事後報告）。`confirm_asked` = 「出た？休んだ？」を聞いた時刻。`enforce`? = 強制モードの宣言 `{"action", "duration_hours", "declared_at", "consented_at", "via", "warned_at"?, "rewarned_at"?, "first_run_at"?, "runs": [{"at", "action"}], "released_at"?}`。ネイティブ版の画面（`native/shiftlock.App`）も同じ形式で書く。`undo`（`{"day", "prev", "at", "what"}`。直前の記録の取り消し用）は、ネイティブ版では 寝る・起きた・行ってきます・帰ってきた のどのボタンでも残す（Python 版の「今のなし」でも戻せる） |
| `STOP.txt` | 本人／`kaijo.cmd`／ネイティブ版のトレイの「緊急解除」 | 存在する間、強制モードは一切発動しない（緊急解除）。中身は自由（解除した時刻と理由を追記。トレイからは `2026-12-07 11:00 tray`） |
| `learned.json` | 聞き返し学習 | `{"kitaku": [...], "neru": [...], "okita": [...], "none": [...]}`（正規化済みの言い方） |
| `events.log` | hook／CLI | TSV `時刻\t乗務日(なければ -)\t種類\tメモ`。種類 = `yasumi` `okita` `worked` `undo` `sleep_at` `kitaku` `neru` `enforce_declare` `enforce_consent` `enforce_off` `enforce_run` `enforce_release` `enforce_resume` `paid` ...。事後報告はメモが `late ...`。`paid` は休みの記録に有給の印を付けた／外した行（メモ `on 出所` / `off 出所`。当日欠勤の目安に数えない）。`sleep_at` / `kitaku` はメモが `出所 時刻`（例 `phone 2026-11-04T10:07`、消したときは `clear`。出所は `claude`＝hook／CLI、`phone`＝iPhone、`app`＝ネイティブ版の画面）で、レポートはこの行を帰宅の履歴として読む。**レポートの睡眠（寝た／起きた）は `sleep_at`・`neru`・`okita` の行と mood.csv から出す**（読み方は [pc-spec.md](pc-spec.md) §6.5）: 乗務日つきの `okita` はメモが `neru HH:MM-HH:MM` / `phone HH:MM-HH:MM`（終わりが起きた時刻）、`auto HH:MM-HH:MM`（自動判定。睡眠の記録には数えない）、空、`no-gap`。乗務日が `-` の `neru` / `okita` は行の時刻がそのまま寝た／起きた時刻で、メモは `free-day`（24 時間以内に乗務が無い日の記録）、`late 出所`（後から足した記録。`shl neru 11/7 23:30`）、`clear 出所`（同じ時刻・同じ種類の記録を消す。`shl neru 11/7 23:30 取消`）。後から足した行は行の時刻が過去になるので、**ファイルの並びは時刻順とは限らない**。`enforce_release` のメモは解除の出所（`tray`＝ネイティブ版のトレイの「緊急解除」）で、解除した宣言が無いときの乗務日は `-` |
| `log/pc-YYYY-MM.jsonl` | 5 分おきの見張り（`guard.ps1`。切り替え後はネイティブ版の Agent） | `{"t", "idle": 秒, "game": 0｜1}`。ネイティブ版の Agent は、この最後の行の時刻で「前の見張りから 5 分たったか」を見る |
| `log/app-YYYY-MM.jsonl` | ネイティブ版の Agent（10 秒ごとに測り，時計の 5 分の区切りで 1 行） | `{"t", "span": 秒, "apps": {"<exe名>": {"cat": "game"｜"dev"｜"adobe", "act": 秒, "cpu": %}}, "other": 秒}`．`t` = 区間の終わり（秒まで），`span` = 測れた秒数，`act` = アクティブ（前面 ＋ 入力か CPU）だった秒数，`cpu` = 子プロセスを含む平均使用率（PC 全体 = 100．小数 1 桁），`other` = 登録もゲームでもないアプリがアクティブだった秒数．名前が載るのは登録した exe と自動判定のゲームだけ．レポートは `t − span/2` の時刻の日・時に `act` を足す |
| `agent.log` / `agent.log.1` | ネイティブ版の Agent | `guard.log` と同じ役割のログ（`yyyy-MM-dd HH:mm:ss 文`、1 行 1 件）。1 MB を超えたら `.1` に回す。個人データではないが実行フォルダにあるのでコミットしない |
| `log/claude-YYYY-MM.jsonl` | Claude Code hook | `{"t", "k": "p"(プロンプト)｜"kw"(キーワード)}`。**本文は記録しない** |
| `usage-pc.json` / `usage-claude.json` | 見張り／hook | 連続稼働の計算用（内部状態） |
| `reports/report_<開始>_<終了>.html/.pdf` | 週次レポート | 締め期間ごとのレポート。保存期間を過ぎたら自動削除 |

## 2. iPhone ショートカット（今動いてる）

`iCloud Drive/Shortcuts/mood.csv`（Windows からは `iCloudDrive\iCloud~is~workflow~my~workflows\mood.csv`）に 1 行ずつ追記。

```
2026-11-01 11:40,okita,
2026-11-01 11:42,2,
2026-11-04 07:05,neru,
2026-11-04 06:55,kitaku,
```
2 列目: `-2`〜`2`（気分）／`neru` `okita` `kitaku`（日本語 `ねる` `おきた` `ただいま` も可）／`yasumi` `deta`（終わった乗務の事後報告。`yasumi:2026-11-02` のように乗務日を付けられる。無ければ記録時点で直近に終わった乗務）。3 列目: メモ（カンマを含んでもよい）。

`neru` / `okita` の行は、レポートが睡眠の記録として**全部**読む（乗務の有無に関係なく。1 列目の日時がそのまま寝た／起きた時刻）。1 列目は過去の日時でもよい（後から思い出して足すショートカットを作れる）。同じ日時・同じ種類の行が重なっても 1 件。間違えた行はファイルを直さずに PC から `shl neru 11/7 23:30 取消` で消す（追記のみの決まりを守る）。

---

## 3. スマホ版との同期（Android 版で追加する予定・案）

Google ドライブ（PC では `H:` の「Google Drive」）に同期フォルダ `shiftlock-sync/` を置く。**書く人ごとにファイルを分ける**ので、競合も上書きも起きない。

```
shiftlock-sync/
  schedule.json                 PC が書く：この先 14 日の乗務と時刻（スマホが通知を予約する元）
  events-android-<端末ID>.jsonl  スマホが書く：記録（追記のみ）
  reports/                      PC が書く：週次レポートの PDF（スマホから開く・共有する）
```

### 3.1 `schedule.json`（PC → スマホ）
```json
{
  "generated": "2026-11-03T09:00:00",
  "settings": {"sleep_target_hours": 5, "sleep_min_hours": 4, "lastcall_minutes": 40, "auto_wake_hours": 4},
  "days": [
    {"date": "2026-11-04", "work": true, "status": null,
     "start_target": "2026-11-04T20:00", "start_latest": "2026-11-04T22:30",
     "depart_target": "2026-11-04T18:30", "depart_latest": "2026-11-04T21:00",
     "lastcall": "2026-11-04T12:50", "soft_close": "2026-11-04T13:30", "hard_close": "2026-11-04T17:00"},
    {"date": "2026-11-08", "work": false, "reason": "日曜"}
  ]
}
```
- 計算（逆算・寝られへん時間の除外・例外日）は **PC 版が正**。スマホはこの値で通知を予約するだけにする（ロジックを二重に持たない）
- スマホ側でもオフラインで動けるよう、14 日ぶん先に持たせる

### 3.2 `events-android-<端末ID>.jsonl`（スマホ → PC）
```
{"t": "2026-11-04T07:05", "type": "kitaku"}
{"t": "2026-11-04T07:10", "type": "neru"}
{"t": "2026-11-04T11:40", "type": "okita"}
{"t": "2026-11-04T11:42", "type": "mood", "v": 2, "memo": "アイデア止まらん"}
{"t": "2026-11-04T17:05", "type": "yasumi"}
{"t": "2026-11-04T17:06", "type": "undo", "ref": "2026-11-04T17:05"}
{"t": "2026-11-05T09:00", "type": "day", "date": "2026-11-12", "kind": "off", "note": "通院"}
{"t": "2026-11-05T10:00", "type": "screen", "on": "2026-11-05T09:58", "off": "2026-11-05T10:00"}
```
- `type`: `kitaku` `neru` `okita` `mood` `yasumi` `deta` `deru` `undo` `day` `screen`
- `yasumi` / `deta` は `date`（乗務日 `YYYY-MM-DD`）を任意で持てる。無ければ記録時点で直近に終わった乗務（PC 版の `late_report` と同じ解決）。`deta` = 出た／出発した
- `screen` はスマホの画面を使ってた区間（自動『起きた』の判定用。PC の無操作区間と合わせて「寝てた」を判定する。**レポートの睡眠には使わない**。レポートは本人が記録した `neru` / `okita` だけ）
- PC 版は mood.csv と同じ仕組み（`import_phone_marks`）でこれを取り込む。取り込みは何回やっても同じ結果になること（冪等）

### 3.3 未確定（実装前に確かめる）
- Android アプリから Drive のこのフォルダに書く方法（Drive API の `drive.file` スコープで足りるか。PC 側が書いた `schedule.json` をアプリが読めるか）。だめなら Firebase か別の同期手段にする
- PC の Google ドライブの実際のパス（`H:\マイドライブ\shiftlock-sync` か）
