using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DotPad.Windows;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        var authors = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;
        var copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;

        VersionBlock.Text = $"Version {version}";
        AuthorsBlock.Text = string.IsNullOrEmpty(authors) ? "" : $"By {authors}";
        AuthorsBlock.IsVisible = !string.IsNullOrEmpty(authors);
        CopyrightBlock.Text = copyright ?? "";
        CopyrightBlock.IsVisible = !string.IsNullOrEmpty(copyright);
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e) => Close();
}
