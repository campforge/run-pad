using RunPad.Format.RPad;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.NonUI.Tests;

// The Dependencies window's Arguments tab: args:, named sets and the one runs use, as a draft.
public class ScriptArgumentsViewModelTests
{
    private static RpadFrontMatter Script() => new()
    {
        Args = ["legacy"],
        ArgumentSets = new(StringComparer.Ordinal)
        {
            ["smoke"] = ["--verbose", "file with spaces.txt"],
            ["multi"] = ["two\nlines"],
        },
        SelectedArgumentSet = "smoke",
    };

    private static ScriptArgumentsViewModel Load(RpadFrontMatter fm)
    {
        var vm = new ScriptArgumentsViewModel();
        vm.Load(fm);
        return vm;
    }

    [Fact]
    public void Load_Should_ShowTheScriptsArguments_AndHaveNoChanges()
    {
        var vm = Load(Script());

        Assert.Equal("legacy", vm.DefaultText);
        Assert.Equal(["smoke", "multi"], vm.Sets.Select(s => s.Name));
        Assert.Equal("--verbose\nfile with spaces.txt", vm.Sets[0].Text);
        Assert.Equal("smoke", vm.RunChoice!.Set!.Name);
        Assert.Equal(["Default (args:)", "smoke", "multi"], vm.RunChoices.Select(c => c.Label));
        Assert.False(vm.HasChanges);
        Assert.False(vm.HasError);
    }

    // An argument with a line break has no line of its own: untouched, it is saved as it was.
    [Fact]
    public void AnUneditedSet_Should_KeepItsArgumentsExactly()
    {
        var fm = Script();
        var vm = Load(fm);
        vm.DefaultText = "new";

        vm.ApplyTo(fm);

        Assert.False(vm.Sets[1].Fits);
        Assert.Equal(["two\nlines"], fm.ArgumentSets["multi"]);
        Assert.Equal(["new"], fm.Args);
    }

    [Fact]
    public void Edits_Should_ApplyAsWritten()
    {
        var fm = Script();
        var vm = Load(fm);
        vm.Sets[0].Text = "a\n\n\"q\"";
        vm.Sets[0].Name = "renamed";
        vm.AddCommand.Execute().Subscribe();
        vm.Sets[2].Text = "";

        Assert.True(vm.HasChanges);
        vm.ApplyTo(fm);

        Assert.Equal(["renamed", "multi", "set"], fm.ArgumentSets.Keys);
        Assert.Equal(["a", "", "\"q\""], fm.ArgumentSets["renamed"]);
        Assert.Empty(fm.ArgumentSets["set"]);
        Assert.Equal("renamed", fm.SelectedArgumentSet); // the choice follows its set's new name
    }

    [Fact]
    public void RemovingTheSetRunsUse_Should_GoBackToTheDefault()
    {
        var fm = Script();
        var vm = Load(fm);
        vm.SelectedSet = vm.Sets[0];

        vm.RemoveCommand.Execute().Subscribe();
        vm.ApplyTo(fm);

        Assert.Null(vm.RunChoice!.Set);
        Assert.Null(fm.SelectedArgumentSet);
        Assert.Equal(["multi"], fm.ArgumentSets.Keys);
    }

    [Fact]
    public void ChoosingTheDefault_Should_ClearTheSelection()
    {
        var fm = Script();
        var vm = Load(fm);

        vm.RunChoice = vm.RunChoices[0];
        vm.ApplyTo(fm);

        Assert.Null(fm.SelectedArgumentSet);
    }

    [Theory]
    [InlineData("multi", "Two argument sets are named 'multi'.")]
    [InlineData(" ", "Every argument set needs a name.")]
    public void AnEmptyOrRepeatedName_Should_BeAnError(string name, string error)
    {
        var vm = Load(Script());

        vm.Sets[0].Name = name;

        Assert.Equal(error, vm.Error);
        vm.Sets[0].Name = "smoke";
        Assert.Null(vm.Error);
    }

    // A selected set the script doesn't have stays in the file until a choice is made.
    [Fact]
    public void AMissingSelectedSet_Should_StayUntilAChoiceIsMade()
    {
        var fm = Script();
        fm.SelectedArgumentSet = "gone";
        var vm = Load(fm);

        Assert.Null(vm.RunChoice!.Set);
        Assert.False(vm.HasChanges);
        vm.DefaultText = "x";
        vm.ApplyTo(fm);
        Assert.Equal("gone", fm.SelectedArgumentSet);

        vm.RunChoice = vm.RunChoices[1];
        vm.ApplyTo(fm);
        Assert.Equal("smoke", fm.SelectedArgumentSet);
    }

    [Fact]
    public void AScriptWithoutArguments_Should_StayWithout()
    {
        var fm = new RpadFrontMatter();
        var vm = Load(fm);

        vm.ApplyTo(fm);

        Assert.False(vm.HasChanges);
        Assert.Null(fm.Args);
        Assert.Empty(fm.ArgumentSets);
        Assert.Null(fm.SelectedArgumentSet);
    }
}
