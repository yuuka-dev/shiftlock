namespace Shiftlock.Agent;

public interface IFolderLister
{
    /// <summary>dir の下を depth 階層まで見て，ファイルとフォルダの名前（パスでなく名前だけ）を返す．読めなければ null．maxEntries を超えたら打ち切って，それまでの分を返す．</summary>
    IReadOnlyList<string>? Names(string dir, int depth, int maxEntries = 20000);
}
