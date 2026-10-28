using RunPad.Format.RPad;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.NonUI.Tests;

// The Dependencies window's Arguments tab: declared arguments as rows, sets of their values and the
// set runs use, as a draft applied on Save.
public class ScriptArgumentsViewModelTests
{
    private static RpadFrontMatter Script() => new()
    {
        Arguments =
        [
            new() { Name = "environment", Type = RpadArgumentType.Enum, Default = RpadValue.Of("dev"), Values = ["dev", "test"] },
            new() { Name = "count", Type = RpadArgumentType.Int },
            new() { Name = "tags", Type = RpadArgumentType.StringList, Default = RpadValue.List([RpadValue.Of("two\nlines")]) },
        ],
        ArgumentSets = new(StringComparer.Ordinal)
        {
            ["smoke"] = new(StringComparer.Ordinal) { ["environment"] = RpadValue.Of("test"), ["count"] = RpadValue.Of("3") },
        },
        SelectedArgumentSet = "smoke",
    };

    private static ScriptArgumentsViewModel Load(RpadFrontMatter fm)
    {
        var vm = new ScriptArgumentsViewModel();
        vm.Load(fm);
        return vm;
    }

    private static RpadFrontMatter Applied(ScriptArgumentsViewModel vm)
    {
        var fm = new RpadFrontMatter();
        vm.ApplyTo(fm);
        return fm;
    }

    [Fact]
    public void Load_Should_ShowRowsAndSets_WithoutChanges()
    {
        var vm = Load(Script());

        Assert.Equal(["environment", "count", "tags"], vm.Arguments.Select(a => a.Name));
        Assert.Equal("enum", vm.Arguments[0].Type.Label);
        Assert.Equal("dev, test", vm.Arguments[0].ValuesText);
        Assert.True(vm.Arguments[0].IsEnum);
        Assert.True(vm.Arguments[2].IsList);
        Assert.Equal(["environment", "count", "tags"], vm.Sets[0].Values.Select(v => v.Argument.Name));
        Assert.Equal(["test", "3", ""], vm.Sets[0].Values.Select(v => v.Text));
        Assert.Equal("default: dev", vm.Sets[0].Values[0].Hint);
        Assert.Equal("not passed", vm.Sets[0].Values[1].Hint);
        Assert.Equal("smoke", vm.RunChoice!.Set!.Name);
        Assert.Equal(["Defaults", "smoke"], vm.RunChoices.Select(c => c.Label));
        Assert.False(vm.HasChanges);
        Assert.False(vm.HasProblems);
    }

    // An enum's default is picked from its values; retyping the values, which empties the picker's
    // selection for a moment, keeps it.
    [Fact]
    public void EnumDefault_Should_PickFromTheValues_AndSurviveRetypingThem()
    {
        var environment = Load(Script()).Arguments[0];
        Assert.Equal("dev", environment.EnumDefault);

        environment.EnumDefault = "test";
        Assert.Equal("test", environment.DefaultText);
        environment.EnumDefault = null;
        Assert.Equal("test", environment.DefaultText);

        environment.ValuesText = "dev";
        Assert.Null(environment.EnumDefault);
        environment.ValuesText = "dev, test, prod";
        Assert.Equal("test", environment.EnumDefault);
    }

    // Untouched, a value saves as it was loaded, even a list item with a line break.
    [Fact]
    public void Apply_Should_GiveBackWhatWasLoaded()
    {
        var fm = Applied(Load(Script()));

        Assert.Equal(RpadArguments.Resolve(Script()).Arguments, RpadArguments.Resolve(fm).Arguments);
        Assert.Equal("two\nlines", fm.Arguments[2].Default!.Items![0].Text);
    }

    [Fact]
    public void AddingARow_Should_DeclareAnArgument_InEverySet()
    {
        var vm = Load(Script());

        vm.AddArgumentCommand.Execute().Subscribe();
        var row = vm.Arguments[^1];
        row.Name = "since";
        row.Type = ArgumentRowViewModel.Types.First(t => t.Type == RpadArgumentType.DateTime);
        vm.Sets[0].Values[^1].Text = "2026-10-26";
        var fm = Applied(vm);

        Assert.True(vm.HasChanges);
        Assert.Equal(RpadArgumentType.DateTime, fm.Arguments[^1].Type);
        Assert.Equal("2026-10-26", fm.ArgumentSets["smoke"]["since"].Text);
        Assert.Contains("--since", RpadArguments.Resolve(fm).Arguments);
    }

    // A renamed argument keeps its values in the sets; a removed one takes them along.
    [Fact]
    public void RenamingAndRemovingRows_Should_CarryTheirValues()
    {
        var vm = Load(Script());

        vm.Arguments[1].Name = "n";
        vm.Arguments[0].RemoveCommand.Execute().Subscribe();
        var fm = Applied(vm);

        Assert.Equal(["n", "tags"], fm.Arguments.Select(a => a.Name));
        Assert.Equal(["n"], fm.ArgumentSets["smoke"].Keys);
        Assert.Equal("3", fm.ArgumentSets["smoke"]["n"].Text);
    }

    [Fact]
    public void AnEmptyValue_Should_UseTheDefault_AndAListTakesOneItemPerLine()
    {
        var vm = Load(Script());

        vm.Sets[0].Values[0].Text = "";
        vm.Sets[0].Values[2].Text = "a\n\nb c";
        var fm = Applied(vm);

        Assert.False(fm.ArgumentSets["smoke"].ContainsKey("environment"));
        Assert.Equal(["--environment", "dev", "--count", "3", "--tags", "a", "--tags", "", "--tags", "b c"],
            RpadArguments.Resolve(fm).Arguments);
    }

    [Fact]
    public void AValueOfTheWrongType_Should_BeAProblem_ButNotBlockSaving()
    {
        var vm = Load(Script());

        vm.Sets[0].Values[1].Text = "three";

        Assert.Contains(vm.Problems, p => p.Contains("'count' is 'three', which is not a int"));
        Assert.False(vm.HasError);
    }

    [Theory]
    [InlineData("two words")]
    [InlineData("count")]
    public void ABadOrRepeatedArgumentName_Should_BlockSaving(string name)
    {
        var vm = Load(Script());

        vm.Arguments[0].Name = name;

        Assert.True(vm.HasError);
        vm.Arguments[0].Name = "environment";
        Assert.False(vm.HasError);
    }

    [Fact]
    public void ARepeatedSetName_Should_BlockSaving_WithoutLosingASet()
    {
        var vm = Load(Script());

        vm.AddSetCommand.Execute().Subscribe();
        vm.Sets[1].Name = "smoke";

        Assert.Equal("Two argument sets are named 'smoke'.", vm.BlockingError);
        vm.Sets[1].Name = "nightly";
        Assert.Null(vm.BlockingError);
        Assert.Equal(["smoke", "nightly"], Applied(vm).ArgumentSets.Keys);
    }

    [Fact]
    public void RunChoice_Should_FollowARenamedSet_AndFallBackWhenItIsRemoved()
    {
        var vm = Load(Script());

        vm.Sets[0].Name = "renamed";
        Assert.Equal("renamed", Applied(vm).SelectedArgumentSet);

        vm.SelectedSet = vm.Sets[0];
        vm.RemoveSetCommand.Execute().Subscribe();
        Assert.Null(vm.RunChoice!.Set);
        Assert.Null(Applied(vm).SelectedArgumentSet);
    }

    [Fact]
    public void AMissingSelectedSet_Should_StayUntilAChoiceIsMade()
    {
        var fm = Script();
        fm.SelectedArgumentSet = "gone";
        var vm = Load(fm);

        Assert.Null(vm.RunChoice!.Set);
        Assert.False(vm.HasChanges);
        Assert.Equal("gone", Applied(vm).SelectedArgumentSet);

        vm.RunChoice = vm.RunChoices[1];
        Assert.Equal("smoke", Applied(vm).SelectedArgumentSet);
    }

    // A set value for a name that isn't declared (a hand-edited file) is kept, and reported.
    [Fact]
    public void AnUndeclaredSetValue_Should_BeKept_AndReported()
    {
        var fm = Script();
        fm.ArgumentSets["smoke"]["old"] = RpadValue.Of("x");
        var vm = Load(fm);

        Assert.Contains(vm.Problems, p => p.Contains("'old', which is not a declared argument"));
        Assert.Equal("x", Applied(vm).ArgumentSets["smoke"]["old"].Text);
    }

    [Fact]
    public void AScriptWithoutArguments_Should_StayWithout()
    {
        var fm = new RpadFrontMatter();
        var vm = Load(fm);

        vm.ApplyTo(fm);

        Assert.False(vm.HasChanges);
        Assert.Empty(fm.Arguments);
        Assert.Empty(fm.ArgumentSets);
        Assert.Null(fm.SelectedArgumentSet);
    }
}
