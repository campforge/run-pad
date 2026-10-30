using System.Text;
using Invoklet.Services.TextFiles;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// The text editor window must save a file back in the
// encoding and with the line breaks it was read with: decoding then re-encoding an unchanged file has to
// give back the very same bytes.
public class TextFileCodecTests
{
    public static TheoryData<string, byte[]> Files() => new()
    {
        { "UTF-8, CRLF", Encoding.UTF8.GetBytes("{\r\n  \"name\": \"Привет\"\r\n}\r\n") },
        { "UTF-8 with BOM, LF", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("key: value\nlist:\n  - ü\n")] },
        { "UTF-16 LE", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("<a>ä</a>\r\n")] },
        { "UTF-16 BE", [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("[s]\nk=v\n")] },
        { "UTF-32 BE", [0x00, 0x00, 0xFE, 0xFF, .. new UTF32Encoding(bigEndian: true, byteOrderMark: true).GetBytes("utf32-be\n")[4..]] },
        { "UTF-32 LE", [0xFF, 0xFE, 0x00, 0x00, .. new UTF32Encoding(false, false).GetBytes("x\ny")] },
        { "mixed line breaks", Encoding.UTF8.GetBytes("a\r\nb\nc\rd") },
        { "empty", [] },
        // not UTF-8 ("Привет" in windows-1251): decoded with the fallback code page, bytes kept as they are
        { "legacy code page", [0xCF, 0xF0, 0xE8, 0xE2, 0xE5, 0xF2, 0x0D, 0x0A] },
    };

    [Theory]
    [MemberData(nameof(Files))]
    public void AnUnchangedFile_Should_BeWrittenBack_ByteForByte(string _, byte[] bytes)
    {
        var content = TextFileCodec.Decode(bytes);

        Assert.Equal(bytes, TextFileCodec.Encode(content.Text, content.Encoding, content.HasBom));
    }

    [Theory]
    [InlineData(new byte[] { 0x61 }, "UTF-8")]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x61 }, "UTF-8 with BOM")]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x61, 0x00 }, "UTF-16 LE")]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x61 }, "UTF-16 BE")]
    [InlineData(new byte[] { 0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, 0x61 }, "UTF-32 BE")]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x61, 0x00, 0x00, 0x00 }, "UTF-32 LE")]
    public void TheEncoding_Should_BeNamed_ForTheStatusBar(byte[] bytes, string label)
    {
        Assert.Equal(label, TextFileCodec.Decode(bytes).EncodingLabel);
    }

    [Fact]
    public void ANonUtf8File_Should_NotBeLabeledUtf8_AndDecodeWithoutReplacementCharacters()
    {
        var content = TextFileCodec.Decode([0xCF, 0xF0, 0xE8, 0xE2, 0xE5, 0xF2]);

        Assert.DoesNotContain("UTF", content.EncodingLabel);
        Assert.DoesNotContain('�', content.Text);
    }

    [Theory]
    [InlineData("a\r\nb\r\n", "CRLF")]
    [InlineData("a\nb\n", "LF")]
    [InlineData("a\rb", "CR")]
    [InlineData("a\r\nb\n", "Mixed")]
    [InlineData("one line", "")]
    public void NewLineLabel_Should_NameTheFilesLineBreaks(string text, string label)
    {
        Assert.Equal(label, TextFileCodec.NewLineLabel(text));
    }

    [Fact]
    public async Task SaveAsync_Should_WriteTheEncoding_AndLeaveNoTemporaryFile()
    {
        var folder = Directory.CreateTempSubdirectory("Invoklet-text-codec").FullName;
        try
        {
            var path = Path.Combine(folder, "settings.json");
            await File.WriteAllBytesAsync(path, [0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}'], TestContext.Current.CancellationToken);
            var content = await TextFileCodec.LoadAsync(path, TestContext.Current.CancellationToken);

            await TextFileCodec.SaveAsync(path, "{\r\n}", content.Encoding, content.HasBom, TestContext.Current.CancellationToken);

            Assert.Equal([0xEF, 0xBB, 0xBF, (byte)'{', 0x0D, 0x0A, (byte)'}'], await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal(["settings.json"], Directory.GetFiles(folder).Select(Path.GetFileName));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task AFailedSave_Should_Throw_AndLeaveTheOriginalFileUntouched()
    {
        var folder = Directory.CreateTempSubdirectory("Invoklet-text-codec").FullName;
        var path = Path.Combine(folder, "readonly.txt");
        try
        {
            await File.WriteAllTextAsync(path, "original", TestContext.Current.CancellationToken);
            File.SetAttributes(path, FileAttributes.ReadOnly);
            // a read-only target can't be replaced on Windows; on Unix a read-only FOLDER stops the temp file
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                TextFileCodec.SaveAsync(path, "changed", new UTF8Encoding(false), hasBom: false, TestContext.Current.CancellationToken));

            Assert.Equal("original", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal(["readonly.txt"], Directory.GetFiles(folder).Select(Path.GetFileName));
        }
        finally
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(folder, recursive: true);
        }
    }
}
