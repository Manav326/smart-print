using System.Text.RegularExpressions;
using Xunit;

namespace SmartPrint.Api.Tests.Architecture;

/// <summary>
/// Guards the dependency boundaries of newly extracted API modules.
/// These checks intentionally apply to the Modules tree so the existing
/// pre-refactoring implementation can be migrated incrementally.
/// </summary>
public sealed class ApiModuleArchitectureTests
{
    private const string ModuleNamespacePrefix = "SmartPrint.Api.Modules.";

    private static readonly HashSet<string> AllowedLayers =
    [
        "Abstractions",
        "Contracts",
        "Domain",
        "Endpoints",
        "Infrastructure",
        "Persistence",
        "Registration",
        "Services"
    ];

    private static readonly Regex NamespaceDeclaration = new(
        @"^\s*namespace\s+(?<namespace>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex UsingDirective = new(
        @"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:[A-Za-z_]\w*\s*=\s*)?(?<namespace>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*;",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex ModuleReference = new(
        @"\bSmartPrint\.Api\.Modules\.(?<module>[A-Za-z_]\w*)\.(?<layer>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    private static readonly Regex EndpointWriteCall = new(
        @"\b(?:db|context|database)\s*\.\s*(?:SaveChanges|SaveChangesAsync|Add|AddAsync|AddRange|AddRangeAsync|Update|UpdateRange|Remove|RemoveRange|ExecuteSqlRaw|ExecuteSqlRawAsync)\s*\(" +
        @"|\b(?:db|context|database)\s*\.\s*[A-Za-z_]\w*\s*\.\s*(?:Add|AddAsync|AddRange|AddRangeAsync|Update|UpdateRange|Remove|RemoveRange)\s*\(" +
        @"|\b(?:db|context|database)\s*\.\s*Database\s*\.\s*BeginTransaction(?:Async)?\s*\(" +
        @"|\b[A-Za-z_]\w*\s*\.\s*Execute(?:Update|Delete)(?:Async)?\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void ModuleSourceFilesUseTheRequiredFolderAndNamespaceConvention()
    {
        var repositoryRoot = FindRepositoryRoot();
        var modulesRoot = Path.Combine(repositoryRoot, "src", "SmartPrint.Api", "Modules");

        Assert.True(
            Directory.Exists(modulesRoot),
            "The API Modules directory must exist. See docs/API_REFACTORING_GUIDELINES.md.");

        var errors = new List<string>();
        foreach (var moduleDirectory in Directory.GetDirectories(modulesRoot))
        {
            var moduleName = new DirectoryInfo(moduleDirectory).Name;
            foreach (var file in Directory.EnumerateFiles(moduleDirectory, "*.cs", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(moduleDirectory, file);
                var pathParts = relativePath.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries);
                var source = RemoveComments(File.ReadAllText(file));
                var namespaceMatch = NamespaceDeclaration.Match(source);

                if (pathParts.Length < 2 || !AllowedLayers.Contains(pathParts[0]))
                {
                    errors.Add($"{relativePath}: place C# files under a named module layer folder.");
                    continue;
                }

                if (!namespaceMatch.Success)
                {
                    errors.Add($"{relativePath}: declare a namespace matching the module and layer folders.");
                    continue;
                }

                var expectedPrefix = $"{ModuleNamespacePrefix}{moduleName}.{pathParts[0]}";
                var actualNamespace = namespaceMatch.Groups["namespace"].Value;
                if (!actualNamespace.StartsWith(expectedPrefix, StringComparison.Ordinal))
                {
                    errors.Add($"{relativePath}: namespace '{actualNamespace}' must start with '{expectedPrefix}'.");
                }
            }
        }

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void ModulesDependOnOtherModulesOnlyThroughContractsOrAbstractions()
    {
        var modulesRoot = Path.Combine(FindRepositoryRoot(), "src", "SmartPrint.Api", "Modules");
        var errors = new List<string>();

        foreach (var file in GetModuleSourceFiles(modulesRoot))
        {
            var source = RemoveComments(File.ReadAllText(file.Path));
            var references = UsingDirective.Matches(source)
                .Cast<Match>()
                .Select(match => match.Groups["namespace"].Value)
                .Concat(ModuleReference.Matches(source)
                    .Cast<Match>()
                    .Select(match => match.Value.TrimStart()))
                .Where(value => value.StartsWith(ModuleNamespacePrefix, StringComparison.Ordinal));

            foreach (var reference in references.Distinct(StringComparer.Ordinal))
            {
                var parts = reference.Split('.');
                if (parts.Length < 5)
                    continue;

                var referencedModule = parts[3];
                var referencedLayer = parts[4];
                if (string.Equals(referencedModule, file.Module, StringComparison.Ordinal))
                    continue;

                if (referencedLayer is not ("Contracts" or "Abstractions"))
                {
                    errors.Add(
                        $"{Path.GetRelativePath(modulesRoot, file.Path)}: cross-module reference '{reference}' " +
                        "is not allowed. Depend on the other module's Contracts or Abstractions only.");
                }
            }
        }

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void DomainAndContractLayersDoNotDependOnFrameworkOrImplementationLayers()
    {
        var modulesRoot = Path.Combine(FindRepositoryRoot(), "src", "SmartPrint.Api", "Modules");
        var errors = new List<string>();

        foreach (var file in GetModuleSourceFiles(modulesRoot))
        {
            var source = RemoveComments(File.ReadAllText(file.Path));
            var references = UsingDirective.Matches(source)
                .Cast<Match>()
                .Select(match => match.Groups["namespace"].Value)
                .Concat(ModuleReference.Matches(source).Cast<Match>().Select(match => match.Value))
                .ToArray();

            IEnumerable<string> forbiddenPrefixes = file.Layer switch
            {
                "Domain" =>
                [
                    "Microsoft.AspNetCore",
                    "Microsoft.EntityFrameworkCore",
                    "SmartPrint.Api.Data",
                    "SmartPrint.Api.Storage",
                    "SmartPrint.Api.Endpoints",
                    "SmartPrint.Api.Services"
                ],
                "Contracts" =>
                [
                    "Microsoft.AspNetCore",
                    "Microsoft.EntityFrameworkCore",
                    "SmartPrint.Api.Data",
                    "SmartPrint.Api.Storage",
                    "SmartPrint.Api.Endpoints",
                    "SmartPrint.Api.Services",
                    "SmartPrint.Api.Domain",
                    "SmartPrint.Api.Modules." + file.Module + ".Domain",
                    "SmartPrint.Api.Modules." + file.Module + ".Persistence",
                    "SmartPrint.Api.Modules." + file.Module + ".Infrastructure"
                ],
                "Services" =>
                [
                    "Microsoft.AspNetCore",
                    "SmartPrint.Api.Modules." + file.Module + ".Endpoints"
                ],
                _ => []
            };

            foreach (var reference in references)
            {
                var forbidden = forbiddenPrefixes.FirstOrDefault(prefix =>
                    reference.Equals(prefix, StringComparison.Ordinal) ||
                    reference.StartsWith(prefix + ".", StringComparison.Ordinal));

                if (forbidden is not null)
                {
                    errors.Add(
                        $"{Path.GetRelativePath(modulesRoot, file.Path)}: layer '{file.Layer}' " +
                        $"must not depend on '{forbidden}' (found '{reference}').");
                }
            }
        }

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void EndpointModulesDoNotOwnDatabaseWritesOrTransactions()
    {
        var modulesRoot = Path.Combine(FindRepositoryRoot(), "src", "SmartPrint.Api", "Modules");
        var errors = new List<string>();

        foreach (var file in GetModuleSourceFiles(modulesRoot).Where(file => file.Layer == "Endpoints"))
        {
            var source = RemoveComments(File.ReadAllText(file.Path));
            if (EndpointWriteCall.IsMatch(source))
            {
                errors.Add(
                    $"{Path.GetRelativePath(modulesRoot, file.Path)}: endpoint modules must delegate " +
                    "database writes and transaction boundaries to an application service.");
            }
        }

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void ProgramReferencesModuleRegistrationEntryPointsOnly()
    {
        var repositoryRoot = FindRepositoryRoot();
        var programPath = Path.Combine(repositoryRoot, "src", "SmartPrint.Api", "Program.cs");
        var source = RemoveComments(File.ReadAllText(programPath));
        var errors = new List<string>();

        var references = UsingDirective.Matches(source)
            .Cast<Match>()
            .Select(match => match.Groups["namespace"].Value)
            .Concat(ModuleReference.Matches(source).Cast<Match>().Select(match => match.Value))
            .Where(reference => reference.StartsWith(ModuleNamespacePrefix, StringComparison.Ordinal));

        foreach (var reference in references.Distinct(StringComparer.Ordinal))
        {
            var parts = reference.Split('.');
            if (parts.Length < 5 || parts[4] != "Registration")
            {
                errors.Add(
                    $"Program.cs references '{reference}'. The composition root may reference " +
                    "a module's Registration entry point only.");
            }
        }

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static IEnumerable<ModuleSourceFile> GetModuleSourceFiles(string modulesRoot)
    {
        if (!Directory.Exists(modulesRoot))
            yield break;

        foreach (var moduleDirectory in Directory.GetDirectories(modulesRoot))
        {
            var moduleName = new DirectoryInfo(moduleDirectory).Name;
            foreach (var path in Directory.EnumerateFiles(moduleDirectory, "*.cs", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(moduleDirectory, path);
                var layer = relativePath.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                yield return new ModuleSourceFile(moduleName, layer, path);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var startingPoints = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        foreach (var startingPoint in startingPoints)
        {
            DirectoryInfo? directory = new DirectoryInfo(startingPoint);
            while (directory is not null)
            {
                var projectPath = Path.Combine(directory.FullName, "src", "SmartPrint.Api", "SmartPrint.Api.csproj");
                if (File.Exists(projectPath))
                    return directory.FullName;

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the SmartPrint repository root from the test execution directory.");
    }

    private static string RemoveComments(string source)
    {
        // Keep this scanner deliberately small: architecture checks inspect namespace and using declarations,
        // not C# syntax trees. Removing comments avoids treating commented-out dependencies as real imports.
        return Regex.Replace(source, @"/\*[\s\S]*?\*/|//[^\r\n]*", string.Empty);
    }

    private sealed record ModuleSourceFile(string Module, string Layer, string Path);
}
