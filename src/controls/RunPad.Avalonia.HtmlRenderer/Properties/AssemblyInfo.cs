using System.Runtime.CompilerServices;
using Avalonia.Metadata;

[assembly: XmlnsDefinition("https://github.com/avaloniaui", "TheArtOfDev.HtmlRenderer.Avalonia")]
[assembly: InternalsVisibleTo("DotPad.Avalonia.HtmlRenderer.Tests")]
// TODO.md #108: HtmlControlInvalidationTests needs internal ControlAdapter/AvaloniaAdapter/
// HtmlControl.RenderCacheForTests — constructing an HtmlControl needs a live Avalonia platform
// (FontManager.Current), which DotPad.Avalonia.HtmlRenderer.Tests doesn't set up (its own tests are
// deliberately plain-xunit, no Avalonia.Headless dependency); DotPad.Runtime.UI.Tests already has
// [AvaloniaFact]/headless infra for exactly this kind of test (see OutputHtmlViewTests).
[assembly: InternalsVisibleTo("DotPad.Runtime.UI.Tests")]
