using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Connectors.Http;
using RunPad.Contracts.Connections;
using RunPad.Format.RPad;
using RunPad.Services.Connections;
using RunPad.Services.Secrets;
using RunPad.TemplatedControls;
using RunPad.Windows;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

public class WindowDesignRegressionTests
{
    internal static Mock<ISecretStore> Secrets()
    {
        var secrets = new Mock<ISecretStore>();
        secrets.Setup(s => s.FilePath).Returns("/sample/secrets.json");
        secrets.Setup(s => s.LoadSnapshot()).Returns(new SecretStoreSnapshot([new Secret("token", "synthetic-first"), new Secret("other", "synthetic-second")], "v1"));
        return secrets;
    }

    internal static (ConnectionsWindow Window, ConnectionsWindowViewModel Vm) Connections()
    {
        var store = new Mock<IConnectionStore>();
        store.Setup(s => s.FilePath).Returns("/sample/connections.json");
        store.Setup(s => s.LoadSnapshot()).Returns(new ConnectionStoreSnapshot([new RpadConnection
        {
            Name = "billing-api", Driver = "http", Params = new()
            {
                ["baseAddress"] = RpadValue.Of("https://example.test/"),
                ["headers"] = RpadValue.Map([new("Authorization", RpadValue.Of("Bearer ${secret:token}"))]),
            },
        }], "v1"));
        var drivers = new ConnectionDriverRegistry();
        drivers.Register(new HttpConnectionDriver());
        var vm = new ConnectionsWindowViewModel(store.Object, drivers, Secrets().Object);
        return (new ConnectionsWindow(vm), vm);
    }

    [AvaloniaFact]
    public void HiddenSecrets_Should_NotAppearInTheAutomationValue_AndSelectionShouldHideAgain()
    {
        var vm = new SecretStoreWindowViewModel(Secrets().Object);
        var window = new SecretStoreWindow(vm);
        window.Show();
        try
        {
            var box = window.FindControl<TextBox>("ValueBox")!;
            var provider = Assert.IsAssignableFrom<IValueProvider>(ControlAutomationPeer.CreatePeerForElement(box));
            Assert.Equal("synthetic-first", box.Text);
            Assert.True(string.IsNullOrEmpty(provider.Value));
            vm.IsValueRevealed = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("synthetic-first", provider.Value);
            bool? maskedWhenSelectionChanges = null;
            box.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty && e.NewValue is "synthetic-second")
                    maskedWhenSelectionChanges = box.PasswordChar != '\0';
            };
            vm.SelectedSecret = vm.Secrets[1];
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("synthetic-second", box.Text);
            Assert.True(maskedWhenSelectionChanges);
            Assert.True(string.IsNullOrEmpty(provider.Value));
            provider.SetValue("synthetic-edited");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("synthetic-edited", vm.SelectedSecret.Value);
            Assert.True(string.IsNullOrEmpty(provider.Value));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(640)]
    [InlineData(820)]
    public void LongTestResults_Should_WrapInsideTheWindow(int width)
    {
        var (window, vm) = Connections();
        window.Width = width;
        vm.SelectedConnection!.TestResult = string.Join(" ", Enumerable.Repeat("A long network error with endpoint details.", 8));
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var result = window.FindControl<SelectableTextBlock>("TestResultText")!;
            Assert.True(result.Bounds.Width <= width - 40, $"Result is {result.Bounds.Width} wide in a {width} window");
            Assert.True(result.Bounds.Height > 30);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void HeaderControls_Should_HaveUsefulAutomationNames()
    {
        var (window, _) = Connections();
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var fields = window.GetVisualDescendants().OfType<TextBox>().Where(t => t.PlaceholderText is "Name" or "Value");
            Assert.Equal(2, fields.Count());
            foreach (var field in fields)
            {
                Assert.False(string.IsNullOrWhiteSpace(ControlAutomationPeer.CreatePeerForElement(field).GetName()));
                Assert.True(field.Bounds.Width >= 200, $"Header field only {field.Bounds.Width} wide");
            }
            var row = window.FindControl<ListBox>("ConnectionList")!.GetVisualDescendants().OfType<ListBoxItem>().First();
            Assert.Contains("billing-api", ControlAutomationPeer.CreatePeerForElement(row).GetName());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void IconButtons_Should_HaveTooltipsNamesAndFocus_AndTheLabelledActionsShouldFit()
    {
        var (window, _) = Connections();
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            // Scrollbar arrows are RepeatButtons with their own icons and names; they aren't this window's controls.
            var iconButtons = window.GetVisualDescendants().OfType<Button>().Where(b => b is not RepeatButton && b.IsEffectivelyVisible && b.Content is PathIcon).ToArray();
            Assert.NotEmpty(iconButtons);
            foreach (var button in iconButtons)
            {
                Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(button) as string));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                Assert.True(button.Focusable);
            }

            // Icon plus label: the label must not be clipped at the minimum width.
            foreach (var name in new[] { "AddButton", "RemoveButton" })
            {
                var control = window.FindControl<Control>(name)!;
                Assert.True(control.DesiredSize.Width <= control.Bounds.Width, $"{name} needs {control.DesiredSize.Width} but has {control.Bounds.Width}");
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TestProgress_Should_BeVisibleInTheList()
    {
        var (window, vm) = Connections();
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            vm.SelectedConnection!.TestResult = "Testing…";
            Dispatcher.UIThread.RunJobs();
            var status = window.FindControl<ListBox>("ConnectionList")!.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Testing…");
            Assert.True(status.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SecretPicker_Should_InsertOnlyThePlaceholder_AndAllowRepeatedChoices()
    {
        var (window, vm) = Connections();
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var picker = window.GetVisualDescendants().OfType<SecretPicker>()
                .Single(p => p.DataContext is ConnectionMapRowViewModel);
            Assert.Equal("Insert secret", picker.Content);
            Assert.True(picker.Bounds.Width >= 116);
            var row = Assert.IsType<ConnectionMapRowViewModel>(picker.DataContext);
            row.Value = "Bearer ";
            var menu = Assert.IsType<MenuFlyout>(picker.Flyout);
            Assert.Equal(new[] { "other", "token" }, menu.Items.Cast<MenuItem>().Select(i => i.Header));
            var item = menu.Items.Cast<MenuItem>().Single(i => Equals(i.Header, "token"));
            item.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Bearer ${secret:token}", row.Value);
            item.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Bearer ${secret:token}${secret:token}", row.Value);
            Assert.Null(picker.SelectedSecret);
            Assert.DoesNotContain("synthetic-first", row.Value, StringComparison.Ordinal);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [MemberData(nameof(AllThemes))]
    public void SecondaryActions_Should_FollowThePaletteInEveryState(string themeName)
    {
        var (window, _) = Connections();
        var theme = new RunPadTheme();
        window.Styles.Add(theme);
        window.RequestedThemeVariant = (ThemeVariant)typeof(RunPadThemeVariants).GetField(themeName)!.GetValue(null)!;
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Classes.Contains("secondary")).ToArray())
            {
                var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().Single(c => c.Name == "PART_ContentPresenter");
                object? Resource(string role)
                {
                    Assert.True(theme.TryGetResource(role, window.ActualThemeVariant, out var value));
                    return value;
                }
                Assert.Same(Resource("RunPad.Text.PrimaryBrush"), presenter.Foreground);
                var root = button is DropDownButton
                    ? button.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "RootBorder") : null;
                object? Background() => root is null ? presenter.Background : root.Background;
                if (root is not null)
                    Assert.Equal(global::Avalonia.Media.Colors.Transparent, Assert.IsAssignableFrom<global::Avalonia.Media.ISolidColorBrush>(presenter.Background).Color);
                Assert.Same(Resource("RunPad.Editor.BackgroundBrush"), Background());
                ((IPseudoClasses)button.Classes).Add(":pointerover");
                Dispatcher.UIThread.RunJobs();
                Assert.Same(Resource("RunPad.Toolbar.Button.HoverBrush"), Background());
                ((IPseudoClasses)button.Classes).Add(":pressed");
                Dispatcher.UIThread.RunJobs();
                Assert.Same(Resource("RunPad.Toolbar.BackgroundBrush"), Background());
                button.IsEnabled = false;
                Dispatcher.UIThread.RunJobs();
                Assert.Same(Resource("RunPad.Text.SecondaryBrush"), presenter.Foreground);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [MemberData(nameof(AllThemes))]
    public void FormInputs_Should_ShareTheButtonBorder_WithoutLosingFocusFeedback(string themeName)
    {
        var (window, _) = Connections();
        var theme = new RunPadTheme();
        window.Styles.Add(theme);
        window.RequestedThemeVariant = (ThemeVariant)typeof(RunPadThemeVariants).GetField(themeName)!.GetValue(null)!;
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var button = window.FindControl<Button>("CancelButton")!;
            foreach (var box in window.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible).ToArray())
            {
                var border = box.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_BorderElement");
                Assert.Same(button.BorderBrush, border.BorderBrush);
                ((IPseudoClasses)box.Classes).Add(":pointerover");
                box.Focus();
                Dispatcher.UIThread.RunJobs();
                Assert.Same(button.BorderBrush, border.BorderBrush);
                Assert.NotNull(box.FocusAdorner);
            }
        }
        finally { window.Close(); }
    }

    public static TheoryData<string> AllThemes() => PrimaryActionAndSplitterTests.AllThemes();

    [AvaloniaTheory]
    [MemberData(nameof(AllThemes))]
    public void Save_Should_UsePrimaryStatesWithoutEnterSavingTheForm(string themeName)
    {
        var (window, _) = Connections();
        var theme = new RunPadTheme();
        window.Styles.Add(theme);
        window.RequestedThemeVariant = (ThemeVariant)typeof(RunPadThemeVariants).GetField(themeName)!.GetValue(null)!;
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var save = window.FindControl<Button>("OkButton")!;
            var presenter = save.GetVisualDescendants().OfType<ContentPresenter>().Single(c => c.Name == "PART_ContentPresenter");
            Assert.False(save.IsDefault);
            object? Resource(string role)
            {
                Assert.True(theme.TryGetResource(role, window.ActualThemeVariant, out var value));
                return value;
            }
            Assert.Same(Resource("RunPad.Button.Primary.BackgroundBrush"), presenter.Background);
            ((IPseudoClasses)save.Classes).Add(":pointerover");
            Dispatcher.UIThread.RunJobs();
            Assert.Same(Resource("RunPad.Button.Primary.HoverBrush"), presenter.Background);
            ((IPseudoClasses)save.Classes).Add(":pressed");
            Dispatcher.UIThread.RunJobs();
            Assert.Same(Resource("RunPad.Button.Primary.PressedBrush"), presenter.Background);
            ((IPseudoClasses)save.Classes).Remove(":pressed");
            ((IPseudoClasses)save.Classes).Remove(":pointerover");
            window.FindControl<TextBox>("NameBox")!.Focus();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsVisible);
            save.IsEnabled = false;
            Dispatcher.UIThread.RunJobs();
            Assert.NotSame(Resource("RunPad.Button.Primary.BackgroundBrush"), presenter.Background);
        }
        finally { window.Close(); }
    }
}
