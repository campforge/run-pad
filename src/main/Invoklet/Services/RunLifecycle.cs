namespace Invoklet.Services;

/// <summary>
/// The state of one tab's runs: at most one run at a time, the protocol of a restart, and the generations that
/// let the work of an earlier run (a callback posted to the UI, a prompt) tell it is no longer the current one.
/// It owns nothing that runs: the host, its cancellation and the clean-up stay with the tab, which asks this
/// class whether it may start, whether it was stopped meanwhile and whether a late callback still counts.
/// Everything happens on the UI thread.
/// </summary>
internal sealed class RunLifecycle
{
    private Task? _active;

    /// <summary>Counts the runs that started, and every stop: a callback of an earlier run compares its own.</summary>
    internal int RunGeneration { get; private set; }

    /// <summary>Counts the stops: whoever began before one sees it has been stopped.</summary>
    internal int StopGeneration { get; private set; }

    /// <summary>A run is executing (from its host being created to its clean-up).</summary>
    internal bool InProgress { get; private set; }

    /// <summary>A run waits for its save, possibly for the user's answer: another one does not start.</summary>
    internal bool SavingBeforeRun { get; private set; }

    internal bool Restarting { get; private set; }

    /// <summary>A restart has stopped the run and waits for it to end; a stop meanwhile cancels the restart.</summary>
    internal bool RestartRequested { get; private set; }

    /// <summary>A run was started and has not finished.</summary>
    internal bool IsBusy => _active is { IsCompleted: false };

    /// <summary>A selection runs only when nothing else does and no restart is under way.</summary>
    internal bool CanStartSelection => !Restarting && !IsBusy;

    /// <summary>The core of a run may begin: none is executing and none waits for its save.</summary>
    internal bool CanBegin => !InProgress && !SavingBeforeRun;

    /// <summary>Starts a run unless one is busy; the body is the run, whose end ends it.</summary>
    internal Task Start(Func<Task> run) => IsBusy ? Task.CompletedTask : _active = run();

    internal void BeginSaving() => SavingBeforeRun = true;

    internal void EndSaving() => SavingBeforeRun = false;

    /// <summary>The run is executing now. Returns its generation.</summary>
    internal int Begin()
    {
        InProgress = true;
        return ++RunGeneration;
    }

    internal void End() => InProgress = false;

    /// <summary>The user stopped the run: what began before this is stale, and a restart waiting for it is off.</summary>
    internal void Stop()
    {
        RestartRequested = false;
        StopGeneration++;
        RunGeneration++;
    }

    internal bool IsCurrent(int generation) => generation == RunGeneration;

    internal bool IsStoppedSince(int stopGeneration) => stopGeneration != StopGeneration;

    /// <summary>
    /// Stops the run that is busy, waits for it to end and starts the next one. Another restart meanwhile is
    /// ignored, and so is the start when the run was stopped by the user while the restart waited.
    /// </summary>
    /// <param name="stop">Stops the run (the tab's Stop, which also calls <see cref="Stop"/>).</param>
    /// <param name="waiting">Shows that the restart waits.</param>
    /// <param name="start">Starts the next run.</param>
    internal async Task RestartAsync(Action stop, Action waiting, Func<Task> start)
    {
        if (Restarting) return;
        Restarting = true;
        try
        {
            var previous = _active;
            if (previous is { IsCompleted: false })
            {
                stop();
                RestartRequested = true;
                waiting();
                await previous;
                if (!RestartRequested) return;
            }

            RestartRequested = false;
            await start();
        }
        finally
        {
            Restarting = false;
            RestartRequested = false;
        }
    }
}
