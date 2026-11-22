namespace Shiftlock.Core;

/// <summary>フェーズ通知の文（pc/shiftlock/plan.py の phase_messages のユーザー向け）．Claude 向けの文は持たない．</summary>
public static class PhaseText
{
    /// <summary>phase（lastcall / soft / hard / go / go_last）の通知の文．それ以外は空．</summary>
    public static string User(string phase, Plan p, DateTime now, ShiftConfig cfg, Messages m) => phase switch
    {
        "lastcall" => m.Format("PHASE_LASTCALL_USER",
            ("soft", TimeUtil.Fmt(p.SoftClose)), ("remain", TimeUtil.FmtDur(p.SoftClose - now, m))),
        "soft" => m.Format("PHASE_SOFT_USER",
            ("soft", TimeUtil.Fmt(p.SoftClose)), ("target_h", cfg.SleepTargetHoursText), ("hard", TimeUtil.Fmt(p.HardClose))),
        "hard" => m.Format("PHASE_HARD_USER", ("min_h", cfg.SleepMinHoursText), ("latest", TimeUtil.Fmt(p.StartLatest))),
        "go" => m.Format("PHASE_GO_USER", ("depart", TimeUtil.Fmt(p.DepartTarget))),
        "go_last" => m.Format("PHASE_GO_LAST_USER",
            ("depart", TimeUtil.Fmt(p.DepartLatest)), ("latest", TimeUtil.Fmt(p.StartLatest))),
        _ => "",
    };
}
