using DotPad.Contracts.Models;

namespace DotPad.Debug;

public sealed class DapMemoryDump : IMemoryDump
{
    public DateTimeOffset CapturedAt { get; init; }
    public IReadOnlyList<IVariableInfo> Variables { get; init; } = [];
}
