using Invoklet.Contracts.IntelliSense;
using Invoklet.Services;
using Moq;

namespace Invoklet.NonUI.Tests;

public sealed class LightbulbEntriesFixture
{
    public static ICodeFixInfo CreateFix(string title, string? diagnosticId = null, DiagnosticSeverity severity = DiagnosticSeverity.Warning, int line = 1)
    {
        var fix = new Mock<ICodeFixInfo>();
        fix.SetupGet(f => f.Title).Returns(title);
        fix.SetupGet(f => f.DiagnosticId).Returns(diagnosticId);
        fix.SetupGet(f => f.Severity).Returns(severity);
        fix.SetupGet(f => f.Line).Returns(line);
        fix.SetupGet(f => f.Key).Returns("key-" + title);
        return fix.Object;
    }

    internal LightbulbEntries CreateSut() => new();
}
