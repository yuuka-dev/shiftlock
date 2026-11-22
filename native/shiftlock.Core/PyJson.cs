using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>
/// Python の json.dumps(obj, ensure_ascii=False) と同じ 1 行の表記（区切りは ", " と ": "）．
/// log/*.jsonl の行を Python 版と同じ字面で書くために使う（レポートや突き合わせが行で比べる）．
/// </summary>
internal static class PyJson
{
    public static string Dumps(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(sb, node);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject o:
                sb.Append('{');
                var firstKey = true;
                foreach (var (k, v) in o)
                {
                    if (!firstKey)
                    {
                        sb.Append(", ");
                    }
                    firstKey = false;
                    Quote(sb, k);
                    sb.Append(": ");
                    Write(sb, v);
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }
                    Write(sb, a[i]);
                }
                sb.Append(']');
                break;
            default:
                switch (node.GetValueKind())
                {
                    case JsonValueKind.String:
                        Quote(sb, node.GetValue<string>());
                        break;
                    case JsonValueKind.True:
                        sb.Append("true");
                        break;
                    case JsonValueKind.False:
                        sb.Append("false");
                        break;
                    case JsonValueKind.Number:
                        sb.Append(node.ToJsonString());
                        break;
                    default:
                        sb.Append("null");
                        break;
                }
                break;
        }
    }

    /// <summary>Python と同じエスケープ: " と \ と制御文字だけ．日本語や絵文字はそのまま．</summary>
    private static void Quote(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append(@"\\"); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                case '\t': sb.Append(@"\t"); break;
                case '\b': sb.Append(@"\b"); break;
                case '\f': sb.Append(@"\f"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append(@"\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }
}
