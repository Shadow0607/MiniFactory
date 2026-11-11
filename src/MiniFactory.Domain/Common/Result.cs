namespace MiniFactory.Domain.Common;

/// <summary>
/// 操作結果。非法轉換等預期內的失敗以 Result 回傳，不拋例外
/// （architecture.md §9：非法轉換一律拒絕並記 Warning，不拋 500）。
/// </summary>
public class Result
{
    protected Result(DomainError? error) => Error = error;

    public DomainError? Error { get; }

    public bool IsSuccess => Error is null;

    public bool IsFailure => !IsSuccess;

    public static Result Success() => new(null);

    public static Result Failure(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(error);
    }

    public static Result<T> Success<T>(T value) => Result<T>.Ok(value);

    public static Result<T> Failure<T>(DomainError error) => Result<T>.Fail(error);
}

/// <summary>帶值的操作結果；失敗時存取 <see cref="Value"/> 會拋 InvalidOperationException（屬程式錯誤）。</summary>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T? value, DomainError? error) : base(error) => _value = value;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Result is a failure and has no value: {Error}");

    internal static Result<T> Ok(T value) => new(value, null);

    internal static Result<T> Fail(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(default, error);
    }
}
