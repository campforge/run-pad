namespace RunPad.Debug.Protocol;

public class VsCodeProtocolRequest { }

// https://github.com/Microsoft/vscode-debugadapter-node/blob/master/debugProtocol.json
public class ProtocolMessage { public int seq; public string? type; }

public class Request : ProtocolMessage
{
    public Request() { type = "request"; }
    public string? command;
}

public class InitializeRequest : Request
{
    public InitializeRequest() { command = "initialize"; }
    public InitializeRequestArguments arguments = new();
}

public class InitializeRequestArguments
{
    public string? clientID; public string? clientName; public string? adapterID; public string? locale;
    public bool? linesStartAt1; public bool? columnsStartAt1; public string? pathFormat;
    public bool? supportsVariableType; public bool? supportsVariablePaging; public bool? supportsRunInTerminalRequest;
}

public class LaunchRequest : Request
{
    public LaunchRequest() { command = "launch"; }
    public LaunchRequestArguments arguments = new();
}

public class LaunchRequestArguments
{
    public string? name; public string? type; public string? preLaunchTask; public string? program;
    public List<string>? args; public string? cwd; public Dictionary<string, string>? env;
    public string? console; public bool stopAtEntry; public bool? justMyCode;
    public bool? enableStepFiltering; public string? internalConsoleOptions; public string? __sessionId;
}

public class AttachRequest : Request
{
    public AttachRequest() { command = "attach"; }
    public AttachRequestArguments arguments = new();
}
public class AttachRequestArguments { public int processId; }

public class ConfigurationDoneRequest : Request
{
    public ConfigurationDoneRequest() { command = "configurationDone"; }
    public ConfigurationDoneArguments? arguments;
}
public class ConfigurationDoneArguments { }

public class ContinueRequest : Request
{
    public ContinueRequest() { command = "continue"; }
    public ContinueArguments arguments = new();
}
public class ContinueArguments { public int threadId; }

public class DisconnectRequest : Request
{
    public DisconnectRequest() { command = "disconnect"; }
    public DisconnectArguments? arguments;
}
public class DisconnectArguments { public bool? restart; public bool? terminateDebuggee; }

public class TerminateRequest : Request
{
    public TerminateRequest() { command = "terminate"; }
    public TerminateArguments? arguments;
}
public class TerminateArguments { public bool? restart; }

public class SetBreakpointsRequest : Request
{
    public SetBreakpointsRequest() { command = "setBreakpoints"; }
    public SetBreakpointsArguments arguments = new();
}
public class SetBreakpointsArguments
{
    public Source source = new(); public List<SourceBreakpoint> breakpoints = new();
    public List<int> lines = new(); public bool? sourceModified;
}

public class SourceBreakpoint
{
    public SourceBreakpoint(int bpLine, string? condition = null, string? hitCondition = null)
    {
        line = bpLine; this.condition = condition; this.hitCondition = hitCondition;
    }
    public int line; public int? column; public string? condition; public string? hitCondition; public string? logMessage;
}

public class Source
{
    public string? name; public string? path; public int? sourceReference; public string? presentationHint;
    public string? origin; public List<Source> sources = new(); public dynamic? adapterData = null;
    public List<Checksum> checksums = new();
}
public class Checksum { public string? algorithm; public string? checksum; }

public class StackTraceRequest : Request
{
    public StackTraceRequest() { command = "stackTrace"; }
    public StackTraceArguments arguments = new();
}
public class StackTraceArguments { public int threadId; public int? startFrame; public int? levels; public StackFrameFormat? format; }

public class ValueFormat { public bool? hex; }
public class StackFrameFormat : ValueFormat
{
    public bool? parameters; public bool? parameterTypes; public bool? parameterNames;
    public bool? parameterValues; public bool? line; public bool? module; public bool? includeAll;
}

public class PauseRequest : Request
{
    public PauseRequest() { command = "pause"; }
    public PauseArguments arguments = new();
}
public class PauseArguments { public int threadId; }

public class ThreadsRequest : Request { public ThreadsRequest() { command = "threads"; } }

public class ScopesRequest : Request
{
    public ScopesRequest() { command = "scopes"; }
    public ScopesArguments arguments = new();
}
public class ScopesArguments { public long frameId; }

public class VariablesRequest : Request
{
    public VariablesRequest() { command = "variables"; }
    public VariablesArguments arguments = new();
}
public class VariablesArguments
{
    public int variablesReference; public string? filter; public int? start; public int? count; public ValueFormat? format;
}

public class EvaluateRequest : Request
{
    public EvaluateRequest() { command = "evaluate"; }
    public EvaluateArguments arguments = new();
}
public class EvaluateArguments { public string? expression; public long? frameId; public string? context; public ValueFormat? format; }

public class SetVariableRequest : Request
{
    public SetVariableRequest() { command = "setVariable"; }
    public SetVariableArguments arguments = new();
}
public class SetVariableArguments { public int variablesReference; public string? name; public string? value; public ValueFormat? format; }

public class NextRequest : Request
{
    public NextRequest() { command = "next"; }
    public NextArguments arguments = new();
}
public class NextArguments { public int threadId; }

public class StepInRequest : Request
{
    public StepInRequest() { command = "stepIn"; }
    public StepInArguments arguments = new();
}
public class StepInArguments { public int threadId; public int? targetId; }

public class StepOutRequest : Request
{
    public StepOutRequest() { command = "stepOut"; }
    public StepOutArguments arguments = new();
}
public class StepOutArguments { public int threadId; }

public class SetExceptionBreakpointsRequest : Request
{
    public SetExceptionBreakpointsRequest() { command = "setExceptionBreakpoints"; }
    public SetExceptionBreakpointsArguments arguments = new();
}
public class SetExceptionBreakpointsArguments
{
    public List<string> filters = new(); public List<ExceptionFilterOptions>? filterOptions;
    public List<ExceptionOptions>? exceptionOptions;
}
public class ExceptionFilterOptions { public string? filterId; public string? condition; }
public class ExceptionOptions { public List<ExceptionPathSegment>? path; public string? breakMode; }
public class ExceptionPathSegment { public bool? negate; public List<string> names = new(); }

public class ExceptionInfoRequest : Request
{
    public ExceptionInfoRequest() { command = "exceptionInfo"; }
    public ExceptionInfoArguments arguments = new();
}
public class ExceptionInfoArguments { public int threadId; }

public class SetExpressionRequest : Request
{
    public SetExpressionRequest() { command = "setExpression"; }
    public SetExpressionArguments arguments = new();
}
public class SetExpressionArguments { public string? expression; public string? value; public int? frameId; public ValueFormat? format; }
