namespace MiniFactory.Application.Abstractions;

/// <summary>
/// 死結 / 鎖等待逾時（MariaDB 1213 / 1205）時，整段重跑一個 Unit of Work（architecture.md §10.6）。
/// <para>
/// <paramref name="work"/> 必須自己開交易（<c>await using var tx = await uow.BeginAsync()</c>）並提交，
/// 且必須可重複執行：重跑時前一次嘗試的所有寫入都已回滾。
/// 重試用盡仍失敗時，拋出 <c>TransientDbException</c>（API 轉 503）。
/// 連線失敗等其他錯誤不重試，直接拋出翻譯後的例外。
/// </para>
/// </summary>
public interface ITransactionRunner
{
    Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default);

    Task RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default);
}
