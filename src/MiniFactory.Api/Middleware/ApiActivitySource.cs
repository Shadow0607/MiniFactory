using System.Diagnostics;

namespace MiniFactory.Api.Middleware;

/// <summary>
/// API 自己建立 root Activity 用的 <see cref="ActivitySource"/>（見 <see cref="UntrustedTraceContextMiddleware"/>）。
/// 名稱集中在 <see cref="Name"/>：Phase 2 加入 OpenTelemetry 時，tracing 設定需 <c>AddSource(ApiActivitySource.Name)</c>，
/// 才會匯出這些 root span。
/// </summary>
public static class ApiActivitySource
{
    public const string Name = "MiniFactory.Api";

    public static ActivitySource Source { get; } = new(Name);
}

/// <summary>
/// 啟動時註冊一個最小的 <see cref="ActivityListener"/>，讓 <see cref="ApiActivitySource"/> 在沒有 OpenTelemetry 的情況下
/// 也一定能建立 Activity：<c>ActivitySource.StartActivity</c> 在沒有 listener 時會回傳 null，
/// 那樣就拿不到新的 root trace id。
/// 取樣結果用 <see cref="ActivitySamplingResult.PropagationData"/>：有 trace id / span id 可傳遞，但不記錄 tag 與事件（幾乎零成本）。
/// Phase 2 的 OpenTelemetry 若自行監聽同一個 source，取樣以較高的結果為準，兩者並存不衝突。
/// </summary>
public sealed class ApiActivityListenerService : IHostedService, IDisposable
{
    private ActivityListener? _listener;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new ActivityListener
        {
            // 以名稱比對（const 字串）：listener 可能早於 Source 靜態初始化，建構 Source 時 Source 屬性仍為 null
            ShouldListenTo = source => source.Name == ApiActivitySource.Name,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.PropagationData,
        };
        ActivitySource.AddActivityListener(_listener);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _listener?.Dispose();
}
