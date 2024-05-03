namespace DotPad.Build.ProcessRunners.Args;

public class LocalExecutionArgs
{
    private readonly string _assemblyPath;
    private readonly string _processId;

    public LocalExecutionArgs(string assemblyPath, string processId)
    {
        _assemblyPath = assemblyPath;
        _processId = processId;
    }

    public override string ToString()
    {
        return $" \"{_assemblyPath}\" --pid {_processId}";
    }
}