using MiniFactory.Domain.Jobs;
using MiniFactory.Domain.Orders;

namespace MiniFactory.UnitTests.Domain;

public class ProductionOrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 2, 15, 30, TimeSpan.Zero);

    private static Func<Guid> Ids() => () => Guid.CreateVersion7();

    private static ProductionOrder NewOrder()
        => ProductionOrder.Create(Guid.CreateVersion7(), "ORD-20260921-001", "Motor-A", 500, "Line-01", "corr-1", null,
            ProcessRoute.Default, Ids(), Now).Value;

    [Fact]
    public void Create_Valid_ProducesThreeJobsFollowingRoute()
    {
        var order = NewOrder();

        Assert.Equal(OrderStatus.Created, order.Status);
        Assert.Equal(3, order.Jobs.Count);
        Assert.Equal([1, 2, 3], order.Jobs.Select(j => j.Sequence));
        Assert.Equal(
            [ProcessOperation.Machining, ProcessOperation.Assembly, ProcessOperation.Inspection],
            order.Jobs.Select(j => j.Operation));
        Assert.All(order.Jobs, j => Assert.Equal(order.Id, j.OrderId));
    }

    [Fact]
    public void Create_FirstJobReadyWithAttemptOne_OthersPending()
    {
        var order = NewOrder();

        Assert.Equal([JobStatus.Ready, JobStatus.Pending, JobStatus.Pending], order.Jobs.Select(j => j.Status));

        var first = order.Jobs[0];
        var execution = Assert.Single(first.Executions);
        Assert.Equal(1, execution.AttemptNo);
        Assert.Equal(1, first.CurrentAttemptNo);
        Assert.Equal(ExecutionStatus.Pending, execution.Status);
        Assert.NotEqual(Guid.Empty, execution.CommandId); // commandId 在派工前就已產生

        Assert.All(order.Jobs.Skip(1), j => Assert.Empty(j.Executions));
    }

    [Fact]
    public void Create_TrimsInputs()
    {
        var order = ProductionOrder.Create(Guid.CreateVersion7(), "  ORD-1  ", " Motor-A ", 1, " Line-01 ", " c ", null,
            ProcessRoute.Default, Ids(), Now).Value;

        Assert.Equal("ORD-1", order.OrderNo);
        Assert.Equal("Motor-A", order.ProductCode);
        Assert.Equal("Line-01", order.LineId);
        Assert.Equal("c", order.CorrelationId);
    }

    [Theory]
    [InlineData(null, "Motor-A", 1, "Line-01", "c", "order.order_no_invalid")]
    [InlineData("  ", "Motor-A", 1, "Line-01", "c", "order.order_no_invalid")]
    [InlineData("ORD", null, 1, "Line-01", "c", "order.product_code_invalid")]
    [InlineData("ORD", "Motor-A", 0, "Line-01", "c", "order.quantity_invalid")]
    [InlineData("ORD", "Motor-A", -5, "Line-01", "c", "order.quantity_invalid")]
    [InlineData("ORD", "Motor-A", 1, "", "c", "order.line_id_invalid")]
    [InlineData("ORD", "Motor-A", 1, "Line-01", null, "order.correlation_id_invalid")]
    public void Create_InvalidInput_ReturnsValidationError(string? orderNo, string? product, int qty, string? line, string? corr, string code)
    {
        var result = ProductionOrder.Create(Guid.CreateVersion7(), orderNo, product, qty, line, corr, null,
            ProcessRoute.Default, Ids(), Now);

        Assert.True(result.IsFailure);
        Assert.Equal(code, result.Error!.Code);
    }

    [Fact]
    public void Create_TooLongFields_AreRejected()
    {
        var tooLongOrderNo = new string('A', ProductionOrder.MaxOrderNoLength + 1);
        var result = ProductionOrder.Create(Guid.CreateVersion7(), tooLongOrderNo, "Motor-A", 1, "Line-01", "c", null,
            ProcessRoute.Default, Ids(), Now);

        Assert.Equal("order.order_no_invalid", result.Error!.Code);
    }

    [Fact]
    public void Create_NewEntities_AllStartAtVersionZero_EvenWithFirstAttemptOpened()
    {
        var order = NewOrder();

        Assert.Equal(0, order.Version);
        Assert.All(order.Jobs, j => Assert.Equal(0, j.Version));
        Assert.All(order.Jobs.SelectMany(j => j.Executions), x => Assert.Equal(0, x.Version));

        // 第 1 道仍有第 1 次 attempt，只是建立不算一次修改
        Assert.Equal(1, order.Jobs[0].CurrentAttemptNo);
        Assert.Single(order.Jobs[0].Executions);
    }

    [Fact]
    public void Create_EmptyId_ReturnsError()
    {
        var result = ProductionOrder.Create(Guid.Empty, "ORD", "Motor-A", 1, "Line-01", "c", null,
            ProcessRoute.Default, Ids(), Now);

        Assert.Equal("order.id_required", result.Error!.Code);
    }

    [Fact]
    public void NextJobAfter_ReturnsFollowingJob_LastReturnsNull()
    {
        var order = NewOrder();

        Assert.Same(order.Jobs[1], order.NextJobAfter(1));
        Assert.Same(order.Jobs[2], order.NextJobAfter(2));
        Assert.Null(order.NextJobAfter(3));
    }

    [Fact]
    public void TransitionTo_Valid_UpdatesStatusAndVersion()
    {
        var order = NewOrder();

        var result = order.TransitionTo(OrderStatus.InProgress, Now.AddSeconds(1));

        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.InProgress, order.Status);
        Assert.Equal(1, order.Version);
        Assert.Null(order.CompletedAt);
    }

    [Fact]
    public void TransitionTo_Illegal_LeavesOrderUnchanged()
    {
        var order = NewOrder();

        var result = order.TransitionTo(OrderStatus.Completed, Now.AddSeconds(1)); // Created 不可直接 Completed

        Assert.True(result.IsFailure);
        Assert.Equal("order.invalid_transition", result.Error!.Code);
        Assert.Equal(OrderStatus.Created, order.Status);
        Assert.Equal(0, order.Version);
    }

    [Theory]
    [InlineData(OrderStatus.Completed)]
    [InlineData(OrderStatus.NeedsReview)]
    public void TransitionTo_Terminal_SetsCompletedAt_AndCannotLeave(OrderStatus terminal)
    {
        var order = NewOrder();
        Assert.True(order.TransitionTo(OrderStatus.InProgress, Now).IsSuccess);

        var at = Now.AddMinutes(1);
        Assert.True(order.TransitionTo(terminal, at).IsSuccess);
        Assert.Equal(at, order.CompletedAt);

        Assert.True(order.TransitionTo(OrderStatus.InProgress, at.AddMinutes(1)).IsFailure);
        Assert.Equal(terminal, order.Status);
    }
}
