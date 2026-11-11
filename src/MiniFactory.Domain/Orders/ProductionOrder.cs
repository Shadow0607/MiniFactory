using MiniFactory.Domain.Common;
using MiniFactory.Domain.Jobs;

namespace MiniFactory.Domain.Orders;

/// <summary>生產訂單（database.md §3.1）。建立時依工序路線同時產生所有 Job。</summary>
public sealed class ProductionOrder
{
    public const int MaxOrderNoLength = 50;
    public const int MaxProductCodeLength = 50;
    public const int MaxLineIdLength = 32;
    public const int MaxCorrelationIdLength = 64;

    private readonly List<ProductionJob> _jobs = [];

    // 供 ORM 使用
    private ProductionOrder()
    {
    }

    public Guid Id { get; private set; }

    public string OrderNo { get; private set; } = string.Empty;

    public string ProductCode { get; private set; } = string.Empty;

    public int Quantity { get; private set; }

    public string LineId { get; private set; } = string.Empty;

    public OrderStatus Status { get; private set; }

    public int Version { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    public string? TraceParent { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public IReadOnlyList<ProductionJob> Jobs => _jobs;

    /// <summary>
    /// 建立訂單與其所有 Job：第 1 道 Ready 並已開啟 attempt 1（commandId 在派工前就持久化），其餘 Pending。
    /// orderNo / productCode / lineId 會 trim。
    /// </summary>
    /// <param name="newId">產生 UUIDv7 的工廠（Guid.CreateVersion7），測試可注入固定序列。</param>
    public static Result<ProductionOrder> Create(
        Guid id,
        string? orderNo,
        string? productCode,
        int quantity,
        string? lineId,
        string? correlationId,
        string? traceParent,
        ProcessRoute route,
        Func<Guid> newId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(newId);

        if (id == Guid.Empty)
        {
            return Fail("order.id_required", "Order id is required.");
        }

        var no = orderNo?.Trim();
        if (string.IsNullOrEmpty(no) || no.Length > MaxOrderNoLength)
        {
            return Fail("order.order_no_invalid", $"Order number is required and at most {MaxOrderNoLength} characters.");
        }

        var product = productCode?.Trim();
        if (string.IsNullOrEmpty(product) || product.Length > MaxProductCodeLength)
        {
            return Fail("order.product_code_invalid", $"Product code is required and at most {MaxProductCodeLength} characters.");
        }

        if (quantity <= 0)
        {
            return Fail("order.quantity_invalid", "Quantity must be greater than 0.");
        }

        var line = lineId?.Trim();
        if (string.IsNullOrEmpty(line) || line.Length > MaxLineIdLength)
        {
            return Fail("order.line_id_invalid", $"Line id is required and at most {MaxLineIdLength} characters.");
        }

        var correlation = correlationId?.Trim();
        if (string.IsNullOrEmpty(correlation) || correlation.Length > MaxCorrelationIdLength)
        {
            return Fail("order.correlation_id_invalid", $"Correlation id is required and at most {MaxCorrelationIdLength} characters.");
        }

        var order = new ProductionOrder
        {
            Id = id,
            OrderNo = no,
            ProductCode = product,
            Quantity = quantity,
            LineId = line,
            Status = OrderStatus.Created,
            CorrelationId = correlation,
            TraceParent = traceParent,
            CreatedAt = now,
            UpdatedAt = now,
        };

        foreach (var step in route.Steps)
        {
            var isFirst = step.Sequence == route.First.Sequence;
            var jobResult = ProductionJob.Create(newId(), id, step, isFirst ? JobStatus.Ready : JobStatus.Pending, now);
            if (jobResult.IsFailure)
            {
                return Result.Failure<ProductionOrder>(jobResult.Error!);
            }

            var job = jobResult.Value;
            if (isFirst)
            {
                var attempt = job.OpenInitialAttempt(newId(), newId(), now);
                if (attempt.IsFailure)
                {
                    return Result.Failure<ProductionOrder>(attempt.Error!);
                }
            }

            order._jobs.Add(job);
        }

        return Result.Success(order);

        static Result<ProductionOrder> Fail(string code, string message)
            => Result.Failure<ProductionOrder>(DomainError.Validation(code, message));
    }

    /// <summary>取得指定序號的下一道 Job；最後一道回傳 null。</summary>
    public ProductionJob? NextJobAfter(int sequence)
        => _jobs.FirstOrDefault(j => j.Sequence == sequence + 1);

    /// <summary>
    /// 套用新的訂單狀態（由 Job 狀態重新計算後呼叫）。進入 Completed / NeedsReview 時記錄 CompletedAt。
    /// </summary>
    public Result TransitionTo(OrderStatus to, DateTimeOffset now)
    {
        var transition = OrderStateMachine.TryTransition(Status, to);
        if (transition.IsFailure)
        {
            return Result.Failure(transition.Error!);
        }

        Status = to;
        if (OrderStateMachine.IsTerminal(to))
        {
            CompletedAt = now;
        }

        Version++;
        UpdatedAt = now;
        return Result.Success();
    }
}
