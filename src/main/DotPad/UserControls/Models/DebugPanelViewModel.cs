using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;

namespace DotPad.UserControls.Models;

public sealed class DebugPanelViewModel : ReactiveObject
{
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
