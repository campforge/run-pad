using System.Globalization;
using System.Runtime.CompilerServices;

namespace Invoklet.IntegrationTests;

// EditorTabItemControlCodeFixApplyTests/EditorTabItemControlCodeFixSettingsTests drive a real
// CSharpIntelliSenseService and assert on Roslyn CodeAction titles by matching English substrings
// (e.g. "using", "formatting"). Roslyn's own resources are localized via satellite assemblies keyed
// off CurrentUICulture, so those assertions silently break on any non-English OS locale (reproduced
// on a ru-RU dev machine). Forcing InvariantCulture here falls back to Roslyn's neutral (English)
// resources regardless of the host OS's locale, matching what CI's English-locale runners already do
// implicitly. See Invoklet.NonUI.Tests.TestCultureSetup for the same fix on the other assembly with
// culture-sensitive Roslyn assertions.
internal static class TestCultureSetup
{
    [ModuleInitializer]
    internal static void ForceInvariantCulture()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
    }
}
