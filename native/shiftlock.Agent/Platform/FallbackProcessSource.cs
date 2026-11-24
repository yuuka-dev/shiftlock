namespace Shiftlock.Agent;

/// <summary>primary が失敗したら secondary で取る．次の回はまた primary から試す．両方失敗したら secondary の例外を出す．</summary>
public sealed class FallbackProcessSource(IProcessSource primary, IProcessSource secondary, OncePerDay log, Func<DateTime> clock) : IProcessSource
{
    public ProcessSnapshot Snapshot()
    {
        try
        {
            return primary.Snapshot();
        }
        catch (Exception e)
        {
            // 例外の文にプロセス名は入らない（各 Source が入れない）
            log.Write("process-fallback",
                $"meter: process list fell back to System.Diagnostics.Process: {e.GetType().Name}: {e.Message}",
                DateOnly.FromDateTime(clock()));
        }
        return secondary.Snapshot();
    }
}
