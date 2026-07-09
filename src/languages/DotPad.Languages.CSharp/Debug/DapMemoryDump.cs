using DotPad.Contracts.Models;

namespace DotPad.Languages.CSharp.Debug;

internal sealed class DapMemoryDump : IMemoryDump
{
    public DateTimeOffset CapturedAt { get; init; }
    public IReadOnlyList<IVariableInfo> Variables { get; init; } = [];
}
