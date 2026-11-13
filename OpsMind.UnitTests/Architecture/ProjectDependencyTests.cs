using System.Xml.Linq;

namespace OpsMind.UnitTests.Architecture;

public sealed class ProjectDependencyTests
{
    [Fact]
    public void DomainHasNoProjectOrNuGetDependencies()
    {
        var project = LoadProject("OpsMind.Domain");

        Assert.Empty(ProjectReferences(project));
        Assert.Empty(PackageReferences(project));
    }

    [Fact]
    public void ApplicationDependsOnlyOnDomainAndHasNoNuGetDependencies()
    {
        var project = LoadProject("OpsMind.Application");

        AssertProjectReferences(project, "OpsMind.Domain");
        Assert.Empty(PackageReferences(project));
    }

    [Fact]
    public void InfrastructureDependsOnlyOnApplicationAndDomain()
    {
        var project = LoadProject("OpsMind.Infrastructure");

        AssertProjectReferences(project, "OpsMind.Application", "OpsMind.Domain");
    }

    [Fact]
    public void ApiIsACompositionRootOverApplicationContractsAndInfrastructure()
    {
        var project = LoadProject("OpsMind.Api");

        AssertProjectReferences(project, "OpsMind.Application", "OpsMind.Contracts", "OpsMind.Infrastructure");
    }

    [Fact]
    public void WorkerIsACompositionRootOverApplicationAndInfrastructure()
    {
        var project = LoadProject("OpsMind.Worker");

        AssertProjectReferences(project, "OpsMind.Application", "OpsMind.Infrastructure");
    }

    [Fact]
    public void MigratorIsACompositionRootOverInfrastructure()
    {
        var project = LoadProject("OpsMind.Migrator");

        AssertProjectReferences(project, "OpsMind.Infrastructure");
    }

    [Fact]
    public void ContractsHasNoProjectOrNuGetDependencies()
    {
        var project = LoadProject("OpsMind.Contracts");

        Assert.Empty(ProjectReferences(project));
        Assert.Empty(PackageReferences(project));
    }

    [Fact]
    public void UnitTestsDependOnlyOnApplicationAndDomain()
    {
        var project = LoadProject("OpsMind.UnitTests");

        AssertProjectReferences(project, "OpsMind.Application", "OpsMind.Domain");
    }

    private static XDocument LoadProject(string projectName)
    {
        var rootDirectory = FindRepositoryRoot();
        var projectPath = Path.Combine(rootDirectory, projectName, $"{projectName}.csproj");

        return XDocument.Load(projectPath);
    }

    private static string FindRepositoryRoot()
    {
        var searchDirectories = new[]
        {
            new DirectoryInfo(AppContext.BaseDirectory),
            new DirectoryInfo(Directory.GetCurrentDirectory())
        };

        foreach (var startDirectory in searchDirectories)
        {
            for (var directory = startDirectory; directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "OpsMind.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root containing OpsMind.slnx.");
    }

    private static string[] ProjectReferences(XDocument project) =>
        project.Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .OfType<string>()
            .Select(include => Path.GetFileNameWithoutExtension(include) ?? string.Empty)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static string[] PackageReferences(XDocument project) =>
        project.Descendants("PackageReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .OfType<string>()
            .OrderBy(include => include, StringComparer.Ordinal)
            .ToArray();

    private static void AssertProjectReferences(XDocument project, params string[] expectedReferences)
    {
        var actualReferences = ProjectReferences(project);
        var orderedExpectedReferences = expectedReferences.OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(orderedExpectedReferences, actualReferences);
    }
}
