using System.Windows.Input;
using Moq;
using RunPad.Contracts;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.NonUI.Tests;

// The package source configurator without a window: ordering, add/remove, validation, and that
// nothing is written until Save (the window's OK).
public class NuGetSourcesWindowViewModelTests
{
    private static (NuGetSourcesWindowViewModel Vm, Mock<INuGetSourceStore> Store) CreateVm(params NuGetSource[] sources)
    {
        var store = new Mock<INuGetSourceStore>();
        store.Setup(s => s.Load()).Returns(sources);
        return (new NuGetSourcesWindowViewModel(store.Object), store);
    }

    private static bool CanExecute(ICommand command) => command.CanExecute(null);

    private static void Execute(ICommand command)
    {
        Assert.True(command.CanExecute(null));
        command.Execute(null);
    }

    private static readonly NuGetSource A = new("a", "https://a.example/v3/index.json");
    private static readonly NuGetSource B = new("b", "https://b.example/v3/index.json", IsEnabled: false);
    private static readonly NuGetSource C = new("c", "https://c.example/v3/index.json", true, "me", "token");

    [Fact]
    public void Moves_Should_FollowTheSelection_AndDisableAtTheEnds()
    {
        var (vm, _) = CreateVm(A, B, C);
        Assert.Same(vm.Sources[0], vm.SelectedSource);
        Assert.False(CanExecute(vm.MoveUpCommand));
        Assert.False(CanExecute(vm.MoveToTopCommand));

        Execute(vm.MoveDownCommand);
        Assert.Equal(["b", "a", "c"], vm.Sources.Select(s => s.Name));
        Assert.Equal("a", vm.SelectedSource?.Name);

        vm.SelectedSource = vm.Sources[2];
        Assert.False(CanExecute(vm.MoveDownCommand));
        Execute(vm.MoveToTopCommand);
        Assert.Equal(["c", "b", "a"], vm.Sources.Select(s => s.Name));
        Assert.False(CanExecute(vm.MoveUpCommand));
    }

    [Fact]
    public void AddAndRemove_Should_KeepNamesUnique_AndSelectSensibly()
    {
        var (vm, _) = CreateVm(new NuGetSource("Package source", "https://x.example/v3/index.json"));

        Execute(vm.AddCommand);
        Assert.Equal("Package source 2", vm.SelectedSource?.Name);
        Assert.True(vm.HasValidationError); // the new source has no URL yet
        vm.SelectedSource!.Url = "https://y.example/v3/index.json";
        Assert.False(vm.HasValidationError);

        vm.SelectedSource = vm.Sources[0];
        Execute(vm.RemoveCommand);
        Assert.Equal(["Package source 2"], vm.Sources.Select(s => s.Name));
        Assert.Same(vm.Sources[0], vm.SelectedSource);
        Execute(vm.RemoveCommand);
        Assert.Null(vm.SelectedSource);
        Assert.False(CanExecute(vm.RemoveCommand));
    }

    [Theory]
    [InlineData("", "https://a.example/v3/index.json", false, "", "needs a name")]
    [InlineData("B", "https://a.example/v3/index.json", false, "", "more than one package source named")]
    [InlineData("a", "", false, "", "Enter a URL or a local folder")]
    [InlineData("a", "https://bad url/", false, "", "not a valid URL")]
    [InlineData("a", "https://a.example/v3/index.json", true, " ", "Enter a username")]
    public void InvalidEntries_Should_BlockSaving_WithAMessage(string name, string url, bool basic, string user, string expected)
    {
        var (vm, store) = CreateVm(A, B);
        var item = vm.Sources[0];
        item.Name = name;
        item.Url = url;
        item.UseBasicAuth = basic;
        item.Username = user;

        Assert.Contains(expected, vm.ValidationError);
        Assert.False(vm.CanSave);
        Assert.False(vm.Save());
        store.Verify(s => s.Save(It.IsAny<IReadOnlyList<NuGetSource>>()), Times.Never);
    }

    [Fact]
    public void Save_Should_WriteTheEditedList_DroppingCredentialsWhenAuthenticationIsNone()
    {
        var (vm, store) = CreateVm(A, B, C);
        IReadOnlyList<NuGetSource>? saved = null;
        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<NuGetSource>>())).Callback<IReadOnlyList<NuGetSource>>(s => saved = s);

        vm.Sources[1].IsEnabled = true;
        vm.Sources[2].UseNoAuth = true; // keeps the typed values in the editor, but saves no credentials
        vm.Sources[0].Name = "  a renamed ";
        Assert.True(vm.Save());

        Assert.Equal(
            [
                new NuGetSource("a renamed", "https://a.example/v3/index.json"),
                new NuGetSource("b", "https://b.example/v3/index.json"),
                new NuGetSource("c", "https://c.example/v3/index.json"),
            ],
            saved);
    }
}
