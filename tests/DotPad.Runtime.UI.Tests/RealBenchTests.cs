using System.Diagnostics;
using DotPad.Contracts.IntelliSense;
using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// THROWAWAY diagnostic benchmark — not part of the permanent suite, deleted after use.
public class RealBenchTests
{
    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
    }

    private static string BuildScript(int classCount)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < classCount; i++)
        {
            sb.AppendLine($$"""
                class Foo{{i}}
                {
                    private int field{{i}} = {{i}};
                    public int Method{{i}}(int x)
                    {
                        var y = field{{i}} + x;
                        if (y > 0) { System.Console.WriteLine(y); }
                        return y;
                    }
                }
                """);
        }
        sb.AppendLine("var probe = 1;");
        return sb.ToString();
    }

    [Theory]
    [InlineData(15)]  // ~140 lines, realistic-ish DotPad script
    [InlineData(60)]  // ~550 lines, bigger script
    public async Task Bench_GetRefactoringsAsync(int classCount)
    {
        using var service = CreateService();
        var code = BuildScript(classCount);
        var position = code.IndexOf("var probe", StringComparison.Ordinal) + 4;

        // Warm up (JIT + first-use provider construction).
        await service.GetRefactoringsAsync(code, position, TestContext.Current.CancellationToken);

        var times = new List<long>();
        for (var i = 0; i < 8; i++)
        {
            var sw = Stopwatch.StartNew();
            var result = await service.GetRefactoringsAsync(code, position, TestContext.Current.CancellationToken);
            sw.Stop();
            times.Add(sw.ElapsedMilliseconds);
        }

        throw new Xunit.Sdk.XunitException(
            $"[{classCount} classes, {code.Length} chars] GetRefactoringsAsync times: {string.Join(", ", times)}ms, avg={times.Average():F1}ms");
    }

    [Theory]
    [InlineData(15)]
    [InlineData(60)]
    public async Task Bench_FullBackgroundPass(int classCount)
    {
        using var service = CreateService();
        var code = BuildScript(classCount);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagCount = 0;
        var fixCount = 0;
        service.DiagnosticsChanged += (_, d) => { diagCount = d.Count; };
        service.CodeFixesChanged += (_, f) => { fixCount = f.Count; tcs.TrySetResult(); };

        var sw = Stopwatch.StartNew();
        service.NotifyTextChanged(code);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        sw.Stop();

        throw new Xunit.Sdk.XunitException(
            $"[{classCount} classes, {code.Length} chars] Full background pass (incl. 500ms debounce): {sw.ElapsedMilliseconds}ms, {diagCount} diagnostics, {fixCount} fixes");
    }
}
