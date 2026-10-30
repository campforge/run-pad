using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.NonUI.Tests.Languages.CSharp.IntelliSense;

// Editor smoothness (2026-10-12): the editor used to hand NotifyTextChanged a full copy of the document
// (editor.Text) on every keystroke, on the UI thread. It now hands a lazy getter over an immutable
// snapshot; the service calls it only for the notification that's still the latest once the debounce
// ends, off the caller's thread.
public class CSharpIntelliSenseLazyTextTests
{
    [Fact]
    public async Task NotifyTextChanged_WithAGetter_Should_BuildOnlyTheLatestText_AndAnalyzeIt()
    {
        var referenceService = new CSharpReferenceService();
        using var service = new CSharpIntelliSenseService(
            referenceService.GetDefault().Append(referenceService.GetExtensionReference()),
            NullLogger<CSharpIntelliSenseService>.Instance)
        {
            BackgroundAnalysisDebounce = TimeSpan.FromMilliseconds(300),
        };
        var analyzed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SemanticTokensChanged += (code, _) => analyzed.TrySetResult(code);
        var built = new List<int>();

        // five keystrokes in quick succession, like typing
        for (var i = 1; i <= 5; i++)
        {
            var keystroke = i;
            service.NotifyTextChanged(() =>
            {
                lock (built) built.Add(keystroke);
                return $"var x{keystroke} = {keystroke};";
            });
        }

        Assert.Empty(built); // nothing materialized on the calling thread

        var code = await analyzed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal("var x5 = 5;", code);
        lock (built) Assert.Equal([5], built); // the four superseded keystrokes never built a string
    }
}
