namespace Shiftlock.Agent;

/// <summary>同じ失敗を 1 日 1 回だけ agent.log に書く（key と日付で数える．再起動すると数え直す）．</summary>
public sealed class OncePerDay(AgentLog log)
{
    private readonly Dictionary<string, DateOnly> _last = [];
    private readonly Lock _gate = new();

    public void Write(string key, string message, DateOnly today)
    {
        lock (_gate)
        {
            if (_last.TryGetValue(key, out var day) && day == today)
            {
                return;
            }
            _last[key] = today;
        }
        log.Write(message);
    }
}
