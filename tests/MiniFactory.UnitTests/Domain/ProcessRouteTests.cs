using MiniFactory.Domain.Jobs;

namespace MiniFactory.UnitTests.Domain;

public class ProcessRouteTests
{
    [Fact]
    public void Default_IsMachiningThenAssemblyThenInspection()
    {
        var steps = ProcessRoute.Default.Steps;

        Assert.Equal(3, steps.Count);
        Assert.Equal([ProcessOperation.Machining, ProcessOperation.Assembly, ProcessOperation.Inspection],
            steps.Select(s => s.Operation));
        Assert.Equal([1, 2, 3], steps.Select(s => s.Sequence));
    }

    [Fact]
    public void Default_FirstAndLast()
    {
        Assert.Equal(ProcessOperation.Machining, ProcessRoute.Default.First.Operation);
        Assert.Equal(ProcessOperation.Inspection, ProcessRoute.Default.Last.Operation);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    public void NextAfter_ReturnsFollowingStep(int sequence, int expectedNext)
        => Assert.Equal(expectedNext, ProcessRoute.Default.NextAfter(sequence)!.Sequence);

    [Theory]
    [InlineData(3)] // 最後一道沒有下一道
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public void NextAfter_LastOrOutOfRange_ReturnsNull(int sequence)
        => Assert.Null(ProcessRoute.Default.NextAfter(sequence));

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void IsLast_OnlyThirdStep(int sequence, bool expected)
        => Assert.Equal(expected, ProcessRoute.Default.IsLast(sequence));

    [Theory]
    [InlineData(1, ProcessOperation.Machining)]
    [InlineData(2, ProcessOperation.Assembly)]
    [InlineData(3, ProcessOperation.Inspection)]
    public void GetStep_ReturnsStepAndRequiredCapability(int sequence, ProcessOperation expected)
    {
        var step = ProcessRoute.Default.GetStep(sequence);
        Assert.NotNull(step);
        Assert.Equal(expected, step.Operation);
        Assert.Equal(expected, step.RequiredCapability);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void GetStep_OutOfRange_ReturnsNull(int sequence)
        => Assert.Null(ProcessRoute.Default.GetStep(sequence));

    [Fact]
    public void Constructor_Empty_Throws()
        => Assert.Throws<ArgumentException>(() => new ProcessRoute([]));

    [Fact]
    public void Constructor_NonContiguousSequence_Throws()
        => Assert.Throws<ArgumentException>(() => new ProcessRoute(
            [new ProcessStep(1, ProcessOperation.Machining), new ProcessStep(3, ProcessOperation.Assembly)]));

    [Fact]
    public void Constructor_NotStartingFromOne_Throws()
        => Assert.Throws<ArgumentException>(() => new ProcessRoute([new ProcessStep(2, ProcessOperation.Machining)]));

    [Fact]
    public void Constructor_DuplicateSequence_Throws()
        => Assert.Throws<ArgumentException>(() => new ProcessRoute(
            [new ProcessStep(1, ProcessOperation.Machining), new ProcessStep(1, ProcessOperation.Assembly)]));

    [Fact]
    public void Constructor_UnorderedInput_IsSortedBySequence()
    {
        var route = new ProcessRoute(
            [new ProcessStep(2, ProcessOperation.Assembly), new ProcessStep(1, ProcessOperation.Machining)]);

        Assert.Equal(ProcessOperation.Machining, route.First.Operation);
        Assert.Equal(ProcessOperation.Assembly, route.Last.Operation);
    }
}
