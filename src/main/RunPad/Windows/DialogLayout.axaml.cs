using Avalonia;
using Avalonia.Controls;

namespace RunPad.Windows;

/// <summary>Presentation slots shared by confirmation and input dialogs; contains no action logic.</summary>
public partial class DialogLayout : UserControl
{
    public static readonly StyledProperty<string?> HeadingProperty = AvaloniaProperty.Register<DialogLayout, string?>(nameof(Heading));
    public static readonly StyledProperty<string?> FileNameProperty = AvaloniaProperty.Register<DialogLayout, string?>(nameof(FileName));
    public static readonly StyledProperty<string?> MessageProperty = AvaloniaProperty.Register<DialogLayout, string?>(nameof(Message));
    public static readonly StyledProperty<bool> ShowFileNameProperty = AvaloniaProperty.Register<DialogLayout, bool>(nameof(ShowFileName));
    public static readonly StyledProperty<Control?> BodyProperty = AvaloniaProperty.Register<DialogLayout, Control?>(nameof(Body));
    public static readonly StyledProperty<Control?> LeadingActionsProperty = AvaloniaProperty.Register<DialogLayout, Control?>(nameof(LeadingActions));
    public static readonly StyledProperty<Control?> ActionsProperty = AvaloniaProperty.Register<DialogLayout, Control?>(nameof(Actions));

    public string? Heading { get => GetValue(HeadingProperty); set => SetValue(HeadingProperty, value); }
    public string? FileName { get => GetValue(FileNameProperty); set => SetValue(FileNameProperty, value); }
    public string? Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public bool ShowFileName { get => GetValue(ShowFileNameProperty); set => SetValue(ShowFileNameProperty, value); }
    public Control? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    public Control? LeadingActions { get => GetValue(LeadingActionsProperty); set => SetValue(LeadingActionsProperty, value); }
    public Control? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    public DialogLayout() => InitializeComponent();
}
