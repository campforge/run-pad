using System.Text;
using DotPad.Build.ProcessRunners.Interfaces;

namespace DotPad.Build.ProcesClients;

public class LocalDebuggerProcessClient //: IProcessClient, IAsyncDisposable
{
    const string TwoCrlf = "\r\n\r\n";
    const string ContentLength = "Content-Length: ";
    
    private IExecutionProcess _process;
    public LocalDebuggerProcessClient(IExecutionProcess process)
    {
        _process = process;
    }

    public bool DoHandshake(int timeout)
    {
        throw new NotImplementedException();
    }

    public async Task Send(string command)
    {
        var bytes = Encoding.UTF8.GetBytes(command);
        var commandSize = bytes.Length.ToString();
        await _process.InputWriter.WriteAsync(ContentLength + commandSize + TwoCrlf + command);
    }

    public string[] Receive(int timeout)
    {
        throw new NotImplementedException();
    }

    public void Close()
    {
        //_process.
    }
}