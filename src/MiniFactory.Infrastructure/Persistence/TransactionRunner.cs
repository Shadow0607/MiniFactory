using Microsoft.Extensions.Logging;
using MiniFactory.Application.Abstractions;
using MiniFactory.Application.Persistence;
using MySqlConnector;

namespace MiniFactory.Infrastructure.Persistence;

/// <summary>重試設定（architecture.md §10.6：最多 3 次【建議值】）。</summary>
public sealed class TransactionRetryOptions
{
    /// <summary>總執行次數上限（含第一次）。【待確認：文件「最多 3 次」解讀為總共 3 次執行】</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>第 n 次失敗後等待 BaseDelay * n 再加隨機抖動，避免兩個死結的交易同時重跑再次互鎖。</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(50);
}

/// <summary>
/// 只對 1213（deadlock）與 1205（lock wait timeout）整段重跑；其他錯誤（含連線失敗）不重試，直接拋出翻譯後的例外。
/// 重跑前交易已由 <c>UnitOfWork</c> 的 scope 回滾（1213 時伺服器端本來就已回滾）。
/// </summary>
public sealed class TransactionRunner(TransactionRetryOptions options, ILogger<TransactionRunner> logger) : ITransactionRunner
{
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var maxAttempts = Math.Max(1, options.MaxAttempts);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await work(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (AsTransient(ex) is not null)
            {
                var transient = AsTransient(ex)!;
                if (attempt >= maxAttempts)
                {
                    logger.LogError(transient, "資料庫鎖衝突（{Failure}）執行 {Attempts} 次仍失敗，放棄。", transient.Failure, attempt);
                    throw transient;
                }

                logger.LogWarning("資料庫鎖衝突（{Failure}），第 {Attempt}/{Max} 次執行失敗，整段重跑。", transient.Failure, attempt, maxAttempts);
                var delay = options.BaseDelay * attempt;
                if (delay > TimeSpan.Zero)
                {
                    delay += TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)Math.Max(1, delay.TotalMilliseconds)));
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    public async Task RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        await RunAsync<object?>(async ct =>
        {
            await work(ct).ConfigureAwait(false);
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>資料存取類別通常已翻譯；若有漏網的原始 MySqlException 也一併處理。</summary>
    private static TransientDbException? AsTransient(Exception ex) => ex switch
    {
        TransientDbException t => t,
        MySqlException m => MySqlErrorTranslator.Translate(m) as TransientDbException,
        _ => null,
    };
}
