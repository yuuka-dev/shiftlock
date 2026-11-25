namespace Shiftlock.Core.Tests;

/// <summary>shl.exe の骨組み: 引数の解釈・設定の場所・ヘルプ・終了コード．</summary>
public sealed class ShlCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shl-cli-" + Guid.NewGuid().ToString("N"));

    public ShlCliTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    private sealed record Result(int Code, string Out, string Err);

    /// <summary>2026-12-10（木）12:00．</summary>
    private static readonly DateTime Noon = new(2026, 12, 10, 12, 0, 0);

    private Result Run(string[] args, string? envDir = null, string? home = null, string stdin = "", DateTime? now = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var io = new ShlCli.Io(new StringReader(stdin), stdout, stderr,
            name => name == ShlCli.DirEnv ? envDir : null, home ?? Path.Combine(_dir, "no-home"), now ?? Noon);
        var code = ShlCli.Run(args, io);
        return new Result(code, stdout.ToString(), stderr.ToString());
    }

    private string Config(string json, string sub = "data")
    {
        var d = Path.Combine(_dir, sub);
        Directory.CreateDirectory(d);
        var path = Path.Combine(d, ShlCli.ConfigName);
        File.WriteAllText(path, json);
        return path;
    }

    [Theory]
    [InlineData()]
    [InlineData("help")]
    [InlineData("-h")]
    [InlineData("--help")]
    [InlineData("?")]
    [InlineData("/?")]
    public void Help_WithoutConfig_PrintsStandardText(params string[] args)
    {
        var r = Run(args);
        Assert.Equal(0, r.Code);
        Assert.Equal(Messages.Load("ja")["CLI_HELP"] + "\n", r.Out);
        Assert.Equal("", r.Err);
    }

    [Fact]
    public void Help_UsesLangOfConfig()
    {
        var path = Config("""{"lang": "ja_kansai"}""");
        var expected = Messages.Load("ja_kansai")["CLI_HELP"] + "\n";
        Assert.NotEqual(Messages.Load("ja")["CLI_HELP"], Messages.Load("ja_kansai")["CLI_HELP"]);

        Assert.Equal(expected, Run(["help", "--config", path]).Out);
        Assert.Equal(expected, Run(["--config", path, "help"]).Out);
        Assert.Equal(expected, Run(["--config=" + path]).Out);
        // --config が無ければ SHIFTLOCK_DIR，それも無ければホームの shift-lock
        Assert.Equal(expected, Run(["help"], envDir: Path.GetDirectoryName(path)).Out);
        var home = Path.Combine(_dir, "home");
        Config("""{"lang": "ja_kansai"}""", Path.Combine("home", ShlCli.DefaultDirName));
        Assert.Equal(expected, Run(["help"], home: home).Out);
    }

    [Fact]
    public void Help_ConfigOptionWinsOverEnv()
    {
        var kansai = Config("""{"lang": "ja_kansai"}""", "a");
        var ja = Config("""{"lang": "ja"}""", "b");
        var r = Run(["help", "--config", ja], envDir: Path.GetDirectoryName(kansai));
        Assert.Equal(Messages.Load("ja")["CLI_HELP"] + "\n", r.Out);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[1]")]
    [InlineData("""{"lang": 3}""")]
    [InlineData("""{"lang": "inm"}""")]
    [InlineData("""{"lang": "ja_kansai", "work_weekdays": ["holiday"]}""")]
    public void Help_WorksWithBrokenConfig(string json)
    {
        var r = Run(["help", "--config", Config(json)]);
        Assert.Equal(0, r.Code);
        Assert.StartsWith("shiftlock の使い方", r.Out);
    }

    [Fact]
    public void ResolveConfig_ReadsWslPath()
    {
        var io = new ShlCli.Io(TextReader.Null, TextWriter.Null, TextWriter.Null, _ => null, @"C:\Users\x", Noon);
        Assert.Equal(@"C:\Users\x\shift-lock\shifts.json", ShlCli.ResolveConfig("/mnt/c/Users/x/shift-lock/shifts.json", io));
        Assert.Equal(@"C:\Users\x\shift-lock\shifts.json", ShlCli.ResolveConfig(null, io));
        Assert.Equal(@"C:\Users\x\shift-lock\shifts.json", ShlCli.ResolveConfig(" ", io));
        var env = io with { GetEnv = _ => @"D:\copy" };
        Assert.Equal(@"D:\copy\shifts.json", ShlCli.ResolveConfig(null, env));
    }

    [Fact]
    public void Hook_Stub_IsSilentAndZero()
    {
        var r = Run(["hook", "--config", Path.Combine(_dir, "none", "shifts.json")], stdin: """{"prompt": "ねる"}""");
        Assert.Equal(new Result(0, "", ""), r);
    }

    [Theory]
    [InlineData("hook", "--config")]
    [InlineData("hook", "--config=")]
    public void Hook_Stub_NeverFailsOnBadOptions(params string[] args)
    {
        // hook は引数が変でもプロンプトを止めない（H1 で中身を入れるまで，何も出さず 0）
        Assert.Equal(new Result(0, "", ""), Run(args));
    }

    [Fact]
    public void EmptyConfigOption_FallsBackToEnv()
    {
        var path = Config("""{"lang": "ja_kansai"}""");
        var r = Run(["help", "--config="], envDir: Path.GetDirectoryName(path));
        Assert.Equal(Messages.Load("ja_kansai")["CLI_HELP"] + "\n", r.Out);
    }

    [Theory]
    [InlineData("sync")]
    [InlineData("enforce", "on")]
    [InlineData("app", "limit", "add", "x.exe")]
    [InlineData("kaijo")]
    public void NotYetCommands_SayNotYet(params string[] args)
    {
        var r = Run(args);
        Assert.Equal(1, r.Code);
        Assert.Equal("", r.Out);
        Assert.Equal(Messages.Load("ja").Format("SHL_NOT_YET", ("cmd", args[0])) + "\n", r.Err);
    }

    [Theory]
    [InlineData("tick", "tick")]
    [InlineData("nanika", "nanika")]
    [InlineData("--yes", "--yes")]
    [InlineData("--config", "--config")]
    public void UnknownCommandOrOption_IsOneNotTwo(string arg, string shown)
    {
        var r = Run([arg]);
        Assert.Equal(1, r.Code);
        Assert.Equal("", r.Out);
        Assert.Equal(Messages.Load("ja").Format("SHL_UNKNOWN", ("cmd", shown)) + "\n", r.Err);
    }

    [Fact]
    public void BadOptionBeforeCommand_IsRefusedEvenForHelp()
    {
        // 値の無い --config は，ヘルプでも黙って通さない
        var r = Run(["help", "--config"]);
        Assert.Equal(1, r.Code);
        Assert.Contains("--config", r.Err);
    }

    // ---- 見るだけのコマンド（status・yotei・day list・kakunin）----

    private const string Base =
        """{"lang":"ja","work_weekdays":["mon","tue","wed","thu","fri"],"start_target":"17:00","start_latest":"17:30","prep_minutes":60,"sleep_target_hours":8,"sleep_min_hours":6,"lastcall_minutes":40}""";

    private (ShiftConfig Cfg, Store Store) Loaded(string path)
    {
        var cfg = ShiftConfig.Load(path);
        return (cfg, new Store(cfg.Dir));
    }

    [Fact]
    public void Status_PrintsStatusText()
    {
        var path = Config(Base);
        var r = Run(["status", "--config", path]);
        var (cfg, store) = Loaded(path);
        Assert.Equal(new Result(0, Commands.StatusText(cfg, store, cfg.Msg, Noon) + "\n", ""), r);
        Assert.StartsWith("[shiftlock] いま 12/10 12:00 / フェーズ: hard\n", r.Out);
    }

    [Fact]
    public void Yotei_Prints14Days_DayList_Prints28Days()
    {
        var path = Config(Base);
        var (cfg, _) = Loaded(path);
        var today = DateOnly.FromDateTime(Noon);

        var yotei = Run(["yotei"], envDir: Path.GetDirectoryName(path));
        Assert.Equal(new Result(0, Commands.ScheduleText(cfg, cfg.Msg, today, 14) + "\n", ""), yotei);
        Assert.Equal(2, yotei.Out.TrimEnd('\n').Split('\n').Length);

        var list = Run(["day", "list", "--config", path]);
        Assert.Equal(new Result(0, Commands.ScheduleText(cfg, cfg.Msg, today, 28) + "\n", ""), list);
        Assert.Equal(4, list.Out.TrimEnd('\n').Split('\n').Length);

        // list の後ろの語は見ない（Python 版と同じ）．--config=パス の形でも同じ
        Assert.Equal(list, Run(["day", "list", "12/11", "--config=" + path]));
        // 予定を見るだけのコマンドは何も書かない（state.json も作らない）
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void Status_WritesOnlyConfirmSince()
    {
        var path = Config(Base);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(Path.Combine(dir, "log"));
        File.WriteAllText(Path.Combine(dir, "log", "pc-2026-12.jsonl"), """{"t": "2026-12-01T09:00", "idle": 30, "game": 0}""" + "\n");

        Assert.Equal(0, Run(["status", "--config", path]).Code);

        Assert.Equal(["shifts.json", "state.json"], Directory.GetFiles(dir).Select(Path.GetFileName).Order());
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "state.json")))!.AsObject();
        Assert.Equal(["confirm_since"], state.Select(kv => kv.Key));
        Assert.Equal("2026-12-09", state["confirm_since"]!.GetValue<string>());
    }

    [Fact]
    public void Kakunin_PrintsUnconfirmed_InLangOfConfig()
    {
        var path = Config(Base.Replace("\"ja\"", "\"ja_kansai\""));
        var r = Run(["kakunin", "--config", path]);
        Assert.Equal(new Result(0, Messages.Load("ja_kansai")["CONFIRM_KAKUNIN_NONE"] + "\n", ""), r);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("yotei")]
    [InlineData("kakunin")]
    [InlineData("day", "list")]
    public void View_WithoutConfig_SaysNoConfig(params string[] args)
    {
        var missing = Path.Combine(_dir, "none", "shifts.json");
        var r = Run([.. args, "--config", missing]);
        Assert.Equal(1, r.Code);
        Assert.Equal("", r.Out);
        Assert.Equal(Messages.Load("ja").Format("APP_NO_CONFIG", ("path", missing)) + "\n", r.Err);
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"lang": "ja_kansai", "work_weekdays": ["holiday"]}""")]
    public void View_WithBrokenConfig_IsOneWithReason(string json)
    {
        var r = Run(["status", "--config", Config(json)]);
        Assert.Equal(1, r.Code);
        Assert.Equal("", r.Out);
        Assert.NotEqual("", r.Err);
        Assert.EndsWith("\n", r.Err);
    }

    [Theory]
    [InlineData("status", "extra")]
    [InlineData("yotei", "28")]
    [InlineData("kakunin", "--yes")]
    public void View_ExtraArgument_IsRefused(string cmd, string extra)
    {
        var r = Run([cmd, extra, "--config", Config(Base)]);
        Assert.Equal(1, r.Code);
        Assert.Equal("", r.Out);
        Assert.Equal(Messages.Load("ja").Format("SHL_UNKNOWN", ("cmd", extra)) + "\n", r.Err);
    }

    [Theory]
    [InlineData("SHL_UNKNOWN", "day", "day")]
    [InlineData("SHL_UNKNOWN", "day nanika", "day", "nanika")]
    [InlineData("SHL_UNKNOWN", "extra", "itte", "extra")]
    [InlineData("SHL_NOT_YET", "okita 7:00", "okita", "7:00")]
    [InlineData("SHL_NOT_YET", "neru 12/9", "neru", "12/9", "23:30")]
    public void OtherForms_AreRefused_AndWriteNothing(string key, string shown, params string[] args)
    {
        var path = Config(Base);
        var r = Run([.. args, "--config", path]);
        Assert.Equal(1, r.Code);
        Assert.Equal("", r.Out);
        Assert.Equal(Messages.Load("ja").Format(key, ("cmd", shown)) + "\n", r.Err);
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    // ---- 記録のコマンド（okita・neru・itte・yasumi・deru・day off|work|rm）----

    private string[] Names(string path) =>
        [.. Directory.GetFiles(Path.GetDirectoryName(path)!).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];

    private static string[] Lines(string path, string name) =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, name)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Neru_Okita_Itte_RecordAndPrint()
    {
        var path = Config(Base);
        var m = Messages.Load("ja");

        var neru = Run(["neru", "--config", path], now: new DateTime(2026, 12, 10, 8, 10, 0));
        Assert.Equal(0, neru.Code);
        Assert.StartsWith(m["PREFIX"], neru.Out);
        Assert.EndsWith("\n", neru.Out);
        Assert.Equal("", neru.Err);

        var okita = Run(["okita"], envDir: Path.GetDirectoryName(path), now: new DateTime(2026, 12, 10, 14, 30, 0));
        Assert.Equal(0, okita.Code);
        Assert.Contains("08:10〜14:30", okita.Out);

        var itte = Run(["itte", "--config", path], now: new DateTime(2026, 12, 10, 16, 0, 0));
        Assert.Equal(new Result(0, m.Format("RECORDS_SHUPPATSU", ("day", "12/10")) + "\n", ""), itte);

        Assert.Equal(["events.log", "shifts.json", "state.json"], Names(path));
        Assert.Equal(
        [
            "2026-12-10T08:10\t2026-12-10\tsleep_at\tclaude 2026-12-10T08:10",
            "2026-12-10T14:30\t2026-12-10\tokita\tneru 08:10-14:30",
            "2026-12-10T16:00\t2026-12-10\tworked\tshuppatsu",
        ], Lines(path, "events.log"));
    }

    [Fact]
    public void Itte_TooEarly_PrintsRefusal_WithZero_AndWritesNothing()
    {
        // 断ったときも返事は標準出力・終了コードは 0（Python 版と同じ）
        var path = Config(Base);
        var r = Run(["itte", "--config", path]);
        Assert.Equal(new Result(0,
            Messages.Load("ja").Format("RECORDS_SHUPPATSU_EARLY", ("day", "12/10"), ("depart", "16:00")) + "\n", ""), r);
        Assert.Equal(["shifts.json"], Names(path));
    }

    [Fact]
    public void Yasumi_Deru_WithoutDate_SetAndClearNextShift()
    {
        var path = Config(Base);
        var m = Messages.Load("ja");
        var early = new DateTime(2026, 12, 10, 7, 0, 0);

        var yasumi = Run(["yasumi", "有給", "--config", path], now: early);
        Assert.Equal(new Result(0, m.Format("CMD_YASUMI", ("day", "12/10"), ("n", 1)) + m["CMD_YASUMI_PAID"] + "\n", ""), yasumi);

        var deru = Run(["deru", "--config", path], now: early.AddMinutes(5));
        Assert.Equal(new Result(0, m.Format("CMD_UNDO", ("day", "12/10"), ("what", m["CMD_UNDO_YASUMI"])) + "\n", ""), deru);

        Assert.Equal(
        [
            "2026-12-10T07:00\t2026-12-10\tyasumi\t",
            "2026-12-10T07:00\t2026-12-10\tpaid\ton shl",
            "2026-12-10T07:05\t2026-12-10\tundo\t",
        ], Lines(path, "events.log"));
    }

    [Fact]
    public void Yasumi_Deru_WithDate_ReportPastShift()
    {
        var path = Config(Base);
        var m = Messages.Load("ja");

        var yasumi = Run(["yasumi", "昨日", "--config", path]);
        Assert.Equal(new Result(0, m.Format("RECORDS_LATE_YASUMI", ("label", "12/09(水)"), ("n", 1)) + "\n", ""), yasumi);

        var deru = Run(["deru", "12/9", "--config", path]);
        Assert.Equal(new Result(0, m.Format("RECORDS_LATE_WORKED", ("label", "12/09(水)")) + "\n", ""), deru);

        // 読めない日付は，断る文を標準出力に出して 0（Python 版と同じ．理由の文だけ違う）
        var bad = Run(["yasumi", "あさって", "--config", path]);
        Assert.Equal(0, bad.Code);
        Assert.StartsWith(m["RECORDS_LATE_BAD_DATE"].Split('{')[0], bad.Out);

        Assert.Equal(
        [
            "2026-12-10T12:00\t2026-12-09\tyasumi\tlate shl",
            "2026-12-10T12:00\t2026-12-09\tworked\tlate shl",
        ], Lines(path, "events.log"));
    }

    [Fact]
    public void Day_Off_Work_Rm_WriteDaysJson_AndPrintSchedule()
    {
        var path = Config(Base);
        var today = DateOnly.FromDateTime(Noon);

        var off = Run(["day", "off", "12/11", "通院", "予約", "--config", path]);
        var (cfg, _) = Loaded(path);
        Assert.Equal(new Result(0, Commands.ScheduleText(cfg, cfg.Msg, today) + "\n", ""), off);
        Assert.Contains("12/11金休（通院 予約）", off.Out);

        var work = Run(["day", "work", "明日", "--config", path]);
        Assert.Contains("12/11金出*", work.Out);

        var rm = Run(["day", "rm", "2026-12-11", "--config", path]);
        Assert.Contains("12/11金出  ", rm.Out);

        Assert.Equal(["days.json", "shifts.json"], Names(path));
    }

    [Fact]
    public void Day_WithoutDate_SaysNeedDate_OnStdout_WithOne()
    {
        var path = Config(Base);
        var r = Run(["day", "off", "--config", path]);
        Assert.Equal(new Result(1, Messages.Load("ja")["CLI_DAY_NEED_DATE"] + "\n", ""), r);
        Assert.Equal(["shifts.json"], Names(path));
    }

    [Theory]
    [InlineData("あさって")]
    [InlineData("13/1")]
    [InlineData("2/30")]
    [InlineData("12/")]
    [InlineData("2026-13-01")]
    public void Day_BadDate_IsOneWithReason_AndWritesNothing(string date)
    {
        var path = Config(Base);
        var r = Run(["day", "off", date, "--config", path]);
        Assert.Equal(1, r.Code);
        Assert.Equal("", r.Out);
        Assert.StartsWith(Messages.Load("ja")["CMD_BAD_DATE"].Split('{')[0], r.Err);
        Assert.Equal(["shifts.json"], Names(path));
    }

    [Theory]
    [InlineData("okita")]
    [InlineData("neru")]
    [InlineData("itte")]
    [InlineData("yasumi")]
    [InlineData("deru", "12/9")]
    [InlineData("day", "off", "12/11")]
    public void Record_WithoutConfig_SaysNoConfig(params string[] args)
    {
        var missing = Path.Combine(_dir, "none", "shifts.json");
        var r = Run([.. args, "--config", missing]);
        Assert.Equal(1, r.Code);
        Assert.Equal("", r.Out);
        Assert.Equal(Messages.Load("ja").Format("APP_NO_CONFIG", ("path", missing)) + "\n", r.Err);
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
    }

    [Theory]
    [InlineData("neru")]
    [InlineData("okita")]
    [InlineData("yasumi")]
    [InlineData("deru")]
    [InlineData("yasumi", "12/9")]
    public void Record_WithBrokenState_IsOne_AndKeepsTheFile(params string[] args)
    {
        // 読めない state.json を空として書き直さない（Python 版は書き直す）
        var path = Config(Base.Replace("\"ja\"", "\"ja_kansai\""));
        var state = Path.Combine(Path.GetDirectoryName(path)!, "state.json");
        File.WriteAllText(state, "{");

        var r = Run([.. args, "--config", path], now: new DateTime(2026, 12, 10, 14, 0, 0));

        Assert.Equal(1, r.Code);
        Assert.Equal("", r.Out);
        Assert.StartsWith(Messages.Load("ja_kansai")["SHL_STATE_BUSY"].Split('{')[0], r.Err);
        Assert.Equal("{", File.ReadAllText(state));
        Assert.Equal(["shifts.json", "state.json"], Names(path));
    }

    [Fact]
    public void Errors_UseLangOfConfig()
    {
        var path = Config("""{"lang": "ja_kansai"}""");
        var r = Run(["nanika", "--config", path]);
        Assert.Equal(Messages.Load("ja_kansai").Format("SHL_UNKNOWN", ("cmd", "nanika")) + "\n", r.Err);
    }
}
