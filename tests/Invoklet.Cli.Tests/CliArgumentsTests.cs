using Invoklet.Format.MdRun;
using Xunit;

namespace Invoklet.Cli.Tests;

public class CliArgumentsTests
{
    [Theory]
    [InlineData(new[] { "a.inv" }, null)]
    [InlineData(new[] { "a.inv", "Hello", "World" }, new[] { "Hello", "World" })]  // lprun
    [InlineData(new[] { "run", "a.inv", "x" }, new[] { "x" })]                     // npad's explicit run
    [InlineData(new[] { "a.inv", "--", "-s", "--" }, new[] { "-s", "--" })]
    [InlineData(new[] { "a.inv", "x", "--", "-k" }, new[] { "x", "-k" })]
    [InlineData(new[] { "a.inv", "--" }, new string[0])]
    [InlineData(new[] { "a.inv", "-unknown", "-" }, new[] { "-unknown", "-" })]
    public void Parse_Should_PassWhatFollowsTheScript_ToTheScript(string[] args, string[]? expected)
    {
        var cli = CliArguments.Parse(args);

        Assert.Null(cli.Error);
        Assert.Equal("a.inv", cli.ScriptPath);
        Assert.Equal(expected, cli.ScriptArguments);
    }

    [Theory]
    [InlineData(new[] { "-lang=e", "a.inv" }, MdRunKind.Expression, null)]
    [InlineData(new[] { "-lang=Program", "a.inv" }, MdRunKind.Program, null)]
    [InlineData(new[] { "-lang=VBStatements", "a.inv" }, MdRunKind.Statements, MdRunLanguage.Vb)]
    [InlineData(new[] { "--kind", "program", "a.inv" }, MdRunKind.Program, null)]
    [InlineData(new[] { "a.inv", "-k", "s" }, MdRunKind.Statements, null)]
    public void Parse_Should_ReadTheKind_InEitherStyle(string[] args, MdRunKind kind, MdRunLanguage? language)
    {
        var cli = CliArguments.Parse(args);

        Assert.Null(cli.Error);
        Assert.Equal(kind, cli.Kind);
        Assert.Equal(language, cli.Language);
        Assert.Null(cli.ScriptArguments);
    }

    [Theory]
    [InlineData("9", "net9.0")]
    [InlineData("10.0", "net10.0")]
    [InlineData("net8.0", "net8.0")]
    [InlineData("latest", "latest")]
    [InlineData("LATEST", "latest")]
    [InlineData("net9", "net9.0")]
    [InlineData("1", "net1.0")]   // valid, if not installed: the runner falls back with a warning
    public void Parse_Should_ReadTheSdk(string value, string expected)
    {
        Assert.Equal(expected, CliArguments.Parse(["--sdk", value, "a.inv"]).TargetSdk?.ToString());
        Assert.Equal(expected, CliArguments.Parse(["a.inv", "-s", value]).TargetSdk?.ToString());
        Assert.Equal(expected, CliArguments.Parse([$"--sdk={value}", "a.inv"]).TargetSdk?.ToString());
    }

    // A missing value must not take the next option (or "--") as the SDK: "--sdk --compile-only a.inv"
    // would otherwise run the script on the newest SDK instead of only compiling it.
    [Theory]
    [InlineData("--sdk --compile-only a.inv")]
    [InlineData("-s -compileonly a.inv")]
    [InlineData("--sdk --help a.inv")]
    [InlineData("a.inv -s -- x")]
    [InlineData("--kind --compile-only a.inv")]
    [InlineData("--sdk= a.inv")]
    [InlineData("-s= a.inv")]
    [InlineData("a.inv --sdk")]
    public void Parse_Should_RejectAMissingValue_WithoutTakingTheNextOption(string commandLine)
    {
        var cli = CliArguments.Parse(commandLine.Split(' '));

        Assert.NotNull(cli.Error);
        Assert.Contains("needs a value", cli.Error);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("net")]
    [InlineData("9x")]
    [InlineData("net9.0-windows")]
    [InlineData("v9")]
    public void Parse_Should_RejectAnSdkItCannotRead(string value)
    {
        foreach (var args in new[] { new[] { "--sdk", value, "a.inv" }, ["a.inv", $"-s={value}"] })
        {
            var cli = CliArguments.Parse(args);

            Assert.NotNull(cli.Error);
            Assert.Contains($"'{value}'", cli.Error);
        }
    }

    [Theory]
    [InlineData("-compileonly")]
    [InlineData("--compile-only")]
    public void Parse_Should_ReadCompileOnly(string option)
    {
        Assert.True(CliArguments.Parse([option, "a.inv"]).CompileOnly);
    }

    [Fact]
    public void Parse_Should_ReadRefreshConnections_BeforeOrAfterTheScript()
    {
        Assert.True(CliArguments.Parse(["--refresh-connections", "a.inv"]).RefreshConnections);
        Assert.True(CliArguments.Parse(["a.inv", "--refresh-connections"]).RefreshConnections);
        Assert.False(CliArguments.Parse(["a.inv"]).RefreshConnections);
        Assert.Equal(["--refresh-connections"], CliArguments.Parse(["a.inv", "--", "--refresh-connections"]).ScriptArguments);
    }

    [Theory]
    [InlineData("-nunuget")]
    [InlineData("--no-cache")]
    [InlineData("-b")]
    public void Parse_Should_AcceptCacheOptions_WithNoEffect(string option)
    {
        var cli = CliArguments.Parse([option, "a.inv"]);

        Assert.Null(cli.Error);
        Assert.Equal("a.inv", cli.ScriptPath);
    }

    [Theory]
    [InlineData("-format=html a.inv")]
    [InlineData("a.inv --format json")]
    [InlineData("-O a.inv")]
    [InlineData("-e 1+1")]
    [InlineData("--bogus a.inv")]
    [InlineData("-lang=sql a.inv")]
    [InlineData("a.inv --kind")]
    public void Parse_Should_Reject_WhatItCannotDo(string commandLine)
    {
        Assert.NotNull(CliArguments.Parse(commandLine.Split(' ')).Error);
    }
}
