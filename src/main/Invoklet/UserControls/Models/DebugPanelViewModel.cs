using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Invoklet.Contracts.Debugging;
using Invoklet.Contracts.Models;
using Invoklet.Shared;
using ReactiveUI;

namespace Invoklet.UserControls.Models;

public sealed class DebugPanelViewModel : ReactiveObject
{
    public EditorLayoutState? Layout { get; internal set; }

    public bool BreakOnAllExceptions
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool BreakOnUncaughtExceptions
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    public ObservableCollection<DebugThread> DebugThreads { get; } = [];
    public ObservableCollection<DebugLocal> DebugLocals { get; } = [];
    public ObservableCollection<DebugFrame> DebugCallStack { get; } = [];

    public ObservableCollection<WatchExpression> WatchExpressions { get; } = [];

    /// <summary>"Type: message" of the exception the debugger is stopped on; null otherwise.</summary>
    public string? StoppedException
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>The debugger's description, stack trace and inner exception of <see cref="StoppedException"/>.</summary>
    public string? StoppedExceptionDetails
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>What the user should know about the current stop: a rejected step, or what Continue will do.</summary>
    public string? StopNotice
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>Shows what the debugger reported about this stop (exception and rejected resume, if any).</summary>
    public void ShowStop(IBreakpointHitContext context)
    {
        var exception = context.StopReason == DebugStopReason.Exception ? context.Exception : null;
        StoppedException = context.StopReason == DebugStopReason.Exception
            ? exception is null ? "Exception" : Headline(exception)
            : null;
        StoppedExceptionDetails = exception is null ? null : Details(exception);

        var notices = new List<string>();
        if (context.ResumeError is { } error)
        {
            notices.Add(context.StopReason == DebugStopReason.Exception
                ? $"{error}. Execution is still stopped on the exception — use Continue or Stop."
                : $"{error}. Execution is still stopped here.");
        }
        else if (exception is { IsUnhandled: true })
        {
            notices.Add("Nothing catches this exception: Continue ends the script with it.");
        }

        StopNotice = notices.Count > 0 ? string.Join(Environment.NewLine, notices) : null;
    }

    /// <summary>Clears the stop information once execution resumes or the run ends.</summary>
    public void ClearStop()
    {
        StoppedException = null;
        StoppedExceptionDetails = null;
        StopNotice = null;
    }

    internal static string Headline(DebugExceptionInfo exception)
    {
        var type = exception.TypeName ?? "Exception";
        return exception.Message is { } message ? $"{type}: {message}" : type;
    }

    // Short type name for the status bar: "System.FormatException" → "FormatException".
    internal static string ShortTypeName(DebugExceptionInfo? exception)
    {
        var type = exception?.TypeName;
        if (string.IsNullOrEmpty(type)) return "exception";
        var genericStart = type.IndexOf('<');
        var lastDot = type.LastIndexOf('.', genericStart < 0 ? type.Length - 1 : genericStart);
        return lastDot < 0 ? type : type[(lastDot + 1)..];
    }

    private static string? Details(DebugExceptionInfo exception)
    {
        var parts = new List<string>();
        if (exception.Description is { } description) parts.Add(description);
        if (exception.StackTrace is { } stackTrace) parts.Add(stackTrace.TrimEnd());
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            parts.Add("Inner exception: " + Headline(inner));
            if (inner.StackTrace is { } innerStack) parts.Add(innerStack.TrimEnd());
        }

        return parts.Count > 0 ? string.Join(Environment.NewLine, parts) : null;
    }

    private WatchExpression? _selectedWatchExpression;
    public WatchExpression? SelectedWatchExpression
    {
        get => _selectedWatchExpression;
        set => this.RaiseAndSetIfChanged(ref _selectedWatchExpression, value);
    }

    public ReactiveCommand<Unit, Unit> AddWatchCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveWatchCommand { get; }

    public DebugPanelViewModel()
    {
        AddWatchCommand = ReactiveCommand.Create(
            () => WatchExpressions.Add(new WatchExpression()));

        RemoveWatchCommand = ReactiveCommand.Create(
            () => { if (SelectedWatchExpression is { } w) WatchExpressions.Remove(w); },
            this.WhenAnyValue(x => x.SelectedWatchExpression).Select(w => w is not null));
    }
}
