using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Invoklet.Services.Preferences;

namespace Invoklet.Windows.Healpers;

// The single description of MainWindow's menu. The
// in-window Menu (Windows/Linux) and the macOS system menu bar (NativeMenu) are both projected from
// the same tree, so the two can't drift apart again. Headers keep the in-window access-key
// underscores; the NativeMenu projection strips them.
internal abstract record MainMenuEntry;

internal sealed record MainMenuSeparator : MainMenuEntry;

internal sealed record MainMenuGroup(string Header, IReadOnlyList<MainMenuEntry> Items) : MainMenuEntry;

// Key: the bindable command whose key Gesture shows (null for keys that are not bindable, such as
// the platform's Copy/Paste).
internal sealed record MainMenuCommand(string Header, ICommand Command, KeyGesture? Gesture = null, KeyCommand? Key = null, object? Parameter = null)
    : MainMenuEntry;

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
            CommandParameter = command.Parameter,
            InputGesture = command.Gesture,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(entry), entry, null),
    };

    private static MenuItem WithItems(MenuItem item, IEnumerable<MainMenuEntry> children)
    {
        foreach (var child in ToMenuItems(children)) item.Items.Add(child);
        return item;
    }

    // NativeMenuItem.Gesture is more than a hint on macOS: it becomes the item's key equivalent, and
    // AppKit offers a keypress to the active window's menu bar before the window sees it. A matching
    // item runs its command and the window never gets that keypress, so MainWindow.OnWindowKeyDown
    // does not run the same action a second time. The main window's menu bar is only active while
    // that window is: other windows keep their own keys.
    public static NativeMenu ToNativeMenu(IEnumerable<MainMenuEntry> entries)
    {
        var menu = new NativeMenu();
        foreach (var entry in entries) menu.Add(ToNativeMenuItem(entry));
        return menu;
    }

    internal static NativeMenuItemBase ToNativeMenuItem(MainMenuEntry entry) => entry switch
    {
        MainMenuSeparator => new NativeMenuItemSeparator(),
        MainMenuGroup group => new NativeMenuItem(StripAccessKey(group.Header)) { Menu = ToNativeMenu(group.Items) },
        MainMenuCommand command => new NativeMenuItem(StripAccessKey(command.Header))
        {
            Command = command.Command,
            CommandParameter = command.Parameter,
            Gesture = command.Gesture,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(entry), entry, null),
    };

    // ── Rebinding ─────────────────────────────────────────────────────────────

    /// <summary>The same entries with the current key of every bindable command; headers and command
    /// instances are kept.</summary>
    public static IReadOnlyList<MainMenuGroup> WithGestures(
        IReadOnlyList<MainMenuGroup> groups, Func<KeyCommand, KeyGesture?> gestureFor)
        => groups.Select(g => (MainMenuGroup)WithGestures(g, gestureFor)).ToList();

    private static MainMenuEntry WithGestures(MainMenuEntry entry, Func<KeyCommand, KeyGesture?> gestureFor) => entry switch
    {
        MainMenuGroup group => group with { Items = group.Items.Select(e => WithGestures(e, gestureFor)).ToList() },
        MainMenuCommand { Key: { } key } command => command with { Gesture = gestureFor(key) },
        _ => entry,
    };

    /// <summary>Shows <paramref name="entries"/>' keys on an in-window menu projected from the same
    /// tree, changing its items in place.</summary>
    public static void UpdateGestures(IReadOnlyList<MainMenuEntry> entries, ItemCollection items)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            switch (entries[i], items[i])
            {
                case (MainMenuGroup group, MenuItem item):
                    UpdateGestures(group.Items, item.Items);
                    break;
                case (MainMenuCommand command, MenuItem item):
                    item.InputGesture = command.Gesture;
                    break;
            }
        }
    }

    /// <summary>The same for a native menu; the macOS exporter follows the changed items.</summary>
    public static void UpdateGestures(IReadOnlyList<MainMenuEntry> entries, NativeMenu menu)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            switch (entries[i], menu.Items[i])
            {
                case (MainMenuGroup group, NativeMenuItem { Menu: { } submenu }):
                    UpdateGestures(group.Items, submenu);
                    break;
                case (MainMenuCommand command, NativeMenuItem item):
                    item.Gesture = command.Gesture;
                    break;
            }
        }
    }

    public static string StripAccessKey(string header) => header.Replace("__", "\0").Replace("_", "").Replace("\0", "_");
}
