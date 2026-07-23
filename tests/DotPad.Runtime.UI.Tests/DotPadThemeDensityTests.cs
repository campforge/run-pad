using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DotPad.Avalonia.Themes;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #113 — live user report ("слишком крупный шрифт и отступы", LINQPad 4 shown as a denser
// reference): app-wide compact overrides for stock FluentTheme controls, added to DotPadTheme.axaml
// (MenuItem/ComboBox/Button/CheckBox/RadioButton/TextBox/NumericUpDown/ToolTip). These build a real
// Window, attach a real DotPadTheme() to its own Styles (TestAppBuilder deliberately doesn't load it
// app-wide — see its own doc comment), and read back the ACTUAL resolved property values on real
// controls after a layout pass — proof each style rule really applies, not just that the XAML parses.
// This can't confirm the resulting visual density matches the (unavailable, from an earlier live
// report) reference screenshot — no way to render/compare pixels in this headless environment — only
// that each override takes effect as written.
public class DotPadThemeDensityTests
{
    private static Window CreateWindowWith(Control content)
    {
        var window = new Window { Content = content };
        window.Styles.Add(new DotPadTheme());
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void MenuItem_Should_UseTheCompactFontSizePaddingAndMinHeight()
    {
        var item = new MenuItem { Header = "Test" };
        CreateWindowWith(item);

        Assert.Equal(13, item.FontSize);
        Assert.Equal(new Thickness(8, 4), item.Padding);
        Assert.Equal(26, item.MinHeight);
    }

    [AvaloniaFact]
    public void ComboBox_Should_UseTheCompactFontSizeAndMinHeight()
    {
        var combo = new ComboBox();
        CreateWindowWith(combo);

        Assert.Equal(13, combo.FontSize);
        Assert.Equal(26, combo.MinHeight);
    }

    [AvaloniaFact]
    public void Button_Should_UseTheCompactFontSizeAndPadding()
    {
        var button = new Button();
        CreateWindowWith(button);

        Assert.Equal(13, button.FontSize);
        Assert.Equal(new Thickness(10, 5), button.Padding);
    }

    [AvaloniaFact]
    public void CheckBox_Should_UseTheCompactFontSize()
    {
        var checkBox = new CheckBox();
        CreateWindowWith(checkBox);

        Assert.Equal(13, checkBox.FontSize);
    }

    [AvaloniaFact]
    public void RadioButton_Should_UseTheCompactFontSize()
    {
        var radio = new RadioButton();
        CreateWindowWith(radio);

        Assert.Equal(13, radio.FontSize);
    }

    [AvaloniaFact]
    public void TextBox_Should_UseTheCompactFontSizeAndPadding()
    {
        var textBox = new TextBox();
        CreateWindowWith(textBox);

        Assert.Equal(13, textBox.FontSize);
        Assert.Equal(new Thickness(6, 4), textBox.Padding);
    }

    [AvaloniaFact]
    public void NumericUpDown_Should_UseTheCompactFontSizeAndMinHeight()
    {
        var numeric = new NumericUpDown();
        CreateWindowWith(numeric);

        Assert.Equal(13, numeric.FontSize);
        Assert.Equal(26, numeric.MinHeight);
    }

    [AvaloniaFact]
    public void ToolTip_Should_UseTheCompactFontSizeAndPadding()
    {
        // ToolTip's own popup content isn't part of the visible-window visual tree until shown by a
        // real hover (which headless input doesn't simulate) — constructed directly as the window's
        // content instead, matching how MenuItem/ComboBox above are checked; ToolTip is itself a
        // ContentControl, so style application doesn't require it to actually be inside a Popup.
        var toolTip = new ToolTip { Content = "hint" };
        CreateWindowWith(toolTip);

        Assert.Equal(12, toolTip.FontSize);
        Assert.Equal(new Thickness(6, 4), toolTip.Padding);
    }
}
