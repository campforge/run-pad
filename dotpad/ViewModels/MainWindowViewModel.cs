using System.Collections.ObjectModel;
using DotPad.Build;
using System.Collections.Generic;
using System.Linq;
using DotPad.Models;

namespace dotpad.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
	public string Greeting => "Welcome to Avalonia!";

	public ObservableCollection<Node> Nodes { get; }

	public List<ExecutionPlatform> DotnetPlatforms { get; }

	public string? SelectedDotnetPlatforms { get; set; }

	public MainWindowViewModel()
	{

		var sdks = new DotnetSdksService();
		var list = sdks.GetSdkInfos().ToList();
		DotnetPlatforms = list;
		SelectedDotnetPlatforms = list.FirstOrDefault()?.Description;


		Nodes = new ObservableCollection<Node>
		{
			new Node("Animals", new ObservableCollection<Node>
			{
				new Node("Mammals", new ObservableCollection<Node>
				{
					new Node("Lion"), new Node("Cat"), new Node("Zebra")
				})
			})
		};
	}
}
