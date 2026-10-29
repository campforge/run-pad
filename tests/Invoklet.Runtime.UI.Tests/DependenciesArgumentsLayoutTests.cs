using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Format.MdRun;
using Invoklet.Services;
using Invoklet.Windows;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// The Arguments tab's declarations: every row's value starts at the Value column, an enum picks its
// default from its values, and a plain button is drawn in the theme's colours.
public class DependenciesArgumentsLayoutTests
{
    [AvaloniaFact]
    public void ArgumentRows_Should_AlignTheirValues_AndAnEnumShouldPickItsDefault()
    {
        var theme = new InvokletTheme();
        Application.Current!.Styles.Add(theme);
        var vm = new DependenciesWindowViewModel(new Mock<IPackageSearchService>().Object,
            new Mock<ILocalPackageCacheService>().Object, new Mock<INuGetSourceStore>().Object);
        vm.Arguments.Load(new MdRunFrontMatter
        {
            Arguments =
            [
                new() { Name = "verbose", Type = MdRunArgumentType.Bool },
                new() { Name = "environment", Type = MdRunArgumentType.Enum, Default = MdRunValue.Of("test"), Values = ["dev", "test", "prod"] },
            ],
        });
        var window = new DependenciesWindow(vm) { Width = 1000, Height = 700, RequestedThemeVariant = InvokletThemeVariants.InvokletMidnight };
        try
        {
            window.Show();
            window.FindControl<TabItem>("ArgumentsTab")!.IsSelected = true;
            Dispatcher.UIThread.RunJobs();
            var rows = window.FindControl<ItemsControl>("ArgumentRows")!;
            var named = (string name) => rows.GetVisualDescendants().OfType<Control>()
                .Where(c => c.IsEffectivelyVisible && AutomationProperties.GetName(c) == name).ToList();

            var plainValue = Assert.IsType<TextBox>(Assert.Single(named("Default value"), c => c is TextBox));
            var enumValues = Assert.IsType<TextBox>(Assert.Single(named("Enum values")));
            Assert.Equal(plainValue.TranslatePoint(default, rows)!.Value.X, enumValues.TranslatePoint(default, rows)!.Value.X, 1);

            var enumDefault = Assert.IsType<ComboBox>(Assert.Single(named("Default value"), c => c is ComboBox));
            Assert.Equal(["dev", "test", "prod"], enumDefault.Items.Cast<string>());
            Assert.Equal("test", enumDefault.SelectedItem);
            enumDefault.SelectedItem = "prod";
            Assert.Equal("prod", vm.Arguments.Arguments[1].DefaultText);

            // The footer's buttons are as tall as the fields.
            Assert.Equal(plainValue.Bounds.Height, window.FindControl<Button>("CancelButton")!.Bounds.Height);
            Assert.Equal(plainValue.Bounds.Height, window.FindControl<Button>("OkButton")!.Bounds.Height);
        }
        finally
        {
            window.Close();
            Application.Current.Styles.Remove(theme);
        }
    }

    // A plain Fluent button takes the theme's surface, as a secondary button does, enabled or not.
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlainButtons_Should_UseTheThemesSurface(bool enabled)
    {
        var theme = new InvokletTheme();
        Application.Current!.Styles.Add(theme);
        var button = new Button { Content = "Browse…", IsEnabled = enabled };
        var window = new Window { Content = button, RequestedThemeVariant = InvokletThemeVariants.InvokletMidnight };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            theme.TryGetResource("Invoklet.Editor.BackgroundBrush", InvokletThemeVariants.InvokletMidnight, out var surface);
            Assert.Equal(((ISolidColorBrush)surface!).Color, ((ISolidColorBrush)presenter.Background!).Color);
        }
        finally
        {
            window.Close();
            Application.Current.Styles.Remove(theme);
        }
    }
}
