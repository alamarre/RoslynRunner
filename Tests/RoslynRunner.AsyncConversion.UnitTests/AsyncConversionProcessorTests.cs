using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynRunner.Abstractions;
using RoslynRunner.Core;
using RoslynRunner.Core.Extensions;
using RoslynRunner.Utilities.InvocationTrees;

namespace RoslynRunner.AsyncConversion.UnitTests;

public class AsyncConversionProcessorTests
{
    private static readonly string[] SampleSourceFiles =
    {
        Path.Combine("samples", "LegacyWebApp", "LegacyWebApp", "LegacyWebApp", "Services", "LegacyOrderRepository.cs"),
        Path.Combine("samples", "LegacyWebApp", "LegacyWebApp", "LegacyWebApp", "Services", "LegacyOrderFormatter.cs"),
        Path.Combine("samples", "LegacyWebApp", "LegacyWebApp", "LegacyWebApp", "Services", "LegacyOrderCoordinator.cs"),
        Path.Combine("samples", "LegacyWebApp", "LegacyWebApp", "LegacyWebApp", "Clients", "LegacyOrderClient.cs"),
        Path.Combine("samples", "LegacyWebApp", "LegacyWebApp", "LegacyWebApp", "Controllers", "SampleController.cs"),
    };

    [Test]
    public async Task GenerateAsyncVersion_ConvertsDependencyChainAcrossDocuments()
    {
        var setup = await CreateLegacyWebAppSolutionAsync(SampleSourceFiles);
        using var workspace = setup.Workspace;
        var solution = setup.Solution;
        var tempDirectory = setup.TempDirectory;

        try
        {
            var cache = await CachedSymbolFinder.FromCache(solution);
            var clientType = cache.GetSymbolByMetadataName("LegacyWebApp.Clients.LegacyOrderClient") as INamedTypeSymbol;
            Assert.That(clientType, Is.Not.Null, "The LegacyOrderClient type should exist in the workspace.");

            var generator = new AsyncConversionGenerator(cache, solution);
            var result = await generator.GenerateAsyncVersion(clientType!, "GetFormattedOrders", CancellationToken.None);

            Assert.That(result, Is.Not.Null, "The async conversion should produce a result.");
            Assert.That(result!.Documents.Length, Is.EqualTo(4), "Four documents should participate in the async conversion.");

            var clientDocument = result.Documents.Single(d => d.UpdatedRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Any(cls => cls.Identifier.Text == "LegacyOrderClient"));
            var clientMethod = clientDocument.ConvertedMethods.Single(m => m.AsyncMethod.Identifier.Text == "GetFormattedOrdersAsync");
            Assert.That(clientMethod.AsyncMethod.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)), Is.True);
            Assert.That(clientMethod.AsyncMethod.ReturnType.ToString(),
                Is.EqualTo("System.Threading.Tasks.Task<IEnumerable<string>>"));
            Assert.That(clientMethod.AsyncMethod.ParameterList.Parameters.Any(p => p.Identifier.Text == "cancellationToken"));

            var coordinatorDocument = result.Documents.Single(d => d.UpdatedRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Any(cls => cls.Identifier.Text == "LegacyOrderCoordinator"));
            var coordinatorMethod = coordinatorDocument.ConvertedMethods.Single();
            Assert.That(coordinatorMethod.AsyncMethod.Body!.ToString(), Does.Contain("await _formatter.FormatOrders"));

            var formatterDocument = result.Documents.Single(d => d.UpdatedRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Any(cls => cls.Identifier.Text == "LegacyOrderFormatter"));
            var formatterMethod = formatterDocument.ConvertedMethods.Single();
            Assert.That(formatterMethod.AsyncMethod.Body!.ToString(), Does.Contain("await _repository.GetOrderNumbersAsync"));

            var repositoryDocument = result.Documents.Single(d => d.UpdatedRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Any(cls => cls.Identifier.Text == "LegacyOrderRepository"));
            var repositoryMethods = repositoryDocument.ConvertedMethods;
            Assert.That(repositoryMethods.Any(m => m.AsyncMethod.Identifier.Text == "GetPrimaryOrderAsync"), Is.True);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Test]
    public async Task GenerateAsyncVersion_DoesNotRenameMethodsWhenRequested()
    {
        var setup = await CreateLegacyWebAppSolutionAsync(SampleSourceFiles);
        using var workspace = setup.Workspace;
        var solution = setup.Solution;
        var tempDirectory = setup.TempDirectory;

        try
        {
            var cache = await CachedSymbolFinder.FromCache(solution);
            var clientType = cache.GetSymbolByMetadataName("LegacyWebApp.Clients.LegacyOrderClient") as INamedTypeSymbol;
            Assert.That(clientType, Is.Not.Null);

            var generator = new AsyncConversionGenerator(cache, solution);
            var result = await generator.GenerateAsyncVersion(
                clientType!,
                "GetFormattedOrders",
                CancellationToken.None,
                renameTransformedMethods: false);

            Assert.That(result, Is.Not.Null);

            var clientDocument = result!.Documents.Single(d => d.UpdatedRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Any(cls => cls.Identifier.Text == "LegacyOrderClient"));
            var clientMethod = clientDocument.ConvertedMethods.Single(m => m.AsyncMethod.Identifier.Text == "GetFormattedOrders");
            Assert.That(clientMethod.AsyncMethod.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)), Is.True);
            Assert.That(clientMethod.AsyncMethod.Identifier.Text, Is.EqualTo("GetFormattedOrders"));
            Assert.That(clientMethod.AsyncMethod.Body!.ToString(), Does.Contain("await _coordinator.PrepareOrders("));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Test]
    public async Task GenerateAsyncVersion_UpdatesInterfaceMethodsToAsyncSignatures()
    {
        const string source = @"namespace TestNamespace;

public interface IFoo
{
    void Bar();
}

public class Foo : IFoo
{
    private readonly Dependency _dependency = new();

    public void Bar()
    {
        _dependency.DoWork();
    }
}

public class Dependency
{
    public void DoWork()
    {
    }

    public System.Threading.Tasks.Task DoWorkAsync(System.Threading.CancellationToken cancellationToken = default)
        => System.Threading.Tasks.Task.CompletedTask;
}";

        var setup = await CreateSolutionFromSourceAsync(source);
        using var workspace = setup.Workspace;
        var solution = setup.Solution;

        try
        {
            var cache = await CachedSymbolFinder.FromCache(solution);
            var fooType = cache.GetSymbolByMetadataName("TestNamespace.Foo") as INamedTypeSymbol;
            Assert.That(fooType, Is.Not.Null);

            var generator = new AsyncConversionGenerator(cache, solution);
            var result = await generator.GenerateAsyncVersion(fooType!, "Bar", CancellationToken.None);

            Assert.That(result, Is.Not.Null);

            var document = result!.Documents.Single();
            var interfaceDeclaration = document.UpdatedRoot.DescendantNodes().OfType<InterfaceDeclarationSyntax>().Single();
            var interfaceMethod = interfaceDeclaration.Members.OfType<MethodDeclarationSyntax>().Single();

            Assert.That(interfaceMethod.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.AsyncKeyword)), Is.False);
            Assert.That(interfaceMethod.Identifier.Text, Is.EqualTo("BarAsync"));
            Assert.That(interfaceMethod.ReturnType.ToString(), Is.EqualTo("System.Threading.Tasks.Task"));
            Assert.That(interfaceMethod.ParameterList.Parameters.Any(parameter => parameter.Identifier.Text == "cancellationToken"), Is.True);

            var classDeclaration = document.UpdatedRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Single(cls => cls.Identifier.Text == "Foo");
            var classMethod = classDeclaration.Members.OfType<MethodDeclarationSyntax>()
                .Single(method => method.Identifier.Text == "BarAsync");

            Assert.That(classMethod.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.AsyncKeyword)), Is.True);
        }
        finally
        {
            if (Directory.Exists(setup.TempDirectory))
            {
                Directory.Delete(setup.TempDirectory, recursive: true);
            }
        }
    }

    [Test]
    public async Task GenerateAsyncVersion_ReturnsNullWhenNoEligibleMethods()
    {
        var setup = await CreateLegacyWebAppSolutionAsync(SampleSourceFiles);
        using var workspace = setup.Workspace;
        var solution = setup.Solution;
        var tempDirectory = setup.TempDirectory;

        try
        {
            var cache = await CachedSymbolFinder.FromCache(solution);
            var controllerType = cache.GetSymbolByMetadataName("LegacyWebApp.Controllers.SampleController") as INamedTypeSymbol;
            Assert.That(controllerType, Is.Not.Null);

            var generator = new AsyncConversionGenerator(cache, solution);
            var result = await generator.GenerateAsyncVersion(controllerType!, methodName: null, CancellationToken.None);

            Assert.That(result, Is.Null, "Types without async alternatives should not produce conversion results.");
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Test]
    public async Task ProcessSolution_AppliesChangesToGitBranch()
    {
        var setup = await CreateLegacyWebAppSolutionAsync(SampleSourceFiles);
        using var workspace = setup.Workspace;
        var solution = setup.Solution;
        var tempDirectory = setup.TempDirectory;
        var repositoryPath = setup.RepositoryPath;

        var branchName = $"async-orders-{Guid.NewGuid():N}";

        try
        {
            InitializeGitRepository(repositoryPath);

            var processor = new AsyncConversionProcessor();
            var parameters = new AsyncConversionParameters(
                repositoryPath,
                "LegacyWebApp.Clients.LegacyOrderClient",
                branchName,
                MethodName: "GetFormattedOrders");

            var runContext = new RunContext(Guid.NewGuid());
            RunContextAccessor.RunContext = runContext;

            try
            {
                await processor.ProcessSolution(solution, parameters, NullLogger.Instance, CancellationToken.None);
            }
            finally
            {
                RunContextAccessor.Clear();
            }

            Assert.That(runContext.Output, Does.Contain($"Updated branch '{branchName}' with async conversions."));

            var branchList = RunGit(repositoryPath, "branch");
            Assert.That(branchList.Split('\n').Any(line => line.Trim().EndsWith(branchName)), Is.True,
                "The async conversion should create a new branch containing the changes.");

            var clientFile = Path.Combine(repositoryPath, SampleSourceFiles[3]);
            var relativeClientPath = Path.GetRelativePath(repositoryPath, clientFile).Replace('\\', '/');
            var branchContents = RunGit(repositoryPath, $"show {branchName}:{relativeClientPath}");
            Assert.That(branchContents, Does.Contain("async Task<IEnumerable<string>> GetFormattedOrdersAsync"));
            Assert.That(branchContents, Does.Contain("await _coordinator.PrepareOrdersAsync"));

            var coordinatorFile = Path.Combine(repositoryPath, SampleSourceFiles[2]);
            var relativeCoordinatorPath = Path.GetRelativePath(repositoryPath, coordinatorFile).Replace('\\', '/');
            var coordinatorContents = RunGit(repositoryPath, $"show {branchName}:{relativeCoordinatorPath}");
            Assert.That(coordinatorContents, Does.Contain("await _formatter.FormatOrders"));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static void InitializeGitRepository(string repositoryPath)
    {
        RunGit(repositoryPath, "init");
        RunGit(repositoryPath, "config user.email test@example.com");
        RunGit(repositoryPath, "config user.name RoslynRunnerTests");
        RunGit(repositoryPath, "add .");
        RunGit(repositoryPath, "commit -m initial");
    }

    private static string RunGit(string repositoryPath, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = repositoryPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git process");
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"git {arguments} failed: {error}");
        }

        return process.StandardOutput.ReadToEnd();
    }

    private static async Task<(AdhocWorkspace Workspace, Solution Solution, string RepositoryPath, string TempDirectory)> CreateLegacyWebAppSolutionAsync(IEnumerable<string> relativePaths)
    {
        var repoRoot = GetRepositoryRoot();
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"legacy-webapp-{Guid.NewGuid():N}");
        var repositoryPath = Path.Combine(tempDirectory, "repo");
        Directory.CreateDirectory(repositoryPath);

        var solutionPath = Path.Combine(repositoryPath, "LegacyWebApp.sln");
        await File.WriteAllTextAsync(solutionPath, string.Empty);
        var projectPath = Path.Combine(repositoryPath, "LegacyWebApp", "LegacyWebApp.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        await File.WriteAllTextAsync(projectPath, string.Empty);

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(CancellationToken).Assembly.Location),
            CreateReferenceFromTrustedPlatformAssemblies("System.Runtime.dll"),
            CreateReferenceFromTrustedPlatformAssemblies("Microsoft.AspNetCore.Mvc.Core.dll"),
            CreateReferenceFromTrustedPlatformAssemblies("Microsoft.AspNetCore.Mvc.Abstractions.dll"),
        };

        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "LegacyWebApp",
            "LegacyWebApp",
            LanguageNames.CSharp,
            filePath: projectPath,
            metadataReferences: references,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var solutionInfo = SolutionInfo.Create(
            SolutionId.CreateNewId(),
            VersionStamp.Create(),
            solutionPath,
            projects: new[] { projectInfo });

        workspace.AddSolution(solutionInfo);

        foreach (var relativePath in relativePaths)
        {
            var sourcePath = Path.Combine(repoRoot, relativePath);
            var destinationPath = Path.Combine(repositoryPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);

            var documentId = DocumentId.CreateNewId(projectId);
            var sourceText = SourceText.From(await File.ReadAllTextAsync(destinationPath));
            var documentInfo = DocumentInfo.Create(
                documentId,
                Path.GetFileName(destinationPath),
                loader: TextLoader.From(TextAndVersion.Create(sourceText, VersionStamp.Create())),
                filePath: destinationPath);

            workspace.AddDocument(documentInfo);
        }

        return (workspace, workspace.CurrentSolution, repositoryPath, tempDirectory);
    }

    private static Task<(AdhocWorkspace Workspace, Solution Solution, string TempDirectory)> CreateSolutionFromSourceAsync(string source)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"async-conversion-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(tempDirectory, "TestProject");
        Directory.CreateDirectory(projectDirectory);

        var solutionPath = Path.Combine(tempDirectory, "TestProject.sln");
        File.WriteAllText(solutionPath, string.Empty);
        var projectPath = Path.Combine(projectDirectory, "TestProject.csproj");
        File.WriteAllText(projectPath, string.Empty);

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(CancellationToken).Assembly.Location),
            CreateReferenceFromTrustedPlatformAssemblies("System.Runtime.dll"),
            CreateReferenceFromTrustedPlatformAssemblies("Microsoft.AspNetCore.Mvc.Core.dll"),
            CreateReferenceFromTrustedPlatformAssemblies("Microsoft.AspNetCore.Mvc.Abstractions.dll"),
        };

        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "TestProject",
            "TestProject",
            LanguageNames.CSharp,
            filePath: projectPath,
            metadataReferences: references,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var solutionInfo = SolutionInfo.Create(
            SolutionId.CreateNewId(),
            VersionStamp.Create(),
            solutionPath,
            projects: new[] { projectInfo });

        workspace.AddSolution(solutionInfo);

        var documentId = DocumentId.CreateNewId(projectId);
        var documentPath = Path.Combine(projectDirectory, "TestDocument.cs");
        File.WriteAllText(documentPath, source);
        var sourceText = SourceText.From(source);
        var documentInfo = DocumentInfo.Create(
            documentId,
            "TestDocument.cs",
            loader: TextLoader.From(TextAndVersion.Create(sourceText, VersionStamp.Create())),
            filePath: documentPath);

        workspace.AddDocument(documentInfo);

        return Task.FromResult((workspace, workspace.CurrentSolution, tempDirectory));
    }

    private static MetadataReference CreateReferenceFromTrustedPlatformAssemblies(string assemblyFileName)
    {
        var trustedPlatformAssemblies = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)
            ?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? Array.Empty<string>();

        var assemblyPath = trustedPlatformAssemblies.FirstOrDefault(path =>
            string.Equals(Path.GetFileName(path), assemblyFileName, StringComparison.OrdinalIgnoreCase));

        if (assemblyPath is null)
        {
            throw new FileNotFoundException($"Unable to locate assembly '{assemblyFileName}' in the trusted platform assemblies.");
        }

        return MetadataReference.CreateFromFile(assemblyPath);
    }

    private static string GetRepositoryRoot()
    {
        var assemblyDirectory = AppContext.BaseDirectory;
        var repositoryRoot = Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "..", "..", "..", ".."));
        return repositoryRoot;
    }
}
