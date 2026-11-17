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

ENFORCE_LABEL = {"lock": "ロック", "sleep": "スリープ", "shutdown": "シャットダウン"}
ENFORCE_CAUTION = {"sleep": "スリープは遠隔（Parsec）から戻せへん。",
                   "shutdown": "シャットダウンは未保存の作業が消える（60 秒の猶予のあと強制終了）。遠隔（Parsec）から戻せへん。"}
ENFORCE_TERMS_WHAT = {"lock": "ロック（ログインし直せば続きはできる）",
                      "sleep": "スリープ（未保存の作業は残るけど、遠隔からは戻せへん）",
                      "shutdown": "シャットダウン（60 秒の猶予のあと強制終了。未保存の作業は消える。遠隔からは戻せへん）"}
ENFORCE_TERMS = ("強制モードの内容（{day} の乗務）\n"
                 "  何を    : {what}\n"
                 "  いつから: 最低ライン {hard} 以降に PC を触ったら、{grace} 分前に予告してから\n"
                 "  どれだけ: 最初の実行から {hours} 時間（{end} ごろまで）。戻ってきたら {relock} 分の予告のあと、また実行（最大 {max_runs} 回）\n"
                 "  解除    : kaijo.cmd（shl kaijo）か、実行フォルダに STOP.txt を置く。解除は記録に残る\n"
                 "  注意    : 『おきた』は寝た記録（『ねる』か 2.5 時間以上の離席）があるときだけ効く。リモート（Parsec）の検出は未対応で、遠隔操作中でも発動する")
ENFORCE_TERMS_CAUTION_LINE = "  ！      : {caution}"
ENFORCE_DIALOG_TITLE = "shiftlock 強制モードの同意"
ENFORCE_DIALOG_TEXT = "{day} の乗務を強制モード（{label} {hours}h）にする。ええ？\n解除は kaijo.cmd か STOP.txt。"
ENFORCE_BAD_ACTION = PREFIX + "action は lock / sleep / shutdown のどれかで。"
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
ENFORCE_SUFFIX = " 今日は強制モード（{label} {hours}h）やで{stop}。解除は kaijo.cmd。"
ENFORCE_STATUS_HEAD = PREFIX + "強制モード（宣言は shl enforce on、解除は kaijo.cmd か STOP.txt）"
ENFORCE_STATUS_DEFAULT = "  既定: {label} / {hours}h / 予告 {grace}分（settings の 19）"
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
  scan-games / show-games    ゲーム判定の確認
Claude Code のプロンプトに打つ言葉（ねる / おきた / 今日休む / 11/5 休み / 状況 / 予定）は README の「解除ワード」を見てな"""
CLI_ARG_ENFORCE_OPTS = "on のとき lock|sleep|shutdown と 2h のような時間"
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
