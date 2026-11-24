namespace MuseLauncher.Core;

/// <summary>
/// 后台账号闲置挂起策略（对应 WebView2 TrySuspendAsync，降低内存和 CPU 占用）。
/// 保护正在前台使用、正在播放语音/音频、或正在下载文件的会话不被挂起。
/// </summary>
public static class IdleSuspendPolicy
{
    public const long DefaultIdleThresholdMs = 3L * 60L * 1000L; // 3 分钟

    public readonly record struct SessionActivity(
        long AccountId,
        bool IsCurrent,
        bool IsSuspended,
        bool IsBusy,
        long LastActiveAtMs);

    public static IReadOnlyList<long> SelectAccountsToSuspend(
        IEnumerable<SessionActivity> sessions,
        long nowMs,
        long idleThresholdMs = DefaultIdleThresholdMs)
    {
        var result = new List<long>();
        foreach (var s in sessions)
        {
            if (s.IsCurrent || s.IsSuspended || s.IsBusy) continue;
            if (s.LastActiveAtMs <= 0) continue;
            if (nowMs - s.LastActiveAtMs >= idleThresholdMs)
                result.Add(s.AccountId);
        }
        return result;
    }
}
