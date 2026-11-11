using System.Diagnostics;
using MiniFactory.Api.Middleware;

namespace MiniFactory.IntegrationTests.Api;

/// <summary>R5：外部 traceparent 不被信任時，API 自己的 root Activity 由專屬 ActivitySource 建立（不需資料庫）。</summary>
public class ApiActivityListenerTests
{
    [Fact]
    public void SourceName_IsMiniFactoryApi()
    {
        Assert.Equal("MiniFactory.Api", ApiActivitySource.Name);
        Assert.Equal("MiniFactory.Api", ApiActivitySource.Source.Name);
    }

    [Fact]
    public async Task StartActivity_WithListenerService_CreatesNewRootWithOwnTraceId()
    {
        // 先觸發 Source 的靜態初始化，避免結果受執行順序影響（已知問題見下方 Skip 的測試）
        _ = ApiActivitySource.Source;
        using var listener = new ApiActivityListenerService();
        await listener.StartAsync(TestContext.Current.CancellationToken);

        using var root = ApiActivitySource.Source.StartActivity("test-root", ActivityKind.Server, default(ActivityContext));

        Assert.NotNull(root);
        Assert.Equal(default, root!.ParentSpanId);
        Assert.NotEqual(default, root.TraceId);
        Assert.Null(root.TraceStateString);
    }

    /// <summary>
    /// 回歸測試：listener 先於 Source 的靜態初始化啟動（正式環境即是如此）時，
    /// ShouldListenTo 必須以名稱比對，否則建構 Source 當下 Source 屬性仍為 null，StartActivity 會回傳 null。
    /// 靜態初始化在同一程序只會發生一次、受測試執行順序影響，因此這裡在 listener 啟動「之後」
    /// 才建立一個同名的新 ActivitySource，確定性地重現「listener 先於 Source」的情境
    /// （此時 ApiActivitySource.Source 不是這個實例，舊的 ReferenceEquals 寫法必定比對失敗）。
    /// </summary>
    [Fact]
    public async Task StartActivity_ListenerStartedBeforeSourceFirstUse_StillCreatesActivity()
    {
        using var listener = new ApiActivityListenerService();
        await listener.StartAsync(TestContext.Current.CancellationToken);

        using var lateSource = new ActivitySource(ApiActivitySource.Name);
        using var root = lateSource.StartActivity("test-root", ActivityKind.Server, default(ActivityContext));

        Assert.NotNull(root);
    }
}
