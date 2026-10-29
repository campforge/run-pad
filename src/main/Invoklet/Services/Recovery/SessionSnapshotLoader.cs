using System.Text;
using Invoklet.Format.MdRun;
using Invoklet.UserControls.Models;

namespace Invoklet.Services.Recovery;

public interface ISessionSnapshotLoader
{
    Task<IReadOnlyList<RecoveryTab>> LoadAsync(IReadOnlyList<RecoveryTab> tabs);
}

/// <summary>Revalidates named clean tabs against disk before the UI recreates them.</summary>
public sealed class DiskSessionSnapshotLoader : ISessionSnapshotLoader
{
    public async Task<IReadOnlyList<RecoveryTab>> LoadAsync(IReadOnlyList<RecoveryTab> tabs)
    {
        return await Task.Run(() => tabs.Select(Load).ToList()).ConfigureAwait(false);
    }

    private static RecoveryTab Load(RecoveryTab tab)
    {
        if (tab.IsDirty || tab.FilePath is null) return tab with { IsDirty = true };

        try
        {
            var bytes = File.ReadAllBytes(tab.FilePath);
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
            var text = reader.ReadToEnd();
            var parsed = MdRunParser.Parse(text);
            return tab with
            {
                EditorText = parsed.Document?.RunCode ?? text,
                Document = parsed.Document?.CaptureSnapshot(),
                ParseError = parsed.Error,
                BaselineKnown = true,
                DiskHash = ScriptFileState.Of(bytes).Hash,
                MetadataBaseline = null,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return tab with { IsDirty = true };
        }
    }
}
