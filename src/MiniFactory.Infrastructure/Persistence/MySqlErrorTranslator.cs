using MiniFactory.Application.Persistence;
using MySqlConnector;

namespace MiniFactory.Infrastructure.Persistence;

/// <summary>
/// 把 <see cref="MySqlException"/> 轉成 Application 看得懂的例外（architecture.md §10.7.6）。
/// 只看錯誤碼，不解析訊息文字。
/// </summary>
public static class MySqlErrorTranslator
{
    // MariaDB 錯誤碼（MySqlErrorCode 列舉沒有 4025，所以統一用數字常數）
    public const int DuplicateKey = 1062;
    public const int LockWaitTimeout = 1205;
    public const int Deadlock = 1213;
    public const int RowIsReferenced = 1451;
    public const int NoReferencedRow = 1452;
    public const int CheckConstraintFailed = 4025;

    /// <summary>轉譯；無對應者回傳 null，呼叫端應照原例外往上拋。</summary>
    public static DatabaseException? Translate(MySqlException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var code = (int)exception.ErrorCode;
        var message = exception.Message;

        switch (code)
        {
            case DuplicateKey:
                return new UniqueConstraintViolationException(code, message, exception);
            case Deadlock:
                return new TransientDbException(TransientDbFailure.Deadlock, code, message, exception);
            case LockWaitTimeout:
                return new TransientDbException(TransientDbFailure.LockWaitTimeout, code, message, exception);
            case RowIsReferenced:
            case NoReferencedRow:
                return new ReferenceViolationException(code, message, exception);
            case CheckConstraintFailed:
                return new ConstraintViolationException(code, message, exception);
        }

        // 連線失敗 / 逾時 / 伺服器暫時不可用。鎖錯誤已在上面先處理（MySqlConnector 的 IsTransient 也包含鎖錯誤）
        if (exception.ErrorCode == MySqlErrorCode.UnableToConnectToHost
            || exception.ErrorCode == MySqlErrorCode.ConnectionCountError
            || exception.IsTransient)
        {
            return new DatabaseUnavailableException(code, message, exception);
        }

        return null;
    }
}
