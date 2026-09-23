using System.Xml.Linq;
using System.Runtime.CompilerServices;

namespace Trading.BacktestTests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Deterministic_module_dependency_graph_excludes_external_adapters()
    {
        var directory = FindRoot(AppContext.BaseDirectory) ?? FindRoot(Directory.GetCurrentDirectory()) ??
            FindRoot(Path.GetDirectoryName(SourceFile())!);
        Assert.NotNull(directory);
        var root = directory.FullName;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Inspect(Path.Combine(root, "src", "Trading.Backtesting", "Trading.Backtesting.csproj"));
        Assert.Contains(visited, path => Path.GetFileName(path) == "Trading.Domain.csproj");

        void Inspect(string project)
        {
            project = Path.GetFullPath(project);
            if (!visited.Add(project)) return;
            Assert.DoesNotContain(Path.GetFileNameWithoutExtension(project),
                new[] { "Trading.AI", "Trading.Infrastructure", "Trading.Execution", "Trading.ExternalValidation",
                    "Trading.Api", "Trading.Web" });
            foreach (var reference in XDocument.Load(project).Descendants("ProjectReference"))
                Inspect(Path.Combine(Path.GetDirectoryName(project)!, reference.Attribute("Include")!.Value));
        }

        static DirectoryInfo? FindRoot(string start)
        {
            var candidate = new DirectoryInfo(start);
            while (candidate is not null && !File.Exists(Path.Combine(candidate.FullName, "TradingCommandCenter.sln")))
                candidate = candidate.Parent;
            return candidate;
        }

        static string SourceFile([CallerFilePath] string path = "") => path;
    }
}
