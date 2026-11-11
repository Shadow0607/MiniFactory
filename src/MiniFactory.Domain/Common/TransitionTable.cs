namespace MiniFactory.Domain.Common;

/// <summary>狀態轉換表：各狀態機共用的實作，轉換規則以資料表表達，方便對照文件。</summary>
internal sealed class TransitionTable<TStatus> where TStatus : struct, Enum
{
    private readonly string _entity;
    private readonly Dictionary<TStatus, TStatus[]> _map;

    public TransitionTable(string entity, IReadOnlyDictionary<TStatus, TStatus[]> allowed)
    {
        _entity = entity;
        _map = new Dictionary<TStatus, TStatus[]>();
        foreach (var status in Enum.GetValues<TStatus>())
        {
            // 未列出的狀態視為終態（沒有任何出口）
            _map[status] = allowed.TryGetValue(status, out var next) ? next : [];
        }
    }

    /// <summary>相同狀態不算轉換（回傳 false）。</summary>
    public bool CanTransition(TStatus from, TStatus to) => Array.IndexOf(_map[from], to) >= 0;

    public bool IsTerminal(TStatus status) => _map[status].Length == 0;

    public IReadOnlyCollection<TStatus> NextStates(TStatus status) => _map[status];

    public Result<TStatus> TryTransition(TStatus from, TStatus to)
        => CanTransition(from, to)
            ? Result.Success(to)
            : Result.Failure<TStatus>(DomainError.InvalidTransition(_entity, from, to));
}
