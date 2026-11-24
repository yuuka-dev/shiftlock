using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Shiftlock.Core;

/// <summary>
/// ユーザー向けの文面．Python の言語ファイル（pc/shiftlock/i18n/）から書き出した JSON
/// （native/shared/messages.&lt;lang&gt;.json．pc/tools/export_native.py が作る）を埋め込みリソースから読む．
/// 文は C# に直書きしない．
/// </summary>
public sealed class Messages
{
    /// <summary>lang が無い・知らない値のときの言語．ネイティブ版は配る言語だけを持つ（任意の言語 inm は標準語で出す）．</summary>
    private const string FallbackLang = "ja";

    private static readonly string[] Langs = ["ja", "ja_kansai"];

    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _maps = new(StringComparer.Ordinal);

    private Messages(string lang)
    {
        Lang = lang;
    }

    /// <summary>読み込んだ言語（"ja" か "ja_kansai"）．</summary>
    public string Lang { get; }

    /// <summary>設定の lang を，持っている言語の名前にする．それ以外・null は "ja"．</summary>
    public static string ResolveLang(string? lang) =>
        lang is not null && Array.IndexOf(Langs, lang) >= 0 ? lang : FallbackLang;

    /// <summary>埋め込みの JSON を読む．知らない言語は "ja" を読む．</summary>
    public static Messages Load(string? lang)
    {
        var name = ResolveLang(lang);
        var resource = $"messages.{name}.json";
        using var stream = typeof(Messages).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"埋め込みリソース {resource} が無い");
        using var doc = JsonDocument.Parse(stream);
        var m = new Messages(name);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                m._texts[prop.Name] = prop.Value.GetString()!;
            }
            else if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var sub in prop.Value.EnumerateObject())
                {
                    map[sub.Name] = sub.Value.GetString()
                        ?? throw new InvalidOperationException($"{resource}: {prop.Name}.{sub.Name} が文字列ではない");
                }
                m._maps[prop.Name] = map;
            }
            else
            {
                throw new InvalidOperationException($"{resource}: {prop.Name} が文字列でも表でもない");
            }
        }
        return m;
    }

    /// <summary>文だけの Messages を作る（テストで文のキーを合成するため．配る文面は Load が読む）．</summary>
    public static Messages FromTexts(string lang, IReadOnlyDictionary<string, string> texts)
    {
        var m = new Messages(ResolveLang(lang));
        foreach (var (key, text) in texts)
        {
            m._texts[key] = text;
        }
        return m;
    }

    /// <summary>文を引く．無いキー（表のキーを含む）は KeyNotFoundException．</summary>
    public string this[string key] =>
        _texts.TryGetValue(key, out var text) ? text : throw new KeyNotFoundException($"文面 {key} が無い");

    /// <summary>表（Python 側で辞書の文面）を引く．無いキー（文のキーを含む）は KeyNotFoundException．</summary>
    public IReadOnlyDictionary<string, string> Map(string key) =>
        _maps.TryGetValue(key, out var map) ? map : throw new KeyNotFoundException($"文面の表 {key} が無い");

    /// <summary>文を引いて {name} を埋める．</summary>
    public string Format(string key, params (string Name, object? Value)[] args) => Fill(this[key], args);

    /// <summary>
    /// テンプレートの {name} を args の値で埋める（Python の str.format のうち，名前付きの置き換えだけ）．
    /// {{ と }} は波かっこそのもの．テンプレートに在って args に無い名前は ArgumentException，
    /// 閉じていない波かっこや空の {} は FormatException．使わない引数は無視する．
    /// 数は文化に依らない表記で入れる．Python と表記が変わる値（5.0 など）は呼ぶ側で文字列にして渡す．
    /// </summary>
    public static string Fill(string template, params (string Name, object? Value)[] args)
    {
        var sb = new StringBuilder(template.Length + 16);
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    sb.Append('{');
                    i += 2;
                    continue;
                }
                var end = template.IndexOf('}', i + 1);
                if (end < 0)
                {
                    throw new FormatException($"波かっこが閉じていない: {template}");
                }
                var name = template[(i + 1)..end];
                if (name.Length == 0 || name.Contains('{'))
                {
                    throw new FormatException($"プレースホルダの名前が変: {template}");
                }
                sb.Append(Text(Lookup(name, args, template)));
                i = end + 1;
            }
            else if (c == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}')
                {
                    sb.Append('}');
                    i += 2;
                    continue;
                }
                throw new FormatException($"波かっこが開いていない: {template}");
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }

    private static object? Lookup(string name, (string Name, object? Value)[] args, string template)
    {
        foreach (var (argName, value) in args)
        {
            if (argName == name)
            {
                return value;
            }
        }
        throw new ArgumentException($"{{{name}}} に入れる値が無い: {template}", nameof(args));
    }

    private static string Text(object? value) => value switch
    {
        null => "",
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
