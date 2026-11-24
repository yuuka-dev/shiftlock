using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Shiftlock.Core;

namespace Shiftlock.Agent;

/// <summary>
/// 時刻と Windows 側の値を指定して，見張りを 1 回だけ行う（Python 版との突き合わせ用．shiftlock-agent.exe --tick-once）．
/// pc/tools/tick_once.py と同じ引数・同じ形の出力（キーは名前順，日本語はそのまま，インデント 1）．
/// 通知は出さず，強制も実行しない（結果の JSON を出すだけ）．渡したフォルダのファイルは書き換えるので，本番フォルダのコピーに向けて使う．
/// 本番の実行フォルダ（%USERPROFILE%\shift-lock）を指していたら，何もせず終了コード 2 で断る．
///   --dir &lt;フォルダ&gt; --now 2026-12-01T09:00 --idle 30 --game 0 [--uptime 7200] [--dry-run]
/// </summary>
public static class TickOnce
{
    /// <summary>引数がおかしい・本番のフォルダを指している．</summary>
    public const int Refused = 2;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentCharacter = ' ',
        IndentSize = 1,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>args は --tick-once より後ろ．成功 0，見張りの失敗 1，引数がおかしい・本番のフォルダ 2．</summary>
    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        string? dir = null;
        DateTime? now = null;
        double? idle = null;
        bool? game = null;
        double? uptime = null;
        var dryRun = false;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--dry-run")
            {
                dryRun = true;
                continue;
            }
            if (i + 1 >= args.Count)
            {
                return Refuse(error, $"bad argument: {args[i]}");
            }
            var value = args[++i];
            switch (args[i - 1])
            {
                case "--dir":
                    dir = value;
                    break;
                case "--now" when TimeUtil.ParseIso(value) is { } t:
                    now = t;
                    break;
                case "--idle" when Number(value) is { } d:
                    idle = d;
                    break;
                case "--game" when value is "0" or "1":
                    game = value == "1";
                    break;
                case "--uptime" when Number(value) is { } d:
                    uptime = d;
                    break;
                default:
                    return Refuse(error, $"bad argument: {args[i - 1]} {value}");
            }
        }
        if (string.IsNullOrEmpty(dir) || now is null || idle is null || game is null)
        {
            return Refuse(error, "required: --dir --now --idle --game");
        }
        if (IsProduction(dir))
        {
            return Refuse(error, $"refusing the production folder (use a copy): {dir}");
        }
        try
        {
            var cfg = ShiftConfig.Load(Path.Combine(dir, "shifts.json"));
            var result = Tick.Run(cfg, new Store(dir), cfg.Msg, new TickInput(idle.Value, game.Value, uptime, Remote: false, DryRun: dryRun), now.Value);
            output.Write(Render(result) + "\n");
            return 0;
        }
        catch (Exception e)
        {
            error.WriteLine($"tick-once: {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    /// <summary>本番の実行フォルダ（%USERPROFILE%\shift-lock）か．大文字小文字と末尾の区切りは区別しない．</summary>
    public static bool IsProduction(string dir)
    {
        var home = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrEmpty(home))
        {
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        try
        {
            return string.Equals(Normalize(dir), Normalize(Path.Combine(home, "shift-lock")), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>結果を Python の json.dumps(sort_keys=True, ensure_ascii=False, indent=1) と同じ形にする（run_tick の戻り値と同じキー）．</summary>
    public static string Render(TickResult result)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteStartArray("actions");
            foreach (var a in result.Actions)
            {
                w.WriteStartObject();
                if (a.Action is not null)
                {
                    w.WriteString("action", a.Action);
                }
                w.WriteString("kind", a.Kind);
                w.WriteString("method", a.Method);
                w.WriteString("sound", a.Sound);
                w.WriteBoolean("speak", a.Speak);
                w.WriteString("text", a.Text);
                w.WriteString("title", a.Title);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartObject("sound");
            w.WriteNumber("max_seconds", result.SoundMaxSeconds);
            w.WritePropertyName("volume");
            w.WriteRawValue(PyFloat(result.SoundVolume));
            w.WriteEndObject();
            w.WriteStartArray("warnings");
            foreach (var warning in result.Warnings)
            {
                w.WriteStringValue(warning);
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Python の float の表記（1.0 は "1.0"）．無限大・NaN は JSON に書けないので 0.0．</summary>
    private static string PyFloat(double d)
    {
        if (!double.IsFinite(d))
        {
            return "0.0";
        }
        var text = d.ToString("R", CultureInfo.InvariantCulture);
        return text.AsSpan().IndexOfAny('.', 'E') < 0 ? text + ".0" : text;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;

    private static int Refuse(TextWriter error, string message)
    {
        error.WriteLine("tick-once: " + message);
        return Refused;
    }
}
