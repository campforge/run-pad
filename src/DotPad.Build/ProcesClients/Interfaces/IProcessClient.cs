namespace DotPad.Build.ProcesClients.Interfaces;

public interface IProcessClient
{
    bool DoHandshake(int timeout);
    void Send(string cmd);
    string[] Receive(int timeout);
    void Close();
}