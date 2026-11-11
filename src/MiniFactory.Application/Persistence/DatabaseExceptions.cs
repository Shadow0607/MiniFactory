namespace MiniFactory.Application.Persistence;

/// <summary>
/// 資料庫錯誤的基底類別（architecture.md §10.7.6）。
/// Application 依「哪一個語句失敗」判斷是哪個約束，<b>不解析</b> <see cref="ServerMessage"/>；
/// 該訊息（含索引名稱）只用來寫日誌。
/// </summary>
public abstract class DatabaseException : Exception
{
    protected DatabaseException(string message, int errorCode, string? serverMessage, Exception? inner)
        : base(message, inner)
    {
        ErrorCode = errorCode;
        ServerMessage = serverMessage;
    }

    /// <summary>MariaDB 錯誤碼（例 1062）；非伺服器端錯誤時為 0。</summary>
    public int ErrorCode { get; }

    /// <summary>伺服器回傳的原始訊息，僅供日誌。</summary>
    public string? ServerMessage { get; }
}

/// <summary>1062：違反唯一約束。InnoDB 只回滾該語句，交易仍可繼續使用。</summary>
public sealed class UniqueConstraintViolationException(int errorCode, string? serverMessage, Exception? inner = null)
    : DatabaseException("違反唯一約束（1062）。", errorCode, serverMessage, inner);

/// <summary>1451 / 1452：外鍵違反，屬程式邏輯錯誤。</summary>
public sealed class ReferenceViolationException(int errorCode, string? serverMessage, Exception? inner = null)
    : DatabaseException("違反外鍵約束（1451/1452）。", errorCode, serverMessage, inner);

/// <summary>4025：CHECK 約束違反（例如狀態字串不合法），屬程式邏輯錯誤。</summary>
public sealed class ConstraintViolationException(int errorCode, string? serverMessage, Exception? inner = null)
    : DatabaseException("違反 CHECK 約束（4025）。", errorCode, serverMessage, inner);

/// <summary>暫時性鎖衝突的種類。</summary>
public enum TransientDbFailure
{
    /// <summary>1213：InnoDB 已回滾整個交易。</summary>
    Deadlock,

    /// <summary>1205：鎖等待逾時，需顯式回滾後整段重跑。</summary>
    LockWaitTimeout,
}

/// <summary>1213 / 1205：由 <c>TransactionRunner</c> 整段重跑；重試用盡後原樣拋出（API 回 503）。</summary>
public sealed class TransientDbException(TransientDbFailure failure, int errorCode, string? serverMessage, Exception? inner = null)
    : DatabaseException($"暫時性資料庫鎖衝突：{failure}。", errorCode, serverMessage, inner)
{
    public TransientDbFailure Failure { get; } = failure;
}

/// <summary>連線失敗、逾時、伺服器暫時不可用：不自動重試，API 回 503 + Retry-After，Worker nack 並暫停消費。</summary>
public sealed class DatabaseUnavailableException(int errorCode, string? serverMessage, Exception? inner = null)
    : DatabaseException("資料庫暫時無法使用。", errorCode, serverMessage, inner);
