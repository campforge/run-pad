using Invoklet.Format.MdRun;
using Invoklet.Services;

namespace Invoklet.NonUI.Tests.App.Services.Fixtures;

// DependencyDraft has no injected dependencies; the front matter is data, so the tests use the real one.
internal sealed class DependencyDraftFixture
{
    public static MdRunFrontMatter CreateFrontMatter(params (string Id, string Version)[] packages)
        => new() { Packages = packages.ToDictionary(p => p.Id, p => new Package(p.Version)) };

    public DependencyDraft CreateSut(MdRunFrontMatter? loadedFrom = null)
    {
        var draft = new DependencyDraft();
        if (loadedFrom is not null) draft.Load(loadedFrom);
        return draft;
    }
}
