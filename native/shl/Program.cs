// shl: Claude Code hook・コマンド・ダブルクリック用の CLI．
// ここは文字コードと終了コードの受け皿だけ．引数の解釈と振り分けは Shiftlock.Core.ShlCli に置く．
//
// 終了コード: hook（UserPromptSubmit）から呼ばれたとき 2 を返すとプロンプトが消える．
// 2 を返してよいのは ShlCli が hook の決まった場面で返したときだけで，例外は必ず 1 にする．
using System.Text;
using Shiftlock.Core;

var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Encoding? oldOut = null;
var code = 1;
TextWriter? stdout = null;
TextWriter? stderr = null;
try
{
    // 出力は常に UTF-8（BOM なし）．行末は LF のまま出す．
    // パイプ・ファイルにはバイト列をそのまま書く．端末には，コードページを UTF-8 にして書き，終わったら戻す
    // （戻さないと，呼んだ端末のコードページが変わったままになる）．
    if (!Console.IsOutputRedirected || !Console.IsErrorRedirected)
    {
        try
        {
            var before = Console.OutputEncoding;
            Console.OutputEncoding = utf8;
            oldOut = before;
        }
        catch (Exception)
        {
            // コードページを変えられない端末．文字が化けても，コマンドは止めない
        }
    }
    stdout = Writer(Console.IsOutputRedirected, Console.OpenStandardOutput, Console.Out);
    stderr = Writer(Console.IsErrorRedirected, Console.OpenStandardError, Console.Error);
    // 標準入力は使うコマンド（hook）が読むときに初めて開く
    using var stdin = new LazyStdin(utf8);
    var io = new ShlCli.Io(stdin, stdout, stderr, Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    code = ShlCli.Run(args, io);
}
catch (Exception e)
{
    // ここに来るのは想定していない失敗．文面（JSON）が読めないこともあるので，例外の文だけを出す
    try
    {
        (stderr ?? Console.Error).Write("[shiftlock] " + e.Message + "\n");
    }
    catch (Exception)
    {
        // 標準エラーにも書けないときは終了コードだけで知らせる
    }
    code = 1;
}
finally
{
    // 1 つが失敗しても残りは行う（出力先が先に閉じられた＝パイプの相手が終わった，など）．終了コードは変えない
    Quietly(() => stdout?.Flush());
    Quietly(() => stderr?.Flush());
    if (oldOut is not null)
    {
        Quietly(() => Console.OutputEncoding = oldOut);
    }
}
return code;

static void Quietly(Action action)
{
    try
    {
        action();
    }
    catch (Exception)
    {
        // 片付けの失敗は握りつぶす
    }
}

TextWriter Writer(bool redirected, Func<Stream> open, TextWriter console)
{
    if (redirected)
    {
        return new StreamWriter(open(), utf8) { AutoFlush = false, NewLine = "\n" };
    }
    console.NewLine = "\n";
    return console;
}

/// <summary>標準入力を，最初に読まれたときに UTF-8 で開く（読まないコマンドでは開かない）．</summary>
internal sealed class LazyStdin(Encoding encoding) : TextReader
{
    private StreamReader? _inner;

    private StreamReader Inner => _inner ??= new StreamReader(Console.OpenStandardInput(), encoding);

    public override int Peek() => Inner.Peek();

    public override int Read() => Inner.Read();

    public override int Read(char[] buffer, int index, int count) => Inner.Read(buffer, index, count);

    public override string? ReadLine() => Inner.ReadLine();

    public override string ReadToEnd() => Inner.ReadToEnd();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner?.Dispose();
        }
        base.Dispose(disposing);
    }
}
