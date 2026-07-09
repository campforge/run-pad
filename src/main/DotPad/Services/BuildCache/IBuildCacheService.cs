namespace DotPad.Services.BuildCache;

public interface IBuildCacheService
{
    /// <summary>
    /// Returns the persistent compile-output directory for <paramref name="filePath"/>, allocating
    /// one under <paramref name="workDir"/> and recording it the first time this file is seen. The
    /// same directory is returned across app restarts, so MSBuild/NuGet's own caches under obj/
    /// (and the last built binary, for the hot-run path) survive between sessions.
    /// </summary>
    string EnsureOutputDirectory(string filePath, string workDir);

    /// <summary>Marks <paramref name="filePath"/>'s cached directory as just used, so <see cref="PruneStale"/> doesn't reclaim it.</summary>
    void Touch(string filePath);

    /// <summary>Deletes cached directories not used within <paramref name="maxAge"/> and forgets them.</summary>
    void PruneStale(TimeSpan maxAge);
}
