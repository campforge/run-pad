
namespace DotPad.Runtime.Struct
{

    internal readonly struct DumpData
    {
        public object Object { get; }
        public string Header { get; }
        public DumpQuotas Quotas { get; }

        public DumpData(object o, string header, DumpQuotas quotas)
        {
            Object = o;
            Header = header;
            Quotas = quotas;
        }
    }
}