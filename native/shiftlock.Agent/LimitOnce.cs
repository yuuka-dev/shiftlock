using System.Globalization;
using System.Text.Json.Nodes;
using Shiftlock.Core;

namespace Shiftlock.Agent;

/// <summary>
/// 減速・一時停止の判断を 1 回だけ行い，「すること」の並びを JSON で出す（--limit-once．動作確認用）．
/// プロセスには手を加えず（control は呼ばない），ファイルも書かない（limit-games.json・agent.log・limit.json も）．
/// 本番の実行フォルダ（%USERPROFILE%\shift-lock）は断る（--tick-once と同じ．終了コード 2）．
/// 出力は標準出力だけ（ログではないので，対象のアプリの exe 名を出す）:
/// {"ops": [{"op": "cap"|"uncap"|"suspend"|"resume"|"run"|"release"|"notice", ...}], "state": {...}}
/// </summary>
public static class LimitOnce
{
    /// <summary>引数がおかしい・本番のフォルダを指している．</summary>
    public const int Refused = TickOnce.Refused;

    /// <summary>成功 0，判断できなかった（設定・一覧・記録が読めない）1，本番のフォルダ 2．</summary>
    public static int Run(string dir, IProcessSource source, IMeterProbe meterProbe, ISystemProbe probe, IFolderLister folders,
        ILimitControl control, DateTime now, TextWriter output, TextWriter error)
    {
        if (string.IsNullOrEmpty(dir) || TickOnce.IsProduction(dir))
        {
            error.WriteLine($"limit-once: refusing the production folder (use a copy): {dir}");
            return Refused;
        }
        try
        {
            var (cfg, _, loadError) = Watcher.LoadConfig(dir);
            if (cfg is null)
            {
                error.WriteLine(loadError);
                return 1;
            }
            var settings = MeterSettings.Read(cfg);
            var rules = Games.Rules(cfg, probe.SteamCommonDirs());
            var sampler = new MeterSampler(source, meterProbe, probe);
            var sample = sampler.Collect(now, TimeSpan.Zero, rules);
            var snapshot = new MeterSnapshot(sample, settings, rules, sampler.Paths);

            // 何も書かせない: ログは捨て，通知は出さず，control は渡すだけで使わない（Preview は触らない）
            var limiter = new Limiter(dir, () => snapshot, control, folders, _ => { }, () => now,
                new OncePerDay(new AgentLog(Path.Combine(dir, "limit-once-no-log", "never.log"))));
            var (decision, _) = limiter.Preview(snapshot, now);

            var ops = new JsonArray();
            foreach (var op in decision.Ops)
            {
                ops.Add(Describe(op));
            }
            var result = new JsonObject
            {
                ["parents"] = sample.HasParents,
                ["procs"] = sample.Procs.Count,
                ["ops"] = ops,
                ["after"] = new JsonObject
                {
                    ["capped"] = decision.StateAfterOps.Capped.Count,
                    ["suspended"] = decision.StateAfterOps.Suspended.Count,
                },
            };
            output.WriteLine(result.ToJsonString());
            return 0;
        }
        catch (Exception e)
        {
            error.WriteLine($"limit-once: {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    private static JsonObject Describe(LimitOp op) => op switch
    {
        CapOp c => new JsonObject
        {
            ["op"] = "cap", ["exe"] = c.Exe, ["job"] = c.Job, ["procs"] = c.Procs.Count,
            ["percent"] = Math.Round(c.Percent, 2),
        },
        UncapOp u => new JsonObject { ["op"] = "uncap", ["exe"] = u.Exe, ["job"] = u.Job },
        SuspendOp s => new JsonObject { ["op"] = "suspend", ["exe"] = s.Proc.Exe, ["pid"] = s.Proc.Pid },
        ResumeOp r => new JsonObject { ["op"] = "resume", ["exe"] = r.Proc.Exe, ["pid"] = r.Proc.Pid },
        RunOp r => new JsonObject { ["op"] = "run", ["action"] = r.Action, ["step"] = r.Step },
        ReleaseEventOp e => new JsonObject { ["op"] = "release", ["reason"] = e.Reason },
        NoticeOp n => new JsonObject { ["op"] = "notice", ["method"] = n.Method, ["text"] = n.Text },
        _ => new JsonObject { ["op"] = op.GetType().Name },
    };

    /// <summary>プロセスに触る操作を一切持たない control（--limit-once 用．呼ばれたら実装の間違いなので例外）．</summary>
    public sealed class Untouchable : ILimitControl
    {
        public bool Cap(string jobName, IReadOnlyList<ProcKey> procs, double percent) => throw new InvalidOperationException("--limit-once は操作しない");

        public bool Uncap(string jobName) => throw new InvalidOperationException("--limit-once は操作しない");

        public bool Suspend(ProcKey proc) => throw new InvalidOperationException("--limit-once は操作しない");

        public bool Resume(ProcKey proc) => throw new InvalidOperationException("--limit-once は操作しない");

        public bool Exists(ProcKey proc) => throw new InvalidOperationException("--limit-once は操作しない");
    }
}
