using Invoklet.Contracts;
using Invoklet.Services;
using Moq;

namespace Invoklet.NonUI.Tests.App.Services.Fixtures;

internal sealed class PackageSearchRunnerFixture
{
    public Mock<IPackageSearchService> Search { get; } = new();

    public TimeSpan Debounce { get; set; } = TimeSpan.Zero;

    public static PackageSearchOptions Options { get; } = new("nuget.org", false, 0);

    public PackageSearchRunner CreateSut() => new(Search.Object, Debounce);
}
