using System.Globalization;

namespace Shiftlock.Core;

/// <summary>
/// 時刻の整形（pc/shiftlock/timeutil.py の at / fmt / fmt_d / fmt_dur / WD_JA）．
/// 時刻は全部ローカル時刻で，タイムゾーンを付けない．表記は文化の設定に依らない．
/// </summary>
public static class TimeUtil
{
    /// <summary>曜日の 1 文字．月曜が 0（Python の date.weekday() と同じ並び）．</summary>
    public const string WdJa = "月火水木金土日";

    /// <summary>日付と "H:mm" の時刻から日時を作る．"8:00" も "08:00" も受ける．読めなければ FormatException．</summary>
    public static DateTime At(DateOnly d, string hm)
    {
        var parts = hm.Split(':');
        const NumberStyles style = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;
        if (parts.Length != 2
            || !int.TryParse(parts[0], style, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(parts[1], style, CultureInfo.InvariantCulture, out var m)
            || h is < 0 or > 23 || m is < 0 or > 59)
        {
            throw new FormatException($"時刻として読めない: {hm}");
        }
        return new DateTime(d.Year, d.Month, d.Day, h, m, 0);
    }

    /// <summary>"HH:mm"（0 埋め．Python の %H:%M）．</summary>
    public static string Fmt(DateTime t) => t.ToString("HH':'mm", CultureInfo.InvariantCulture);

    /// <summary>"MM/dd HH:mm"（Python の %m/%d %H:%M）．</summary>
    public static string FmtD(DateTime t) => t.ToString("MM'/'dd' 'HH':'mm", CultureInfo.InvariantCulture);

    /// <summary>「2時間30分」「30分」．60 分以上は TIME_DUR_HM，未満は TIME_DUR_M．分は切り捨て．</summary>
    public static string FmtDur(TimeSpan d, Messages m)
    {
        var minutes = (long)Math.Floor(d.TotalSeconds / 60);
        return minutes >= 60
            ? m.Format("TIME_DUR_HM", ("h", minutes / 60), ("m", minutes % 60))
            : m.Format("TIME_DUR_M", ("m", minutes));
    }

    /// <summary>ファイルに書く時刻．分まで（"yyyy-MM-ddTHH:mm"）．</summary>
    public static string Iso(DateTime t) => t.ToString("yyyy'-'MM'-'dd'T'HH':'mm", CultureInfo.InvariantCulture);

    /// <summary>"MM/dd"（Python の %m/%d）．</summary>
    public static string FmtMd(DateTime t) => t.ToString("MM'/'dd", CultureInfo.InvariantCulture);

    private static readonly string[] IsoFormats =
    [
        "yyyy'-'MM'-'dd'T'HH':'mm", "yyyy'-'MM'-'dd'T'HH':'mm':'ss", "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'FFFFFFF",
        "yyyy'-'MM'-'dd' 'HH':'mm", "yyyy'-'MM'-'dd' 'HH':'mm':'ss", "yyyy'-'MM'-'dd' 'HH':'mm':'ss'.'FFFFFFF",
        "yyyy'-'MM'-'dd",
    ];

    /// <summary>
    /// ファイルに書いてある時刻を読む（Python の datetime.fromisoformat が読む形のうち，タイムゾーンなしのもの）．
    /// 分まで・秒まで・小数つき・日付だけを受ける．読めなければ null．
    /// </summary>
    public static DateTime? ParseIso(string? s) =>
        s is not null && DateTime.TryParseExact(s, IsoFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t
            : null;
}
