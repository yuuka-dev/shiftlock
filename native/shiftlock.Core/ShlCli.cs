using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>
/// shl.exe のサブコマンドの配線（pc/shiftlock/cli.py の main に当たる）．
/// 入口（native/shl/Program.cs）は文字コードと終了コードの受け皿だけを持ち，引数の解釈と振り分けはここに置く．
///
/// 終了コードの決まり: 0＝できた，1＝断った・失敗．2 を返してよいのは hook の 3 つの場面だけ
/// （キーワードへの返事・聞き返しへの返事・ハードで止める．docs/native-gap.md §2.3）．
/// hook が 2 を返すとユーザーのプロンプトが消えるので，失敗で 2 にしない．
///
/// Python 版との違い:
/// - 知らないコマンド・引数の間違いは 1（Python 版は argparse が 2 で終わる）
/// - --config は省ける（環境変数 SHIFTLOCK_DIR → %USERPROFILE%\shift-lock の順で shifts.json を探す）．置く場所はどこでもよい
/// - 設定が無い・lang が無いときの言葉づかいは標準語（Python 版の既定は関西弁．ShiftConfig と同じ）
/// - 行末は LF だけ（Python 版は Windows の端末・パイプで CRLF）
/// - 設定が無い・壊れているときは 1 行の文を標準エラーに出して 1（Python 版はトレースバック）
/// - 引数を取らないコマンド（status・yotei・kakunin）に余分な引数があれば断る（Python 版は argparse が 2 で終わる）
/// </summary>
public static class ShlCli
{
    /// <summary>実行フォルダを差し替える環境変数（画面・常駐と同じ名前）．</summary>
    public const string DirEnv = "SHIFTLOCK_DIR";

    /// <summary>実行フォルダの既定の名前（ユーザーのホームの下）．</summary>
    public const string DefaultDirName = "shift-lock";

    /// <summary>設定のファイル名．</summary>
    public const string ConfigName = "shifts.json";

    /// <summary>Python 版の shl にあって，ネイティブ版にまだ入れていないコマンド（docs/native-gap.md §5 の順に減らす）．</summary>
    private static readonly string[] NotYet =
    [
        "okita", "neru", "itte", "yasumi", "deru",
        "sync", "cleanup", "kaijo", "app", "enforce",
        "learned", "learn", "unlearn", "install-hook", "report", "settings",
    ];

    /// <summary>day の操作のうち，まだ入れていないもの（docs/native-gap.md §5 の S3 で入れる）．</summary>
    private static readonly string[] DayNotYet = ["off", "work", "rm"];

    /// <summary>day list の日数（cli.py と同じ）．yotei は Snapshot.ScheduleDays（14 日）．</summary>
    private const int DayListDays = 28;

    /// <summary>ヘルプを出す語（pc/shiftlock-profile.ps1 の shl 関数と同じ）．</summary>
    private static readonly string[] HelpWords = ["help", "-h", "--help", "?", "/?"];

    /// <summary>shl.exe を動かす環境．テストで差し替える．</summary>
    /// <param name="Stdin">標準入力（hook が JSON を読む）．</param>
    /// <param name="Stdout">標準出力．</param>
    /// <param name="Stderr">標準エラー．</param>
    /// <param name="GetEnv">環境変数を引く．</param>
    /// <param name="HomeDir">ユーザーのホーム（%USERPROFILE%）．</param>
    /// <param name="Now">いまの時刻（ローカル）．</param>
    public sealed record Io(TextReader Stdin, TextWriter Stdout, TextWriter Stderr, Func<string, string?> GetEnv, string HomeDir,
        DateTime Now);

    /// <summary>引数を解釈して 1 つのコマンドを動かし，終了コードを返す．</summary>
    public static int Run(IReadOnlyList<string> args, Io io)
    {
        var parsed = Parse(args);
        var configPath = ResolveConfig(parsed.Config, io);

        if (parsed.Command == "hook")
        {
            // 中身は H1 で入れる（docs/native-gap.md §5）．それまでは何も読まず何も出さない．プロンプトは止めない
            return 0;
        }

        var m = Messages.Load(ReadLang(configPath));
        if (parsed.BadOption is not null)
        {
            io.Stderr.Write(m.Format("SHL_UNKNOWN", ("cmd", parsed.BadOption)) + "\n");
            return 1;
        }
        if (parsed.Command is null || Array.IndexOf(HelpWords, parsed.Command) >= 0)
        {
            io.Stdout.Write(m["CLI_HELP"] + "\n");
            return 0;
        }
        switch (parsed.Command)
        {
            case "status" or "yotei" or "kakunin":
                // 引数は取らない（余分な語を黙って捨てない）
                return parsed.Rest.Count > 0
                    ? Refuse(io, m, "SHL_UNKNOWN", parsed.Rest[0])
                    : View(parsed.Command, configPath, m, io);
            case "day":
                var action = parsed.Rest.Count > 0 ? parsed.Rest[0] : null;
                if (action == "list")
                {
                    return View("day list", configPath, m, io);
                }
                return Refuse(io, m, Array.IndexOf(DayNotYet, action) >= 0 ? "SHL_NOT_YET" : "SHL_UNKNOWN",
                    action is null ? "day" : "day " + action);
            default:
                return Refuse(io, m, Array.IndexOf(NotYet, parsed.Command) >= 0 ? "SHL_NOT_YET" : "SHL_UNKNOWN", parsed.Command);
        }
    }

    /// <summary>
    /// 見るだけのコマンド（status・yotei・day list・kakunin）．設定を読んで文を標準出力に出す．
    /// 設定が無い・壊れているときは理由を標準エラーに出して 1．
    /// status と kakunin は，未確認の扱いを始めた日が無ければ state.json に書く（Python 版と同じ）．
    /// </summary>
    private static int View(string command, string configPath, Messages m, Io io)
    {
        ShiftConfig cfg;
        try
        {
            cfg = ShiftConfig.Load(configPath);
        }
        catch (FileNotFoundException)
        {
            io.Stderr.Write(m.Format("APP_NO_CONFIG", ("path", configPath)) + "\n");
            return 1;
        }
        catch (ConfigException e)
        {
            io.Stderr.Write(e.Message + "\n");
            return 1;
        }
        var store = new Store(cfg.Dir);
        var today = DateOnly.FromDateTime(io.Now);
        var text = command switch
        {
            "status" => Commands.StatusText(cfg, store, cfg.Msg, io.Now),
            "yotei" => Commands.ScheduleText(cfg, cfg.Msg, today),
            "day list" => Commands.ScheduleText(cfg, cfg.Msg, today, DayListDays),
            _ => Confirm.KakuninText(cfg, store, cfg.Msg, io.Now),
        };
        io.Stdout.Write(text + "\n");
        return 0;
    }

    private static int Refuse(Io io, Messages m, string key, string shown)
    {
        io.Stderr.Write(m.Format(key, ("cmd", shown)) + "\n");
        return 1;
    }

    /// <summary>
    /// 設定（shifts.json）の場所．--config があればそれ（/mnt/c/... は Windows のパスに直す），
    /// 無ければ SHIFTLOCK_DIR の下，それも無ければホームの shift-lock の下．
    /// </summary>
    public static string ResolveConfig(string? option, Io io)
    {
        if (!string.IsNullOrWhiteSpace(option))
        {
            return LocalPath.ToLocal(option);
        }
        var dir = io.GetEnv(DirEnv);
        return Path.Combine(string.IsNullOrWhiteSpace(dir) ? Path.Combine(io.HomeDir, DefaultDirName) : dir, ConfigName);
    }

    /// <summary>
    /// shifts.json の lang だけを読む（config.py の apply_lang と同じ．設定の検証は通さない）．
    /// 無い・読めない・文字列でないときは null（呼ぶ側は既定の言語で出す）．
    /// </summary>
    public static string? ReadLang(string configPath)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllBytes(configPath)) is JsonObject raw ? JsonX.Str(raw["lang"]) : null;
        }
        catch (Exception)
        {
            // ヘルプは設定が壊れていても出す
            return null;
        }
    }

    private sealed record Parsed(string? Command, string? Config, string? BadOption, IReadOnlyList<string> Rest);

    /// <summary>
    /// --config &lt;パス&gt;／--config=&lt;パス&gt; をどこにあっても抜き出し，残りの先頭をコマンドにする．
    /// コマンドより前の知らないオプション・値の無い --config は BadOption に入れる．コマンドより後ろは各コマンドが解釈する．
    /// </summary>
    private static Parsed Parse(IReadOnlyList<string> args)
    {
        const string opt = "--config";
        string? config = null;
        string? bad = null;
        var rest = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == opt)
            {
                if (i + 1 < args.Count)
                {
                    config = args[++i];
                }
                else
                {
                    bad ??= a;
                }
            }
            else if (a.StartsWith(opt + "=", StringComparison.Ordinal))
            {
                config = a[(opt.Length + 1)..];
            }
            else
            {
                rest.Add(a);
            }
        }
        var command = rest.Count > 0 ? rest[0] : null;
        if (command is not null && command.StartsWith('-') && Array.IndexOf(HelpWords, command) < 0)
        {
            bad ??= command;
        }
        return new Parsed(command, config, bad, rest.Count > 1 ? rest.GetRange(1, rest.Count - 1) : []);
    }
}
