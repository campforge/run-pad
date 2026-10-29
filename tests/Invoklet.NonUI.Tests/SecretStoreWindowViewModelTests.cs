using System.Windows.Input;
using Moq;
using Invoklet.Services.Secrets;
using Invoklet.Windows.Models;
using Xunit;

namespace Invoklet.NonUI.Tests;

// The Secret Store window without a window: add/remove, validation, nothing written until Save (the
// window's OK), and a store that can't be read is never overwritten.
public class SecretStoreWindowViewModelTests
{
    private static (SecretStoreWindowViewModel Vm, Mock<ISecretStore> Store) CreateVm(params Secret[] secrets)
    {
        var store = new Mock<ISecretStore>();
        store.Setup(s => s.LoadSnapshot()).Returns(new SecretStoreSnapshot(secrets, "loaded"));
        store.Setup(s => s.FilePath).Returns("/data/Invoklet/secrets.json");
        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<Secret>>(), It.IsAny<string?>())).Returns("saved");
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
        store.Verify(s => s.Save(It.IsAny<IReadOnlyList<Secret>>(), It.IsAny<string?>()), Times.Never);

        Assert.True(vm.Save());
        store.Verify(s => s.Save(It.Is<IReadOnlyList<Secret>>(list =>
            list.SequenceEqual(new[] { new Secret("db", "two"), new Secret("secret", "token") })), "loaded"));
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
        store.Verify(s => s.Save(It.IsAny<IReadOnlyList<Secret>>(), It.IsAny<string?>()), Times.Never);

        vm.Secrets[1].Name = "other";
        Assert.True(vm.CanSave);
    }

    [Fact]
    public void AStoreThatCantBeRead_Should_BeReported_AndNeverOverwritten()
    {
        var store = new Mock<ISecretStore>();
        store.Setup(s => s.LoadSnapshot()).Throws(new InvalidDataException("The secret store file is damaged."));
        store.Setup(s => s.FilePath).Returns("/data/Invoklet/secrets.json");
        var vm = new SecretStoreWindowViewModel(store.Object);

        Assert.Contains("damaged", vm.LoadError);
        Assert.Contains("/data/Invoklet/secrets.json", vm.LoadError);
        Assert.False(vm.CanSave);
        Assert.False(((ICommand)vm.AddCommand).CanExecute(null));
        Assert.False(vm.Save());
        store.Verify(s => s.Save(It.IsAny<IReadOnlyList<Secret>>(), It.IsAny<string?>()), Times.Never);
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
    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(System.Security.Cryptography.CryptographicException))]
    public void AWriteFailure_Should_KeepEditsAndAllowRetry_WithoutExposingValues(Type exceptionType)
    {
        var (vm, store) = CreateVm(new Secret("db", "sensitive-value"));
        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<Secret>>(), It.IsAny<string?>()))
            .Throws((Exception)Activator.CreateInstance(exceptionType, "sensitive-value")!);
        Assert.False(vm.Save());
        Assert.NotNull(vm.SaveError);
        Assert.DoesNotContain("sensitive-value", vm.SaveError);
        Assert.Equal("sensitive-value", vm.Secrets[0].Value);
        Assert.True(vm.CanSave);
        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<Secret>>(), It.IsAny<string?>())).Returns("saved");
        Assert.True(vm.Save());
        Assert.Null(vm.SaveError);
    }

    [Fact]
    public void HasChanges_Should_TrackTheDraftAgainstTheLoadedStore()
    {
        var (vm, _) = CreateVm(new Secret("db", "one"), new Secret("api", "k"));
        Assert.False(vm.HasChanges);

        vm.SelectedSecret = vm.Secrets[1];
        vm.IsValueRevealed = true;
        Assert.False(vm.HasChanges); // selection and reveal state are display only

        vm.SelectedSecret!.Value = "two";
        Assert.True(vm.HasChanges);
        vm.SelectedSecret!.Value = "k";
        Assert.False(vm.HasChanges); // reverted

        vm.SelectedSecret!.Name = " api ";
        Assert.False(vm.HasChanges); // names are trimmed when saved
        vm.SelectedSecret!.Name = "renamed";
        Assert.True(vm.HasChanges);
        vm.SelectedSecret!.Name = "api";
        Assert.False(vm.HasChanges);

        Execute(vm.AddCommand);
        Assert.True(vm.HasChanges);
        Execute(vm.RemoveCommand);
        Assert.False(vm.HasChanges);

        vm.SelectedSecret = vm.Secrets[0];
        Execute(vm.RemoveCommand);
        Assert.True(vm.HasChanges);
    }

    [Fact]
    public void HasChanges_Should_ResetAfterASuccessfulSave_AndStayAfterAFailedOne()
    {
        var (vm, store) = CreateVm(new Secret("db", "one"));
        vm.SelectedSecret!.Value = "two";
        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<Secret>>(), It.IsAny<string?>())).Throws(new IOException());
        Assert.False(vm.Save());
        Assert.True(vm.HasChanges);

        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<Secret>>(), It.IsAny<string?>())).Returns("saved");
        Assert.True(vm.Save());
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public void AnUnreadableStore_Should_NotBeDirty()
    {
        var store = new Mock<ISecretStore>();
        store.Setup(s => s.LoadSnapshot()).Throws(new InvalidDataException("damaged."));
        store.Setup(s => s.FilePath).Returns("/data/Invoklet/secrets.json");
        Assert.False(new SecretStoreWindowViewModel(store.Object).HasChanges);
    }
}
