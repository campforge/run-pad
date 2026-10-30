using Avalonia.Controls;
using Invoklet.UserControls.Models;

namespace Invoklet.UserControls;

public partial class DebugValueTip : UserControl
{
    public DebugValueTip() : this(new DebugLocal("", "", "")) { }

    public DebugValueTip(DebugLocal root)
    {
        InitializeComponent();
        DataContext = root;
        Tree.ItemsSource = new[] { root };
    }

    internal TreeView TreeForTests => Tree;
}
