namespace DotPad.Services;

public interface IOptionsService
{
    string DotnetPath { get; set; }
    string DebuggerPath { get; set; }
}

public sealed class OptionsService : IOptionsService
{
    public string DotnetPath { get; set; } = @"C:\Program Files\dotnet\dotnet.exe";
    public string DebuggerPath { get; set; } = @"C:\Users\user\Downloads\netcoredbg\netcoredbg.exe";
}
