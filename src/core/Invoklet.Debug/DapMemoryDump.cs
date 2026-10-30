using Invoklet.Contracts.Models;

namespace Invoklet.Debug;

public sealed class DapMemoryDump : IMemoryDump
{
    public DateTimeOffset CapturedAt { get; init; }
    public IReadOnlyList<IVariableInfo> Variables { get; init; } = [];
}
