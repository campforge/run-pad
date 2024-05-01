namespace DotPad.Build.Events;

public delegate void ExecutionReceivedEventHandler(object sender, ExecutionReceivedEventArgs e);
public class ExecutionReceivedEventArgs : EventArgs
{
    private readonly string? _data;
    public ExecutionReceivedEventArgs(string? data)
    {
        _data = data;
    }

    public string? Data
    {
        get { return _data; }
    }
}