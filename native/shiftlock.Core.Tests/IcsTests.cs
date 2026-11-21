using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Shiftlock.Core.Tests;

/// <summary>
/// iCal の解析とカレンダー同期のうち，ベクタ（tick.json の calendar）で見られないところ．
/// 壊れた入力の扱い，Sync の返事の文，強制同期，既定の取得（HTTP）．
/// </summary>
public sealed class IcsTests
{
    private const string Url = "https://cal.example/a.ics";

    private static readonly string[] Off = ["休み", "公休"];
    private static readonly string[] Work = ["出勤"];

    private static readonly DateTime Now = new(2026, 12, 7, 7, 15, 0);

    private static string Ics(params string[] lines) => string.Concat(lines.Select(l => l + "\r\n"));

    private static string AllDay(string summary, string day) =>
        Ics("BEGIN:VEVENT", "DTSTART;VALUE=DATE:" + day, "SUMMARY:" + summary, "END:VEVENT");

    private static TempFolder Folder(string calendar = $$"""{"ics_url":"{{Url}}"}""", string? calendarJson = null)
    {
        var files = new JsonObject
        {
            ["shifts.json"] = JsonNode.Parse(
                """{"lang":"ja","work_weekdays":["mon","tue","wed","thu","fri"],"start_target":"17:00","start_latest":"17:30","confirm":{"mode":"off"}}"""),
        };
        files["shifts.json"]!["calendar"] = JsonNode.Parse(calendar);
        if (calendarJson is not null)
        {
            files["calendar.json"] = JsonNode.Parse(calendarJson);
        }
        return Vectors.Materialize(files);
    }

    private static string Sync(TempFolder folder, Func<string, string> fetch, DateTime? now = null, bool force = false)
    {
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        return CalendarSync.Sync(cfg, new Store(folder.Dir), cfg.Msg, now ?? Now, fetch, force);
    }

    private static JsonObject Source(TempFolder folder, string name = "カレンダー") =>
        JsonNode.Parse(File.ReadAllText(folder.P("calendar.json")))!["sources"]![name]!.AsObject();

    [Theory]
    [InlineData("")]
    [InlineData("BEGIN:VEVENT")]
    [InlineData("BEGIN:VEVENT\r\nSUMMARY:公休\r\nDTSTART;VALUE=DATE:20261207")]
    [InlineData("END:VEVENT\r\nSUMMARY:公休\r\nDTSTART;VALUE=DATE:20261207")]
    public void ParseIcs_NothingToPick_IsEmpty(string text)
    {
        var res = CalendarSync.ParseIcs(text, Off, Work);

        Assert.Equal("""{"off":{},"work":{},"recurring_skipped":0}""", res.ToJsonString());
    }

    [Fact]
    public void ParseIcs_ZTime_IsLocalDate()
    {
        // 15:00Z は日本では翌日の 0:00．テストを動かすマシンのタイムゾーンで期待値を作る
        var local = new DateTime(2026, 12, 7, 15, 0, 0, DateTimeKind.Utc).ToLocalTime();

        var res = CalendarSync.ParseIcs(Ics("BEGIN:VEVENT", "DTSTART:20261207T150000Z", "SUMMARY:公休", "END:VEVENT"), Off, Work);

        Assert.Equal([local.ToString("yyyy-MM-dd")], res["off"]!.AsObject().Select(kv => kv.Key));
    }

    [Theory]
    [InlineData("DTSTART;VALUE=DATE:2026XX07")]
    [InlineData("DTSTART;VALUE=DATE:20261307")]
    [InlineData("DTSTART:2026-12-07T10:00:00")]
    [InlineData("DTSTART:")]
    public void ParseIcs_UnreadableDate_Throws(string dtstart)
    {
        Assert.Throws<FormatException>(() => CalendarSync.ParseIcs(Ics("BEGIN:VEVENT", dtstart, "SUMMARY:公休", "END:VEVENT"), Off, Work));
    }

    [Fact]
    public void ParseIcs_UnreadableDate_OfEventWithoutKeyword_IsIgnored()
    {
        var res = CalendarSync.ParseIcs(Ics("BEGIN:VEVENT", "DTSTART:x", "SUMMARY:歯医者", "END:VEVENT") + AllDay("公休", "20261207"), Off, Work);

        Assert.Equal("""{"2026-12-07":"公休"}""", res["off"]!.ToJsonString(TickRunnerOptions.Relaxed));
    }

    [Fact]
    public void Sync_Unset_SaysSo_AndWritesNothing()
    {
        using var folder = Folder("""{"ics_url":"  "}""");

        Assert.Equal("カレンダー未設定", Sync(folder, _ => throw new InvalidOperationException("呼ばれない")));
        Assert.False(File.Exists(folder.P("calendar.json")));
    }

    [Fact]
    public void Sync_ReturnsOneLinePerSource()
    {
        using var folder = Folder($$"""{"sources":[{"name":"会社","ics_url":"{{Url}}"},{"name":"私用","ics_url":"{{Url}}2"}]}""");

        var first = Sync(folder, url => url == Url ? AllDay("公休", "20261207") + AllDay("出勤", "20261212") + AllDay("休み", "20261208")
            : throw new IOException("fetch failed"));
        var second = Sync(folder, _ => throw new IOException("fetch failed"), Now.AddMinutes(5));

        Assert.Equal("同期OK: 会社 休み 2 日 / 出勤 1 日\n同期失敗（前回の内容で続行）: 私用 fetch failed", first);
        Assert.Equal("スキップ（まだ新しい）: 会社\n同期失敗（前回の内容で続行）: 私用 fetch failed", second);
        Assert.Equal("12/07 07:20 OSError: fetch failed", Source(folder, "私用")["error"]!.GetValue<string>());
    }

    [Fact]
    public void Sync_Force_IgnoresSyncMinutes()
    {
        using var folder = Folder();
        Sync(folder, _ => AllDay("公休", "20261207"));

        Sync(folder, _ => AllDay("公休", "20261208"), Now.AddMinutes(1), force: true);

        Assert.Equal("""{"2026-12-08":"公休"}""", Source(folder)["off"]!.ToJsonString(TickRunnerOptions.Relaxed));
    }

    [Fact]
    public void Sync_UnreadableDate_KeepsPrevious_AndRecordsError()
    {
        using var folder = Folder();
        Sync(folder, _ => AllDay("公休", "20261207"));

        var line = Sync(folder, _ => AllDay("公休", "2026XX08"), Now.AddHours(2));

        Assert.StartsWith("同期失敗（前回の内容で続行）: カレンダー ", line);
        var src = Source(folder);
        Assert.Equal("""{"2026-12-07":"公休"}""", src["off"]!.ToJsonString(TickRunnerOptions.Relaxed));
        Assert.Equal("2026-12-07T07:15:00", src["fetched"]!.GetValue<string>());
        Assert.StartsWith("12/07 09:15 FormatException: ", src["error"]!.GetValue<string>());
    }

    [Fact]
    public void Sync_LongError_IsCutAt300()
    {
        using var folder = Folder();

        Sync(folder, _ => throw new IOException(new string('x', 500)));

        Assert.Equal(300, Source(folder)["error"]!.GetValue<string>().Length);
    }

    [Theory]
    [InlineData("""{"ics_url":"https://cal.example/a.ics","sync_minutes":"毎時"}""", null)]
    [InlineData("""{"ics_url":5}""", null)]
    [InlineData("""{"ics_url":"https://cal.example/a.ics"}""", """{"sources":[]}""")]
    [InlineData("""{"ics_url":"https://cal.example/a.ics"}""", """{"sources":{"カレンダー":"x"}}""")]
    public void Sync_BrokenSettingsOrFile_Throws_AndLeavesFileAlone(string calendar, string? calendarJson)
    {
        // Python 版も同じところで例外になり，daily_maintenance が握る（その回は同期しない）
        using var folder = Folder(calendar, calendarJson);
        var before = calendarJson is null ? null : File.ReadAllText(folder.P("calendar.json"));

        Assert.ThrowsAny<Exception>(() => Sync(folder, _ => AllDay("公休", "20261207")));

        Assert.Equal(before, File.Exists(folder.P("calendar.json")) ? File.ReadAllText(folder.P("calendar.json")) : null);
    }

    [Fact]
    public void Sync_KeywordsThatAreNotStrings_FailThatSourceOnly()
    {
        using var folder = Folder($$"""{"sources":[{"name":"会社","ics_url":"{{Url}}","off_keywords":[5]},{"name":"私用","ics_url":"{{Url}}2"}]}""");

        Sync(folder, _ => AllDay("公休", "20261207"));

        Assert.NotNull(Source(folder, "会社")["error"]);
        Assert.Null(Source(folder, "私用")["error"]);
        Assert.Equal("""{"2026-12-07":"公休"}""", Source(folder, "私用")["off"]!.ToJsonString(TickRunnerOptions.Relaxed));
    }

    // ---- 既定の取得（HTTP） ----

    /// <summary>1 回だけ応答する小さな HTTP サーバー．返すのは決めたバイト列．</summary>
    private static (string Url, Task Served) Serve(string status, byte[] body)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = Task.Run(async () =>
        {
            try
            {
                // 誰も取りに来なければ 10 秒で諦める（テストを止めない）
                using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var client = await listener.AcceptTcpClientAsync(giveUp.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
                {
                }
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head);
                await stream.WriteAsync(body);
            }
            finally
            {
                listener.Stop();
            }
        });
        return ($"http://127.0.0.1:{port}/a.ics", served);
    }

    [Fact]
    public async Task HttpGet_ReadsUtf8_AndReplacesBadBytes()
    {
        var (url, served) = Serve("200 OK", [.. Encoding.UTF8.GetBytes("SUMMARY:公休"), 0xFF]);

        var text = CalendarSync.HttpGet(url);
        await served;

        Assert.Equal("SUMMARY:公休�", text);
    }

    [Fact]
    public async Task HttpGet_ErrorStatus_Throws()
    {
        var (url, served) = Serve("404 Not Found", []);

        Assert.Throws<HttpRequestException>(() => CalendarSync.HttpGet(url));
        await served;
    }
}

internal static class TickRunnerOptions
{
    /// <summary>日本語を \uXXXX にしない JSON の表記（比べる文字列を読みやすくする）．</summary>
    public static readonly System.Text.Json.JsonSerializerOptions Relaxed = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
