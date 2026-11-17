using System.Globalization;

namespace Shiftlock.Core;

/// <summary>時刻の整形。Python 版 timeutil.py に対応。時刻は全部ローカル時刻（Kind は見ない）。</summary>
public static class TimeUtil
{
    /// <summary>日付と "HH:MM" から日時を作る（Python: at）。</summary>
    public static DateTime At(DateOnly d, string hm)
    {
        var parts = hm.Split(':');
        if (parts.Length != 2)
            throw new FormatException($"HH:MM ではない: {hm}");
        var h = int.Parse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture);
        var m = int.Parse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture);
        return new DateTime(d.Year, d.Month, d.Day, h, m, 0, DateTimeKind.Unspecified);
    }

    /// <summary>"HH:MM"（Python: fmt）。</summary>
    public static string Fmt(DateTime dt) => dt.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"MM/DD HH:MM"（Python: fmt_d）。</summary>
    public static string FmtD(DateTime dt) => dt.ToString("MM'/'dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"YYYY-MM-DD"（ファイルに書く日付の形）。</summary>
    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>曜日の短い名前（mon〜sun）。設定ファイルに書く値。</summary>
    public static string Weekday(DateOnly d) => Weekdays[((int)d.DayOfWeek + 6) % 7];

    public static readonly IReadOnlyList<string> Weekdays = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];
}
