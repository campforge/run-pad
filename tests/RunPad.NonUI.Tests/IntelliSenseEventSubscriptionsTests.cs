using Moq;
using RunPad.Contracts.IntelliSense;
using RunPad.UserControls;
using Xunit;

namespace RunPad.NonUI.Tests;

public sealed class IntelliSenseEventSubscriptionsTests
{
    [Fact]
    public void Constructor_RejectsNullInputs()
    {
        var service = new Mock<IIntelliSenseService>().Object;
        Assert.Throws<ArgumentNullException>(() => new IntelliSenseEventSubscriptions(
            service, null!, (_, _) => { }, (_, _) => { }, (_, _) => { }));
        Assert.Throws<ArgumentNullException>(() => new IntelliSenseEventSubscriptions(
            service, (_, _) => { }, null!, (_, _) => { }, (_, _) => { }));
    }

    [Fact]
    public void Dispose_DetachesEveryServiceEvent_AndIsIdempotent()
    {
        var service = new Mock<IIntelliSenseService>();
        var diagnostics = 0;
        var tokens = 0;
        var fixes = 0;
        var hints = 0;
        using (var subscriptions = new IntelliSenseEventSubscriptions(
                   service.Object,
                   (_, _) => diagnostics++,
                   (_, _) => tokens++,
                   (_, _) => fixes++,
                   (_, _) => hints++))
        {
            service.Raise(s => s.DiagnosticsChanged += null, "code", Array.Empty<IDiagnosticInfo>());
            service.Raise(s => s.SemanticTokensChanged += null, "code", Array.Empty<ISemanticToken>());
            service.Raise(s => s.CodeFixesChanged += null, "code", Array.Empty<ICodeFixInfo>());
            service.Raise(s => s.InlayHintsChanged += null, "code", Array.Empty<IInlayHint>());
            Assert.Equal(1, diagnostics);
            Assert.Equal(1, tokens);
            Assert.Equal(1, fixes);
            Assert.Equal(1, hints);

            subscriptions.Dispose();
            subscriptions.Dispose();
            service.Raise(s => s.DiagnosticsChanged += null, "code", Array.Empty<IDiagnosticInfo>());
            service.Raise(s => s.SemanticTokensChanged += null, "code", Array.Empty<ISemanticToken>());
            service.Raise(s => s.CodeFixesChanged += null, "code", Array.Empty<ICodeFixInfo>());
            service.Raise(s => s.InlayHintsChanged += null, "code", Array.Empty<IInlayHint>());
        }

        Assert.Equal(1, diagnostics);
        Assert.Equal(1, tokens);
        Assert.Equal(1, fixes);
        Assert.Equal(1, hints);
    }
}
