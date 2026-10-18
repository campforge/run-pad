using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using RunPad.Avalonia.HtmlRenderer;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

public class HtmlOutputClipboardTests
{
    private static RawInputModifiers Modifiers(KeyModifiers modifiers)
        => (modifiers.HasFlag(KeyModifiers.Control) ? RawInputModifiers.Control : 0)
            | (modifiers.HasFlag(KeyModifiers.Meta) ? RawInputModifiers.Meta : 0)
            | (modifiers.HasFlag(KeyModifiers.Alt) ? RawInputModifiers.Alt : 0)
            | (modifiers.HasFlag(KeyModifiers.Shift) ? RawInputModifiers.Shift : 0);

    [AvaloniaFact]
    public async Task FocusedResults_Should_CopySelection_WithPlatformShortcut()
    {
        var output = new HtmlPanel { Text = "<div>Selected result text</div>" };
        var window = new Window { Width = 600, Height = 300, Content = output };
        window.Show();
        try
        {
            window.UpdateLayout();
            output.Focus();
            var hotkeys = Application.Current!.PlatformSettings!.HotkeyConfiguration;
            var select = hotkeys.SelectAll.First();
            var copy = hotkeys.Copy.First();
            window.KeyPressQwerty(PhysicalKey.A, Modifiers(select.KeyModifiers));
            Assert.Contains("Selected result text", output.SelectedText);
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText("sentinel"));
            await window.Clipboard!.SetDataAsync(transfer);
            window.KeyPressQwerty(PhysicalKey.C, Modifiers(copy.KeyModifiers));
            string? text = null;
            for (var i = 0; i < 100; i++)
            {
                var data = await window.Clipboard.TryGetDataAsync();
                text = data is null ? null : await data.TryGetTextAsync();
                if (text?.Contains("Selected result text") == true) break;
                await Task.Delay(10);
            }
            Assert.Contains("Selected result text", text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CopyShortcut_Should_NotBubblePastResults_WhilePlainLettersRemainUnhandled()
    {
        var output = new HtmlPanel { Text = "<div>Result</div>" };
        var window = new Window { Content = output };
        window.Show();
        try
        {
            var bubbled = 0;
            window.KeyDown += (_, _) => bubbled++;
            output.Focus();
            var copy = Application.Current!.PlatformSettings!.HotkeyConfiguration.Copy.First();
            window.KeyPressQwerty(PhysicalKey.C, Modifiers(copy.KeyModifiers));
            Assert.Equal(0, bubbled);
            window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.None);
            Assert.Equal(1, bubbled);
        }
        finally { window.Close(); }
    }
}
