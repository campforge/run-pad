using Invoklet.Build.Core.Secrets;

namespace Invoklet.NonUI.Tests.App.Services.Secrets.Fixtures;

// A keychain in memory that counts how often it was read: the tests are about whether the keychain is asked at all.
internal sealed class CountingKeySource : ISecretKeySource
{
    private byte[]? _key;

    public int FindCalls { get; private set; }

    public byte[]? Find()
    {
        FindCalls++;
        return _key;
    }

    public byte[] GetOrCreate() => _key ??= new byte[32].Select((_, i) => (byte)(i + 1)).ToArray();
}
