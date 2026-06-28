namespace DotPad.Contracts;

[Flags]
public enum LanguageHostCapabilities
{
    None               = 0,
    Run                = 1 << 0,
    Debug              = 1 << 1,
    Breakpoints        = 1 << 2,
    VariableInspection = 1 << 3,
    MemoryDump         = 1 << 4,
}
