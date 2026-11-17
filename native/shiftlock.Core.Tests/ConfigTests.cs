using System.Text.Json.Nodes;

namespace Shiftlock.Core.Tests;

/// <summary>設定の読み込み・検証と乗務日の判定が Python 版（config.load_config / plan.is_workday）と同じになること．</summary>
public sealed class ConfigTests
{
    private const string Base =
        """{"lang":"ja","work_weekdays":["mon","tue","wed","thu","fri"],"start_target":"17:00","start_latest":"17:30","prep_minutes":60,"sleep_target_hours":8,"sleep_min_hours":6,"lastcall_minutes":40}""";

    public static IEnumerable<object[]> WorkdayCases => Vectors.Cases("plan.json", "workday");

    public static IEnumerable<object[]> ConfigErrorCases => Vectors.Cases("plan.json", "config_error");

    /// <summary>土台の設定に over（JSON のオブジェクト）を上書きした shifts.json を置いたフォルダ．</summary>
    private static TempFolder Folder(string over = "{}")
    {
        var shifts = JsonNode.Parse(Base)!.AsObject();
        foreach (var (k, v) in JsonNode.Parse(over)!.AsObject())
        {
            shifts[k] = v?.DeepClone();
        }
        return Vectors.Materialize(new JsonObject { ["shifts.json"] = shifts });
    }

    [Theory]
    [MemberData(nameof(WorkdayCases))]
    public void IsWorkday_MatchesPython(VectorCase c)
    {
        using var folder = Vectors.Materialize(c.Files);
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        Assert.Equal(c.Expect["work"]!.GetValue<bool>(), Planner.IsWorkday(cfg, Vectors.Day(c.Input["day"])));
    }

    [Theory]
    [MemberData(nameof(ConfigErrorCases))]
    public void Load_ValidatesLikePython(VectorCase c)
    {
        using var folder = Vectors.Materialize(c.Files);
        if (c.Expect.ContainsKey("ok"))
        {
            ShiftConfig.Load(folder.ShiftsJson);
            return;
        }
        var e = Assert.Throws<ConfigException>(() => ShiftConfig.Load(folder.ShiftsJson));
        // error が null のケースは Python 版が自前の文を持たない（例外の英語の文になる）ので，断ることだけ比べる
        if (c.Expect["error"] is { } expected)
        {
            Assert.Equal(expected.GetValue<string>(), e.Message);
        }
        else
        {
            Assert.NotEqual("", e.Message);
        }
    }

    [Fact]
    public void Load_MissingFile_ThrowsFileNotFound()
    {
        using var folder = new TempFolder();
        Assert.Throws<FileNotFoundException>(() => ShiftConfig.Load(folder.ShiftsJson));
        Assert.Throws<FileNotFoundException>(() => ShiftConfig.Load(Path.Combine(folder.Dir, "none", "shifts.json")));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("""{"a":1,"a":2}""")]
    public void Load_BrokenJson_ThrowsConfigException(string text)
    {
        using var folder = Vectors.Materialize(new JsonObject { ["shifts.json"] = text });
        var e = Assert.Throws<ConfigException>(() => ShiftConfig.Load(folder.ShiftsJson));
        Assert.StartsWith("JSON の書き方がおかしいよ", e.Message);
    }

    [Theory]
    [InlineData("""{"no_sleep":[{"weekday":"tue","from":"10:00"}]}""", "no_sleep[1]")]
    [InlineData("""{"no_sleep":[{"weekday":"tue","from":"10:00","to":"11:00"},{"date":"2026-12-01","to":"11:00"}]}""", "no_sleep[2]")]
    [InlineData("""{"no_sleep":[{"weekday":"tue","from":"10:00","to":"25:00"}]}""", "no_sleep[1]")]
    [InlineData("""{"no_sleep":[{"weekday":2,"from":"10:00","to":"11:00"}]}""", "no_sleep[1]")]
    [InlineData("""{"no_sleep":["tue"]}""", "no_sleep[1]")]
    [InlineData("""{"no_sleep":{"weekday":"tue"}}""", "no_sleep")]
    public void Load_BadNoSleepRule_ThrowsConfigException(string over, string key)
    {
        // Python 版は読み込みでは通し，逆算の途中で落ちる．ネイティブ版は落ちずに設定のエラーにする
        using var folder = Folder(over);
        var e = Assert.Throws<ConfigException>(() => ShiftConfig.Load(folder.ShiftsJson));
        Assert.StartsWith(key + " の書き方がおかしいよ", e.Message);
    }

    [Theory]
    [InlineData("""{"prep_minutes":"abc"}""", "prep_minutes")]
    [InlineData("""{"prep_minutes":null}""", "prep_minutes")]
    [InlineData("""{"sleep_target_hours":[8]}""", "sleep_target_hours")]
    [InlineData("""{"sleep_min_hours":"nan"}""", "sleep_min_hours")]
    [InlineData("""{"lastcall_minutes":1e300}""", "lastcall_minutes")]
    [InlineData("""{"start_target":1700}""", "start_target")]
    [InlineData("""{"start_latest":null}""", "start_latest")]
    [InlineData("""{"shift_end_earliest":"7時"}""", "shift_end_earliest")]
    [InlineData("""{"work_weekdays":5}""", "work_weekdays")]
    public void Load_BadValue_ThrowsConfigException(string over, string key)
    {
        using var folder = Folder(over);
        var e = Assert.Throws<ConfigException>(() => ShiftConfig.Load(folder.ShiftsJson));
        Assert.StartsWith(key + " の書き方がおかしいよ", e.Message);
    }

    [Fact]
    public void Load_BadValueMessage_ShowsTheValue()
    {
        using var folder = Folder("""{"prep_minutes":"九十"}""");
        var e = Assert.Throws<ConfigException>(() => ShiftConfig.Load(folder.ShiftsJson));
        Assert.Equal("prep_minutes の書き方がおかしいよ: \"九十\"", e.Message);
    }

    [Fact]
    public void Load_LangMissing_IsJa()
    {
        // lang が無い → 標準語（Python 版の既定は関西弁．設計書 4.6）
        using var folder = Vectors.Materialize(new JsonObject { ["shifts.json"] = new JsonObject() });
        Assert.Equal("ja", ShiftConfig.Load(folder.ShiftsJson).Lang);
        using var inm = Folder("""{"lang":"inm"}""");
        Assert.Equal("ja", ShiftConfig.Load(inm.ShiftsJson).Lang);
        using var kansai = Folder("""{"lang":"ja_kansai"}""");
        Assert.Equal("ja_kansai", ShiftConfig.Load(kansai.ShiftsJson).Lang);
    }

    [Fact]
    public void Load_EmptyObject_UsesPythonDefaults()
    {
        using var folder = Vectors.Materialize(new JsonObject { ["shifts.json"] = new JsonObject() });
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        Assert.Equal(["mon", "tue", "wed", "thu", "fri", "sat"], cfg.WorkWeekdays);
        Assert.Equal(("20:00", "22:30"), (cfg.StartTarget, cfg.StartLatest));
        Assert.Equal((90, 40, 5, 4), (cfg.PrepMinutes, cfg.LastcallMinutes, cfg.SleepTargetHours, cfg.SleepMinHours));
        Assert.Empty(cfg.NoSleep);
        Assert.Equal(folder.Dir, cfg.Dir);
    }

    [Theory]
    [InlineData("{}", "6")]
    [InlineData("""{"sleep_min_hours":5}""", "5")]
    [InlineData("""{"sleep_min_hours":5.0}""", "5.0")]
    [InlineData("""{"sleep_min_hours":4.5}""", "4.5")]
    [InlineData("""{"sleep_min_hours":5.50}""", "5.5")]
    [InlineData("""{"sleep_min_hours":"5"}""", "5")]
    public void SleepMinHoursText_KeepsJsonNotation(string over, string expected)
    {
        using var folder = Folder(over);
        Assert.Equal(expected, ShiftConfig.Load(folder.ShiftsJson).SleepMinHoursText);
    }

    [Fact]
    public void SleepMinHoursText_DefaultIs4()
    {
        using var folder = Vectors.Materialize(new JsonObject { ["shifts.json"] = new JsonObject() });
        Assert.Equal("4", ShiftConfig.Load(folder.ShiftsJson).SleepMinHoursText);
    }

    [Theory]
    [InlineData("{}", "6")]
    [InlineData("""{"auto_wake_hours":null}""", "6")]
    [InlineData("""{"auto_wake_hours":0}""", "6")]
    [InlineData("""{"auto_wake_hours":3}""", "3")]
    [InlineData("""{"auto_wake_hours":3.5}""", "3.5")]
    public void AutoWakeHoursText_FallsBackToSleepMin(string over, string expected)
    {
        using var folder = Folder(over);
        Assert.Equal(expected, ShiftConfig.Load(folder.ShiftsJson).AutoWakeHoursText);
    }

    [Fact]
    public void WorkWeekdays_DictKeysAndEmptyString_ReadLikePython()
    {
        // Python 版は「曜日 in work_weekdays」で判定するので，辞書はキー，空の文字列は空として通る
        using var dict = Folder("""{"work_weekdays":{"sat":1}}""");
        Assert.Equal(["sat"], ShiftConfig.Load(dict.ShiftsJson).WorkWeekdays);
        using var empty = Folder("""{"work_weekdays":""}""");
        Assert.Empty(ShiftConfig.Load(empty.ShiftsJson).WorkWeekdays);
    }

    [Theory]
    [InlineData("""["x'y"]""", """["x'y"]""")]
    [InlineData("""["a\"b'c"]""", """['a"b\'c']""")]
    [InlineData("""[1, 2.0, true, null, "日"]""", "[1, 2.0, True, None, '日']")]
    [InlineData("""[{"k":["v"]}]""", "[{'k': ['v']}]")]
    [InlineData("\"mo\"", "['m', 'o']")]
    public void BadWeekday_MessageUsesPythonRepr(string weekdays, string repr)
    {
        using var folder = Folder($$"""{"work_weekdays":{{weekdays}}}""");
        var e = Assert.Throws<ConfigException>(() => ShiftConfig.Load(folder.ShiftsJson));
        Assert.Equal("work_weekdays に知らない曜日: " + repr, e.Message);
    }

    [Fact]
    public void DaysOffAndWork_KeepReasons()
    {
        using var folder = Vectors.Materialize(new JsonObject
        {
            ["shifts.json"] = JsonNode.Parse(Base),
            ["days.json"] = JsonNode.Parse("""{"off":{"2026-12-03":"用事"},"work":{"2026-12-05":""}}"""),
            ["calendar.json"] = JsonNode.Parse("""{"sources":{"A":{"off":{"2026-12-04":"公休","2026-12-03":"別"}}}}"""),
        });
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        Assert.Equal("用事", cfg.DaysOff["2026-12-03"]);   // 手動がカレンダーより優先
        Assert.Equal("公休", cfg.DaysOff["2026-12-04"]);
        Assert.Equal("", cfg.DaysWork["2026-12-05"]);
    }

    [Fact]
    public void TimesFor_BadOverride_ThrowsConfigException()
    {
        using var folder = Vectors.Materialize(new JsonObject
        {
            ["shifts.json"] = JsonNode.Parse(Base),
            ["days.json"] = JsonNode.Parse("""{"times":{"2026-12-01":{"start_target":"夕方"},"2026-12-02":{"sleep_min_hours":"x"}}}"""),
        });
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        Assert.Throws<ConfigException>(() => cfg.TimesFor("2026-12-01"));
        Assert.Throws<ConfigException>(() => Planner.MakePlan(cfg, new DateOnly(2026, 12, 2)));
        Assert.Equal(new DayTimes(null, null, null, null), cfg.TimesFor("2026-12-03"));
    }
}
