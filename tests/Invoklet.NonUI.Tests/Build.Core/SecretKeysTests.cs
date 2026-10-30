using Invoklet.Build.Core.Secrets;
using Xunit;

namespace Invoklet.NonUI.Tests.Build.Core;

public sealed class SecretKeysTests
{
    [Theory]
    [InlineData("var token = Util.GetSecret(\"api\");", null, true)]
    [InlineData("Util.SetSecret(\"api\", value);", null, true)]
    [InlineData("var a = GetSecret(\"x\");", null, true)]
    [InlineData("Console.WriteLine(1);", "var p = Util.GetSecret(\"db\");", true)]
    [InlineData("Console.WriteLine(1);", null, false)]
    [InlineData("Console.WriteLine(1);", "public static class Connections { }", false)]
    [InlineData("", "", false)]
    [InlineData(null, null, false)]
    [InlineData("// the getsecret in lower case is not a call", null, false)]
    public void ScriptMayUseSecrets_TheTextOfTheScriptAndItsConnections_SaysWhetherItCanAskForASecret(string? code, string? connections, bool expected)
        => Assert.Equal(expected, SecretKeys.ScriptMayUseSecrets(code, connections));
}
