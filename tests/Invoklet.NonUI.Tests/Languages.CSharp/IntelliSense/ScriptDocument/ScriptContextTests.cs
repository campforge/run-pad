using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp.IntelliSense.ScriptDocument;
using Invoklet.NonUI.Tests.Languages.CSharp.IntelliSense.ScriptDocument.Fixtures;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Invoklet.NonUI.Tests.Languages.CSharp.IntelliSense.ScriptDocument;

public sealed class ScriptContextTests
{
    private static MdRunArgumentContract ContractOf(params string[] names)
        => new(names.Select(n => new MdRunArgumentContractMember(n, "string", [], [])).ToList());

    [Fact]
    public void Classify_FilesThatDoNotExist_AreLeftOut()
    {
        // Arrange
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll");

        // Act
        var set = ReferenceSet.Classify([missing], [missing]);

        // Assert
        Assert.Empty(set.All);
    }

    [Fact]
    public void Classify_TheSamePathTwiceOrAsAdditional_IsKeptOnceAndTheAdditionalOneIsNotTheFramework()
    {
        // Arrange
        var folder = Directory.CreateTempSubdirectory().FullName;
        var runtime = Path.Combine(folder, "System.Runtime.dll");
        var other = Path.Combine(folder, "Invoklet.Runtime.dll");
        File.WriteAllText(runtime, "");
        File.WriteAllText(other, "");

        try
        {
            // Act
            var set = ReferenceSet.Classify([runtime, runtime], [other]);

            // Assert
            Assert.Equal([runtime, other], set.All);
            Assert.Equal([runtime], set.DefaultFramework);
            Assert.Equal([other], set.Other);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void UpdateArgumentContract_Members_AreKeptAndAddAGeneratedDocumentToTheProject()
    {
        // Arrange
        using var fixture = new ScriptContextFixture();
        var sut = fixture.CreateSut();
        var documents = fixture.Session.Project.Documents.Count();

        // Act
        sut.UpdateArgumentContract(ContractOf("name"));

        // Assert
        Assert.Equal(["name"], sut.ArgumentContract.Select(m => m.Name));
        Assert.Equal(documents + 1, fixture.Session.Project.Documents.Count());
    }

    [Fact]
    public void UpdateArgumentContract_TheSameContractAgain_ChangesNothingInTheContextVersion()
    {
        // Arrange
        using var fixture = new ScriptContextFixture();
        var sut = fixture.CreateSut();
        sut.UpdateArgumentContract(ContractOf("name"));
        var version = fixture.Session.ContextVersion;

        // Act
        sut.UpdateArgumentContract(ContractOf("name"));

        // Assert
        Assert.Equal(version, fixture.Session.ContextVersion);
    }

    [Fact]
    public void UpdateArgumentContract_NoContract_TakesTheGeneratedDocumentOut()
    {
        // Arrange
        using var fixture = new ScriptContextFixture();
        var sut = fixture.CreateSut();
        var documents = fixture.Session.Project.Documents.Count();
        sut.UpdateArgumentContract(ContractOf("name"));

        // Act
        sut.UpdateArgumentContract(null);

        // Assert
        Assert.Empty(sut.ArgumentContract);
        Assert.Equal(documents, fixture.Session.Project.Documents.Count());
    }

    [Fact]
    public void IsAddedReference_AnythingBeforeTheScriptAddsReferences_IsFalse()
    {
        // Arrange
        using var fixture = new ScriptContextFixture();
        var sut = fixture.CreateSut();

        // Act
        var added = sut.IsAddedReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));

        // Assert
        Assert.False(added);
    }

    [Fact]
    public void ApplyExecutionContext_CancelledBeforeItStarts_Throws()
    {
        // Arrange
        using var fixture = new ScriptContextFixture();
        var sut = fixture.CreateSut();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // Act
        var thrown = Record.Exception(() => sut.ApplyExecutionContext(new(), cancelled.Token));

        // Assert
        Assert.IsType<OperationCanceledException>(thrown);
    }
}
