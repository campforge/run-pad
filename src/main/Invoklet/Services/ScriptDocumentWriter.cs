using System.Text;
using Invoklet.Format.MdRun;

namespace Invoklet.Services;

/// <summary>What was made of a script to write: the bytes of its file, and the ID of the document in them.</summary>
/// <param name="DocumentId">The ID written into the file; null for a file that is not a document.</param>
internal sealed record SerializedScript(byte[] Bytes, Guid? DocumentId);

/// <summary>
/// The bytes of a script's file: a document with its header, a file that is no document as it is. Nothing is
/// written here and nothing of the document changes but its code: the ID and the references that go to
/// another file stay the document's own until the tab really moves there.
/// </summary>
internal sealed class ScriptDocumentWriter
{
    /// <param name="document">The tab's document; null for a file that is not one, which is written verbatim.</param>
    /// <param name="text">What the editor holds.</param>
    /// <param name="path">The file to be written.</param>
    /// <param name="ownPath">The tab's own file; null for an untitled tab.</param>
    /// <param name="referenceAnchorPath">The file the document's relative references are relative to.</param>
    internal SerializedScript Serialize(MdRunDocument? document, string text, string path, string? ownPath, string? referenceAnchorPath)
    {
        if (document is not { } rpad)
        {
            // As File.WriteAllText(path, text) did: UTF-8 without a byte order mark.
            return new SerializedScript(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text), null);
        }

        rpad.RunCode = text;
        // The document's identity: its own file keeps it, and a file written before IDs gets one
        // here. Another file (Save As, Duplicate) is a separate document with a new ID, which the
        // tab takes only once it really moved there (OnFilePathChanged) — Duplicate never moves it,
        // and a failed Save As leaves it as it was.
        var id = rpad.FrontMatter.Id;
        if (string.Equals(path, ownPath, StringComparison.Ordinal))
            id = rpad.FrontMatter.Id ??= Guid.NewGuid();
        else
            rpad.FrontMatter.Id = Guid.NewGuid();
        // A script written to another folder (Save As) gets its relative references rewritten so
        // they still reach the same files; the tab adopts them only once it really moved there
        // (OnFilePathChanged) — a cancelled or failed Save As leaves its references as they were.
        var references = rpad.FrontMatter.References;
        if (references is not null && RebasedReferences(references, referenceAnchorPath, path) is { } rebased)
            rpad.FrontMatter.References = rebased;
        Guid? written;
        try
        {
            // As File.WriteAllText(path, text, Encoding.UTF8) did: with the UTF-8 byte order mark.
            var bytes = (byte[])[.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(MdRunWriter.Write(rpad))];
            written = rpad.FrontMatter.Id;
            return new SerializedScript(bytes, written);
        }
        finally
        {
            rpad.FrontMatter.References = references;
            rpad.FrontMatter.Id = id;
        }
    }

    // The references as written for the script saved at newPath instead of oldPath (its own file, or
    // the one an untitled copy was made from), or null if nothing changes (same folder, an untitled
    // tab whose relative references had no folder yet, or nothing relative).
    internal List<string>? RebasedReferences(List<string> references, string? oldPath, string newPath)
    {
        if (oldPath is null) return null;
        var oldDirectory = Path.GetDirectoryName(Path.GetFullPath(oldPath))!;
        var newDirectory = Path.GetDirectoryName(Path.GetFullPath(newPath))!;
        var rebased = references.Select(r => ReferencePath.Rebase(r, oldDirectory, newDirectory, OperatingSystem.IsWindows())).ToList();
        return rebased.SequenceEqual(references, StringComparer.Ordinal) ? null : rebased;
    }
}
