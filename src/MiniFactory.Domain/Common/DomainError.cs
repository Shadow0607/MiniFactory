namespace MiniFactory.Domain.Common;

/// <summary>領域錯誤：Code 供程式判斷（穩定），Message 供日誌與除錯。</summary>
public sealed record DomainError(string Code, string Message)
{
    /// <summary>非法狀態轉換。Code 格式：{entity}.invalid_transition。</summary>
    public static DomainError InvalidTransition<TStatus>(string entity, TStatus from, TStatus to)
        where TStatus : struct, Enum
        => new($"{entity}.invalid_transition", $"{entity} cannot transition from {from} to {to}.");

    /// <summary>輸入或不變條件驗證失敗。</summary>
    public static DomainError Validation(string code, string message) => new(code, message);

    public override string ToString() => $"{Code}: {Message}";
}
