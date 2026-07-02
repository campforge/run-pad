using System.Collections;
using DotPad.Build.Core;

namespace DotPad.Build.Core.Sdk;

public sealed class ListSdks : IEnumerable<SdkInfoModel>
{
    private const string Command = "dotnet --list-sdks";

    private readonly List<SdkInfoModel> _sdks;

    public ListSdks() => _sdks = Parse().ToList();

    public IEnumerator<SdkInfoModel> GetEnumerator() => _sdks.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static IEnumerable<SdkInfoModel> Parse()
    {
        foreach (var line in Utils.Shell(Command))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var spaceIdx = trimmed.IndexOf(' ');
            if (spaceIdx < 0) continue;

            var name = trimmed[..spaceIdx];

            var openBracket = trimmed.IndexOf('[', spaceIdx);
            var closeBracket = trimmed.LastIndexOf(']');
            if (openBracket < 0 || closeBracket <= openBracket) continue;

            var path = trimmed[(openBracket + 1)..closeBracket];
            yield return new SdkInfoModel(name, path);
        }
    }
}
