using System.Security.Cryptography;

namespace RunPad.UserControls.Models;

/// <summary>
/// What a script file held when RunPad last read or wrote it: the SHA-256 of its exact bytes, or
/// null when there was no file. Content, not the time stamp, decides whether it changed since —
/// an atomic replace or a delete and recreate can keep the time and the length.
/// </summary>
public sealed record ScriptFileState(byte[]? Hash)
{
    public static ScriptFileState Of(ReadOnlySpan<byte> bytes) => new(SHA256.HashData(bytes));

    public static ScriptFileState Absent { get; } = new((byte[]?)null);

    /// <summary>
    /// Whether writing <paramref name="path"/> now would replace content this state doesn't know:
    /// the file differs from what was read or written, or appeared where there was none. A file
    /// that has gone since can be written again — nothing on disk is lost by recreating it.
    /// </summary>
    public bool ConflictsWith(string path)
    {
        if (!File.Exists(path)) return false;
        if (Hash is null) return true;
        return !SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(Hash);
    }
}
