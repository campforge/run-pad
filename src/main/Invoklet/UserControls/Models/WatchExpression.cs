using ReactiveUI;

namespace Invoklet.UserControls.Models;

public class WatchExpression : ReactiveObject
{
    private string _expression = "";
    private string _value = "?";
    private string _typeName = "";

    public string Expression
    {
        get => _expression;
        set => this.RaiseAndSetIfChanged(ref _expression, value);
    }

    public string Value
    {
        get => _value;
        set => this.RaiseAndSetIfChanged(ref _value, value);
    }

    public string TypeName
    {
        get => _typeName;
        set => this.RaiseAndSetIfChanged(ref _typeName, value);
    }
}
