namespace Invoklet.Contracts.Debugging;

public interface IMemoryDump
{
    DateTimeOffset CapturedAt { get; }
    IReadOnlyList<IVariableInfo> Variables { get; }
}
