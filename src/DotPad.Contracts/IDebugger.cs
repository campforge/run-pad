namespace DotPad.Contracts;

public interface IBreakpoint
{
    string FileName { get; }
    int Line { get; set; }
}

public interface IBreakpointStore : IEnumerable<IBreakpoint>
{
    void Remove(IBreakpoint bp);
    void Add(int lineClicked);
    
}

public interface IRunner
{
    
}

public interface IDebugger
{
    
}