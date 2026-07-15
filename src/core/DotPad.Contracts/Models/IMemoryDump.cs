namespace DotPad.Contracts.Models;

public interface IMemoryDump
{
    DateTimeOffset CapturedAt { get; }
    IReadOnlyList<IVariableInfo> Variables { get; }
}