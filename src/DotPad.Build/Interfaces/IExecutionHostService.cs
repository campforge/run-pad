namespace DotPad.Build.Interfaces;

internal interface IExecutionHostService
{
    Task Execute(string code, string[] references, CancellationToken cancellationToken);
    Task Terminate();
}

