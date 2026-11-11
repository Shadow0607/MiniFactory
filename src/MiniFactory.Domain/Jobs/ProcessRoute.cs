using MiniFactory.Domain.Common;

namespace MiniFactory.Domain.Jobs;

/// <summary>工序識別字；同時也是設備的 capability（architecture.md §1：兩者值相同）。</summary>
public enum ProcessOperation
{
    /// <summary>加工</summary>
    Machining,

    /// <summary>組裝</summary>
    Assembly,

    /// <summary>檢驗</summary>
    Inspection,
}

/// <summary>工序路線中的一步。</summary>
public sealed record ProcessStep(int Sequence, ProcessOperation Operation)
{
    /// <summary>執行此工序所需的設備能力（= 工序本身）。</summary>
    public ProcessOperation RequiredCapability => Operation;
}

/// <summary>
/// 工序路線。第一版寫死為 加工 → 組裝 → 檢驗（<see cref="Default"/>），所有產品共用。
/// 順序由 1 起連續編號。
/// </summary>
public sealed class ProcessRoute
{
    public static readonly ProcessRoute Default = new(
    [
        new ProcessStep(1, ProcessOperation.Machining),
        new ProcessStep(2, ProcessOperation.Assembly),
        new ProcessStep(3, ProcessOperation.Inspection),
    ]);

    private readonly ProcessStep[] _steps;

    /// <summary>建立路線；順序必須自 1 起連續，且不可為空。</summary>
    public ProcessRoute(IEnumerable<ProcessStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps = [.. steps.OrderBy(s => s.Sequence)];

        if (_steps.Length == 0)
        {
            throw new ArgumentException("Process route must contain at least one step.", nameof(steps));
        }

        for (var i = 0; i < _steps.Length; i++)
        {
            if (_steps[i].Sequence != i + 1)
            {
                throw new ArgumentException("Process route sequences must be contiguous starting from 1.", nameof(steps));
            }
        }
    }

    public IReadOnlyList<ProcessStep> Steps => _steps;

    public ProcessStep First => _steps[0];

    public ProcessStep Last => _steps[^1];

    public ProcessStep? GetStep(int sequence)
        => sequence >= 1 && sequence <= _steps.Length ? _steps[sequence - 1] : null;

    /// <summary>下一道工序；最後一道（或序號不存在）回傳 null。</summary>
    public ProcessStep? NextAfter(int sequence)
        => sequence >= 1 && sequence < _steps.Length ? _steps[sequence] : null;

    public bool IsLast(int sequence) => sequence == Last.Sequence;
}
