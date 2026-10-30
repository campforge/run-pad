namespace Invoklet.Services;

/// <summary>
/// The work of the workspace that is still going on: operations of the file menu and the file tree that were
/// started, and the saves of the tabs. A snapshot of the session, or the closing of the window, waits until it is over,
/// so that it sees the state either before an operation or after it, never half of it. Used on the UI thread.
/// </summary>
internal sealed class WorkspaceWork(TimeSpan? readyTimeout = null)
{
    private readonly HashSet<Task> _operations = [];
    private readonly TimeSpan _readyTimeout = readyTimeout ?? TimeSpan.FromSeconds(20);

    /// <summary>Starts the operation and remembers it until it ends; returns its task.</summary>
    public Task Track(Func<Task> operation)
    {
        _operations.RemoveWhere(task => task.IsCompleted);
        var task = operation();
        if (!task.IsCompleted) _operations.Add(task);
        return task;
    }

    /// <summary>
    /// Returns the included tabs once every one of them is ready (its editor exists: a closing window must not skip a
    /// tab whose editor is still being created) and neither a tracked operation nor a save of any tab is going on. Saves
    /// that begin while it waits are waited for too; the tabs are read again at the end, because some may have come or gone.
    /// </summary>
    /// <exception cref="TimeoutException">A tab is not ready in time.</exception>
    public async Task<List<T>> WaitUntilIdleAsync<T>(
        Func<IReadOnlyList<T>> tabs, Func<T, bool> include, Func<T, Task> ready, Func<T, IEnumerable<Task?>> saves)
    {
        var included = tabs().Where(include).ToList();
        await Task.WhenAll(included.Select(ready)).WaitAsync(_readyTimeout);
        // A Save As owns the rebasing of paths as well as the write: a snapshot must see both or neither.
        while (true)
        {
            var pending = tabs().SelectMany(saves).OfType<Task>().Concat(_operations)
                .Where(task => !task.IsCompleted).Distinct().ToArray();
            if (pending.Length == 0) break;
            await Task.WhenAll(pending);
        }

        _operations.RemoveWhere(task => task.IsCompleted);
        included = tabs().Where(include).ToList();
        await Task.WhenAll(included.Select(ready)).WaitAsync(_readyTimeout);
        return included;
    }
}
