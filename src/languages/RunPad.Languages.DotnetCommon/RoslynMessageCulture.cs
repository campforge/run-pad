using System.Globalization;

namespace RunPad.Languages.DotnetCommon;

// RunPad's interface is English only, but Roslyn localizes compiler diagnostics and the IDE features'
// code-action titles by the UI culture current where they are produced — on a Russian or German OS
// the error list and the lightbulb would come out in that language among English menus. Only the UI
// culture (resource lookup) is changed: CurrentCulture, which formats numbers and dates, is the
// user's own, and compiled scripts run in their own process with the system's cultures.
public static class RoslynMessageCulture
{
    // csc/vbc run by the SDK with /preferreduilang:en-US for the same reason.
    public static CultureInfo Culture { get; } = CultureInfo.GetCultureInfo("en-US");

    // CurrentUICulture flows with the ExecutionContext: everything the work awaits sees English, while
    // the setting stays inside the queued work item and never reaches the caller or the pool thread.
    public static Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct) =>
        Task.Run(() =>
        {
            CultureInfo.CurrentUICulture = Culture;
            return work();
        }, ct);

    public static Task RunAsync(Func<Task> work, CancellationToken ct) =>
        Task.Run(() =>
        {
            CultureInfo.CurrentUICulture = Culture;
            return work();
        }, ct);
}
