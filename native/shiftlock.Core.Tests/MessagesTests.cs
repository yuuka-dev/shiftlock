using System.Globalization;

namespace Shiftlock.Core.Tests;

/// <summary>文面の読み込み・プレースホルダの置き換えと，時刻の整形．</summary>
public class MessagesTests
{
    [Theory]
    [InlineData("ja", "ja")]
    [InlineData("ja_kansai", "ja_kansai")]
    [InlineData("inm", "ja")]
    [InlineData(null, "ja")]
    [InlineData("xx", "ja")]
    public void ResolveLang(string? input, string expected)
    {
        Assert.Equal(expected, Messages.ResolveLang(input));
    }

    [Fact]
    public void Load_ReadsBothLanguages()
    {
        var ja = Messages.Load("ja");
        Assert.Equal("ja", ja.Lang);
        Assert.Equal("帰ってきた", ja["APP_BTN_KITAKU"]);
        Assert.StartsWith("[shiftlock] ", ja["RECORDS_KITAKU"]);

        var kansai = Messages.Load("ja_kansai");
        Assert.Equal("ja_kansai", kansai.Lang);
        Assert.Equal("帰ってきた", kansai["APP_BTN_KITAKU"]);
        Assert.NotEqual(ja["APP_NO_SHIFT"], kansai["APP_NO_SHIFT"]);

        // 知らない言語は標準語
        Assert.Equal("ja", Messages.Load("inm").Lang);
        Assert.Equal("ja", Messages.Load(null).Lang);
    }

    [Fact]
    public void Map_ReturnsNestedTable()
    {
        var m = Messages.Load("ja");
        var phase = m.Map("APP_PHASE");
        Assert.Equal(new[] { "free", "go", "go_last", "hard", "lastcall", "soft" }, phase.Keys.Order());
        Assert.Equal("いまは自由時間", phase["free"]);
    }

    [Fact]
    public void MissingKey_Throws()
    {
        var m = Messages.Load("ja");
        Assert.Throws<KeyNotFoundException>(() => m["NO_SUCH_KEY"]);
        Assert.Throws<KeyNotFoundException>(() => m.Map("NO_SUCH_KEY"));
        // 型の違うキー（表を文として，文を表として引く）も同じ扱い
        Assert.Throws<KeyNotFoundException>(() => m["APP_PHASE"]);
        Assert.Throws<KeyNotFoundException>(() => m.Map("APP_BTN_KITAKU"));
    }

    [Fact]
    public void Format_FillsNamedPlaceholders()
    {
        Assert.Equal("1とx", Messages.Fill("{a}と{b}", ("a", 1), ("b", "x")));
        Assert.Equal("次の乗務  12/01(火)", Messages.Load("ja").Format("APP_NEXT_SHIFT", ("day", "12/01(火)")));
        // 同じ名前が 2 回出てもよい．使わない引数は無視する（Python の str.format と同じ）
        Assert.Equal("x-x", Messages.Fill("{a}-{a}", ("a", "x"), ("b", "y")));
        Assert.Equal("", Messages.Fill(""));
    }

    [Fact]
    public void Fill_UnescapesDoubledBraces()
    {
        Assert.Equal("usage は {...}", Messages.Fill("{key} は {{...}}", ("key", "usage")));
        Assert.Equal("usage は {...} の形で書いてね（settings.cmd で直せる）",
            Messages.Load("ja").Format("CONFIG_NOT_DICT", ("key", "usage")));
    }

    [Fact]
    public void Fill_ValueWithBracesIsNotReinterpreted()
    {
        // 埋めた値の中の波かっこは置き換えの対象にしない
        Assert.Equal("{b}", Messages.Fill("{a}", ("a", "{b}"), ("b", "x")));
    }

    [Fact]
    public void Fill_MissingArgumentThrows()
    {
        Assert.Throws<ArgumentException>(() => Messages.Fill("{a}と{b}", ("a", 1)));
    }

    [Theory]
    [InlineData("{a")]
    [InlineData("a}")]
    [InlineData("{}")]
    public void Fill_BrokenTemplateThrows(string template)
    {
        Assert.Throws<FormatException>(() => Messages.Fill(template, ("a", 1)));
    }

    [Fact]
    public void Fill_NumbersDoNotFollowCurrentCulture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fi-FI");   // 小数点が「,」の文化
            Assert.Equal("2.5", Messages.Fill("{h}", ("h", 2.5)));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void FmtDur()
    {
        var m = Messages.Load("ja");
        Assert.Equal("30分", TimeUtil.FmtDur(TimeSpan.FromMinutes(30), m));
        Assert.Equal("2時間30分", TimeUtil.FmtDur(TimeSpan.FromMinutes(150), m));
        Assert.Equal("59分", TimeUtil.FmtDur(new TimeSpan(0, 59, 59), m));
        Assert.Equal("1時間0分", TimeUtil.FmtDur(TimeSpan.FromMinutes(60), m));
        Assert.Equal("0分", TimeUtil.FmtDur(TimeSpan.Zero, m));
        // 24 時間を超えても時間で数える（日に繰り上げない）
        Assert.Equal("25時間5分", TimeUtil.FmtDur(new TimeSpan(1, 1, 5, 0), m));
    }

    [Fact]
    public void Fmt_IsZeroPadded()
    {
        var t = new DateTime(2026, 12, 1, 7, 5, 0);
        Assert.Equal("07:05", TimeUtil.Fmt(t));
        Assert.Equal("12/01 07:05", TimeUtil.FmtD(t));
        Assert.Equal("2026-12-01T07:05", TimeUtil.Iso(t));
        Assert.Equal("2026-12-01T07:05", TimeUtil.Iso(t.AddSeconds(59)));
    }

    [Fact]
    public void Fmt_DoesNotFollowCurrentCulture()
    {
        // 区切りや暦が文化の設定で変わらない（和暦や「.」区切りの環境でも同じ文字列）
        var before = CultureInfo.CurrentCulture;
        try
        {
            var t = new DateTime(2026, 12, 1, 7, 5, 0);
            var jp = new CultureInfo("ja-JP");
            jp.DateTimeFormat.Calendar = new JapaneseCalendar();
            CultureInfo.CurrentCulture = jp;
            Assert.Equal("2026-12-01T07:05", TimeUtil.Iso(t));
            Assert.Equal("12/01 07:05", TimeUtil.FmtD(t));
            CultureInfo.CurrentCulture = new CultureInfo("fi-FI");
            Assert.Equal("07:05", TimeUtil.Fmt(t));
            Assert.Equal("12/01 07:05", TimeUtil.FmtD(t));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData("8:00", 8, 0)]
    [InlineData("08:00", 8, 0)]
    [InlineData("17:30", 17, 30)]
    [InlineData(" 7 : 05 ", 7, 5)]
    public void At_AcceptsLooseTime(string hm, int h, int min)
    {
        Assert.Equal(new DateTime(2026, 12, 1, h, min, 0), TimeUtil.At(new DateOnly(2026, 12, 1), hm));
    }

    [Theory]
    [InlineData("")]
    [InlineData("8")]
    [InlineData("8:00:00")]
    [InlineData("24:00")]
    [InlineData("8:60")]
    [InlineData("a:b")]
    public void At_BadTimeThrows(string hm)
    {
        Assert.Throws<FormatException>(() => TimeUtil.At(new DateOnly(2026, 12, 1), hm));
    }

    [Fact]
    public void WdJa_StartsOnMonday()
    {
        Assert.Equal(7, TimeUtil.WdJa.Length);
        Assert.Equal('月', TimeUtil.WdJa[0]);
        Assert.Equal('日', TimeUtil.WdJa[6]);
    }
}
