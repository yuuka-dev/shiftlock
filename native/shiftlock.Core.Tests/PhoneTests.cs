using System.Text;
using System.Text.Json.Nodes;

namespace Shiftlock.Core.Tests;

/// <summary>
/// iPhone の記録（mood.csv）の読み取りと事後報告の部品．手で壊れた・iCloud が重ねた mood.csv でも止まらないこと（Review Focus 2）．
/// </summary>
public sealed class PhoneTests
{
    private const string Base =
        """{"lang":"ja","work_weekdays":["mon","tue","wed","thu","fri"],"start_target":"17:00","start_latest":"17:30","prep_minutes":60,"sleep_target_hours":8,"sleep_min_hours":6,"lastcall_minutes":40,"confirm":{"mode":"off"}}""";

    private static DateTime T(string s) => TimeUtil.ParseIso(s)!.Value;

    private static (TempFolder Folder, ShiftConfig Cfg) Open(byte[]? mood, string? moodFile = null, string over = "{}")
    {
        var folder = new TempFolder();
        if (mood is not null)
        {
            File.WriteAllBytes(folder.P("mood.csv"), mood);
        }
        var shifts = JsonNode.Parse(Base)!.AsObject();
        shifts["mood_file"] = moodFile ?? folder.P("mood.csv");
        foreach (var (k, v) in JsonNode.Parse(over)!.AsObject())
        {
            shifts[k] = v?.DeepClone();
        }
        File.WriteAllText(folder.ShiftsJson, shifts.ToJsonString());
        return (folder, ShiftConfig.Load(folder.ShiftsJson));
    }

    private static byte[] Utf8(string s, bool bom = false) =>
        [.. bom ? Encoding.UTF8.GetPreamble() : [], .. Encoding.UTF8.GetBytes(s)];

    [Fact]
    public void ReadMarks_Bom_Crlf_BrokenLines_Duplicates()
    {
        var text = string.Join("\r\n",
            "2026-12-07 08:10,neru",                 // 先頭行（BOM の直後）
            "2026-12-07 08:10,neru",                 // iCloud が重ねた同じ行は 1 件
            "neru",                                  // 列が 1 つ
            "きのう,okita",                          // 読めない日時
            "2026-13-40 08:10,okita",                // 無い日付
            "2026-12-07 25:10,okita",                // 無い時刻
            "2026-12-07 09:00,1,メモ",               // 気分の行
            "2026-12-07 14:20,起きた",
            "2026-12-07 07:05 , ただいま , 余分",
            "2026-12-08T09:00:59,yasumi: 2026-12-07 ",
            "2026-12-08 09:00,deta:",
            "",
            ",");
        var (folder, cfg) = Open(Utf8(text, bom: true));
        using (folder)
        {
            Assert.Equal(
            [
                (T("2026-12-07T07:05"), "kitaku", null),
                (T("2026-12-07T08:10"), "neru", null),
                (T("2026-12-07T14:20"), "okita", null),
                (T("2026-12-08T09:00"), "worked", null),
                (T("2026-12-08T09:00"), "yasumi", "2026-12-07"),
            ], Phone.ReadMarks(cfg));
        }
    }

    [Fact]
    public void ReadMarks_SameTime_SortsByKindThenExtra()
    {
        var (folder, cfg) = Open(Utf8("2026-12-08 09:00,yasumi:2026-12-07\n2026-12-08 09:00,yasumi:2026-12-04\n2026-12-08 09:00,okita\n"));
        using (folder)
        {
            Assert.Equal([("okita", null), ("yasumi", "2026-12-04"), ("yasumi", "2026-12-07")],
                Phone.ReadMarks(cfg).Select(x => (x.Kind, x.Extra)));
        }
    }

    [Fact]
    public void ReadMarks_WslStylePath()
    {
        // 設定に /mnt/c/... と書いてあっても Windows のパスとして読む
        var (folder, _) = Open(Utf8("2026-12-07 08:10,neru\n"));
        using (folder)
        {
            var root = Path.GetPathRoot(folder.Dir)!;
            var wsl = "/mnt/" + char.ToLowerInvariant(root[0]) + "/" + folder.Dir[root.Length..].Replace('\\', '/') + "/mood.csv";
            File.WriteAllText(folder.ShiftsJson, new JsonObject { ["mood_file"] = wsl }.ToJsonString());
            Assert.Single(Phone.ReadMarks(ShiftConfig.Load(folder.ShiftsJson)));
        }
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("[\"mood.csv\"]")]
    [InlineData("\"C:\\\\none\\\\none\\\\mood.csv\"")]
    [InlineData("\"mood<>|?.csv\"")]
    public void ReadMarks_NoFileOrOddSetting_IsEmpty(string moodFile)
    {
        using var folder = Vectors.Materialize(new JsonObject { ["shifts.json"] = JsonNode.Parse($$"""{"mood_file":{{moodFile}}}""") });
        Assert.Empty(Phone.ReadMarks(ShiftConfig.Load(folder.ShiftsJson)));
    }

    [Fact]
    public void ReadMarks_InvalidUtf8_DoesNotThrow()
    {
        var (folder, cfg) = Open([.. Utf8("2026-12-07 08:10,neru\n"), 0xFF, 0xFE, 0x0A, .. Utf8("2026-12-07 14:20,okita\n")]);
        using (folder)
        {
            Assert.Equal(["neru", "okita"], Phone.ReadMarks(cfg).Select(x => x.Kind));
        }
    }

    [Fact]
    public void ImportMarks_FileHeldByAnotherProcess_ReturnsEmpty()
    {
        var (folder, cfg) = Open(Utf8("2026-12-07 08:10,neru\n"));
        using (folder)
        {
            using (new FileStream(folder.P("mood.csv"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Equal("", Phone.ImportMarks(cfg, new Store(folder.Dir), cfg.Msg, T("2026-12-07T08:15")));
            }
            Assert.False(File.Exists(folder.P("state.json")));
        }
    }

    // ---- 事後報告の部品 ----

    [Theory]
    [InlineData("2026-12-02", "2026-12-02")]
    [InlineData(" 12/2 ", "2026-12-02")]
    [InlineData("12／02", "2026-12-02")]
    [InlineData("今日", "2026-12-10")]
    [InlineData("きのう", "2026-12-09")]
    [InlineData("昨日", "2026-12-09")]
    [InlineData("一昨日", "2026-12-08")]
    [InlineData("おととい", "2026-12-08")]
    [InlineData("明日", "2026-12-11")]
    [InlineData("12/31", "2026-12-31")]      // 未来でも年末年始でなければ今年のまま
    public void ParseDay_Past(string text, string expected)
    {
        Assert.Equal(DateOnly.Parse(expected), LateReport.ParseDay(text, new DateOnly(2026, 12, 10)));
    }

    [Fact]
    public void ParseDay_Past_NewYear_GoesToLastYear()
    {
        // 1〜2 月に「12/30」と書いたら去年の 12 月
        Assert.Equal(new DateOnly(2026, 12, 30), LateReport.ParseDay("12/30", new DateOnly(2027, 1, 3)));
        Assert.Equal(new DateOnly(2027, 1, 2), LateReport.ParseDay("1/2", new DateOnly(2027, 1, 3)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("あさって")]
    [InlineData("13/1")]
    [InlineData("2/30")]
    [InlineData("12/1/2")]
    [InlineData("2026-12-40")]
    [InlineData("12-02x")]
    public void ParseDay_Unreadable_Throws(string text)
    {
        Assert.Throws<FormatException>(() => LateReport.ParseDay(text, new DateOnly(2026, 12, 10)));
    }

    [Fact]
    public void Report_FailureMessages()
    {
        var (folder, cfg) = Open(null);
        using (folder)
        {
            var store = new Store(folder.Dir);
            var m = cfg.Msg;
            var now = T("2026-12-10T10:00");

            Assert.Equal((false, "[shiftlock] 12/11(金) は未来だよ。"), LateReport.Report(cfg, store, m, now, "yasumi", "12/11"));
            Assert.Equal((false, "[shiftlock] 12/05(土) はもともと休みだよ。"), LateReport.Report(cfg, store, m, now, "yasumi", "12/5"));
            Assert.Equal((false, "[shiftlock] 12/10(木) の乗務はまだ終わっていないよ。休むなら『今日休む』、出発するなら『行ってきます』。"),
                LateReport.Report(cfg, store, m, now, "worked", "今日"));
            var (ok, text) = LateReport.Report(cfg, store, m, now, "yasumi", "あさって");
            Assert.False(ok);
            Assert.StartsWith("[shiftlock] 日付が読めない: ", text);
            Assert.False(File.Exists(folder.P("state.json")));
        }
    }

    [Fact]
    public void Report_TooOld_And_NoRecentShift()
    {
        var (folder, cfg) = Open(null, over: """{"work_weekdays":[],"retention":{"days_keep":3}}""");
        using (folder)
        {
            var store = new Store(folder.Dir);
            var now = T("2026-12-10T10:00");
            Assert.Equal((false, "[shiftlock] 12/06(日) は古すぎて記録できない（3 日より前）。"),
                LateReport.Report(cfg, store, cfg.Msg, now, "worked", "12/6"));
            Assert.Equal((false, "[shiftlock] 直近 7 日に終わった乗務はないよ。日付付きなら『11/3 休んだ』のように。"),
                LateReport.Report(cfg, store, cfg.Msg, now, "worked"));
        }
    }

    [Fact]
    public void Report_Yasumi_WithPaid_ThenTogglePaid_ThenAlready()
    {
        var (folder, cfg) = Open(null);
        using (folder)
        {
            var store = new Store(folder.Dir);
            var m = cfg.Msg;
            var now = T("2026-12-10T10:00");

            // 日付なし → 直近に終わった乗務（12/09）
            Assert.Equal((true, "[shiftlock] 12/09(水) は休んだことにしたよ（事後報告）。今月の休み 1 回目。違ったら『今のなし』。（有給）"),
                LateReport.Report(cfg, store, m, now, "yasumi", paid: true));
            Assert.True(store.ShiftStatus()["2026-12-09"]!["paid"]!.GetValue<bool>());

            Assert.Equal((true, "[shiftlock] 12/09(水) の休みの有給を外したよ（当日欠勤に数える）。"),
                LateReport.Report(cfg, store, m, now, "yasumi", "12/9", paid: false));
            Assert.False(store.ShiftStatus()["2026-12-09"]!.AsObject().ContainsKey("paid"));

            Assert.Equal((false, "[shiftlock] 12/09(水) はもう休みになっているよ。"), LateReport.Report(cfg, store, m, now, "yasumi", "12/9"));

            Assert.Equal(
            [
                "2026-12-10T10:00\t2026-12-09\tyasumi\tlate claude",
                "2026-12-10T10:00\t2026-12-09\tpaid\ton claude",
                "2026-12-10T10:00\t2026-12-09\tpaid\toff claude",
            ], File.ReadAllText(folder.P("events.log")).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
    }

    [Fact]
    public void Report_NoDate_PrefersTheShiftThatWasAsked()
    {
        // 直近 36 時間に「出た？休んだ？」と聞いた未確認の乗務が，直近に終わった乗務より先
        var (folder, cfg) = Open(null);
        using (folder)
        {
            File.WriteAllText(folder.P("state.json"),
                """{"shifts":{"2026-12-07":{"confirm_asked":"2026-12-09T08:00"},"2026-12-08":{"confirm_asked":"2026-12-09T09:00","status":"worked"},"2026-12-04":{"confirm_asked":"こわれた"}}}""");
            var store = new Store(folder.Dir);

            var (ok, text) = LateReport.Report(cfg, store, cfg.Msg, T("2026-12-10T10:00"), "worked");

            Assert.True(ok);
            Assert.Equal("[shiftlock] 12/07(月) は出たことにしたよ。違ったら『今のなし』。", text);
        }
    }
}
