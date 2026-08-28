namespace AuraFlow.OpenRgb;

internal static class ThreadExtensions
{
    /// <summary>Awaits thread exit on the thread pool (Join has no async overload).</summary>
    public static Task JoinAsync(this Thread thread, CancellationToken ct)
    {
        return Task.Run(thread.Join, ct);
    }
}
