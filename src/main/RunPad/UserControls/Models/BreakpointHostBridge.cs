using RunPad.Contracts;

namespace RunPad.UserControls.Models;

/// <summary>Translates editor breakpoint changes to the language host's breakpoint contract.</summary>
internal static class BreakpointHostBridge
{
    public static void Add(ILanguageHost host, string fileName, int line, string? condition, string? hitCondition)
        => host.AddBreakpoint(new HostBreakpoint(fileName, line, condition, hitCondition));

    public static void Remove(ILanguageHost host, string fileName, int line)
        => host.RemoveBreakpoint(new HostBreakpoint(fileName, line, null, null));

    private sealed class HostBreakpoint(string fileName, int line, string? condition, string? hitCondition) : IBreakpoint
    {
        public string FileName { get; } = fileName;
        public int Line { get; set; } = line;
        public string? Condition { get; } = condition;
        public string? HitCondition { get; } = hitCondition;
    }
}
