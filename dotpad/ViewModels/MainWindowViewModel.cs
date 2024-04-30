using DynamicData;
using System.Collections.ObjectModel;


namespace dotpad.ViewModels
{
	public class MainWindowViewModel : ViewModelBase
	{
#pragma warning disable CA1822 // Mark members as static
		public string Greeting => "Welcome to Avalonia!";
#pragma warning restore CA1822 // Mark members as static

		public ObservableCollection<Node> Nodes { get; }

		public MainWindowViewModel() {


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
	public class Node
	{
		public ObservableCollection<Node>? SubNodes { get; }
		public string Title { get; }

		public Node(string title)
		{
			Title = title;
		}

		public Node(string title, ObservableCollection<Node> subNodes)
		{
			Title = title;
			SubNodes = subNodes;
		}
	}

}