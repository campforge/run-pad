using System.Windows.Input;
using Moq;
using RunPad.Services.Secrets;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.NonUI.Tests;

// The Secret Store window without a window: add/remove, validation, nothing written until Save (the
// window's OK), and a store that can't be read is never overwritten.
public class SecretStoreWindowViewModelTests
{
    private static (SecretStoreWindowViewModel Vm, Mock<ISecretStore> Store) CreateVm(params Secret[] secrets)
    {
        var store = new Mock<ISecretStore>();
        store.Setup(s => s.Load()).Returns(secrets);
        store.Setup(s => s.FilePath).Returns("/data/RunPad/secrets.json");
        return (new SecretStoreWindowViewModel(store.Object), store);
    }

    private static void Execute(ICommand command)
    {
        Assert.True(command.CanExecute(null));
        command.Execute(null);
    }

    [Fact]
    public void Edits_Should_BeSavedOnlyBySave()
    {
        var (vm, store) = CreateVm(new Secret("db", "one"));
        Assert.Equal("db", vm.SelectedSecret?.Name);

        vm.SelectedSecret!.Value = "two";
        Execute(vm.AddCommand);
        Assert.Equal("secret", vm.SelectedSecret?.Name);
        vm.SelectedSecret!.Value = "token";
        store.Verify(s => s.Save(It.IsAny<IReadOnlyList<Secret>>()), Times.Never);

        Assert.True(vm.Save());
        store.Verify(s => s.Save(It.Is<IReadOnlyList<Secret>>(list =>
            list.SequenceEqual(new[] { new Secret("db", "two"), new Secret("secret", "token") }))));
    }

    [Fact]
    public void AddedNames_Should_NotRepeat_AndRemoveShouldSelectANeighbour()
    {
        var (vm, _) = CreateVm(new Secret("secret", "a"), new Secret("Secret2", "b"));
        Execute(vm.AddCommand);
        Assert.Equal("secret3", vm.SelectedSecret?.Name);

        vm.SelectedSecret = vm.Secrets[1];
        Execute(vm.RemoveCommand);
        Assert.Equal(["secret", "secret3"], vm.Secrets.Select(s => s.Name));
        Assert.Equal("secret3", vm.SelectedSecret?.Name);
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("DB")]
    public void InvalidNames_Should_BlockSaving(string name)
    {
        var (vm, store) = CreateVm(new Secret("db", "a"), new Secret("other", "b"));
        vm.Secrets[1].Name = name;
        Assert.True(vm.HasValidationError);
        Assert.False(vm.Save());
        store.Verify(s => s.Save(It.IsAny<IReadOnlyList<Secret>>()), Times.Never);

        vm.Secrets[1].Name = "other";
        Assert.True(vm.CanSave);
    }

    [Fact]
    public void AStoreThatCantBeRead_Should_BeReported_AndNeverOverwritten()
    {
        var store = new Mock<ISecretStore>();
        store.Setup(s => s.Load()).Throws(new InvalidDataException("The secret store file is damaged."));
        store.Setup(s => s.FilePath).Returns("/data/RunPad/secrets.json");
        var vm = new SecretStoreWindowViewModel(store.Object);

        Assert.Contains("damaged", vm.LoadError);
        Assert.Contains("/data/RunPad/secrets.json", vm.LoadError);
        Assert.False(vm.CanSave);
        Assert.False(((ICommand)vm.AddCommand).CanExecute(null));
        Assert.False(vm.Save());
        store.Verify(s => s.Save(It.IsAny<IReadOnlyList<Secret>>()), Times.Never);
    }

    [Fact]
    public void AShownValue_Should_BeHiddenAgain_WhenAnotherSecretIsSelected()
    {
        var (vm, _) = CreateVm(new Secret("a", "1"), new Secret("b", "2"));
        Assert.Equal('●', vm.ValuePasswordChar);
        vm.IsValueRevealed = true;
        Assert.Equal('\0', vm.ValuePasswordChar);

        vm.SelectedSecret = vm.Secrets[1];
        Assert.False(vm.IsValueRevealed);
        Assert.Equal('●', vm.ValuePasswordChar);
    }
}
