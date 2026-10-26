using Xunit;

namespace RunPad.Format.RPad.Tests;

// Declared arguments (arguments:), sets of their values (argumentSets:, selectedArgumentSet:) and the
// args a run gets from them (RpadArguments).
public class RpadArgumentSetsTests
{
    private const string Script = """
        ---
        version: "1.0"
        arguments:
          - name: environment
            type: enum
            default: dev
            values:
              - dev
              - test
          - name: count
            type: int
            default: "1"
          - name: verbose
            type: bool
          - name: rate
            type: double
          - name: price
            type: decimal
          - name: since
            type: datetime
          - name: timeout
            type: timespan
          - name: tags
            type: string[]
            default:
              - a
              - "b c"
          - name: title
            type: string
        argumentSets:
          smoke:
            environment: test
            count: "3"
            verbose: "true"
            rate: "0.5"
            price: "9.99"
            since: 2026-10-26T09:30:00
            timeout: "00:01:30"
            tags:
              - x
            title: "say \"hi\""
          defaults: {}
        selectedArgumentSet: smoke
        ---
        ```csharp run
        1.Dump();
        ```

        """;

    private static RpadDocument Parse(string text) =>
        RpadParser.Parse(text.Replace("\r\n", "\n")) is { IsSuccess: true } r ? r.Document : throw new Xunit.Sdk.XunitException(RpadParser.Parse(text).Error);

    [Fact]
    public void Arguments_Should_ParseWithTheirTypesDefaultsAndValues()
    {
        var fm = Parse(Script).FrontMatter;

        Assert.Equal(["environment", "count", "verbose", "rate", "price", "since", "timeout", "tags", "title"], fm.Arguments.Select(a => a.Name));
        Assert.Equal(RpadArgumentType.Enum, fm.Arguments[0].Type);
        Assert.Equal(["dev", "test"], fm.Arguments[0].Values);
        Assert.Equal(RpadArgumentType.StringList, fm.Arguments[7].Type);
        Assert.Equal(["a", "b c"], fm.Arguments[7].Default!.Items!.Select(i => i.Text));
        Assert.Null(fm.Arguments[2].Default);
        Assert.Equal(["smoke", "defaults"], fm.ArgumentSets.Keys);
        Assert.Equal("say \"hi\"", fm.ArgumentSets["smoke"]["title"].Text);
        Assert.Empty(fm.ArgumentSets["defaults"]);
        Assert.Empty(fm.UnknownEntries);
        Assert.Empty(RpadArguments.Validate(fm));
    }

    [Fact]
    public void Arguments_Should_SurviveWriteAndParse()
    {
        var written = RpadWriter.Write(Parse(Script));
        var again = Parse(written).FrontMatter;

        Assert.Equal(written, RpadWriter.Write(Parse(written)));
        Assert.Equal(RpadArguments.Resolve(Parse(Script).FrontMatter).Arguments, RpadArguments.Resolve(again).Arguments);
        Assert.Contains("\n  - name: tags\n    type: string[]\n", written);
        Assert.Contains("\n  defaults: {}\n", written);
    }

    [Fact]
    public void AFileWithoutArguments_Should_NotGainTheKeys()
    {
        var written = RpadWriter.Write(Parse("---\nargs:\n  - a\n---\n```csharp run\n1.Dump();\n```\n"));

        Assert.DoesNotContain("arguments", written);
        Assert.DoesNotContain("argumentSets", written);
        Assert.DoesNotContain("selectedArgumentSet", written);
    }

    [Theory]
    [InlineData("---\nargumentSets:\n  a: {}\n  a: {}\n---\n", "the argument set 'a' is defined twice")]
    [InlineData("---\nargumentSets:\n  a:\n    n: x\n    n: y\n---\n", "the argument set 'a' gives 'n' twice")]
    [InlineData("---\narguments:\n  - name: n\n    type: float\n---\n", "Unknown argument type 'float'")]
    public void AStructuralProblem_Should_BeAParseError(string frontMatter, string error)
    {
        var result = RpadParser.Parse(frontMatter + "```csharp run\n1.Dump();\n```\n");

        Assert.False(result.IsSuccess);
        Assert.Contains(error, result.Error);
    }

    [Fact]
    public void Snapshot_Should_CopyArgumentsAndSets()
    {
        var doc = Parse(Script);
        var restored = RpadDocument.FromSnapshot(doc.CaptureSnapshot());
        doc.FrontMatter.Arguments[0].Values.Add("prod");
        doc.FrontMatter.ArgumentSets["smoke"].Remove("count");

        Assert.Equal(["dev", "test"], restored.FrontMatter.Arguments[0].Values);
        Assert.True(restored.FrontMatter.ArgumentSets["smoke"].ContainsKey("count"));
        Assert.Equal("smoke", restored.FrontMatter.SelectedArgumentSet);
    }

    // ── The args a run gets ─────────────────────────────────────────────────────

    [Fact]
    public void Resolve_Should_PassEachArgumentAsNameAndValue_FromTheSelectedSet_ThenArgs()
    {
        var fm = Parse(Script).FrontMatter;

        Assert.Equal(
            ["--environment", "test", "--count", "3", "--verbose", "true", "--rate", "0.5", "--price", "9.99",
             "--since", "2026-10-26T09:30:00", "--timeout", "00:01:30", "--tags", "x", "--title", "say \"hi\""],
            RpadArguments.Resolve(fm).Arguments);
    }

    [Fact]
    public void Contract_Should_ExposeSelectedTypedValues()
    {
        var fm = Parse(Script).FrontMatter;

        var contract = RpadArgumentContract.Build(fm, out var errors);

        Assert.Empty(errors);
        Assert.Equal(["environment", "count", "verbose", "rate", "price", "since", "timeout", "tags", "title"],
            contract.Members.Select(m => m.Name));
        Assert.Equal("int", contract.Members.Single(m => m.Name == "count").Type);
        Assert.Equal(["3"], contract.Members.Single(m => m.Name == "count").Values);
        Assert.Equal(["dev", "test"], contract.Members.Single(m => m.Name == "environment").EnumValues);
        Assert.Contains("\"Name\":\"environment\"", contract.ToJson());
        Assert.Contains("\"Type\":\"int\"", contract.ToJson());
    }

    [Fact]
    public void Contract_Should_ReadExplicitNamedOverrides()
    {
        var fm = Parse(Script).FrontMatter;
        fm.SelectedArgumentSet = null;

        var contract = RpadArgumentContract.Build(fm, out var errors, ["--count", "7"]);

        Assert.Empty(errors);
        Assert.Equal(["7"], contract.Members.Single(m => m.Name == "count").Values);
    }

    [Fact]
    public void Contract_Should_BeEmptyAndReportErrorsForInvalidMetadata()
    {
        var fm = Parse(Script).FrontMatter;
        fm.ArgumentSets["smoke"]["count"] = RpadValue.Of("not-an-int");
        fm.ArgumentSets["smoke"]["unknown"] = RpadValue.Of("x");

        var contract = RpadArgumentContract.Build(fm, out var errors);

        Assert.Empty(contract.Members);
        Assert.Contains(errors, e => e.Contains("not-an-int"));
        Assert.Contains(errors, e => e.Contains("unknown"));
    }

    // Defaults where the set gives nothing; an argument with neither is left out.
    [Fact]
    public void Resolve_Should_UseDefaults_AndLeaveOutArgumentsWithoutAValue()
    {
        var fm = Parse(Script).FrontMatter;

        Assert.Equal(["--environment", "dev", "--count", "1", "--tags", "a", "--tags", "b c"],
            RpadArguments.Resolve(fm, null, "defaults").Arguments);
        fm.SelectedArgumentSet = null;
        Assert.Equal(["--environment", "dev", "--count", "1", "--tags", "a", "--tags", "b c"],
            RpadArguments.Resolve(fm).Arguments);
    }

    [Fact]
    public void Resolve_Should_PassGivenArgumentsAsTheyAre()
    {
        var fm = Parse(Script).FrontMatter;

        Assert.Equal(["cli"], RpadArguments.Resolve(fm, ["cli"], "nightly").Arguments);
        Assert.Empty(RpadArguments.Resolve(fm, [], null).Arguments);
        Assert.Null(RpadArguments.Resolve(fm, ["cli"], "nightly").Error);
    }

    [Fact]
    public void ScriptsWithOnlyArgs_Should_RunAsBefore()
    {
        var fm = Parse("---\nargs:\n  - \"from file\"\n---\n```csharp run\n1.Dump();\n```\n").FrontMatter;

        Assert.Empty(RpadArguments.Resolve(fm).Arguments);
        Assert.Empty(RpadArguments.Resolve(null).Arguments);
    }

    // Every problem at once, and no run.
    [Fact]
    public void Resolve_Should_ReportEveryInvalidValue()
    {
        var fm = Parse(Script).FrontMatter;
        var smoke = fm.ArgumentSets["smoke"];
        smoke["count"] = RpadValue.Of("three");
        smoke["verbose"] = RpadValue.Of("yes");
        smoke["rate"] = RpadValue.Of("0,5");
        smoke["since"] = RpadValue.Of("26.10.2026");
        smoke["timeout"] = RpadValue.Of("90s");
        smoke["environment"] = RpadValue.Of("prod");
        smoke["title"] = RpadValue.List([RpadValue.Of("a")]);
        smoke["missing"] = RpadValue.Of("x");

        var result = RpadArguments.Resolve(fm);

        Assert.Empty(result.Arguments);
        Assert.Contains("'count' is 'three', which is not a int", result.Error);
        Assert.Contains("'verbose' is 'yes', which is not a bool", result.Error);
        Assert.Contains("'rate' is '0,5', which is not a double", result.Error);
        Assert.Contains("'since' is '26.10.2026', which is not a datetime", result.Error);
        Assert.Contains("'timeout' is '90s', which is not a timespan", result.Error);
        Assert.Contains("'environment' is 'prod', which is not one of dev, test", result.Error);
        Assert.Contains("'title' must be a single value, not a list", result.Error);
        Assert.Contains("The set 'smoke' gives 'missing', which is not a declared argument", result.Error);
    }

    // A mistake in a set this run doesn't use doesn't stop it; Validate still reports it.
    [Fact]
    public void AnotherSetsMistake_Should_NotStopTheRun()
    {
        var fm = Parse(Script).FrontMatter;
        fm.ArgumentSets["defaults"]["count"] = RpadValue.Of("three");

        Assert.Null(RpadArguments.Resolve(fm).Error);
        Assert.NotNull(RpadArguments.Resolve(fm, null, "defaults").Error);
        Assert.Contains(RpadArguments.Validate(fm), e => e.Contains("In the set 'defaults'"));
    }

    [Fact]
    public void Validate_Should_ReportBadDeclarations()
    {
        var fm = new RpadFrontMatter
        {
            Arguments =
            [
                new() { Name = "two words" },
                new() { Name = "n" },
                new() { Name = "n" },
                new() { Name = "level", Type = RpadArgumentType.Enum },
                new() { Name = "count", Type = RpadArgumentType.Int, Default = RpadValue.Of("x") },
            ],
        };

        var errors = RpadArguments.Validate(fm);

        Assert.Contains(errors, e => e.Contains("'two words' can't be an argument name"));
        Assert.Contains(errors, e => e.Contains("'n' is declared twice"));
        Assert.Contains(errors, e => e.Contains("'level' is an enum without values"));
        Assert.Contains(errors, e => e.Contains("Default: 'count' is 'x'"));
    }

    [Fact]
    public void Validate_Should_RejectGeneratedPropertyNameCollisions()
    {
        var fm = new RpadFrontMatter
        {
            Arguments = [new() { Name = "value" }, new() { Name = "Value" }],
        };

        var errors = RpadArguments.Validate(fm);

        Assert.Contains(errors, e => e.Contains("conflicts with 'value'"));
    }

    [Theory]
    [InlineData("prod-us", "prod us")]
    [InlineData("dev", "Dev")]
    [InlineData("", "Value")]
    [InlineData("dev", "ArgumentsEnv")]
    public void Validate_Should_ReportConflictingEnumIdentifiers(string first, string second)
    {
        var fm = new RpadFrontMatter
        {
            Arguments = [new() { Name = "env", Type = RpadArgumentType.Enum, Values = [first, second] }],
        };
        var contract = RpadArgumentContract.Build(fm, out var errors);
        Assert.Contains(errors, e => e.Contains("conflicts with the generated enum name"));
        Assert.Empty(contract.Members);
    }

    [Fact]
    public void Contract_Should_GeneratePascalCaseArgumentsFacade()
    {
        var contract = new RpadArgumentContract([
            new("value", "string", [], [], IsNullable: true),
            new("count", "int", ["3"], []),
        ]);

        var source = contract.GenerateCSharpFacade();

        Assert.Contains("public static global::System.String? Value", source);
        Assert.Contains("ArgumentRuntime.Get(\"value\")", source);
        Assert.Contains("public static global::System.Int32 Count", source);
    }

    [Fact]
    public void Contract_Should_NotInferPropertiesFromUndeclaredCliArguments()
    {
        var contract = RpadArgumentContract.Build(new RpadFrontMatter(), out var errors, ["--value", "3", "--env", "prod"]);

        Assert.Empty(errors);
        Assert.Empty(contract.Members);
    }

    [Fact]
    public void Resolve_Should_RefuseAnUnknownSet()
    {
        var fm = Parse(Script).FrontMatter;

        var named = RpadArguments.Resolve(fm, null, "nightly");
        fm.SelectedArgumentSet = "gone";
        var selected = RpadArguments.Resolve(fm);

        Assert.Empty(named.Arguments);
        Assert.Contains("no argument set 'nightly'. Its sets: smoke, defaults.", named.Error);
        Assert.Contains("'gone'", selected.Error);
        Assert.Contains("defines none", RpadArguments.Resolve(new RpadFrontMatter { SelectedArgumentSet = "x" }).Error);
    }
}
