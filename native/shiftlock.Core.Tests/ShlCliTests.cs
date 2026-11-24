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

    private Result Run(string[] args, string? envDir = null, string? home = null, string stdin = "")
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var io = new ShlCli.Io(new StringReader(stdin), stdout, stderr,
            name => name == ShlCli.DirEnv ? envDir : null, home ?? Path.Combine(_dir, "no-home"));
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
        var io = new ShlCli.Io(TextReader.Null, TextWriter.Null, TextWriter.Null, _ => null, @"C:\Users\x");
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
    [InlineData("status")]
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

    [Fact]
    public void Errors_UseLangOfConfig()
    {
        var path = Config("""{"lang": "ja_kansai"}""");
        var r = Run(["nanika", "--config", path]);
        Assert.Equal(Messages.Load("ja_kansai").Format("SHL_UNKNOWN", ("cmd", "nanika")) + "\n", r.Err);
    }
}
