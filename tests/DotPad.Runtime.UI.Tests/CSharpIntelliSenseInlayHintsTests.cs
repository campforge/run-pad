using DotPad.Contracts.IntelliSense;
using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #62 "inlay hints" — CSharpIntelliSenseService.ComputeInlayHintsAsync, wired to
// NotifyTextChanged the same way SemanticTokensChanged/DiagnosticsChanged are (see
// CSharpIntelliSenseBackgroundAnalysisTests.cs, same real-background-pass end-to-end approach).
public class CSharpIntelliSenseInlayHintsTests
{
    private static readonly TimeSpan BackgroundAnalysisTimeout = TimeSpan.FromSeconds(30);

    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
    }

    private static async Task<IReadOnlyList<IInlayHint>> WaitForHintsAsync(CSharpIntelliSenseService service, string code)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<IInlayHint>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.InlayHintsChanged += (c, hints) =>
        {
            if (c == code) tcs.TrySetResult(hints);
        };
        service.NotifyTextChanged(code);
        return await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAHint_ForAnImplicitlyTypedLocal()
    {
        using var service = CreateService();
        const string code = "var x = 5;";

        var hints = await WaitForHintsAsync(service, code);

        var hint = Assert.Single(hints);
        Assert.Equal("int ", hint.Text);
        Assert.Equal(code.IndexOf('x'), hint.Offset);
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAHint_WithAMinimallyQualifiedGenericTypeName()
    {
        using var service = CreateService();
        const string code = "var list = new System.Collections.Generic.List<string>();";

        var hints = await WaitForHintsAsync(service, code);

        var hint = Assert.Single(hints);
        // The trailing "?" is genuine Roslyn behavior, not a DotPad quirk — confirmed with a bare
        // CSharpCompilation probe outside this codebase entirely: GetTypeInfo on a `var` TypeSyntax
        // under nullable-enable returns NullableAnnotation.Annotated for `new List<string>()` here,
        // same as what real Roslyn/VS's own (internal) CSharpInlineTypeHintsService would show —
        // this test locks in that this is expected, not something to "fix" later out of surprise.
        Assert.Equal("List<string>? ", hint.Text);
        Assert.Equal(code.IndexOf("list", StringComparison.Ordinal), hint.Offset);
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseNoHints_ForAnExplicitlyTypedLocal()
    {
        using var service = CreateService();

        var hints = await WaitForHintsAsync(service, "int x = 5;");

        Assert.Empty(hints);
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseNoHints_ForAMultiDeclaratorVarStatement()
    {
        using var service = CreateService();

        // Roslyn's own CSharpInlineTypeHintsService skips this shape entirely too (only one
        // inferred type for the whole statement — hinting just the first declarator would
        // misleadingly suggest it doesn't apply to the rest).
        var hints = await WaitForHintsAsync(service, "var a = 1, b = 2;");

        Assert.Empty(hints);
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAHint_ForAnImplicitlyTypedForEachVariable()
    {
        using var service = CreateService();
        const string code = "foreach (var c in \"abc\") { }";

        var hints = await WaitForHintsAsync(service, code);

        var hint = Assert.Single(hints);
        Assert.Equal("char ", hint.Text);
        Assert.Equal(code.IndexOf('c', code.IndexOf("var", StringComparison.Ordinal)), hint.Offset);
    }
}
