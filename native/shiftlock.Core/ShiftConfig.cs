using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Shiftlock.Core;

/// <summary>設定が読めない・検証に落ちた（Python 版の load_config が例外で止まる場合）．Message は画面に出す文．</summary>
public sealed class ConfigException(string message) : Exception(message);

/// <summary>寝られない時間帯（no_sleep の 1 項目）．Weekday は小文字の先頭 3 文字（"tue"）．</summary>
public sealed record NoSleepRule(string? Date, string? Weekday, string From, string To);

/// <summary>その日だけの上書き（days.json の times と旧形式 shifts の辞書）．無い項目は null．</summary>
public sealed record DayTimes(string? StartTarget, string? StartLatest, double? SleepTargetHours, double? SleepMinHours);

/// <summary>
/// 設定（shifts.json）と例外日（days.json・calendar.json）．pc/shiftlock/config.py の load_config / merged_days /
/// calendar_days と同じ読み方・同じ検証をする．読むだけで，書かない．
/// Python 版との違い: lang が無い・知らない値のときは標準語（Python 版の既定は関西弁）．
/// 数や no_sleep の形がおかしい設定は，Python 版は逆算の途中で落ちるが，ここでは読み込みで設定のエラーにする．
/// </summary>
public sealed partial class ShiftConfig
{
    /// <summary>曜日の名前．月曜が 0（Python の date.weekday() と同じ並び）．</summary>
    internal static readonly string[] WeekdayNames = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    private static readonly string[] DefaultWorkWeekdays = ["mon", "tue", "wed", "thu", "fri", "sat"];

    /// <summary>load_config が「辞書であること」を確かめるキー（確かめる順）．</summary>
    private static readonly string[] DictKeys =
        ["usage", "notify", "calendar", "retention", "report", "confirm", "enforce", "absence"];

    /// <summary>時間として扱う数の上限（分でも時間でも）．これを超える値は日時の計算があふれるので設定のエラーにする．</summary>
    private const double MaxDuration = 1_000_000;

    /// <summary>既定値（pc/shiftlock/config.py の DEFAULTS と同じ値．lang は除く）．見張りが使う節はここから読む．</summary>
    private const string DefaultsJson = """
        {
         "work_weekdays": ["mon", "tue", "wed", "thu", "fri", "sat"],
         "start_target": "20:00",
         "start_latest": "22:30",
         "prep_minutes": 90,
         "sleep_target_hours": 5,
         "sleep_min_hours": 4,
         "lastcall_minutes": 40,
         "block_claude_on_hard": true,
         "no_sleep": [],
         "usage": {"nag_after_minutes": 120, "nag_every_minutes": 30, "idle_reset_minutes": 20, "game_idle_minutes": 180},
         "notify": {"phase": "toast", "hard": "popup", "nag": "toast", "speak": false, "sound": {},
                    "sound_volume": 0.8, "sound_max_seconds": 20},
         "game_processes": [],
         "mood_file": "",
         "shift_end_earliest": "07:00",
         "home_by_latest": "09:00",
         "auto_wake": true,
         "auto_wake_hours": null,
         "steam_libraries": true,
         "game_dirs": [],
         "game_exclude_dirs": [],
         "game_exclude": ["UnityCrashHandler*", "CrashReport*", "*crashpad*", "unins*", "vc_redist*",
                          "dxsetup*", "*Redist*", "steamerrorreporter*", "*Setup*"],
         "calendar": {"ics_url": "", "sources": [], "off_keywords": ["休み", "公休", "有給"],
                      "work_keywords": ["出勤"], "sync_minutes": 60},
         "retention": {"days_keep": 60, "log_keep_days": 180, "events_keep_days": 365},
         "report": {"closing_day": 20, "keep_months": 3},
         "confirm": {"mode": "ask", "lookback_days": 7, "home_activity_minutes": 60, "okita_means_worked": true},
         "absence": {"window_shifts": 24, "warn_at": 5},
         "enforce": {"action": "lock", "duration_hours": 2, "grace_minutes": 15, "relock_minutes": 5,
                     "max_runs": 6, "boot_grace_minutes": 15, "skip_if_remote": true},
         "apps": {"registered": {}, "active_input_minutes": 5, "active_cpu_percent": 5}
        }
        """;

    private readonly Dictionary<string, string> _daysOff = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _daysWork = new(StringComparer.Ordinal);
    private readonly HashSet<string> _legacyDays = new(StringComparer.Ordinal);
    private readonly List<JsonObject> _legacyTimes = [];
    private JsonObject _times = new();

    private ShiftConfig(string dir, Messages msg)
    {
        Dir = dir;
        Msg = msg;
    }

    /// <summary>実行フォルダ（shifts.json のあるフォルダ）．</summary>
    public string Dir { get; }

    /// <summary>文面の言語（"ja" か "ja_kansai"）．</summary>
    public string Lang => Msg.Lang;

    /// <summary>この設定の言語の文面．</summary>
    public Messages Msg { get; }

    public IReadOnlyList<string> WorkWeekdays { get; private set; } = DefaultWorkWeekdays;
    public string StartTarget { get; private set; } = "20:00";
    public string StartLatest { get; private set; } = "22:30";
    public double PrepMinutes { get; private set; } = 90;
    public double LastcallMinutes { get; private set; } = 40;
    public double SleepTargetHours { get; private set; } = 5;
    public double SleepMinHours { get; private set; } = 4;

    /// <summary>文面に入れる sleep_min_hours の表記（Python の str()．5 → "5"，5.0 → "5.0"）．</summary>
    public string SleepMinHoursText { get; private set; } = "4";

    /// <summary>文面に入れる sleep_target_hours の表記（Python の str()）．</summary>
    public string SleepTargetHoursText { get; private set; } = "5";

    /// <summary>文面に入れる auto_wake_hours の表記．無い・0 なら SleepMinHoursText．</summary>
    public string AutoWakeHoursText { get; private set; } = "4";

    public IReadOnlyList<NoSleepRule> NoSleep { get; private set; } = [];

    /// <summary>節の中の整数（Python の int(値)）．読めなければ ConfigException（Python 版はここで落ちる）．</summary>
    public int SectionInt(string section, string key) =>
        Py.Int(Section(section)[key]) ?? throw BadValue($"{section}.{key}", Section(section)[key]);

    /// <summary>文面に入れる値の表記（Python の str(値)）．</summary>
    internal static string PyText(JsonNode? v) => PyStr(v);

    /// <summary>
    /// 自動「起きた」に要る睡眠時間（auto_wake_hours．無い・0 なら sleep_min_hours）．数として読めなければ ConfigException．
    /// </summary>
    public double AutoWakeHours =>
        Raw.TryGetPropertyValue("auto_wake_hours", out var v) && IsTruthy(v) ? ToNumber("auto_wake_hours", v) : SleepMinHours;

    /// <summary>乗務の翌朝，家に帰っているはずの時刻（home_by_latest．既定 09:00）を d の日時で．読めなければ ConfigException．</summary>
    public DateTime HomeByLatest(DateOnly d)
    {
        var v = Raw.TryGetPropertyValue("home_by_latest", out var node) ? node : JsonValue.Create("09:00");
        return TimeUtil.At(d, LooseTime("home_by_latest", v));
    }

    /// <summary>
    /// 既定値に shifts.json を重ねたもの（Python の load_config が返す cfg と同じ中身．_dir・_days は無い）．
    /// 見張りの各段は，Python 版と同じ「その場での読み方」（壊れた値は既定に戻す，など）をするためにここから読む．
    /// 逆算に使う値（上の StartTarget など）は読み込みで検証済みのプロパティを使う．
    /// </summary>
    public JsonObject Raw { get; private set; } = new();

    /// <summary>辞書の節（usage・notify・calendar・retention・report・confirm・enforce・absence）．読み込みで辞書と確かめてある．</summary>
    public JsonObject Section(string key) => Raw[key] as JsonObject ?? new JsonObject();

    /// <summary>
    /// 節の中の数（分・時間など）．無ければ fallback．JSON の数・真偽値でなければ ConfigException
    /// （Python 版は timedelta(minutes=値) で落ちる．数字の文字列も通らない）．
    /// </summary>
    public double SectionNumber(string section, string key, double fallback)
    {
        if (!Section(section).TryGetPropertyValue(key, out var v))
        {
            return fallback;
        }
        double? d = v?.GetValueKind() switch
        {
            JsonValueKind.Number => v.GetValue<double>(),
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null,
        };
        if (d is not { } value || !double.IsFinite(value) || Math.Abs(value) > MaxDuration)
        {
            throw BadValue($"{section}.{key}", v);
        }
        return value;
    }

    /// <summary>休みの例外日（日 → 理由）．手動がカレンダーより優先．</summary>
    public IReadOnlyDictionary<string, string> DaysOff => _daysOff;

    /// <summary>出勤の例外日（日 → 理由）．</summary>
    public IReadOnlyDictionary<string, string> DaysWork => _daysWork;

    /// <summary>
    /// 設定を読む．shifts.json が無ければ FileNotFoundException，JSON として読めない・検証に落ちれば ConfigException．
    /// 他のプロセスが掴んでいて読めないときは IOException（呼ぶ側は次の機会にやり直す）．
    /// </summary>
    public static ShiftConfig Load(string shiftsJsonPath)
    {
        var full = Path.GetFullPath(shiftsJsonPath);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(full);
        }
        catch (DirectoryNotFoundException e)
        {
            throw new FileNotFoundException(e.Message, full, e);
        }

        var fallback = Messages.Load(null);
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(bytes);
            JsonFiles.Touch(node);
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            throw new ConfigException(fallback.Format("APP_CONFIG_BAD_JSON", ("err", e.Message)));
        }
        if (node is not JsonObject raw)
        {
            throw new ConfigException(fallback.Format("CONFIG_NOT_DICT", ("key", Path.GetFileName(full))));
        }

        // ここから先のエラーの文は設定の言語で出す（Python 版と同じ順: 言語 → 時刻 → 曜日 → 辞書 → sources）
        var msg = Messages.Load(JsonX.Str(raw["lang"]));
        var cfg = new ShiftConfig(Path.GetDirectoryName(full) ?? ".", msg);

        cfg.StartTarget = cfg.StrictTime(raw, "start_target", cfg.StartTarget);
        cfg.StartLatest = cfg.StrictTime(raw, "start_latest", cfg.StartLatest);
        cfg.StrictTime(raw, "shift_end_earliest", "07:00");
        cfg.WorkWeekdays = cfg.ReadWeekdays(raw);
        foreach (var key in DictKeys)
        {
            if (raw.TryGetPropertyValue(key, out var v) && v is not JsonObject)
            {
                throw new ConfigException(msg.Format("CONFIG_NOT_DICT", ("key", key)));
            }
        }
        if (raw["calendar"] is JsonObject cal && cal.TryGetPropertyValue("sources", out var sources) && sources is not JsonArray)
        {
            throw new ConfigException(msg["CONFIG_SOURCES_NOT_LIST"]);
        }

        cfg.Raw = DeepMerge(JsonNode.Parse(DefaultsJson)!.AsObject(), raw);
        if (cfg.Section("notify")["sound"] is not JsonObject)
        {
            cfg.Section("notify")["sound"] = new JsonObject();   // 任意機能の設定ミスで本線を止めない（Python 版と同じ）
        }

        cfg.PrepMinutes = cfg.Number(raw, "prep_minutes", cfg.PrepMinutes);
        cfg.LastcallMinutes = cfg.Number(raw, "lastcall_minutes", cfg.LastcallMinutes);
        cfg.SleepTargetHours = cfg.Number(raw, "sleep_target_hours", cfg.SleepTargetHours);
        cfg.SleepMinHours = cfg.Number(raw, "sleep_min_hours", cfg.SleepMinHours);
        cfg.SleepMinHoursText = raw.TryGetPropertyValue("sleep_min_hours", out var minH) ? PyStr(minH) : "4";
        cfg.SleepTargetHoursText = raw.TryGetPropertyValue("sleep_target_hours", out var targetH) ? PyStr(targetH) : "5";
        cfg.AutoWakeHoursText = raw.TryGetPropertyValue("auto_wake_hours", out var aw) && IsTruthy(aw)
            ? PyStr(aw)
            : cfg.SleepMinHoursText;
        cfg.NoSleep = cfg.ReadNoSleep(raw);
        cfg.ReadLegacyShifts(raw);
        cfg.ReadDays();
        return cfg;
    }

    /// <summary>その日だけの上書き（plan.day_times）．days.json の times が旧形式 shifts より優先．形がおかしければ ConfigException．</summary>
    public DayTimes TimesFor(string day)
    {
        var t = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (_times[day] is JsonObject own)
        {
            foreach (var (k, v) in own)
            {
                t[k] = v;
            }
        }
        foreach (var x in _legacyTimes)
        {
            if (JsonX.Str(x["date"]) != day)
            {
                continue;
            }
            foreach (var (k, v) in x)
            {
                t.TryAdd(k, v);   // 先に入っているもの（times，前の項目）が勝つ
            }
        }
        return new DayTimes(
            t.TryGetValue("start_target", out var st) ? LooseTime($"{day} start_target", st) : null,
            t.TryGetValue("start_latest", out var sl) ? LooseTime($"{day} start_latest", sl) : null,
            t.TryGetValue("sleep_target_hours", out var th) ? ToNumber($"{day} sleep_target_hours", th) : null,
            t.TryGetValue("sleep_min_hours", out var mh) ? ToNumber($"{day} sleep_min_hours", mh) : null);
    }

    /// <summary>旧形式 shifts に書いてある日か（plan.is_workday の legacy）．</summary>
    public bool IsLegacyShiftDay(string day) => _legacyDays.Contains(day);

    // ---- 読み込みの部品 ----

    /// <summary>config.deep_merge: 辞書どうしは中まで重ね，それ以外は over の値に置き換える．@base を書き換えて返す．</summary>
    private static JsonObject DeepMerge(JsonObject @base, JsonObject over)
    {
        foreach (var (k, v) in over)
        {
            if (v is JsonObject o && @base[k] is JsonObject b)
            {
                DeepMerge(b, o);
            }
            else
            {
                @base[k] = v?.DeepClone();
            }
        }
        return @base;
    }

    [GeneratedRegex(@"^(2[0-3]|[01][0-9]|[0-9]):([0-5][0-9]|[0-9])$", RegexOptions.CultureInvariant)]
    private static partial Regex StrictTimeRegex();

    internal ConfigException BadValue(string key, JsonNode? value) =>
        new(Msg.Format("APP_CONFIG_BAD_VALUE", ("key", key), ("value", JsonX.Text(value))));

    /// <summary>Python の datetime.strptime(v, "%H:%M") が通る形だけ受ける（"8:00" は通る．前後の空白は通らない）．</summary>
    private string StrictTime(JsonObject raw, string key, string fallback)
    {
        if (!raw.TryGetPropertyValue(key, out var v))
        {
            return fallback;
        }
        var s = JsonX.Str(v);
        if (s is null || !StrictTimeRegex().IsMatch(s))
        {
            throw BadValue(key, v);
        }
        return s;
    }

    /// <summary>逆算に使う時刻（timeutil.at が読める形）．</summary>
    private string LooseTime(string key, JsonNode? v)
    {
        var s = JsonX.Str(v);
        try
        {
            if (s is not null)
            {
                TimeUtil.At(DateOnly.MinValue, s);
                return s;
            }
        }
        catch (FormatException)
        {
        }
        throw BadValue(key, v);
    }

    private double Number(JsonObject raw, string key, double fallback) =>
        raw.TryGetPropertyValue(key, out var v) ? ToNumber(key, v) : fallback;

    /// <summary>Python の float() と同じものを受ける: JSON の数，数字の文字列（前後の空白は可），真偽値．</summary>
    private double ToNumber(string key, JsonNode? v)
    {
        double? d = v?.GetValueKind() switch
        {
            JsonValueKind.Number => v.GetValue<double>(),
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            JsonValueKind.String when double.TryParse(v.GetValue<string>().Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
        if (d is not { } value || !double.IsFinite(value) || Math.Abs(value) > MaxDuration)
        {
            throw BadValue(key, v);
        }
        return value;
    }

    /// <summary>
    /// work_weekdays．Python 版は「w not in WEEKDAYS の要素があれば断る」「曜日 in work_weekdays で判定」なので，
    /// 配列は要素，辞書はキー，空の文字列は空として同じ結果になるように読む．
    /// </summary>
    private IReadOnlyList<string> ReadWeekdays(JsonObject raw)
    {
        if (!raw.TryGetPropertyValue("work_weekdays", out var v))
        {
            return DefaultWorkWeekdays;
        }
        List<JsonNode?> items;
        switch (v)
        {
            case JsonArray a:
                items = [.. a];
                break;
            case JsonObject o:
                items = [.. o.Select(kv => (JsonNode?)JsonValue.Create(kv.Key))];
                break;
            case JsonValue when JsonX.Str(v) is { } s:
                items = [.. s.Select(c => (JsonNode?)JsonValue.Create(c.ToString()))];
                break;
            default:
                throw BadValue("work_weekdays", v);
        }
        var bad = items.Where(i => JsonX.Str(i) is not { } s || Array.IndexOf(WeekdayNames, s) < 0).ToList();
        if (bad.Count > 0)
        {
            var repr = "[" + string.Join(", ", bad.Select(PyRepr)) + "]";
            throw new ConfigException(Msg.Format("CONFIG_BAD_WEEKDAY", ("bad", repr)));
        }
        return [.. items.Select(i => JsonX.Str(i)!)];
    }

    private IReadOnlyList<NoSleepRule> ReadNoSleep(JsonObject raw)
    {
        if (!raw.TryGetPropertyValue("no_sleep", out var v))
        {
            return [];
        }
        if (v is not JsonArray rules)
        {
            throw BadValue("no_sleep", v);
        }
        var result = new List<NoSleepRule>(rules.Count);
        for (var i = 0; i < rules.Count; i++)
        {
            var key = $"no_sleep[{i + 1}]";
            if (rules[i] is not JsonObject r)
            {
                throw BadValue(key, rules[i]);
            }
            string? weekday = null;
            if (r.TryGetPropertyValue("weekday", out var wd))
            {
                var s = JsonX.Str(wd) ?? throw BadValue(key, r);
                s = s.ToLowerInvariant();
                weekday = s.Length > 3 ? s[..3] : s;
            }
            try
            {
                var from = LooseTime(key, r["from"]);
                var to = LooseTime(key, r["to"]);
                result.Add(new NoSleepRule(JsonX.Str(r["date"]), weekday, from, to));
            }
            catch (ConfigException)
            {
                throw BadValue(key, r);
            }
        }
        return result;
    }

    private void ReadLegacyShifts(JsonObject raw)
    {
        if (raw["shifts"] is not JsonArray shifts)
        {
            return;
        }
        foreach (var x in shifts)
        {
            if (JsonX.Str(x) is { } day)
            {
                _legacyDays.Add(day);
            }
            else if (x is JsonObject o)
            {
                _legacyTimes.Add(o);
                if (JsonX.Str(o["date"]) is { } d)
                {
                    _legacyDays.Add(d);
                }
            }
        }
    }

    /// <summary>手動（days.json）とカレンダー（calendar.json）の例外日をまとめる（config.merged_days）．手動が優先．</summary>
    private void ReadDays()
    {
        var man = JsonFiles.ReadObjectOrEmpty(Path.Combine(Dir, "days.json"));
        var cal = JsonFiles.ReadObjectOrEmpty(Path.Combine(Dir, "calendar.json"));
        var manOff = man["off"] as JsonObject ?? new JsonObject();
        var manWork = man["work"] as JsonObject ?? new JsonObject();

        CalendarDays(cal, _daysOff, _daysWork);
        foreach (var (d, why) in manOff)
        {
            _daysOff[d] = WhyText(why);
        }
        foreach (var (d, why) in manWork)
        {
            _daysWork[d] = WhyText(why);
        }
        // Python 版と同じ順で消す（同じ日が手動の休みと出勤の両方にあると，両方から消える）
        foreach (var (d, _) in manOff)
        {
            _daysWork.Remove(d);
        }
        foreach (var (d, _) in manWork)
        {
            _daysOff.Remove(d);
        }
        _times = man["times"] as JsonObject ?? new JsonObject();
    }

    /// <summary>calendar.json から例外日を取り出す（config.calendar_days）．新形式は sources の順に先勝ち．</summary>
    private static void CalendarDays(JsonObject cal, Dictionary<string, string> off, Dictionary<string, string> work)
    {
        if (!cal.ContainsKey("sources"))
        {
            Put(cal["off"], off, null);
            Put(cal["work"], work, null);
            return;
        }
        if (cal["sources"] is not JsonObject sources)
        {
            return;
        }
        foreach (var (_, src) in sources)
        {
            if (src is not JsonObject s)
            {
                continue;
            }
            Put(s["off"], off, work);
            Put(s["work"], work, off);
        }

        // other が null なら上書き（旧形式），あれば「どちらにもまだ無い日だけ」足す（新形式の先勝ち）
        static void Put(JsonNode? from, Dictionary<string, string> to, Dictionary<string, string>? other)
        {
            if (from is not JsonObject days)
            {
                return;
            }
            foreach (var (d, why) in days)
            {
                if (other is null)
                {
                    to[d] = WhyText(why);
                }
                else if (!to.ContainsKey(d) && !other.ContainsKey(d))
                {
                    to[d] = WhyText(why);
                }
            }
        }
    }

    private static string WhyText(JsonNode? why) => why is null ? "" : JsonX.Str(why) ?? JsonX.Text(why);

    // ---- Python の表記 ----

    private static bool IsTruthy(JsonNode? v) => JsonX.Truthy(v);

    /// <summary>Python の str(値)．文字列はそのまま，それ以外は repr と同じ．</summary>
    private static string PyStr(JsonNode? v) => JsonX.Str(v) ?? PyRepr(v);

    /// <summary>Python の repr(値)（JSON から読んだ値のぶんだけ）．文面に入るリストや数の表記を Python 版と揃える．</summary>
    internal static string PyRepr(JsonNode? v)
    {
        switch (v?.GetValueKind())
        {
            case null:
                return "None";
            case JsonValueKind.True:
                return "True";
            case JsonValueKind.False:
                return "False";
            case JsonValueKind.String:
                return PyQuote(v.GetValue<string>());
            case JsonValueKind.Number:
                return PyNumber(v);
            case JsonValueKind.Array:
                return "[" + string.Join(", ", ((JsonArray)v).Select(PyRepr)) + "]";
            default:
                return "{" + string.Join(", ", ((JsonObject)v).Select(kv => PyQuote(kv.Key) + ": " + PyRepr(kv.Value))) + "}";
        }
    }

    private static string PyNumber(JsonNode v)
    {
        var text = v.ToJsonString();
        if (text.AsSpan().IndexOfAny('.', 'e', 'E') < 0)
        {
            return text;   // 整数は書いてあるとおり
        }
        var r = v.GetValue<double>().ToString("R", CultureInfo.InvariantCulture);
        return r.AsSpan().IndexOfAny('.', 'E') < 0 && double.IsFinite(v.GetValue<double>()) ? r + ".0" : r;
    }

    private static string PyQuote(string s)
    {
        var quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder(s.Length + 2).Append(quote);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                case '\t': sb.Append(@"\t"); break;
                default:
                    if (c == quote)
                    {
                        sb.Append('\\').Append(c);
                    }
                    else if (c < 0x20 || c == 0x7f)
                    {
                        sb.Append(@"\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.Append(quote).ToString();
    }
}

/// <summary>JsonNode を読むときの小さな部品．</summary>
internal static class JsonX
{
    private static readonly JsonWriterOptions Compact = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>文字列ならその値，それ以外（無い・null・数など）は null．</summary>
    public static string? Str(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>Python の真偽（if 値:）．無い・null・false・0・空の文字列・空の配列・空の辞書は偽．</summary>
    public static bool Truthy(JsonNode? v) => v?.GetValueKind() switch
    {
        null or JsonValueKind.Null or JsonValueKind.False => false,
        JsonValueKind.Number => v.GetValue<double>() != 0,
        JsonValueKind.String => v.GetValue<string>().Length > 0,
        JsonValueKind.Array => ((JsonArray)v).Count > 0,
        JsonValueKind.Object => ((JsonObject)v).Count > 0,
        _ => true,
    };

    /// <summary>JSON の表記（1 行．日本語はエスケープしない）．エラーの文に値を見せるとき用．</summary>
    public static string Text(JsonNode? n)
    {
        if (n is null)
        {
            return "null";
        }
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Compact))
        {
            n.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
