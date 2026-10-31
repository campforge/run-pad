using Invoklet.NonUI.Tests.Languages.DotnetCommon.Fixtures;
using Xunit;

namespace Invoklet.NonUI.Tests.Languages.DotnetCommon;

// The key of the Secret Store comes from the system keychain, which may ask the user to unlock it: the host reads it only for a script that can
// ask for a secret.
[Trait("Category", "Integration")]
[Trait("Category", "RealBuild")]
public sealed class CompiledDotnetLanguageHostSecretKeyTests
{
    [Fact]
    public async Task RunAsync_AScriptThatNeverMentionsASecret_NeverExportsTheKey()
    {
        // Arrange
        using var fixture = new CompiledDotnetLanguageHostFixture();
        var sut = fixture.CreateSut();

        // Act
        await sut.RunAsync("Console.WriteLine(1);", (_, _) => { }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, fixture.SecretKeyExports);
    }

    [Theory]
    [InlineData("var p = Util.GetSecret(\"db\");")]
    [InlineData("Util.SetSecret(\"db\", \"x\");")]
    public async Task RunAsync_AScriptThatAsksForASecret_ExportsTheKeyOnceBeforeItStarts(string code)
    {
        // Arrange
        using var fixture = new CompiledDotnetLanguageHostFixture();
        var sut = fixture.CreateSut();

        // Act
        await sut.RunAsync(code, (_, _) => { }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, fixture.SecretKeyExports);
    }

    [Fact]
    public async Task RunAsync_ConnectionsThatReadASecret_ExportTheKeyEvenThoughTheScriptDoesNot()
    {
        // Arrange
        using var fixture = new CompiledDotnetLanguageHostFixture();
        var sut = fixture.CreateSut(connectionSource: "var token = Util.GetSecret(\"api\");");

        // Act
        await sut.RunAsync("Console.WriteLine(1);", (_, _) => { }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, fixture.SecretKeyExports);
    }

    [Fact]
    public async Task RunAsync_NothingToExportWith_AsksNobodyAndStillRuns()
    {
        // Arrange
        using var fixture = new CompiledDotnetLanguageHostFixture();
        var sut = fixture.CreateSut(withExporter: false);

        // Act
        var result = await sut.RunAsync("Util.GetSecret(\"db\");", (_, _) => { }, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.Succeeded);
        Assert.Equal(0, fixture.SecretKeyExports);
    }
}
