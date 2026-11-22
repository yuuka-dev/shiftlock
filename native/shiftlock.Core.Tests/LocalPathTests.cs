namespace Shiftlock.Core.Tests;

/// <summary>設定に書かれた WSL のパス（/mnt/c/...）を Windows のパスとして読む（config.to_local_path の逆向き）．</summary>
public sealed class LocalPathTests
{
    [Theory]
    [InlineData("/mnt/c/Users/x/a.csv", @"C:\Users\x\a.csv")]
    [InlineData("/mnt/d/music/hard sound.mp3", @"D:\music\hard sound.mp3")]
    [InlineData("/mnt/C/a", @"C:\a")]
    [InlineData("/mnt/c/", @"C:\")]
    [InlineData("/mnt/c", @"C:\")]
    [InlineData(@"C:\Users\x\a.csv", @"C:\Users\x\a.csv")]
    [InlineData("C:/Users/x/a.csv", "C:/Users/x/a.csv")]
    [InlineData("/mnt/cc/a", "/mnt/cc/a")]
    [InlineData("/mnt/1/a", "/mnt/1/a")]
    [InlineData("/home/x/a.csv", "/home/x/a.csv")]
    [InlineData("mood.csv", "mood.csv")]
    [InlineData("", "")]
    public void ToLocal(string input, string expected)
    {
        Assert.Equal(expected, LocalPath.ToLocal(input));
    }
}
