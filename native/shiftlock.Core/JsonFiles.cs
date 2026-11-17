using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>
/// 実行フォルダの JSON の読み書き（pc/shiftlock/store.py の _read / _write と同じ形）．
/// 書く形: UTF-8（BOM なし）・LF・インデント 1・日本語はエスケープしない・末尾に改行なし．
/// 知らないキーとキーの順は読んだまま残る．
/// </summary>
public static class JsonFiles
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentCharacter = ' ',
        IndentSize = 1,
        NewLine = "\n",
        // Python の ensure_ascii=False に合わせる（日本語や &lt; &gt; &amp; ' + を \uXXXX にしない）
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>無い・壊れている・オブジェクトでない・読めない → 空（Python の _read(path, {})）．</summary>
    public static JsonObject ReadObjectOrEmpty(string path)
    {
        TryReadObject(path, out var obj);
        return obj;
    }

    /// <summary>
    /// 無いファイルは true で空．在るのに読めない（壊れている・オブジェクトでない・他のプロセスが掴んでいる）は false で空．
    /// 書き戻す前に読むときはこちらを使う（読めなかったものを空として書き戻すと中身が消える）．
    /// </summary>
    public static bool TryReadObject(string path, out JsonObject obj)
    {
        obj = new JsonObject();
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(bytes) is not JsonObject parsed)
            {
                return false;
            }
            Touch(parsed);
            obj = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // 同じキーが 2 回あるオブジェクト（JsonObject は中身に触った時点で投げる）
            return false;
        }
    }

    /// <summary>node を path に書く．&lt;名前&gt;.tmp に書いてから置き換える．失敗したら .tmp を消して IOException を投げる．</summary>
    public static void Write(string path, JsonNode node)
    {
        var tmp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, WriterOptions))
            {
                node.WriteTo(writer);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(tmp);
            // 置き換え先を他のプロセスが開いているとき，Windows は「アクセス拒否」を返すことがある．呼ぶ側が 1 種類で受けられるようにする
            throw e as IOException ?? new IOException(e.Message, e);
        }
    }

    /// <summary>木を全部たどって，遅延で作られる中身を先に作らせる（壊れたところをここで見つける）．</summary>
    internal static void Touch(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (_, child) in o)
                {
                    Touch(child);
                }
                break;
            case JsonArray a:
                foreach (var child in a)
                {
                    Touch(child);
                }
                break;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
