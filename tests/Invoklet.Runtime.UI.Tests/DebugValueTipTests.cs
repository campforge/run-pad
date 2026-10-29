using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// The debugger's value tip: members open under their own parent, at any depth, and fold back.
public class DebugValueTipTests
{
    private static DebugLocal Node(string name, int depth) => new(name, "{T}", "T", depth < 4,
        depth < 4 ? () => Task.FromResult<IReadOnlyList<DebugLocal>>([Node(name + ".A", depth + 1), Node(name + ".B", depth + 1)]) : null);

    private static TreeViewItem Row(DebugValueTip tip, DebugLocal node)
        => tip.TreeForTests.GetVisualDescendants().OfType<TreeViewItem>().Single(i => i.DataContext == node);

    [AvaloniaFact]
    public void Chevron_Should_OpenNestedMembersUnderTheirParent_AndFoldThem()
    {
        var root = Node("c", 0);
        var tip = new DebugValueTip(root);
        var window = new Window { Content = tip, Width = 600, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var node = root;
        for (var depth = 0; depth < 4; depth++)
        {
            var chevron = Row(tip, node).GetVisualDescendants().OfType<ToggleButton>()
                .Single(b => b.Name == "PART_ExpandCollapseChevron");
            Assert.True(chevron.IsVisible);
            chevron.IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            Assert.True(node.IsExpanded);
            var child = node.Children[0];
            Assert.Equal(node.Name + ".A", child.Name);
            Assert.Same(Row(tip, node), Row(tip, child).GetLogicalParent());
            node = child;
        }
        Assert.False(Row(tip, node).GetVisualDescendants().OfType<ToggleButton>()
            .Single(b => b.Name == "PART_ExpandCollapseChevron").IsVisible);

        root.IsExpanded = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, root.Children.Count);
        Assert.False(Row(tip, root).IsExpanded);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("\"text\"", true, false)]
    [InlineData("42", false, true)]
    [InlineData("-1.5", false, true)]
    [InlineData("{Customer}", false, false)]
    public void Value_Should_BeColouredByKind(string value, bool isString, bool isNumber)
    {
        var local = new DebugLocal("v", value, "T");
        Assert.Equal(isString, local.IsStringValue);
        Assert.Equal(isNumber, local.IsNumberValue);
    }
}
