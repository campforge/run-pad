using Invoklet.Languages.CSharp;
using Xunit;

namespace Invoklet.NonUI.Tests;

// CSharpReferenceService.GetDefault()'s "System.*.dll" glob used to match native
// companion libraries too (e.g. Windows' System.IO.Compression.Native.dll), which aren't valid
// managed assemblies and blow up MetadataReference.CreateFromFile with CS0009. Fixed by checking
// the PE header instead of trusting the filename — these tests exercise that check directly,
// since the actual native file from the reported bug only exists on Windows.
public class CSharpReferenceServiceTests
{
    [Fact]
    public void IsManagedAssembly_Should_ReturnTrue_ForARealManagedAssembly()
    {
        var realDll = typeof(object).Assembly.Location;

        Assert.True(CSharpReferenceService.IsManagedAssembly(realDll));
    }

    [Fact]
    public void IsManagedAssembly_Should_ReturnFalse_ForANativeLookingFileWithNoManagedMetadata()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), $"System.Fake.Native.{Guid.NewGuid():N}.dll");
        try
        {
            // A minimal "MZ" DOS header with no PE/CLI header after it — enough to look like a
            // binary file on disk, nowhere near enough to be a managed assembly.
            File.WriteAllBytes(tmpFile, "MZ-this-is-not-a-real-PE-file"u8.ToArray());

            Assert.False(CSharpReferenceService.IsManagedAssembly(tmpFile));
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void GetDefault_Should_OnlyReturnFilesThatActuallyExist()
    {
        var referenceService = new CSharpReferenceService();

        var refs = referenceService.GetDefault();

        Assert.NotEmpty(refs);
        Assert.All(refs, path => Assert.True(File.Exists(path)));
    }
}
