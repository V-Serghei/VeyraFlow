using Veyra.Application.Abstractions.Setup;

namespace Veyra.Infrastructure.Native.Setup;

public sealed class SetupState : ISetupState
{
    private readonly HashSet<string> _dirs = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _exts = new(System.StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> WatchedDirectories => _dirs.ToList().AsReadOnly();
    public IReadOnlyCollection<string> TrackedExtensions  => _exts.ToList().AsReadOnly();

    public void SetWatchedDirectories(IEnumerable<string> dirs)
    {
        _dirs.Clear();
        foreach (var d in dirs.Where(s => !string.IsNullOrWhiteSpace(s)))
            _dirs.Add(d.Trim());
    }

    public void SetTrackedExtensions(IEnumerable<string> exts)
    {
        _exts.Clear();
        foreach (var e in exts.Where(s => !string.IsNullOrWhiteSpace(s)))
            _exts.Add(e.Trim().ToLowerInvariant());
    }
}
