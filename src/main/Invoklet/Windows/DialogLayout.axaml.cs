using Avalonia;
using Avalonia.Controls;

namespace Invoklet.Windows;

/// <summary>What a dialog is about, shown as its badge: a question to answer, a warning, an error.</summary>
public enum DialogKind
{
    Question,
    Warning,
    Error,
}

/// <summary>Presentation slots shared by confirmation and input dialogs; contains no action logic.</summary>
public partial class DialogLayout : UserControl
{
    public static readonly StyledProperty<string?> HeadingProperty = AvaloniaProperty.Register<DialogLayout, string?>(nameof(Heading));
    public static readonly StyledProperty<string?> FileNameProperty = AvaloniaProperty.Register<DialogLayout, string?>(nameof(FileName));
    public static readonly StyledProperty<string?> MessageProperty = AvaloniaProperty.Register<DialogLayout, string?>(nameof(Message));
    public static readonly StyledProperty<DialogKind> KindProperty = AvaloniaProperty.Register<DialogLayout, DialogKind>(nameof(Kind));
    public static readonly StyledProperty<Control?> BodyProperty = AvaloniaProperty.Register<DialogLayout, Control?>(nameof(Body));
    public static readonly StyledProperty<Control?> LeadingActionsProperty = AvaloniaProperty.Register<DialogLayout, Control?>(nameof(LeadingActions));
    public static readonly StyledProperty<Control?> ActionsProperty = AvaloniaProperty.Register<DialogLayout, Control?>(nameof(Actions));

    public string? Heading { get => GetValue(HeadingProperty); set => SetValue(HeadingProperty, value); }
    public string? FileName { get => GetValue(FileNameProperty); set => SetValue(FileNameProperty, value); }
    public string? Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    // The file line is shown exactly when there is a file name: one source of truth.
    public bool ShowFileName => !string.IsNullOrEmpty(FileName);
    public DialogKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Control? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    public Control? LeadingActions { get => GetValue(LeadingActionsProperty); set => SetValue(LeadingActionsProperty, value); }
    public Control? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    public DialogLayout()
    {
        InitializeComponent();
        ApplyKind();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty) ApplyKind();
    }

    // The theme's own roles: the primary action's colour for a question, the status colours otherwise.
    private void ApplyKind()
    {
        var (glyph, brush) = Kind switch
        {
            DialogKind.Warning => ("!", "Invoklet.Status.PausedBrush"),
            DialogKind.Error => ("×", "Invoklet.Status.ErrorBrush"),
            _ => ("?", "Invoklet.Button.Primary.BackgroundBrush"),
        };
        KindGlyph.Text = glyph;
        KindBadge.Bind(Border.BackgroundProperty, KindBadge.GetResourceObservable(brush));
    }
}
