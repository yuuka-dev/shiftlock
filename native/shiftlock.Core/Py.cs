using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>設定の値を Python の float() / int() と同じ読み方で数にする（Python 版と同じ値・同じ「読めない」になるように）．</summary>
internal static class Py
{
    /// <summary>float(v): JSON の数・真偽値・数字の文字列（前後の空白は可）．読めなければ null．</summary>
    public static double? Float(JsonNode? v) => v?.GetValueKind() switch
    {
        JsonValueKind.Number => v.GetValue<double>(),
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        JsonValueKind.String when double.TryParse(v.GetValue<string>().Trim(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    /// <summary>int(v): 数は 0 に向けて切り捨て，文字列は整数の表記だけ．読めなければ null．</summary>
    public static int? Int(JsonNode? v)
    {
        switch (v?.GetValueKind())
        {
            case JsonValueKind.True:
                return 1;
            case JsonValueKind.False:
                return 0;
            case JsonValueKind.Number:
                return Whole(v.GetValue<double>());
            case JsonValueKind.String:
                return int.TryParse(v.GetValue<string>().Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
                    ? n
                    : null;
            default:
                return null;
        }
    }

    /// <summary>int(float(v)): 数字の文字列も小数も受けて切り捨てる．読めなければ null．</summary>
    public static int? IntOfFloat(JsonNode? v) => Float(v) is { } d ? Whole(d) : null;

    private static int? Whole(double d)
    {
        var t = Math.Truncate(d);
        return double.IsFinite(t) && Math.Abs(t) <= int.MaxValue ? (int)t : null;
    }
}
