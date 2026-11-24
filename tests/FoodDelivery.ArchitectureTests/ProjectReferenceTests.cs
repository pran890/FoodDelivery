using System.Xml.Linq;

namespace FoodDelivery.ArchitectureTests;

// Rules about PROJECT references, read from the .csproj files. The compiled assembly is not enough:
// the compiler drops a reference that no code uses, so a forbidden one could hide until it is used.
public class ProjectReferenceTests
{
    private static readonly string _src = Path.Combine(FindRepoRoot(), "src");

    // Shared code every service may use. Everything else in src/ (except the AppHost) is a service.
    private static readonly string[] _shared =
        ["FoodDelivery.ServiceDefaults", "FoodDelivery.Contracts", "FoodDelivery.Web"];

    private static readonly Dictionary<string, string[]> _references = Directory
        .EnumerateFiles(_src, "*.csproj", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetFileNameWithoutExtension(path), ReadProjectReferences);

    /// <summary>One test case per service project, found automatically, so new services are covered.</summary>
    public static TheoryData<string> Services =>
        [.. _references.Keys.Where(name => name != "FoodDelivery.AppHost" && !_shared.Contains(name))];

    [Theory]
    [MemberData(nameof(Services))]
    public void Service_references_only_shared_projects(string service)
    {
        // Step 30 (Clean Architecture) will relax this for projects of the SAME service.
        var forbidden = _references[service].Except(_shared);

        Assert.Empty(forbidden);
    }

    [Fact]
    public void Shared_projects_never_reference_services()
    {
        var forbidden = _references
            .Where(project => _shared.Contains(project.Key))
            .SelectMany(project => project.Value)
            .Except(_shared);

        Assert.Empty(forbidden);
    }

    [Fact]
    public void Contracts_references_no_project_and_no_package()
    {
        var csproj = Directory.EnumerateFiles(_src, "FoodDelivery.Contracts.csproj", SearchOption.AllDirectories).Single();
        var items = XDocument.Load(csproj).Descendants()
            .Where(e => e.Name.LocalName is "ProjectReference" or "PackageReference" or "FrameworkReference");

        Assert.Empty(items);
    }

    private static string[] ReadProjectReferences(string csproj) =>
        [.. XDocument.Load(csproj).Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')))];

    private static string FindRepoRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "FoodDelivery.slnx")))
        {
            folder = folder.Parent;
        }

        return folder?.FullName ?? throw new InvalidOperationException("FoodDelivery.slnx not found above the test output.");
    }
}
