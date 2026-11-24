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
    [InlineData("okita")]
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
    [InlineData("SHL_NOT_YET", "day off", "day", "off", "12/11")]
    [InlineData("SHL_NOT_YET", "day work", "day", "work", "12/12")]
    [InlineData("SHL_NOT_YET", "day rm", "day", "rm", "12/11")]
    [InlineData("SHL_UNKNOWN", "day", "day")]
    [InlineData("SHL_UNKNOWN", "day nanika", "day", "nanika")]
    public void Day_OtherActions_AreRefused_AndWriteNothing(string key, string shown, params string[] args)
    {
        var path = Config(Base);
        var r = Run([.. args, "--config", path]);
        Assert.Equal(1, r.Code);
        Assert.Equal("", r.Out);
        Assert.Equal(Messages.Load("ja").Format(key, ("cmd", shown)) + "\n", r.Err);
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void Errors_UseLangOfConfig()
    {
        var path = Config("""{"lang": "ja_kansai"}""");
        var r = Run(["nanika", "--config", path]);
        Assert.Equal(Messages.Load("ja_kansai").Format("SHL_UNKNOWN", ("cmd", "nanika")) + "\n", r.Err);
    }
}
