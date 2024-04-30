namespace DotPad.Build;

internal interface IExecutionService
{
	Task ExecuteAsync(string code);
	Task TerminateAsync();
}

