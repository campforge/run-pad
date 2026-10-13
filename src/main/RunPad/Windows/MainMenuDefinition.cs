using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;

namespace RunPad.Windows;

// The single description of MainWindow's menu. The
// in-window Menu (Windows/Linux) and the macOS system menu bar (NativeMenu) are both projected from
// the same tree, so the two can't drift apart again. Headers keep the in-window access-key
// underscores; the NativeMenu projection strips them.
internal abstract record MainMenuEntry;

internal sealed record MainMenuSeparator : MainMenuEntry;

internal sealed record MainMenuGroup(string Header, IReadOnlyList<MainMenuEntry> Items) : MainMenuEntry;

internal sealed record MainMenuCommand(string Header, ICommand Command, KeyGesture? Gesture = null) : MainMenuEntry;

internal static class MainMenuDefinition
{
    public static IEnumerable<Control> ToMenuItems(IEnumerable<MainMenuEntry> entries)
        => entries.Select(ToMenuItem);

    private static Control ToMenuItem(MainMenuEntry entry) => entry switch
    {
        MainMenuSeparator => new Separator(),
        MainMenuGroup group => WithItems(new MenuItem { Header = group.Header }, group.Items),
        MainMenuCommand command => new MenuItem
        {
            Header = command.Header,
            Command = command.Command,
            InputGesture = command.Gesture,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(entry), entry, null),
    };

    private static MenuItem WithItems(MenuItem item, IEnumerable<MainMenuEntry> children)
    {
        foreach (var child in ToMenuItems(children)) item.Items.Add(child);
        return item;
    }

    // No NativeMenuItem.Gesture: KeyboardSettings gestures are Ctrl-based and already dispatched by
    // MainWindow.OnWindowKeyDown; registering them as menu key equivalents too would risk running an
    // action twice per keypress.
    public static NativeMenu ToNativeMenu(IEnumerable<MainMenuEntry> entries)
    {
        var menu = new NativeMenu();
        foreach (var entry in entries) menu.Add(ToNativeMenuItem(entry));
        return menu;
    }

    private static NativeMenuItemBase ToNativeMenuItem(MainMenuEntry entry) => entry switch
    {
        MainMenuSeparator => new NativeMenuItemSeparator(),
        MainMenuGroup group => new NativeMenuItem(StripAccessKey(group.Header)) { Menu = ToNativeMenu(group.Items) },
        MainMenuCommand command => new NativeMenuItem(StripAccessKey(command.Header)) { Command = command.Command },
        _ => throw new ArgumentOutOfRangeException(nameof(entry), entry, null),
    };

    public static string StripAccessKey(string header) => header.Replace("_", "");
}
