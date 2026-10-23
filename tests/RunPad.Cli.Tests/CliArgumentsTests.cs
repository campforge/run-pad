using RunPad.Cli;
using RunPad.Format.RPad;
using Xunit;

namespace RunPad.Cli.Tests;

public class CliArgumentsTests
{
    [Theory]
    [InlineData(new[] { "a.rpad" }, null)]
    [InlineData(new[] { "a.rpad", "Hello", "World" }, new[] { "Hello", "World" })]  // lprun
    [InlineData(new[] { "run", "a.rpad", "x" }, new[] { "x" })]                     // npad's explicit run
    [InlineData(new[] { "a.rpad", "--", "-s", "--" }, new[] { "-s", "--" })]
    [InlineData(new[] { "a.rpad", "x", "--", "-k" }, new[] { "x", "-k" })]
    [InlineData(new[] { "a.rpad", "--" }, new string[0])]
    [InlineData(new[] { "a.rpad", "-unknown", "-" }, new[] { "-unknown", "-" })]
    public void Parse_Should_PassWhatFollowsTheScript_ToTheScript(string[] args, string[]? expected)
    {
        var cli = CliArguments.Parse(args);

        Assert.Null(cli.Error);
        Assert.Equal("a.rpad", cli.ScriptPath);
        Assert.Equal(expected, cli.ScriptArguments);
    }

    [Theory]
    [InlineData(new[] { "-lang=e", "a.rpad" }, RpadKind.Expression, null)]
    [InlineData(new[] { "-lang=Program", "a.rpad" }, RpadKind.Program, null)]
    [InlineData(new[] { "-lang=VBStatements", "a.rpad" }, RpadKind.Statements, RpadLanguage.Vb)]
    [InlineData(new[] { "--kind", "program", "a.rpad" }, RpadKind.Program, null)]
    [InlineData(new[] { "a.rpad", "-k", "s" }, RpadKind.Statements, null)]
    public void Parse_Should_ReadTheKind_InEitherStyle(string[] args, RpadKind kind, RpadLanguage? language)
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
        Assert.Equal(expected, CliArguments.Parse(["--sdk", value, "a.rpad"]).TargetSdk?.ToString());
        Assert.Equal(expected, CliArguments.Parse(["a.rpad", "-s", value]).TargetSdk?.ToString());
        Assert.Equal(expected, CliArguments.Parse([$"--sdk={value}", "a.rpad"]).TargetSdk?.ToString());
    }

    // A missing value must not take the next option (or "--") as the SDK: "--sdk --compile-only a.rpad"
    // would otherwise run the script on the newest SDK instead of only compiling it.
    [Theory]
    [InlineData("--sdk --compile-only a.rpad")]
    [InlineData("-s -compileonly a.rpad")]
    [InlineData("--sdk --help a.rpad")]
    [InlineData("a.rpad -s -- x")]
    [InlineData("--kind --compile-only a.rpad")]
    [InlineData("--sdk= a.rpad")]
    [InlineData("-s= a.rpad")]
    [InlineData("a.rpad --sdk")]
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
        foreach (var args in new[] { new[] { "--sdk", value, "a.rpad" }, ["a.rpad", $"-s={value}"] })
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
        Assert.True(CliArguments.Parse([option, "a.rpad"]).CompileOnly);
    }

    [Fact]
    public void Parse_Should_ReadRefreshConnections_BeforeOrAfterTheScript()
    {
        Assert.True(CliArguments.Parse(["--refresh-connections", "a.rpad"]).RefreshConnections);
        Assert.True(CliArguments.Parse(["a.rpad", "--refresh-connections"]).RefreshConnections);
        Assert.False(CliArguments.Parse(["a.rpad"]).RefreshConnections);
        Assert.Equal(["--refresh-connections"], CliArguments.Parse(["a.rpad", "--", "--refresh-connections"]).ScriptArguments);
    }

    [Theory]
    [InlineData("-nunuget")]
    [InlineData("--no-cache")]
    [InlineData("-b")]
    public void Parse_Should_AcceptCacheOptions_WithNoEffect(string option)
    {
        var cli = CliArguments.Parse([option, "a.rpad"]);

        Assert.Null(cli.Error);
        Assert.Equal("a.rpad", cli.ScriptPath);
    }

    [Theory]
    [InlineData("-format=html a.rpad")]
    [InlineData("a.rpad --format json")]
    [InlineData("-O a.rpad")]
    [InlineData("-e 1+1")]
    [InlineData("--bogus a.rpad")]
    [InlineData("-lang=sql a.rpad")]
    [InlineData("a.rpad --kind")]
    public void Parse_Should_Reject_WhatItCannotDo(string commandLine)
    {
        Assert.NotNull(CliArguments.Parse(commandLine.Split(' ')).Error);
    }
}
