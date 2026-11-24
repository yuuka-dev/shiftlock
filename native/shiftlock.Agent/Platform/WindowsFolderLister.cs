namespace Shiftlock.Agent;

/// <summary>フォルダの一覧（幅優先）．リンク（再解析ポイント）はたどらない．根が読めなければ null，奥が読めなければそのフォルダだけ飛ばす．</summary>
public sealed class WindowsFolderLister : IFolderLister
{
    public IReadOnlyList<string>? Names(string dir, int depth, int maxEntries = 20000)
    {
        var names = new List<string>();
        try
        {
            if (!Directory.Exists(dir))
            {
                return null;
            }
            var level = new List<string> { dir };
            for (var d = 0; d <= Math.Max(0, depth) && level.Count > 0; d++)
            {
                var next = new List<string>();
                foreach (var folder in level)
                {
                    List<string> entries;
                    try
                    {
                        entries = Directory.EnumerateFileSystemEntries(folder).ToList();
                    }
                    catch (Exception e) when (e is UnauthorizedAccessException or IOException)
                    {
                        if (d == 0)
                        {
                            return null;   // 根が読めない
                        }
                        continue;          // 奥は飛ばす
                    }
                    foreach (var entry in entries)
                    {
                        if (names.Count >= maxEntries)
                        {
                            return names;
                        }
                        names.Add(Path.GetFileName(entry));
                        if (d < depth && IsRealDirectory(entry))
                        {
                            next.Add(entry);
                        }
                    }
                }
                level = next;
            }
            return names;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsRealDirectory(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            return attr.HasFlag(FileAttributes.Directory) && !attr.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
