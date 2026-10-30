using Xunit;

namespace Invoklet.NonUI.Tests;

// Pass 135 — tests here do REAL builds and run real script processes (the in-process Roslyn script
// compilers and package restore, directly or through a CSharpLanguageHost/VbLanguageHost, not a
// FakeCompiler). Introduced when builds were `dotnet build` child processes: xUnit parallelizes across test classes by default, and running several of these
// concurrently was enough to exhaust the machine under load in practice — one test timed out waiting
// on a real build's stderr drain, and a separate run saw MSBuild itself crash ("Child node exited
// prematurely") from the induced resource pressure. Everything else in this assembly is pure logic
// and stays fully parallel (see xunit.runner.json); only this specific, expensive, real-subprocess
// group needs to run one at a time relative to each other.
[CollectionDefinition(Name, DisableParallelization = true)]
public class RealBuildTestCollection
{
    public const string Name = "Real script builds";
}
