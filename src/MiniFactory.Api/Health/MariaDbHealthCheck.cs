using Microsoft.Extensions.Diagnostics.HealthChecks;
using MySqlConnector;

namespace MiniFactory.Api.Health;

/// <summary>MariaDB 就緒檢查：開連線並執行 <c>SELECT 1</c>，逾時 2 秒（architecture.md §14.6）。</summary>
public sealed class MariaDbHealthCheck(MySqlDataSource dataSource, ILogger<MariaDbHealthCheck> logger) : IHealthCheck
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(timeout.Token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("timeout");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "MariaDB 健康檢查失敗");

            // api.md §9：description 只放錯誤類別，不含主機名稱 / 帳號 / 連線字串
            return HealthCheckResult.Unhealthy(ex.GetType().Name);
        }
    }
}
