using Invoklet.Services;

namespace Invoklet.NonUI.Tests.App.Services.Fixtures;

// LatestRequest has no injected dependencies.
internal sealed class LatestRequestFixture
{
    public LatestRequest CreateSut() => new();
}
