using DotPad.Build.ProcesClients.Interfaces;

namespace DotPad.Build.ProcesClients;

public class LocalProcessClient : IProcessClient
{
    public bool DoHandshake(int timeout)
    {
        throw new NotImplementedException();
    }

    public void Send(string cmd)
    {
        throw new NotImplementedException();
    }

    public string[] Receive(int timeout)
    {
        throw new NotImplementedException();
    }

    public void Close()
    {
        throw new NotImplementedException();
    }
}