using System.Globalization;
using System.Runtime.CompilerServices;

namespace RunPad.Runtime.UI.Tests;

// Several tests here (CSharpIntelliSenseAnalyzersTests, CSharpIntelliSenseRefactoringsTests) assert
// on real Microsoft.CodeAnalysis.CSharp.Features CodeAction/diagnostic titles by matching English
// substrings (e.g. "expression body", "accessibility", "readonly"). Roslyn's own resources are
// localized via satellite assemblies keyed off CurrentUICulture, so those assertions silently break
// on any non-English OS locale (reproduced on a ru-RU dev machine — titles came back in Russian).
// Forcing InvariantCulture here falls back to Roslyn's neutral (English) resources regardless of the
// host OS's locale, matching what CI's English-locale runners already do implicitly.
internal static class TestCultureSetup
{
    [ModuleInitializer]
    internal static void ForceInvariantCulture()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
    }
}
