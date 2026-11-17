using System.Text;
using System.Text.Json.Nodes;

namespace Shiftlock.Core.Tests;

/// <summary>JSON の読み書きが Python 版（store._read / store._write）と同じ形になること．</summary>
public sealed class JsonFilesTests : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly string _dir = Directory.CreateTempSubdirectory("shiftlock-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    private string[] Files() => Directory.GetFiles(_dir).Select(p => Path.GetFileName(p)).ToArray();

    private const string Sample =
        """{"shifts":{"2026-12-01":{"status":"okita","at":"2026-12-01T14:30"}},"メモ":"あ","n":5.0,"e":{},"l":[]}""";

    // Python の json.dump(indent=1, ensure_ascii=False) が書く形
    private const string SamplePython =
        "{\n \"shifts\": {\n  \"2026-12-01\": {\n   \"status\": \"okita\",\n   \"at\": \"2026-12-01T14:30\"\n  }\n },\n \"メモ\": \"あ\",\n \"n\": 5.0,\n \"e\": {},\n \"l\": []\n}";

    [Fact]
    public void Write_MatchesPythonFormat()
    {
        var path = P("state.json");
        JsonFiles.Write(path, JsonNode.Parse(Sample)!);
        // 末尾に改行なし．BOM なし
        Assert.Equal(Utf8NoBom.GetBytes(SamplePython), File.ReadAllBytes(path));
    }

    [Fact]
    public void Write_NestedArrayAndEscapesMatchPythonFormat()
    {
        var path = P("a.json");
        JsonFiles.Write(path, JsonNode.Parse("""{"l":[1,"a",{"k":null},[true,false]],"q":"\"\\/<>&'+","t":"a\tb\nc"}""")!);
        var expected = "{\n \"l\": [\n  1,\n  \"a\",\n  {\n   \"k\": null\n  },\n  [\n   true,\n   false\n  ]\n ],\n"
            + " \"q\": \"\\\"\\\\/<>&'+\",\n \"t\": \"a\\tb\\nc\"\n}";
        Assert.Equal(expected, File.ReadAllText(path, Utf8NoBom));
    }

    [Fact]
    public void Write_KeepsUnknownKeysAndOrder()
    {
        var path = P("state.json");
        File.WriteAllText(path, SamplePython, Utf8NoBom);
        Assert.True(JsonFiles.TryReadObject(path, out var obj));
        obj["shifts"]!["2026-12-01"]!["status"] = "worked";
        JsonFiles.Write(path, obj);
        Assert.Equal(SamplePython.Replace("\"okita\"", "\"worked\""), File.ReadAllText(path, Utf8NoBom));
    }

    [Fact]
    public void Write_ReplacesExistingFile()
    {
        var path = P("state.json");
        File.WriteAllText(path, """{"old": 1, "long": "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"}""");
        JsonFiles.Write(path, JsonNode.Parse("""{"a":1}""")!);
        Assert.Equal("{\n \"a\": 1\n}", File.ReadAllText(path));
    }

    [Fact]
    public void Write_LeavesNoTmpFile()
    {
        var path = P("state.json");
        JsonFiles.Write(path, JsonNode.Parse(Sample)!);
        JsonFiles.Write(path, JsonNode.Parse(Sample)!);
        Assert.Equal(new[] { "state.json" }, Files());
    }

    [Fact]
    public void Write_Failure_LeavesNoTmpFileAndKeepsOld()
    {
        var path = P("state.json");
        File.WriteAllText(path, "{}");
        // ほかのプロセスが掴んでいる状態（共有なしで開く）→ 置き換えられない
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => JsonFiles.Write(path, JsonNode.Parse(Sample)!));
        }
        Assert.Equal(new[] { "state.json" }, Files());
        Assert.Equal("{}", File.ReadAllText(path));
    }

    [Fact]
    public void ReadObjectOrEmpty_MissingOrBroken_ReturnsEmpty()
    {
        Assert.Empty(JsonFiles.ReadObjectOrEmpty(P("none.json")));
        File.WriteAllText(P("broken.json"), "{");
        Assert.Empty(JsonFiles.ReadObjectOrEmpty(P("broken.json")));
        File.WriteAllText(P("empty.json"), "");
        Assert.Empty(JsonFiles.ReadObjectOrEmpty(P("empty.json")));
        File.WriteAllText(P("list.json"), "[1, 2]");
        Assert.Empty(JsonFiles.ReadObjectOrEmpty(P("list.json")));
        File.WriteAllText(P("ok.json"), """{"a": "あ"}""", Utf8NoBom);
        Assert.Equal("あ", (string?)JsonFiles.ReadObjectOrEmpty(P("ok.json"))["a"]);
    }

    [Fact]
    public void TryReadObject_BrokenFile_ReturnsFalse()
    {
        Assert.True(JsonFiles.TryReadObject(P("none.json"), out var none));
        Assert.Empty(none);

        File.WriteAllText(P("broken.json"), "{");
        Assert.False(JsonFiles.TryReadObject(P("broken.json"), out var broken));
        Assert.Empty(broken);

        // オブジェクトでない・空のファイルも「在るのに読めない」
        File.WriteAllText(P("list.json"), "[]");
        Assert.False(JsonFiles.TryReadObject(P("list.json"), out _));
        File.WriteAllText(P("empty.json"), "");
        Assert.False(JsonFiles.TryReadObject(P("empty.json"), out _));

        File.WriteAllText(P("ok.json"), """{"a": 1}""");
        Assert.True(JsonFiles.TryReadObject(P("ok.json"), out var ok));
        Assert.Equal(1, (int)ok["a"]!);
    }

    [Fact]
    public void TryReadObject_DuplicateKeys_ReturnsFalse()
    {
        // 同じキーが 2 回あるファイルは，あとで触ったときに例外になる．読む時点で「読めない」にする
        File.WriteAllText(P("dup.json"), """{"shifts": {"a": 1, "a": 2}}""");
        Assert.False(JsonFiles.TryReadObject(P("dup.json"), out _));
        Assert.Empty(JsonFiles.ReadObjectOrEmpty(P("dup.json")));
    }

    [Fact]
    public void TryReadObject_LockedFile_ReturnsFalse()
    {
        var path = P("state.json");
        File.WriteAllText(path, """{"a": 1}""");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(JsonFiles.TryReadObject(path, out _));
            Assert.Empty(JsonFiles.ReadObjectOrEmpty(path));
        }
    }
}
