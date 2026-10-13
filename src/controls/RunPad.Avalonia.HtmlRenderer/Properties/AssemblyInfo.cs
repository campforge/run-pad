using System.Runtime.CompilerServices;
using Avalonia.Metadata;

[assembly: XmlnsDefinition("https://github.com/avaloniaui", "TheArtOfDev.HtmlRenderer.Avalonia")]
[assembly: InternalsVisibleTo("RunPad.Avalonia.HtmlRenderer.Tests")]
// HtmlControlInvalidationTests needs internal ControlAdapter/AvaloniaAdapter/
// HtmlControl.RenderCacheForTests — constructing an HtmlControl needs a live Avalonia platform
// (FontManager.Current), which RunPad.Avalonia.HtmlRenderer.Tests doesn't set up (its own tests are
// deliberately plain-xunit, no Avalonia.Headless dependency); RunPad.Runtime.UI.Tests already has
// [AvaloniaFact]/headless infra for exactly this kind of test (see OutputHtmlViewTests).
[assembly: InternalsVisibleTo("RunPad.Runtime.UI.Tests")]
