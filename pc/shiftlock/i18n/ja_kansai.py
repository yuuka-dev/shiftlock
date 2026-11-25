"""関西弁の文面（定数）。既定の言語。土台は ja.py（標準語）で、ここに無いキーはそちらの文が出る。

使い方: 各モジュールで `from . import i18n as M` → `M.PHASE_SOFT_USER.format(soft=..., target_h=..., hard=...)`

決まり
- プレースホルダは名前付き `{name}` だけ（位置引数 `{}` と書式指定 `{x:g}` は使わない。数や日付は呼ぶ側で文字列にしてから渡す）。
  文字どおりの `{` `}` は `{{` `}}`
- `_USER` はユーザーに見せる文、`_CTX` は Claude に渡す文脈（additionalContext）
- 接頭辞は領域（モジュール）ごと: PHASE_ AUTOWAKE_ PHONE_ SLEEP_ INTENT_ RECORDS_ CONFIRM_ ENFORCE_ NOTIFY_ DAYS_ CMD_ STATUS_
  HOOK_ TICK_ CONFIG_ CONSOLE_ MENU_ CAL_ REPORT_ CLI_ TIME_。複数の領域で同じ文は COMMON_ に 1 つ置いて、
  各領域の名前（RECORDS_SHUPPATSU_NO_PLAN = COMMON_NO_PLAN など）から参照する（片方だけ変えたくなったらそこで分ける）
- `M.X` は使う場所で毎回引く。モジュール直下で `LABEL = M.INTENT_LABEL` のように束縛しない（import 時の言語で固定されて、
  設定の `lang` が効かなくなる）。判定・保存・順番に使うキーの集合（ENFORCE_ACTIONS、LEARNED_KINDS、SOUND_KIND_KEYS など）は
  ここに置かない。ここの dict はキー → 表示の言葉だけ
- ここに置かへんもの: 入力の語彙と正規表現（keywords.py / intents.py）、ファイルに書く値（events.log のメモ、カレンダーの
  ソース名「カレンダー」、via）、設定の既定値（off_keywords など）、日付・時刻の表記（%m/%d や WD_JA は timeutil.py）、
  HTML/CSS の構造（report_html.py）
"""

PREFIX = "[shiftlock] "

# ================================================================ 言語（設定メニューの表示名）

LANG_LABEL = {"ja": "標準語", "ja_kansai": "関西弁", "inm": "淫夢語録"}   # lang のキー → 表示の言葉

# ================================================================ 共通（複数のモジュールで同じ文）

COMMON_NO_PLAN = PREFIX + "24時間以内に乗務の予定はないで。"
COMMON_BAD_DATE = PREFIX + "日付が読めへん: {err}"
COMMON_ASK_OK = "  ええ？ (y/N): "
COMMON_NONE = "（なし）"
COMMON_DAY_RULE = "曜日ルールどおり"
COMMON_PAID_MARK = "（有給）"   # 休みの記録に有給の印が付いたときに文末に添える
COMMON_DAY_KIND_LABEL = {"off": "休み", "work": "出勤", None: COMMON_DAY_RULE}   # days.json の種別 → 表示の言葉

# ================================================================ timeutil（時間の表記）

TIME_DUR_HM = "{h}時間{m}分"
TIME_DUR_M = "{m}分"

# ================================================================ plan（フェーズ）

PHASE_LASTCALL_USER = "就寝目安 {soft} まであと{remain}。20〜30分級の長い処理はもう投げんとき。"
PHASE_LASTCALL_CTX = PREFIX + "乗務前の就寝リミットまで残り{remain}。完了まで20分以上かかりそうなタスクは開始せず、ユーザーに確認すること。"
PHASE_SOFT_USER = "就寝目安 {soft} 過ぎてるで（目標睡眠 {target_h}h）。{hard} からロックかかるで。"
PHASE_SOFT_CTX = PREFIX + "乗務前の就寝リミットを過ぎている。新しい作業は始めず、今の作業を最小限で締めて続きのTODOを短く残し、休むよう一言促すこと。"
PHASE_HARD_USER = "最低ライン（{min_h}h）も切ったで。ロック中（〜{latest}）。寝るか、休むなら会社に連絡して『今日休む』。寝てたなら『起きた』。"
PHASE_GO_USER = "出発目安 {depart}。そろそろ着替えよか。"
PHASE_GO_CTX = PREFIX + "ユーザーはまもなく乗務に出発する。作業は短く区切ること。"
PHASE_GO_LAST_USER = "最終出発 {depart}（出庫 {latest}）。もう出な間に合わへんで。"
PHASE_GO_LAST_CTX = PREFIX + "ユーザーは今すぐ乗務に出発する必要がある。作業を中断して送り出すこと。"

# ================================================================ autowake（自動『起きた』）

AUTOWAKE_OKITA = ("おはよう。{start}〜{end}（{dur}）寝てたっぽいから、自動で『起きた』にしといたで。"
                  "出発目安 {depart}（最終 {latest}）。")

# ================================================================ phone（iPhone の記録）

PHONE_LATE_PREFIX = "（iPhone）"
PHONE_OKITA = "おはよう（iPhone）。{sleep_at}〜{when}（{dur}）。出発目安 {depart}。"
PHONE_MOOD_LABEL = {-2: "どん底", -1: "しんどい", 0: "ふつう", 1: "アゲ", 2: "止まらん"}
PHONE_MOOD_GLYPH = {-2: "▼▼", -1: "▼", 0: "・", 1: "▲", 2: "▲▲"}

# ================================================================ sleeplog（後から足す睡眠の記録）

SLEEP_KIND = {"neru": "寝た", "okita": "起きた"}   # 記録の種類 → 表示の言葉
SLEEP_USAGE = PREFIX + "時刻が 1 つ要るで（例: shl neru 23:30 / shl okita 11/7 7:00。消すときは末尾に 取消）。"
SLEEP_BAD_DATE = COMMON_BAD_DATE
SLEEP_FUTURE = PREFIX + "{when} はまだ来てへんで。"
SLEEP_TOO_OLD = PREFIX + "{when} は古すぎて記録を残されへん（{days} 日より前）。"
SLEEP_RECORDED = PREFIX + "{when} に{what}、で記録したで（レポート用の記録だけ。ロックや乗務の状態は変わらへん）。"
SLEEP_PAIR = " {start}〜{end}（{dur}）の睡眠として数えるで。"
SLEEP_CLEARED = PREFIX + "{when} に{what}、の記録を消したで。"
SLEEP_CLEAR_NONE = PREFIX + "{when} に{what}、の記録は無いで。"

# ================================================================ intents（話し言葉・学習）

INTENT_LABEL = {"kitaku": "帰宅", "neru": "就寝", "okita": "起床", "yasumi": "休み", "worked": "出勤"}
INTENT_ASK_LABEL = {"kitaku": "帰ってきたってこと", "neru": "寝るってこと", "okita": "起きたってこと"}
INTENT_LABEL_SEP = "・"   # 『帰宅・就寝』のように並べるときの区切り
INTENT_LEARNED_HEAD = "覚えた言い方"
INTENT_LEARNED_LABELS = {"kitaku": "帰宅", "neru": "就寝", "okita": "起床", "none": "何もしない"}   # learned.json のキー → 表示
INTENT_LEARNED_LINE = "  {name}: {phrases}"
INTENT_LEARNED_SEP = " / "
INTENT_FORGOT = "『{core}』を忘れたで"
INTENT_NOT_LEARNED = "『{core}』は覚えてへんかった"

# ================================================================ records（自己申告の記録・取り消し）

RECORDS_LEARNED_YES = PREFIX + "了解、『{core}』は{label}として覚えたで。{msg}"
RECORDS_LEARNED_OTHER = PREFIX + "ほな『{core}』は{label}として覚えたで。{msg}"
RECORDS_LEARNED_NONE = PREFIX + "了解、『{core}』は何もせえへん言い方として覚えたで。"
RECORDS_UNDO_NOTHING = PREFIX + "取り消せる記録はないで（1時間以内のぶんだけ）。"
RECORDS_UNDO_DONE = PREFIX + "さっきの『{what}』の記録を取り消したで。"
RECORDS_KITAKU_FREE = PREFIX + "おかえり。記録だけしといたで。"
RECORDS_KITAKU = PREFIX + "おかえり。{when} 帰宅で記録したで。"
RECORDS_NERU_FREE = PREFIX + "おやすみ。24時間以内に乗務はないから、記録だけしといたで。"
RECORDS_NERU = (PREFIX + "おやすみ。{when} から数えるで。{hours}h 以上寝てから PC を触ったら自動で『起きた』にするし、"
                "起きたら『おきた』でもええよ。出発目安 {depart}（最終 {latest}）。")
RECORDS_OKITA_FREE = PREFIX + "おはよう。24時間以内に乗務はないから、記録だけしといたで。"
RECORDS_OKITA_ENFORCE_GATE = PREFIX + "強制中は寝た記録（『ねる』か 2.5 時間以上の離席）が要るで。ほんまに緊急なら kaijo.cmd か STOP.txt。"
RECORDS_OKITA_SLEPT = PREFIX + "おはよう。{sleep_at}〜{when}（{dur}）寝てたで。ロック解除、出発目安は {depart}（最終 {latest}）。"
RECORDS_OKITA_SHORT = " ……最低ライン（{min_h}h）には届いてへんで。"
RECORDS_OKITA = PREFIX + "おはよう。ロック解除、出発目安は {depart}（最終 {latest}）。"
RECORDS_OKITA_NO_GAP = " ……でもPCの記録だとまとまって離席してへんっぽいで。ほんまに寝た？"
RECORDS_OKITA_KEEP_SLEPT = PREFIX + "おはよう。{sleep_at}〜{when}（{dur}）寝てたで。{day} の乗務は{what}のままにしとくで。"
RECORDS_OKITA_KEEP = PREFIX + "おはよう。記録だけしといたで。{day} の乗務は{what}のままにしとくで。"
RECORDS_SHUPPATSU_NO_PLAN = COMMON_NO_PLAN
RECORDS_SHUPPATSU_EARLY = PREFIX + "まだ早いで（{day} の出発目安 {depart}）。出発の 2 時間前からにしてな。"
RECORDS_SHUPPATSU = (PREFIX + "行ってらっしゃい。{day} の乗務を出発済みにしたで（通知とロックは止まる）。"
                     "帰ったら『ただいま』、結局休んだなら『今日休む』。違ったら『今のなし』か『やっぱ出る』。")
RECORDS_LATE_BAD_DATE = COMMON_BAD_DATE
RECORDS_LATE_NONE = PREFIX + "直近 {days} 日に終わった乗務はないで。日付付きなら『11/3 休んだ』みたいに。"
RECORDS_LATE_FUTURE = PREFIX + "{label} は未来やで。"
RECORDS_LATE_TOO_OLD = PREFIX + "{label} は古すぎて記録できへん（{days} 日より前）。"
RECORDS_LATE_KIND_YASUMI = "休み"
RECORDS_LATE_KIND_WORKED = "出た"
RECORDS_LATE_ALREADY = PREFIX + "{label} はもう{what}になってるで。"
RECORDS_LATE_NOT_YET = PREFIX + "{label} の乗務はまだ終わってへんで。休むなら『今日休む』、出発するなら『行ってきます』。"
RECORDS_LATE_OFF_DAY = PREFIX + "{label} はもともと休みやで。"
RECORDS_LATE_YASUMI = PREFIX + "{label} は休んだことにしたで（事後報告）。今月の休み {n} 回目。違ったら『今のなし』。"
RECORDS_LATE_WORKED = PREFIX + "{label} は出たことにしたで。違ったら『今のなし』。"

RECORDS_YASUMI_PAID = COMMON_PAID_MARK
RECORDS_PAID_SET = PREFIX + "{label} の休みを有給にしたで（当日欠勤には数えへん）。"
RECORDS_PAID_UNSET = PREFIX + "{label} の休みの有給を外したで（当日欠勤に数える）。"
RECORDS_PAID_NO_YASUMI = PREFIX + "{label} は休みの記録がないで。先に『11/3 休んだ』か『今日休む』で。"
RECORDS_PAID_BAD_DATE = COMMON_BAD_DATE

# ================================================================ attendance（当日欠勤の目安）

ABSENCE_OVER = " ！ 直近 {shifts} 乗務で当日欠勤 {n} 回（有給は除く）。目安の {limit} 回に達してるで。"

# ================================================================ confirm（終わった乗務の確認）

CONFIRM_HOME_HINT = "PC の記録やと家におったっぽいで（Parsec なら気にせんでええ）。"
CONFIRM_MORE = "（ほか {n} 件は shl kakunin で）"
CONFIRM_USER = "{label} の乗務、出た？休んだ？ Claude Code か shl で『出た』『休んだ』を打ってな。{hint}{more}"
CONFIRM_CTX = (PREFIX + "返事の最後に一言だけ『{label} の乗務は出た？休んだ？（出た／休んだ）』と聞くこと。{hint}{more}"
               "記録は shiftlock がやるので、ユーザーが答えても自分で記録・実行しようとしないこと。")
CONFIRM_KAKUNIN_NONE = "未確認の乗務はないで。"
CONFIRM_KAKUNIN_HEAD = "未確認の乗務（『出た』『休んだ』か、shl deru 11/3 / shl yasumi 11/3 で記録）:"

# ================================================================ enforce（強制モード）

ENFORCE_LABEL = {"lock": "ロック", "sleep": "スリープ", "shutdown": "シャットダウン", "throttle": "アプリの減速", "suspend": "アプリの一時停止"}
ENFORCE_CAUTION = {"throttle": "上限がかかったアプリは遅くなる。終了はせえへん。",
                   "suspend": "止まったアプリは再開するまで操作できへん。終了はせえへん。",
                   "sleep": "スリープは遠隔（Parsec）から戻せへん。",
                   "shutdown": "シャットダウンは未保存の作業が消える（60 秒の猶予のあと強制終了）。遠隔（Parsec）から戻せへん。"}
ENFORCE_TERMS_WHAT = {"lock": "ロック（ログインし直せば続きはできる）",
                      "sleep": "スリープ（未保存の作業は残るけど、遠隔からは戻せへん）",
                      "shutdown": "シャットダウン（60 秒の猶予のあと強制終了。未保存の作業は消える。遠隔からは戻せへん）",
                      "throttle": "アプリの減速（対象のアプリの CPU 使用率の上限を、段階的に下げる。終了はせえへん。解除したら戻る）",
                      "suspend": "アプリの一時停止（上限を下げたあと、最低ラインで対象のアプリを止める。終了はせえへん。解除したら続きから動く）"}
ENFORCE_TERMS = ("強制モードの内容（{day} の乗務）\n"
                 "  何を    : {what}\n"
                 "  いつから: 最低ライン {hard} 以降に PC を触ったら、{grace} 分前に予告してから\n"
                 "  どれだけ: 最初の実行から {hours} 時間（{end} ごろまで）。戻ってきたら {relock} 分の予告のあと、また実行（最大 {max_runs} 回）\n"
                 "  解除    : kaijo.cmd（shl kaijo）か、実行フォルダに STOP.txt を置く。解除は記録に残る\n"
                 "  注意    : 『おきた』は寝た記録（『ねる』か 2.5 時間以上の離席）があるときだけ効く。リモート（Parsec）の検出は未対応で、遠隔操作中でも発動する")
ENFORCE_TERMS_CAUTION_LINE = "  ！      : {caution}"
ENFORCE_TERMS_LIMIT = ("強制モードの内容（{day} の乗務）\n"
                       "  何を    : {what}\n"
                       "  段階    : 上限の割合は、そのアプリの直近の使用率（減速を始める前の最大値）に対するもの。最低ラインは {hard}\n"
                       "{stages}\n"
                       "  対象    : ゲーム（Agent が自動で判定）と、足したアプリだけ\n"
                       "{games}\n"
                       "{apps}\n"
                       "  戻る    : 『行ってきます』か『今日休む』を記録したとき、または {back} になったら、全部元に戻るで\n"
                       "  解除    : kaijo.cmd（shl kaijo）か、実行フォルダに STOP.txt を置く。すぐ元に戻り、解除は記録に残る")
ENFORCE_TERMS_LIMIT_STAGE = "            段階 {step}: {at} から {what}"
ENFORCE_TERMS_LIMIT_PERCENT = "上限 {percent}%"
ENFORCE_TERMS_LIMIT_STOPPED = "一時停止"
ENFORCE_TERMS_LIMIT_GAMES = "  ゲーム  : {names}"
ENFORCE_TERMS_LIMIT_GAMES_NONE = "  ゲーム  : まだ見たゲームはないで（Agent が動いている間に見たものだけ載る）"
ENFORCE_TERMS_LIMIT_EXCLUDED = "  外した  : {names}（アンチチートの印があるので手を加えへん）"
ENFORCE_TERMS_LIMIT_UNKNOWN = "  外した  : {names}（アンチチートを調べられへんので手を加えへん）"
ENFORCE_TERMS_LIMIT_APPS = "  アプリ  : {names}（足したもの）"
ENFORCE_TERMS_LIMIT_APPS_NONE = "  アプリ  : 足したものはないで"
ENFORCE_TERMS_LIMIT_ANTICHEAT_NOTE = "  注意    : アンチチートの判定は一覧との照合やから、見逃すことがある。見逃したゲームにも手を加えてしまうし、ゲーム側がどう反応するかは分からへん"
ENFORCE_TERMS_LIMIT_SUSPEND_NOTE = "  注意    : 止まったアプリの窓は『応答なし』になる。内容は残るけど、保存してへん作業は先に保存しとき"
ENFORCE_LIMIT_NO_AGENT = "  ！      : Agent が動いてへんみたいやから、このままでは効かへん（shiftlock-agent を起動しとき）"
ENFORCE_LIMIT_NO_HOURS = PREFIX + "アプリの減速・一時停止に時間の指定はいらんで（最低ラインまでの時間で段階が決まる）。時間を付けんと指定してな。"
ENFORCE_LIMIT_BAD_STEPS = PREFIX + "設定 limit.steps は、1 以上 100 未満の整数を大きい順に 4 個（例 [75, 50, 25, 10]）。直してからもう一回な。"
ENFORCE_DIALOG_TEXT_LIMIT = "{day} の乗務を強制モード（{label}）にする。ええ？\n解除は kaijo.cmd か STOP.txt。"
ENFORCE_DECLARED_LIMIT = PREFIX + "了解。{day} の乗務は強制モード（{label}）。解除は kaijo.cmd か STOP.txt。{extra}"
ENFORCE_STATUS_LINE_LIMIT = "  {day}: {label} / {state} / 実行 {runs} 回{last}"
ENFORCE_DIALOG_TITLE = "shiftlock 強制モードの同意"
ENFORCE_DIALOG_TEXT = "{day} の乗務を強制モード（{label} {hours}h）にする。ええ？\n解除は kaijo.cmd か STOP.txt。"
ENFORCE_BAD_ACTION = PREFIX + "action は lock / sleep / shutdown / throttle / suspend のどれかで。"
ENFORCE_BAD_HOURS = PREFIX + "時間は 2 や 3h みたいに書いてな。"
ENFORCE_HOURS_RANGE = PREFIX + "時間は 0.5〜12 で。"
ENFORCE_NO_PLAN = COMMON_NO_PLAN
ENFORCE_SHIFT_OFF = PREFIX + "{day} の乗務は休み／出発済みやで。"
ENFORCE_ALREADY_RUNNING = PREFIX + "もう実行が始まってる。止めるなら kaijo.cmd。"
ENFORCE_NEED_TTY = PREFIX + "強制の宣言は、対話できる端末（pwsh / cmd のウィンドウ）から shl enforce on で。パイプや Claude Code からは受け付けへん。"
ENFORCE_ASK_OK = COMMON_ASK_OK
ENFORCE_DECLINED = PREFIX + "やめといたで（宣言なし）。"
ENFORCE_DIALOG_DENIED = PREFIX + "Windows のダイアログで『はい』が押されへんかったので宣言なし。"
ENFORCE_STOP_NOTE = " いまは STOP.txt があるので効かへん（shl enforce resume で再開）。"
ENFORCE_DECLARED = PREFIX + "了解。{day} の乗務は強制モード（{label} {hours}h）。解除は kaijo.cmd か STOP.txt。{extra}"
ENFORCE_OFF_NONE = PREFIX + "宣言はないで。"
ENFORCE_OFF_DONE = PREFIX + "{day} の強制モードを取り消したで。"
ENFORCE_RELEASE_ALREADY = PREFIX + "もう解除中やで（STOP.txt あり）。再開は shl enforce resume。"
ENFORCE_RELEASED = PREFIX + "強制を解除したで（STOP.txt）。再開は shl enforce resume、次の乗務で使うなら shl enforce on からやり直し。"
ENFORCE_RESUME_NONE = PREFIX + "解除中やないで（STOP.txt は無い）。"
ENFORCE_RESUMED = PREFIX + "再開したで（STOP.txt を消した）。解除した乗務の宣言はやり直し（shl enforce on）。"
ENFORCE_SUFFIX_STOP = "（STOP.txt があるので今は効かへん）"
ENFORCE_SUFFIX_LIMIT = " 今日は強制モード（{label}）やで{stop}。解除は kaijo.cmd。"
ENFORCE_SUFFIX = " 今日は強制モード（{label} {hours}h）やで{stop}。解除は kaijo.cmd。"
ENFORCE_STATUS_HEAD = PREFIX + "強制モード（宣言は shl enforce on、解除は kaijo.cmd か STOP.txt）"
ENFORCE_STATUS_DEFAULT = "  既定: {label} / {hours}h / 予告 {grace}分（settings の 19）"
ENFORCE_STATUS_DEFAULT_LIMIT = "  既定: {label}（settings の 19）"
ENFORCE_STATUS_STOP = "  STOP.txt: {stop}"
ENFORCE_STATUS_STOP_ON = "あり（解除中。shl enforce resume で再開）"
ENFORCE_STATUS_STOP_OFF = "なし"
ENFORCE_STATE_RELEASED = "解除済み {at}"
ENFORCE_STATE_RUNNING = "実行中"
ENFORCE_STATE_WARNED = "予告済み"
ENFORCE_STATE_CONSENTED = "同意済み"
ENFORCE_STATE_PENDING = "同意待ち"
ENFORCE_STATUS_LINE = "  {day}: {label} {hours}h / {state} / 実行 {runs} 回{last}"
ENFORCE_STATUS_LAST = "（最後 {at}）"
ENFORCE_STATUS_NONE = "  宣言: なし"
ENFORCE_REWARN = "{relock}分後にまた{label}するで（強制モード）。やめるなら kaijo.cmd。"
ENFORCE_WARN = "強制モード: {grace}分後に{label}するで。寝るか、やめるなら kaijo.cmd。"
ENFORCE_WARN_SOON = "あと{mins}分で{label}するで（強制モード）。やめるなら kaijo.cmd。"
ENFORCE_RUN = "{label}するで（強制モード）"

# ================================================================ notify（通知音）

NOTIFY_SOUND_MISSING = "通知音のファイルが見つからへん: {path}"

# ================================================================ days（例外日）

DAYS_WORK = "出"
DAYS_OFF = "休"
DAYS_NOTE = "（{why}）"
DAYS_EXCEPTION_MARK = "*"
DAYS_CELL = "{md}{wd}{mark}{extra}"

# ================================================================ commands（キーワード応答・状況）

CMD_BAD_DATE = COMMON_BAD_DATE
CMD_DAY_KIND_LABEL = COMMON_DAY_KIND_LABEL
CMD_DAY_SET = PREFIX + "{label} を{kind}にしたで。\n{schedule}"
CMD_SCHEDULE = PREFIX + "この先2週間（* = 例外日）\n{schedule}"
CMD_NO_PLAN = COMMON_NO_PLAN
CMD_YASUMI = PREFIX + "{day} は休みにしたで。ロックも警告も解除。会社への連絡だけ忘れんとってな。（今月の休み {n} 回目）"
CMD_YASUMI_PAID = COMMON_PAID_MARK
CMD_UNDO_WORKED = "出発済み"
CMD_UNDO_YASUMI = "休み"
CMD_UNDO = PREFIX + "{day} の{what}を取り消したで。"
STATUS_HEAD = PREFIX + "いま {now} / フェーズ: {phase}"
STATUS_SHIFT = "  乗務 {day}（{status}）: 出庫 {start_target}〜{start_latest} / 出発 {depart_target}〜{depart_latest}"
STATUS_TIMES = "  就寝目安 {soft}（{target_h}h） / 最低ライン {hard}（{min_h}h） / 就寝準備 {lastcall}〜"
STATUS_NO_SHIFT = "  予定されてる乗務はもうないで"
STATUS_YASUMI_COUNT = "  今月の休み: {n} 回"
STATUS_ABSENCE = "  当日欠勤（有給除く）: 直近 {shifts} 乗務で {n} 回（有給 {paid}）/ 目安 {limit} 回{over}"
STATUS_ABSENCE_OVER = " ！"
STATUS_CAL = "  カレンダー「{name}」: 最終同期 {fetched}{error}"
STATUS_CAL_NEVER = "未"
STATUS_CAL_ERROR = " / エラー {error}"
STATUS_UNCONFIRMED = "  未確認の乗務: {days}（『出た』『休んだ』で記録）"
STATUS_UNCONFIRMED_SEP = ", "
STATUS_ENFORCE_RELEASED = "解除済み"
STATUS_ENFORCE_RUNS = "実行 {n} 回"
STATUS_ENFORCE_ITEM = "{day} {label} {hours}h（{state}）"
STATUS_ENFORCE_ITEM_LIMIT = "{day} {label}（{state}）"
STATUS_ENFORCE_STOP = "STOP.txt で解除中 "
STATUS_ENFORCE = "  強制モード: {stop}{items}"
STATUS_ENFORCE_SEP = "、"
STATUS_SCHEDULE_HEAD = "  この先2週間（* = 例外日）"

# ================================================================ hook（Claude Code の hook）

HOOK_CONFIG_ERROR = PREFIX + "shifts.json 読めへん: {err}\n"
HOOK_FUZZY_UNDO_HINT = "{msg}（違ったら『今のなし』）"
HOOK_HARD_FUZZY_HEAD = PREFIX + "{msg}（違ったら『今のなし』）\n"
HOOK_HARD_ASK_HEAD = PREFIX + "『{core}』って{ask_label}？（うん／ちゃう で答えてな）\n"
HOOK_FUZZY_CTX = PREFIX + "ユーザーの発言から『{what}』を記録した。普段どおり返事しつつ、記録したことには一言だけ触れること（長い説明はしない）。"
HOOK_ASK_CTX = (PREFIX + "この発言が『{label}』の意味か判定できへんかった。普段どおり返事して、"
                "最後に一言だけ『{ask_label}？（うん／ちゃう）』と聞くこと。返事の処理は shiftlock がやるので、"
                "ユーザーが答えても自分で何かを実行・記録しようとしないこと。")
HOOK_NAG_USER = "Claude Code 連続{dur}やで。そろそろやめよ♡"
HOOK_NAG_CTX = PREFIX + "ユーザーは連続{dur}作業している。応答の最後に一言だけ休憩を勧めること。"
HOOK_USER_SEP = " / "   # systemMessage に複数の文を並べるときの区切り

# ================================================================ tick（5 分おきの見張り）

TICK_NAG_GAME = "ゲーム"
TICK_NAG_PC = "PC"
TICK_NAG = "{what}連続{dur}やで。そろそろやめよ♡"
TICK_ENFORCE_ERROR = "強制モードの判断に失敗（発動せず）: {err}"
TICK_SOUND_CONFIG_ERROR = "通知音の設定が読めへん: {err}"
TICK_SOUND_NUMBER_ERROR = "notify.sound_volume / sound_max_seconds が数字やない"

# ================================================================ config（設定の読み込み）

CONFIG_BAD_WEEKDAY = "work_weekdays に知らん曜日: {bad}"
CONFIG_NOT_DICT = "{key} は {{...}} の形で書いてな（settings.cmd で直せる）"
CONFIG_SOURCES_NOT_LIST = "calendar.sources はリスト [ ... ] で書いてな"

# ================================================================ console（対話入力の部品）

CONSOLE_ASK = "  {prompt} [{cur}]: "
CONSOLE_ERROR = "  ✗ {err}"
CONSOLE_NUM_RANGE = "{lo}〜{hi} で"
CONSOLE_WD_HINT = "『月火水木金土』みたいに書いてな"
CONSOLE_LIST_TITLE = "  --- {title} ---"
CONSOLE_LIST_ITEM = "   {i}. {x}"
CONSOLE_LIST_EMPTY = "   " + COMMON_NONE
CONSOLE_LIST_PROMPT = "  a) 追加  d番号) 削除（例 d2）  Enter) 戻る: "
CONSOLE_LIST_ADD = "  追加するもの（パスはエクスプローラーの『パスのコピー』をそのまま貼ってOK）: "

# ================================================================ settings_menu（設定メニュー）

MENU_NONE = "なし"
MENU_UNSET = "未設定"
MENU_YES = "はい"
MENU_NO = "いいえ"
MENU_NO_SLEEP_ITEM = "{wd}{frm}-{to}{label}"
MENU_NO_SLEEP_SEP = ", "
MENU_CAL_COUNT = "{n} 本"
MENU_HEAD = "\n=== shiftlock 設定 ==="
MENU_1 = " 1) 出勤する曜日          {wd}"
MENU_2 = " 2) 出庫（目標/最終）     {target} / {latest}"
MENU_3 = " 3) 準備時間              {prep}分"
MENU_4 = " 4) 睡眠（目標/最低）     {target_h}h / {min_h}h"
MENU_5 = " 5) 就寝準備              就寝目安の{lastcall}分前"
MENU_6 = " 6) 最低ライン後のClaude Code {block}"
MENU_6_BLOCK = "止める"
MENU_6_WARN = "警告だけ"
MENU_7 = " 7) 寝られへん時間        {ns}"
MENU_8 = " 8) 連続稼働の通知        {after}分後、{every}分おき"
MENU_9 = " 9) ゲーム判定            Steam {steam} / 追加 {dirs} / 除外フォルダ {xdirs} / 除外名 {xnames} / 名前指定 {procs}"
MENU_9_AUTO = "自動"
MENU_9_OFF = "オフ"
MENU_10 = "10) 気分CSVのパス         {path}"
MENU_11 = "11) Googleカレンダー      {cal}（休み: {off}、出勤: {work}）"
MENU_12 = "12) 休み・出勤の例外日    （追加/削除/一覧）"
MENU_13 = "13) 保存期間              例外日{days}日 / ログ{logs}日"
MENU_14_MODES = {"auto": "PC無操作でも判定", "neru": "『ねる』の記録があるときだけ", "off": "オフ"}
MENU_14 = "14) 自動『起きた』判定    {mode}（{hours}h以上・乗務明けは{home}から数える）"
MENU_15 = "15) 覚えた言い方          {n} 件（聞き返して覚えた『寝る/起きた/帰った』の言い方。その場で保存）"
MENU_16 = "16) 訪看さん向けレポート  {closing} / {keep}か月保存（毎週火曜 9:00 に自動作成）"
MENU_16_CLOSING = "{closing}日締め"
MENU_16_MONTHLY = "月単位（1日〜月末）"
MENU_17 = "17) 通知音                {sound}"
MENU_17_SET = "{n} 種類に設定"
MENU_17_UNSET = "未設定（トーストの既定音）"
MENU_18 = "18) 乗務の確認            {ask}（直近{days}日 / 起きた記録があれば出たとみなす: {omw}）"
MENU_18_ASK = "聞く"
MENU_18_NO = "聞かない"
MENU_19 = "19) 強制モード（既定値）  {label} / {hours}h / 予告 {grace}分（宣言は shl enforce on、解除は kaijo.cmd か STOP.txt）"
MENU_20 = "20) 言葉づかい            {lang}"
MENU_21 = "21) 当日欠勤の目安        直近{shifts}乗務で{limit}回以上なら注意（有給は除く。0 で注意なし）"
MENU_FOOT = " s) 保存して終了   q) 保存せず終了"
MENU_PROMPT = "> "
MENU_SAVED = "保存したで。"
MENU_NOT_SAVED = "保存せずに終わるで。"
MENU_ENTER_KEEPS = "  （Enter だけで今のまま）"
MENU_UNKNOWN = "  ？ 番号で選んでな"
MENU_ASK_1 = "出勤する曜日（例: 月火水木金土）"
MENU_ASK_2A = "出庫の目標"
MENU_ASK_2B = "出庫の最終"
MENU_ASK_3 = "準備時間（分）"
MENU_ASK_4A = "目標睡眠（時間）"
MENU_ASK_4B = "最低睡眠（時間）"
MENU_ASK_5 = "就寝目安の何分前から"
MENU_ASK_6 = "止める？(y/n)"
MENU_7_CURRENT = "  今の設定: {ns}"
MENU_7_PROMPT = "  a) 追加  d) 全部消す  Enter) 戻る: "
MENU_7_WD = "曜日（1文字）"
MENU_7_FROM = "開始"
MENU_7_TO = "終了"
MENU_7_LABEL = "  名前（例: 訪問看護）: "
MENU_ASK_8A = "何分続いたら"
MENU_ASK_8B = "そのあと何分おき"
MENU_ASK_10 = "mood.csv のパス（エクスプローラーでコピーしたまま貼ってOK）"
MENU_ASK_13A = "例外日・休み記録を残す日数"
MENU_ASK_13B = "活動ログを残す日数"
MENU_ASK_16A = "締め日（1〜31、月末なら 31、0 で月単位）"
MENU_ASK_16B = "何か月残す"
MENU_16_NOTE = "  曜日・時刻を変えるときは install-task.ps1 -ReportDay Monday -ReportTime 08:30 みたいに登録し直してな"
MENU_ASK_18A = "終わった乗務の『出た？休んだ？』を聞く？(y/n)"
MENU_ASK_18B = "何日前まで"
MENU_ASK_18C = "起きた記録があれば出たとみなす？(y/n)"
MENU_14_BAD = "1/2/3 で"
MENU_14_OPT1 = "  1) PC無操作でも判定（ご飯や外出で4h超えると誤判定もありえる）"
MENU_14_OPT2 = "  2) 『ねる』を記録したときだけ（Claude Code / iPhone / neru.cmd）"
MENU_14_OPT3 = "  3) オフ（毎回『おきた』を打つ）"
MENU_ASK_14A = "どれにする"
MENU_ASK_14B = "乗務明け、遅くても何時には帰ってる？"
MENU_ASK_14C = "何時間無操作で『寝てた』とみなす？"
MENU_20_OPT = "  {i}) {label}"
MENU_20_NOTE = "  （保存したあと、次に動くときから変わるで）"
MENU_ASK_20 = "どれにする"
MENU_20_BAD = "番号で"
MENU_ASK_21A = "何乗務ぶんを見る"
MENU_ASK_21B = "何回以上で注意（0 でなし）"
MENU_GAMES_INTRO1 = "  ゲーム判定: 起動中のプロセスの exe が、下のフォルダの中（奥深くまで）にあればゲーム扱い。"
MENU_GAMES_INTRO2 = "  新しく入れたゲームも勝手に対象になる。何が引っかかるかは scan-games.cmd で確認できる。"
MENU_GAMES_S = "  s) Steam ライブラリを自動で入れる: {on}"
MENU_GAMES_ON = "する"
MENU_GAMES_OFF = "しない"
MENU_GAMES_G = "  g) 追加フォルダ        {n} 件"
MENU_GAMES_X = "  x) 除外フォルダ        {n} 件"
MENU_GAMES_N = "  n) 除外する名前（* 可） {n} 件"
MENU_GAMES_P = "  p) プロセス名で直接指定 {n} 件（フォルダ外のゲーム用）"
MENU_GAMES_PROMPT = "  どれ？（Enter で戻る）: "
MENU_GAMES_G_TITLE = "追加フォルダ"
MENU_GAMES_X_TITLE = "除外フォルダ"
MENU_GAMES_N_TITLE = "除外する名前（例 *Launcher*）"
MENU_GAMES_P_TITLE = "プロセス名（.exe なし）"
MENU_CAL_HEAD = "  --- Google カレンダー（上が優先。同じ日は上のソースが勝つ）---"
MENU_CAL_ITEM = "   {i}. {name}  休み: {off}  出勤: {work}  {url}"
MENU_CAL_URL_YES = "URL あり"
MENU_CAL_URL_NO = "URL なし"
MENU_CAL_PROMPT = "  a) 追加  e番号) 編集  d番号) 削除  y) 保存して今すぐ全部同期  Enter) 戻る: "
MENU_CAL_NAME = "  名前 [{name}]: "
MENU_CAL_URL = "  iCal 形式の秘密のアドレス（Enter で今のまま）: "
MENU_CAL_OFF_KW = "休みと見なす言葉（カンマ区切り）"
MENU_CAL_WORK_KW = "出勤と見なす言葉（カンマ区切り）"
MENU_ENFORCE_NEED_TTY = "  強制モードの既定値は、対話できる端末（settings.cmd / shl settings）からだけ変えられる。"
MENU_ENFORCE_INTRO1 = "  強制モードは、本人が shl enforce on を実行して内容に y で同意した乗務にだけ効く。ここで変えられるのは既定値だけ。"
MENU_ENFORCE_INTRO2 = "  解除は kaijo.cmd（shl kaijo）か、実行フォルダに STOP.txt を置く。"
MENU_ENFORCE_ACTION = "何をする？ 1) ロック 2) スリープ 3) シャットダウン"
MENU_ENFORCE_CAUTION = "  ！ {caution}"
MENU_ENFORCE_STILL_OK = "  それでもええ？ (y/N): "
MENU_ENFORCE_HOURS = "強制が続く時間（時間。例 2 / 3）"
MENU_ENFORCE_HOURS_NOTE = "  最初の実行から {hours} 時間、戻ってきたら {relock} 分の予告のあと繰り返す（最大 {max_runs} 回）。"
MENU_ENFORCE_GRACE = "最初の予告から実行までの分"
MENU_SOUND_LABELS = {"default": "全部の既定", "lastcall": "就寝準備", "soft": "就寝目安後", "hard": "最低ライン",
                     "go": "出発目安", "go_last": "最終出発", "nag": "連続稼働", "auto_okita": "自動『起きた』",
                     "confirm": "乗務の確認", "enforce": "強制の予告"}   # notify.sound のキー → 表示（順番は settings_menu.SOUND_KIND_KEYS）
MENU_SOUND_HEAD = "  --- 通知音（Windows のパス。エクスプローラーの『パスのコピー』をそのまま貼ってOK。- で消す）---"
MENU_SOUND_ITEM = "   {i}. {label} {path}"
MENU_SOUND_DEFAULT = "（既定音）"
MENU_SOUND_VOL = "   音量 {volume} / 最長 {max_seconds} 秒"
MENU_SOUND_PROMPT = "  番号) パスを設定  v) 音量  m) 最長秒  Enter) 戻る: "
MENU_SOUND_FILE = "  {kind} のファイル [{cur}]（- で消す）: "
MENU_SOUND_VOLUME = "音量（0〜1）"
MENU_SOUND_MAX = "最長（秒）"
MENU_LEARNED_PROMPT = "  a) 追加  r) 忘れる  Enter) 戻る: "
MENU_LEARNED_KIND = "  どれ？ 1)帰宅 2)就寝 3)起床 4)何もしない: "
MENU_LEARNED_PHRASE = "  言い方: "
MENU_LEARNED_OK = "  『{core}』を覚えたで"
MENU_LEARNED_SHORT = "  短すぎるで"
MENU_LEARNED_FORGET = "  忘れる言い方: "
MENU_DAYS_HEAD = "\n  --- この先2週間（* = 例外日）---"
MENU_DAYS_PROMPT = "  o) 休みを追加  w) 出勤を追加  r) 例外を消す  Enter) 戻る: "
MENU_DAYS_DATE = "日付（11/5・明日 など）"
MENU_DAYS_NOTE = "  メモ（任意）: "
MENU_DAYS_SET = "  {md} を{kind}にしたで"

# ================================================================ calendar_sync（カレンダー同期）

CAL_UNSET = "カレンダー未設定"
CAL_SKIP = "スキップ（まだ新しい）: {name}"
CAL_FAIL = "同期失敗（前回の内容で続行）: {name} {err}"
CAL_OK = "同期OK: {name} 休み {off} 日 / 出勤 {work} 日"

# ================================================================ report_html（レポート）
# HTML の骨組み（CSS・script・タグ）は report_html.py。ここは目に見える言葉だけ。<small> はタイルの値の中の飾りなので文と一緒に持つ

REPORT_TITLE = "活動・睡眠レポート"
REPORT_SUB = "{start} 〜 {end}（{days}日間{period}）・作成 {generated}"
REPORT_TILE_SHIFTS = "乗務"
REPORT_TILE_SHIFTS_VAL = "{worked}<small> / {planned} 回</small>"
REPORT_TILE_SHIFTS_NOTE = "予定のうち出た回数"
REPORT_TILE_SHIFTS_UNCONFIRMED = "（未確認 {n}）"
REPORT_TILE_YASUMI = "休み"
REPORT_TILE_YASUMI_VAL = "{n}<small> 回</small>"
REPORT_TILE_YASUMI_NOTE = "『今日休む』『休んだ』の回数"
REPORT_TILE_YASUMI_LATE = "（事後報告 {n}）"
REPORT_TILE_SLEEP = "睡眠（中央値）"
REPORT_TILE_SLEEP_NOTE = "本人の記録がある日 {n} / {days} 日"
REPORT_TILE_LONG = "長時間覚醒の疑い"
REPORT_TILE_DAYS_VAL = "{n}<small> 日</small>"
REPORT_TILE_LONG_NOTE = "まとまった離席が3時間未満"
REPORT_TILE_LOW = "低活動"
REPORT_TILE_LOW_NOTE = "乗務なしでPC操作1時間未満"
REPORT_TILE_ENFORCE = "強制モード"
REPORT_TILE_ENFORCE_VAL = "{n}<small> 回実行</small>"
REPORT_TILE_ENFORCE_NOTE = "宣言 {declare} ・ 解除 {release}（本人が予約した就寝前のロック等）"
REPORT_TILE_ABSENCE = "当日欠勤（有給除く）"
REPORT_TILE_ABSENCE_VAL = "{n}<small> / 直近 {shifts} 乗務</small>"
REPORT_TILE_ABSENCE_NOTE = "目安 {limit} 回以上で注意・有給 {paid}"
REPORT_TILE_ABSENCE_OVER = "・目安に達している"
REPORT_TILE_MOOD = "気分の記録"
REPORT_TILE_MOOD_VAL = "{n}<small> / {days} 日</small>"
REPORT_TILE_MOOD_NOTE = "▼のある日 {down} ・ ▲のある日 {up}"
REPORT_MOOD_TIP = "{hm} {mood} {label}"
REPORT_MOOD_TIP_MEMO = "：{memo}"
REPORT_TIP_NODATA = "記録なし"
REPORT_TIP_AWAY = "乗務で外出中"
REPORT_TIP_PC = "PC {pc}分"
REPORT_TIP_GAME = " / ゲーム {game}分"
REPORT_TIP_DEV = " / 開発 {dev}分"
REPORT_TIP_ADOBE = " / Adobe {adobe}分"
REPORT_TIP_CLAUDE = " / Claude {n}回"
REPORT_TIP_VISIT = "（訪問の時間帯）"
REPORT_TIP_CELL = "{label} {h}時台: {tip}"
REPORT_SHIFT = {"worked": "出", "unconfirmed": "出?", "yasumi": "休", "planned": "予定", None: ""}
REPORT_FLAG = {"long": "覚醒長", "low": "低活動"}
REPORT_SINCE_NOTE = "記録は {md} から。それより前の日は乗務欄を空欄にしている。"
REPORT_NO_RECORD_NOTE = "まだ記録がないので乗務欄は空欄。"
REPORT_PERIOD_CLOSED = "・{md} 締め"
REPORT_PERIOD_ONGOING = "・{md} 締めの途中経過"
REPORT_MEMO_HEAD = "気分メモ"
REPORT_MOOD_NOTE = ("気分は iPhone から本人が記録した値（−2 どん底／−1 しんどい／0 ふつう／+1 アゲ／+2 止まらん）。"
                    "1日に4回以上あるときは最新3件を表示。")
REPORT_ABSENCE_NOTE = ("「当日欠勤」は『今日休む』『休んだ』のうち有給の印が無いもので、期間の終わり（途中経過なら作成日）までの"
                       "直近 {shifts} 乗務で数えている（目安 {limit} 回）。")
REPORT_LEGEND_PC = "PCを触ってた時間（1時間あたり）"
REPORT_LEGEND_RAMP = "0 → 60分"
REPORT_LEGEND_GAME = "ゲーム15分以上"
REPORT_LEGEND_DEV = "開発15分以上"
REPORT_LEGEND_ADOBE = "Adobe15分以上"
REPORT_LEGEND_CLAUDE = "Claude Code 使用"
REPORT_LEGEND_AWAY = "乗務で外出中"
REPORT_LEGEND_NODATA = "記録なし"
REPORT_LEGEND_VISIT = "訪問の時間帯"
REPORT_TABLE_ARIA = "日ごとの時間帯別活動"
REPORT_COL_SHIFT = "乗務"
REPORT_COL_NERU = "寝た"
REPORT_COL_OKITA = "起きた"
REPORT_COL_SLEEP = "睡眠"
REPORT_COL_PC = "PC(h)"
REPORT_COL_GAME = "ゲーム(h)"
REPORT_COL_DEV = "開発(h)"
REPORT_COL_ADOBE = "Adobe(h)"
REPORT_COL_CLAUDE = "Claude"
REPORT_COL_MOOD = "気分"
REPORT_COL_MEMO = "メモ"
REPORT_NOTE = ("PC（Windows）の操作記録と本人の記録から自動で作ったもの。「寝た」「起きた」「睡眠」は、本人が『ねる』『おきた』を"
               "記録した時刻（Claude Code・PC・iPhone）だけで出していて、PCを触っていない時間からの推定はしていない。"
               "記録が無い日は空欄で、眠っていないという意味ではない（しんどくて記録できなかった日も空欄になる）。"
               "「睡眠」はその日に起きた分の合計で、『ねる』から24時間以内に『おきた』の記録が無いものは数えていない。"
               "図の灰色は乗務で外出していた時間（出発してから翌朝{shift_end}まで。帰宅を記録した日はその時刻まで）。"
               "Mac とスマホの操作は含まない。"
               "「覚醒長」＝記録が20時間以上ある日で、PCを触っていないまとまった時間が3時間未満（睡眠の記録とは別に、PCの記録だけで判定）。"
               "「低活動」＝記録が20時間以上ある乗務のない日で、PC操作が1時間未満。"
               "Claude の回数はプロンプトを送った回数で、内容は記録していない。"
               "「出?」は本人の確認（『出た』『休んだ』）が無い日で、出た前提で集計している。{mood_note}{since_note}")

# ================================================================ cli（ヘルプ・コマンド出力）
# FILE\t<パス> の行は make-report.ps1 が読む機械向けの出力なのでここには置かない

CLI_HELP = """shiftlock の使い方（pwsh / Windows PowerShell のどのディレクトリからでも）
  shl status                 いまのフェーズ・次の乗務・2 週間の予定
  shl neru / okita           寝る / 起きた（ロック解除して出発リマインドへ）
  shl neru 23:30 / shl okita 11/7 7:00   寝た・起きた時刻を後から足す（レポート用の記録だけ）。末尾に 取消 で消す
  shl yasumi / deru          今日休む / やっぱ出る（shl yasumi 有給 で有給の当日欠勤）
  shl yasumi 11/3 / shl deru 11/3   終わった乗務の事後報告（休んだ / 出た）。shl yasumi 11/3 有給 / 有給なし で有給の印
  shl itte                   行ってきます（今日の乗務を出発済みに。通知とロックが止まる）
  shl kakunin                未確認の乗務（出た？休んだ？がまだの日）
  shl enforce                強制モードの状態（宣言・実行・STOP.txt）
  shl enforce on [lock|sleep|shutdown] [2h]   寝る前の強制を今日の乗務に予約（内容を見て y で同意。Claude Code からは不可）
  shl enforce off / resume   宣言の取り消し / 解除（STOP.txt）後の再開
  shl kaijo                  緊急解除（STOP.txt を置いて、予約済みのシャットダウンも中止）
  shl yotei                  2 週間の出／休
  shl day off 11/5 通院      予定の休み（欠勤にはならへん）
  shl day work 11/8          休みの曜日に出勤 / shl day rm 11/5 で戻す / shl day list で一覧
  shl report [14]            訪看さん向けレポート（締め期間 2 本 / 直近 14 日）
  shl sync                   カレンダーを今すぐ同期（全部のソース）
  shl settings               設定メニュー
  shl learned                覚えた言い方 / shl learn neru ばたんきゅー / shl unlearn ばたんきゅー
  shl cleanup                古い記録の整理を今すぐ
  shl app add <exe> <dev|adobe> / remove <exe> / list   時間を測るアプリの登録（開発・Adobe。ゲームは自動）
  shl app limit add|remove|exclude|include <exe> / list   減速・一時停止の対象（add・include は自分の端末から）
  scan-games / show-games    ゲーム判定の確認
Claude Code のプロンプトに打つ言葉（ねる / おきた / 今日休む / 11/5 休み / 状況 / 予定）は README の「解除ワード」を見てな"""
CLI_ARG_ENFORCE_OPTS = "on のとき lock|sleep|shutdown と 2h のような時間、または throttle|suspend（時間は付けへん）"
CLI_ARG_SLEEP_WHEN = "時刻（と日付）を付けると後から足す睡眠の記録（例: 11/7 23:30。末尾に 取消 で消す）"
CLI_ARG_PAST_DATE = "日付を付けると終わった乗務の事後報告（例: 11/3）"
CLI_ARG_PAID = "有給 / 有給なし（休みに有給の印を付ける／外す。当日欠勤の目安に数えへん）"
CLI_ARG_UPTIME = "起動からの秒数（無ければ強制は発動しない）"
CLI_ARG_REMOTE = "リモート操作中なら 1（検出できる場合）"
CLI_ARG_DRY_RUN = "強制モードの状態を進めない（guard.ps1 -DryRun）"
CLI_ARG_PERIOD = "締め期間で作る（20日締めなら 21日〜20日）"
CLI_ARG_AUTO = "今の期間と前の期間を reports/ に作って古いのを消す（火曜の自動生成用）"
CLI_ARG_WIN_SETTINGS = "Windows 版 Claude Code の settings.json（例 /mnt/c/Users/<名前>/.claude/settings.json）"
CLI_DAY_NEED_DATE = "日付がいるで（例: day off 11/5 通院）"
CLI_LEARNED = "『{core}』→ {label} で覚えたで"
CLI_LEARNED_NONE = "何もしない"
CLI_LEARNED_SHORT = "短すぎるで（2文字以上）"
CLI_INSTALL_DONE = "hook 本体: {dest}\nsettings : {settings}（最初の状態は .bak-shiftlock に退避）"
CLI_INSTALL_WIN = "Windows  : {win}（Git Bash から wsl.exe 経由で同じ本体を呼ぶ）"
CLI_INSTALL_RESTART = "Claude Code を再起動して /hooks で UserPromptSubmit と Stop に入ってるか確認してな"
# ネイティブ版の shl.exe だけが使う文（Python 版は argparse が断る。Python 側では使わず書き出すだけ）
SHL_UNKNOWN = "知らんコマンドやで: {cmd}（shl help で一覧）"
SHL_NOT_YET = "{cmd} はネイティブ版（shl.exe）にまだ無いで。Python 版の shl を使ってな"
SHL_STATE_BUSY = "いま記録のファイルを読み書きできへんかったわ（{err}）。少し待ってから状況（shl status）を確かめてな。"

# ---- ネイティブ版（native/）の画面 ----
# Python 側では使わへん。pc/tools/export_native.py が native/shared/messages.<lang>.json に書き出して、C# が読む
APP_PHASE = {"free": "いまは自由時間", "lastcall": "就寝準備の時間", "soft": "就寝目安を過ぎてるで",
             "hard": "最低ラインを過ぎてるで", "go": "そろそろ出発", "go_last": "出発の最終が近いで"}
APP_COUNTDOWN = {"lastcall": "就寝目安まで あと {remain}", "soft": "最低ラインまで あと {remain}",
                 "hard": "出発の最終まで あと {remain}", "go": "出発目安まで あと {remain}",
                 "go_late": "出発の最終まで あと {remain}", "go_last": "出庫の最終まで あと {remain}"}
APP_NEXT_SHIFT = "次の乗務  {day}"
APP_NO_SHIFT = "14 日以内に乗務はないで"
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
APP_NO_CONFIG = "まだ設定がないで（{path} が見つからへん）。"
APP_BAD_CONFIG = "設定が読めへん: {err}"
APP_CONFIG_BAD_JSON = "JSON の書き方がおかしいで（{err}）"
APP_CONFIG_BAD_VALUE = "{key} の書き方がおかしいで: {value}"
APP_STATE_BUSY = "いま記録のファイルを読み書きできへんかったわ。もう一度押してな。"
APP_LOG_BUSY = "（履歴の events.log には書けへんかったわ。記録そのものは残ってるから、押し直さんでええで。）"
APP_NAV_NOW = "いま"
APP_NAV_SCHEDULE = "予定"
APP_SCHEDULE_LONG = "この先 4 週間の予定"
APP_SCHEDULE_HINT = "日を押したら、その日だけ休み・乗務に変えられるで（* は例外日）。"
APP_DAY_MEMO = "メモ（任意）"
APP_DAY_FROM_CALENDAR = "カレンダーから来た例外日やで。ここで上書きはできるけど、取り消すのはカレンダーの側でな。"
APP_BTN_DAY_OFF = "休みにする"
APP_BTN_DAY_WORK = "乗務にする"
APP_BTN_DAY_RM = "例外を取り消す"
APP_DAY_SET = "{day} を{kind}にしたで。"
APP_DAY_RM_DONE = "{day} の例外を取り消したで。"
APP_NAV_REPORT = "休み・報告"
APP_REPORT_TITLE = "まだ確認してへん乗務"
APP_REPORT_HINT = "出たか休んだか押してな。押し間違えたら、すぐ下の「取り消す」で戻せるで。"
APP_BTN_WORKED = "出た"
APP_BTN_YASUMI = "休んだ"
APP_BTN_YASUMI_PAID = "休んだ（有給）"
APP_NEXT_TITLE = "次の乗務を休む"
APP_NEXT_HINT = "24 時間以内に始まる乗務を休みにするで。「やっぱ出る」で元に戻せる。"
APP_BTN_YASUMI_NEXT = "今日休む"
APP_BTN_YASUMI_NEXT_PAID = "今日休む（有給）"
APP_BTN_DERU = "やっぱ出る"

# ================================================================ agent（ネイティブ版の常駐。native/shiftlock.Agent）
# Python 側では使わず、tools/export_native.py が JSON に書き出す。トレイのメニュー・確認・知らせ。
AGENT_MENU_OPEN = "状態を開く"
AGENT_MENU_RELEASE = "緊急解除"
AGENT_MENU_EXIT = "終了"
AGENT_RELEASE_TITLE = "shiftlock 緊急解除"
AGENT_RELEASE_ASK = "強制モードを解除する？\n（予約済みのシャットダウンを止めて、STOP.txt を置くで。再開は shl enforce resume）"
AGENT_RELEASE_PARTIAL = "強制は解除したで（STOP.txt あり）。ただ、記録の一部が書けへんかった。"
AGENT_RELEASE_FAILED = "解除を記録できへんかった。{path} を手で作ってな（中身は空でええ）。"
AGENT_TIP_GUARD_TASK = "見張りを止めてるで（タスク shiftlock-guard が有効）"
AGENT_TIP_FAILED = "前の見張りに失敗したで（agent.log を見てな）"
AGENT_OPEN_FAILED = "shiftlock.exe が見つからへん（{path}）"
AGENT_TEST_TOAST = "トーストのテストやで"
AGENT_TEST_POPUP = "ポップアップのテストやで"
# 切り替え（shiftlock-agent.exe --install / --uninstall の結果。1 行ずつ出す）
AGENT_SETUP_AUTOSTART_ON = "ログオン時の自動起動を登録したで"
AGENT_SETUP_AUTOSTART_OFF = "自動起動の登録を外したで"
AGENT_SETUP_TASK_DISABLED = "タスク {task} を無効にしたで（Python 版の見張りは止まる）"
AGENT_SETUP_TASK_ENABLED = "タスク {task} を有効に戻したで（Python 版の見張りに戻る）"
AGENT_SETUP_TASK_MISSING = "タスク {task} は無いから、そのままにするで"
AGENT_SETUP_TASK_FAILED = "タスク {task} を切り替えられへんかった: {err}"
AGENT_SETUP_ROLLED_BACK = "二重の見張りにならんよう、自動起動の登録は取り消したで"
AGENT_SETUP_ROLLBACK_FAILED = "自動起動の登録を取り消されへんかった: {err}（このままやと次のログオンから二重の見張りになる。shiftlock-agent.exe --uninstall で外してな）"
AGENT_SETUP_STARTED = "Agent を起動したで"
AGENT_SETUP_STOPPED = "動いてた Agent を終わらせたで"
AGENT_SETUP_FAILED = "失敗したわ: {err}"

# アプリの減速・一時停止（native/shiftlock.Agent の Limiter。アプリの名前は入れない）
AGENT_LIMIT_NOTICE = "{at} から、対象のアプリ {n} 件の CPU を直近の使用率の {percent}% に下げるで（強制モード）。解除は shl kaijo かトレイの「緊急解除」。"
AGENT_LIMIT_NOTICE_SUSPEND = "{at} に、対象のアプリ {n} 件を一時停止するで（強制モード）。保存してへん内容は先に保存しときや。解除は shl kaijo かトレイの「緊急解除」。"
AGENT_LIMIT_SUSPENDED = "対象のアプリ {n} 件を一時停止したで。"
AGENT_LIMIT_RELEASED = "アプリの減速・一時停止を元に戻したで。"
AGENT_LIMIT_SKIPPED = "手を加えたらあかんもんが一緒に動いてるアプリがあるから、そのアプリは見送ったで。"
AGENT_LIMIT_FAILED = "アプリの減速か一時停止が一部できへんかったで（agent.log を見てな）。"
AGENT_LIMIT_BAD_RECORD = "減速・一時停止の記録（limit.json）が読めへんかったから、limit.json.bad に名前を変えたで。止まったままのアプリがあったら、終了して起動し直してな。"
AGENT_LIMIT_STALE_CAP = "前回の上限が残ってるかもしれんで。対象のアプリを起動し直すと戻るで。"

# ---- shl app（計測するアプリの登録）----
APPS_ADDED = "{name} を {cat} で登録したで"
APPS_CHANGED = "{name} を {old} から {cat} に付け替えたで"
APPS_REMOVED = "{name} の登録を外したで"
APPS_NOT_FOUND = "{name} は登録されてへんで"
APPS_NO_GAME = "ゲームは登録せんでええで（自動で判定する。ゲームのフォルダは shl settings で）"
APPS_BAD_CAT = "カテゴリは dev か adobe やで（例: shl app add code.exe dev）"
APPS_BAD_NAME = "exe の名前だけ書いてな（フォルダは書かへん。例: code.exe）"
APPS_BAD_SECTION = "shifts.json の apps の形がおかしいから書き換えへんで"
APPS_USAGE = "使い方: shl app add <exe名> <dev|adobe> / shl app remove <exe名> / shl app list"
APPS_LIST_LINE = "{cat}: {names}"
APPS_LIST_EMPTY = "登録は無いで（shl app add code.exe dev で足せる）"

# ---- shl app limit（減速・一時停止の対象）----
APPS_LIMIT_USAGE = "使い方: shl app limit add <exe名> / remove <exe名> / exclude <exe名> / include <exe名> / list"
APPS_LIMIT_NEED_TTY = "対象を広げる操作（add・include）は，自分で端末を開いて打ってな（ここからは足せへんで）"
APPS_LIMIT_NOT_REGISTERED = "{name} は登録されてへんから足せへんで（先に shl app add {name} dev か adobe で登録してな）"
APPS_LIMIT_NEVER = "{name} は手を加えへん名前の一覧に入ってるから足せへんで（limit.never）"
APPS_LIMIT_ADDED = "{name} を減速・一時停止の対象に足したで（次に宣言する乗務から効く）"
APPS_LIMIT_ANTICHEAT_WARN = "注意: アンチチートのあるアプリに上限や一時停止をかけると，切断などが起きることがあるで"
APPS_LIMIT_PROTECT_NOTE = "注意: 子プロセスに claude.exe・node.exe・wsl.exe などが居るアプリは，ターミナルで Claude Code が動いてるかもしれんから手を加えんと見送るで（エディタなど）"
APPS_LIMIT_REMOVED = "{name} を対象から外したで"
APPS_LIMIT_NOT_FOUND = "{name} は足されてへんで"
APPS_LIMIT_EXCLUDED = "{name} を対象から外したで（ゲームは自動で対象になるから，外す印を付けた）"
APPS_LIMIT_INCLUDED = "{name} を対象に戻したで（次に宣言する乗務から効く）"
APPS_LIMIT_NOT_EXCLUDED = "{name} は外してへんで"
APPS_LIMIT_LIST_APPS = "足したアプリ: {names}"
APPS_LIMIT_LIST_APPS_NONE = "足したアプリ: なし"
APPS_LIMIT_LIST_EXCLUDED = "外したゲーム: {names}"
APPS_LIMIT_LIST_EXCLUDED_NONE = "外したゲーム: なし"
APPS_LIMIT_LIST_GAMES = "最近見たゲーム:"
APPS_LIMIT_LIST_GAMES_NONE = "最近見たゲーム: なし（Agent が動いてる間に見たものだけ載る）"
APPS_LIMIT_LIST_GAME_LINE = "  {name}: {verdict}{excluded}"
APPS_LIMIT_LIST_GAME_EXCLUDED = "（手で外してある）"
APPS_LIMIT_VERDICT_TARGET = "対象"
APPS_LIMIT_VERDICT_ANTICHEAT = "アンチチートの印があるから対象外"
APPS_LIMIT_VERDICT_UNKNOWN = "調べられへんから対象外"
