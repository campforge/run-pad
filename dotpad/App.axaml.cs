using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using dotpad.ViewModels;
using dotpad.Views;
using RoslynPad.Resources;

namespace dotpad
{
	public partial class App : Application
	{
		public override void Initialize()
		{
			Resources.MergedDictionaries.Add(new Icons());
			AvaloniaXamlLoader.Load(this);
		}

		public override void OnFrameworkInitializationCompleted()
		{
			if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
			{
				desktop.MainWindow = new MainWindow
				{
					DataContext = new MainWindowViewModel(),
				};
			}

			base.OnFrameworkInitializationCompleted();
		}

	}
}