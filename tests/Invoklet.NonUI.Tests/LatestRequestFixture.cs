using Invoklet.Services;

namespace Invoklet.NonUI.Tests;

// LatestRequest has no injected dependencies.
internal sealed class LatestRequestFixture
{
    public LatestRequest CreateSut() => new();
}
