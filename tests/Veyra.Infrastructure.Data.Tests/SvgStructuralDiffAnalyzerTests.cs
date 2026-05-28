using Veyra.Infrastructure.Data.Preview;

namespace Veyra.Infrastructure.Data.Tests;

public sealed class SvgStructuralDiffAnalyzerTests
{
    [Fact]
    public void TryAnalyze_DetectsStructuralChanges()
    {
        using var scope = new TempFileScope();
        var baselinePath = scope.CreateSvg(
            "baseline-struct.svg",
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="128" height="96">
              <rect id="background" width="128" height="96" fill="#111827" />
              <rect id="box" x="12" y="12" width="40" height="28" fill="#60a5fa" />
              <text id="label" x="18" y="64">Before</text>
            </svg>
            """);
        var currentPath = scope.CreateSvg(
            "current-struct.svg",
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="128" height="96">
              <rect id="background" width="128" height="96" fill="#111827" />
              <rect id="box" x="18" y="12" width="48" height="28" fill="#34d399" />
              <circle id="badge" cx="96" cy="24" r="12" fill="#f97316" />
              <text id="label" x="18" y="64">After</text>
            </svg>
            """);

        var result = SvgStructuralDiffAnalyzer.TryAnalyze(baselinePath, currentPath);

        Assert.NotNull(result);
        Assert.Equal(1, result.AddedElementCount);
        Assert.Equal(0, result.RemovedElementCount);
        Assert.True(result.ModifiedElementCount >= 2);
        Assert.True(result.ChangedAttributeCount >= 3);
    }

    private sealed class TempFileScope : IDisposable
    {
        private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "veyra-svg-structural-diff-tests", Guid.NewGuid().ToString("N"));

        public TempFileScope()
            => Directory.CreateDirectory(_rootPath);

        public string CreateSvg(string fileName, string content)
        {
            var path = Path.Combine(_rootPath, fileName);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (!Directory.Exists(_rootPath))
                return;

            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch
            {
            }
        }
    }
}
