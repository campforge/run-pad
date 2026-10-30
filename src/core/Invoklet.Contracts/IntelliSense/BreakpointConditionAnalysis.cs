namespace Invoklet.Contracts.IntelliSense;

public sealed record BreakpointConditionAnalysis(string? Error, IReadOnlyList<string> Suggestions);
