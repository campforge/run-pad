using Xunit;

namespace DotPad.Runtime.Tests;

// Console.Out/Console.In are process-wide global state — any test that temporarily redirects them
// (ObjectExtensionsTests, UtilTests) must not run concurrently with another test doing the same, or
// one test's restore can clobber another's still-in-flight redirect mid-write. xUnit parallelizes
// across test classes by default, so without this they're different collections and can race.
[CollectionDefinition(Name, DisableParallelization = true)]
public class ConsoleTestCollection
{
    public const string Name = "Console redirection";
}
