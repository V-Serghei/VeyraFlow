namespace Veyra.Application.Abstractions.Setup;

public interface ISetupState
{
    IReadOnlyCollection<string> WatchedDirectories { get; }
    IReadOnlyCollection<string> TrackedExtensions { get; }

    void SetWatchedDirectories(IEnumerable<string> dirs);
    void SetTrackedExtensions(IEnumerable<string> exts);
}
