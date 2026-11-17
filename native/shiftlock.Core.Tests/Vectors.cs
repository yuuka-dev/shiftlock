using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Shiftlock.Core.Tests;

/// <summary>テストベクタの 1 ケース（docs/test-vectors/*.json．pc/tools/export_native.py が Python 版から書き出す）．</summary>
public sealed record VectorCase(string Name, JsonObject Files, JsonObject Input, JsonObject Expect)
{
    // テストの一覧にケースの名前を出す
    public override string ToString() => Name;
}

/// <summary>テストベクタの読み込みと，一時フォルダへの展開．</summary>
public static class Vectors
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>file（"plan.json" など）の節 section の全ケース．xUnit の MemberData 用．</summary>
    public static IEnumerable<object[]> Cases(string file, string section)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vectors", file);
        var root = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        var cases = root[section]?.AsArray() ?? throw new InvalidOperationException($"{file} に節 {section} が無い");
        foreach (var c in cases)
        {
            var o = c!.AsObject();
            yield return
            [
                new VectorCase(o["name"]!.GetValue<string>(), o["files"]!.AsObject(), o["input"]!.AsObject(), o["expect"]!.AsObject()),
            ];
        }
    }

    /// <summary>
    /// 一時フォルダを作り，files（名前 → 中身）を書いて返す．中身が true なら空のファイル，文字列ならそのままの中身，
    /// それ以外は JSON．使い終わったら Dispose でフォルダごと消す．
    /// </summary>
    public static TempFolder Materialize(JsonObject files)
    {
        var folder = new TempFolder();
        foreach (var (name, body) in files)
        {
            var path = folder.P(name);
            if (body is JsonValue v && v.TryGetValue<bool>(out _))
            {
                File.WriteAllBytes(path, []);
            }
            else if (body is JsonValue s && s.TryGetValue<string>(out var text))
            {
                File.WriteAllText(path, text, Utf8NoBom);
            }
            else
            {
                File.WriteAllText(path, body?.ToJsonString() ?? "null", Utf8NoBom);
            }
        }
        return folder;
    }

    /// <summary>ベクタの時刻（yyyy-MM-ddTHH:mm）を読む．</summary>
    public static DateTime Time(JsonNode? node) =>
        DateTime.ParseExact(node!.GetValue<string>(), "yyyy'-'MM'-'dd'T'HH':'mm", CultureInfo.InvariantCulture);

    /// <summary>ベクタの日付（yyyy-MM-dd）を読む．</summary>
    public static DateOnly Day(JsonNode? node) =>
        DateOnly.ParseExact(node!.GetValue<string>(), "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture);
}

/// <summary>テスト用の一時フォルダ（実行フォルダの代わり）．</summary>
public sealed class TempFolder : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("shiftlock-test-").FullName;

    public string P(string name) => Path.Combine(Dir, name);

    public string ShiftsJson => P("shifts.json");

    public void Dispose() => Directory.Delete(Dir, recursive: true);
}
