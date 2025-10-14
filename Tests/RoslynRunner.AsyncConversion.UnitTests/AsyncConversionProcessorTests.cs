using System;
using System.Collections.Generic;
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
    [Test]
    public async Task ProcessSolution_UpdatesDependencyChainAcrossDocuments()
    {
        await using var environment = await LegacyWebAppTestEnvironment.CreateAsync().ConfigureAwait(false);
        var processor = new AsyncConversionProcessor();

        var outputFile = Path.Combine(environment.TempDirectory, "LegacyWeatherServicePreview.cs");
        var branchName = environment.CreateBranchName();

        var parameters = new AsyncConversionParameters(
            outputFile,
            "LegacyWebApp.Services.LegacyWeatherService",
            MethodName: "GetWeatherReport",
            ReplaceExistingMethods: true,
            BranchName: branchName);

        var runContext = new RunContext(Guid.NewGuid());
        RunContextAccessor.RunContext = runContext;

        try
        {
            await processor.ProcessSolution(environment.Solution, parameters, NullLogger.Instance, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            RunContextAccessor.Clear();
        }

        Assert.That(File.Exists(outputFile), Is.True, "Processor should create a preview file.");
        Assert.That(runContext.Output, Does.Contain($"Created file: {Path.GetFullPath(outputFile)}"));
        Assert.That(runContext.Output, Does.Contain($"Updated branch: {branchName}"));

        var serviceContent = await environment.ReadFileFromBranchAsync(
            branchName,
            environment.GetSampleRelativePath("Services", "LegacyWeatherService.cs")).ConfigureAwait(false);
        var repositoryContent = await environment.ReadFileFromBranchAsync(
            branchName,
            environment.GetSampleRelativePath("Data", "LegacyWeatherRepository.cs")).ConfigureAwait(false);

        var serviceRoot = CSharpSyntaxTree.ParseText(serviceContent).GetCompilationUnitRoot();
        var serviceClass = serviceRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(node => node.Identifier.Text == "LegacyWeatherService");

        var asyncServiceMethod = serviceClass.Members.OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.Text == "GetWeatherReport");
        Assert.That(asyncServiceMethod.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)), Is.True);
        Assert.That(asyncServiceMethod.ReturnType.ToString(), Is.EqualTo("System.Threading.Tasks.Task<LegacyWeatherReport>"));
        Assert.That(asyncServiceMethod.ParameterList.Parameters.Any(p => p.Identifier.Text == "cancellationToken"), Is.True);

        var serviceAwaitExpressions = asyncServiceMethod.DescendantNodes().OfType<AwaitExpressionSyntax>().ToArray();
        Assert.That(serviceAwaitExpressions.Length, Is.EqualTo(2), "Service should await both repository calls.");
        Assert.That(
            serviceAwaitExpressions.All(expr => expr.Expression.ToString().Contains("cancellationToken", StringComparison.Ordinal)),
            "Awaited calls should forward the cancellation token.");

        var repositoryRoot = CSharpSyntaxTree.ParseText(repositoryContent).GetCompilationUnitRoot();
        var repositoryClass = repositoryRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(node => node.Identifier.Text == "LegacyWeatherRepository");

        var primaryMethod = repositoryClass.Members.OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.Text == "GetPrimaryObservation");
        Assert.That(primaryMethod.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)), Is.True);
        Assert.That(primaryMethod.ReturnType.ToString(), Is.EqualTo("System.Threading.Tasks.Task<LegacyWeatherObservation>"));
        Assert.That(primaryMethod.ParameterList.Parameters.Any(p => p.Identifier.Text == "cancellationToken"), Is.True);
        Assert.That(
            primaryMethod.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(invocation => invocation.Expression.ToString().Contains("GetLatestObservationAsync", StringComparison.Ordinal)),
            "Repository method should invoke the async database call.");

        var secondaryMethod = repositoryClass.Members.OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.Text == "GetSecondaryObservation");
        Assert.That(secondaryMethod.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)), Is.True);
        Assert.That(
            secondaryMethod.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(invocation => invocation.Expression.ToString().Contains("GetLatestObservationAsync", StringComparison.Ordinal)),
            "Secondary repository method should invoke the async database call.");
    }

    [Test]
    public async Task ProcessSolution_AppendsAsyncMethodsWhenRequested()
    {
        await using var environment = await LegacyWebAppTestEnvironment.CreateAsync().ConfigureAwait(false);
        var processor = new AsyncConversionProcessor();

        var outputFile = Path.Combine(environment.TempDirectory, "LegacyWeatherServiceAppendPreview.cs");
        var branchName = environment.CreateBranchName();

        var parameters = new AsyncConversionParameters(
            outputFile,
            "LegacyWebApp.Services.LegacyWeatherService",
            MethodName: "GetWeatherReport",
            ReplaceExistingMethods: false,
            BranchName: branchName);

        await processor.ProcessSolution(environment.Solution, parameters, NullLogger.Instance, CancellationToken.None)
            .ConfigureAwait(false);

        var serviceContent = await environment.ReadFileFromBranchAsync(
            branchName,
            environment.GetSampleRelativePath("Services", "LegacyWeatherService.cs")).ConfigureAwait(false);

        var repositoryContent = await environment.ReadFileFromBranchAsync(
            branchName,
            environment.GetSampleRelativePath("Data", "LegacyWeatherRepository.cs")).ConfigureAwait(false);

        var serviceRoot = CSharpSyntaxTree.ParseText(serviceContent).GetCompilationUnitRoot();
        var serviceClass = serviceRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(node => node.Identifier.Text == "LegacyWeatherService");
        var overloads = serviceClass.Members.OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.Text == "GetWeatherReport")
            .ToArray();

        Assert.That(overloads.Length, Is.EqualTo(2), "Both sync and async versions should be present.");
        Assert.That(overloads.Count(method => method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword))), Is.EqualTo(1));
        Assert.That(overloads.Count(method => method.ParameterList.Parameters.Any(p => p.Identifier.Text == "cancellationToken")), Is.EqualTo(1));
        Assert.That(overloads.Count(method => method.ParameterList.Parameters.Count == 1), Is.EqualTo(1));

        var repositoryRoot = CSharpSyntaxTree.ParseText(repositoryContent).GetCompilationUnitRoot();
        var repositoryClass = repositoryRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(node => node.Identifier.Text == "LegacyWeatherRepository");
        var repositoryOverloads = repositoryClass.Members.OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.Text == "GetPrimaryObservation")
            .ToArray();

        Assert.That(repositoryOverloads.Length, Is.EqualTo(2), "Repository should include both method variants.");
        Assert.That(repositoryOverloads.Count(method => method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword))), Is.EqualTo(1));
    }

    [Test]
    public async Task GenerateAsyncVersion_ReturnsNullWhenNoEligibleMethods()
    {
        await using var environment = await LegacyWebAppTestEnvironment.CreateAsync().ConfigureAwait(false);

        var cache = await CachedSymbolFinder.FromCache(environment.Solution).ConfigureAwait(false);
        var serviceType = cache.GetSymbolByMetadataName("LegacyWebApp.Services.LegacyTrivialService") as INamedTypeSymbol;
        Assert.That(serviceType, Is.Not.Null);

        var engine = new AsyncConversionEngine(cache, environment.Solution);
        var conversionResult = await engine.GenerateAsyncVersion(serviceType!, methodName: null, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.That(conversionResult, Is.Null);
    }

    [Test]
    public async Task GenerateAsyncVersion_UsesProvidedInvocationTreeResult()
    {
        await using var environment = await LegacyWebAppTestEnvironment.CreateAsync().ConfigureAwait(false);

        var cache = await CachedSymbolFinder.FromCache(environment.Solution).ConfigureAwait(false);
        var serviceType = cache.GetSymbolByMetadataName("LegacyWebApp.Services.LegacyWeatherService") as INamedTypeSymbol;
        Assert.That(serviceType, Is.Not.Null);

        var treeResult = await InvocationTreeBuilder.BuildInvocationTreeWithCacheAsync(
            cache,
            serviceType!,
            environment.Solution,
            methodFilter: null,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        var engine = new AsyncConversionEngine(cache, environment.Solution);
        var conversionResult = await engine.GenerateAsyncVersion(
                serviceType!,
                methodName: null,
                CancellationToken.None,
                treeResult)
            .ConfigureAwait(false);

        Assert.That(conversionResult, Is.Not.Null);
        Assert.That(conversionResult!.ConvertedMethods, Is.Not.Empty);

        var asyncMethod = conversionResult.ConvertedMethods
            .Single(method => method.AsyncMethod.Identifier.Text == "GetWeatherReport");
        Assert.That(asyncMethod.AsyncMethod.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)), Is.True);
    }

    private sealed class LegacyWebAppTestEnvironment : IAsyncDisposable
    {
        private LegacyWebAppTestEnvironment(AdhocWorkspace workspace, string tempDirectory)
        {
            Workspace = workspace;
            TempDirectory = tempDirectory;
        }

        public AdhocWorkspace Workspace { get; }

        public Solution Solution => Workspace.CurrentSolution;

        public string TempDirectory { get; }

        public string RepositoryPath => TempDirectory;

        public static async Task<LegacyWebAppTestEnvironment> CreateAsync()
        {
            var repositoryRoot = FindRepositoryRoot();
            var sourcePath = Path.Combine(repositoryRoot, "samples", "LegacyWebApp");
            var tempDirectory = Path.Combine(Path.GetTempPath(), $"async-tests-{Guid.NewGuid():N}");

            CopyDirectory(sourcePath, tempDirectory);

            var requiredServicePath = Path.Combine(
                tempDirectory,
                "LegacyWebApp",
                "LegacyWebApp",
                "Services",
                "LegacyWeatherService.cs");
            if (!File.Exists(requiredServicePath))
            {
                throw new DirectoryNotFoundException($"Missing expected file at '{requiredServicePath}'.");
            }

            await RunGitAsync(tempDirectory, "init").ConfigureAwait(false);
            await RunGitAsync(tempDirectory, "config user.name \"Test User\"").ConfigureAwait(false);
            await RunGitAsync(tempDirectory, "config user.email \"test@example.com\"").ConfigureAwait(false);
            await RunGitAsync(tempDirectory, "add .").ConfigureAwait(false);
            await RunGitAsync(tempDirectory, "commit -m \"Initial\"").ConfigureAwait(false);

            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(CancellationToken).Assembly.Location),
            };

            var projectInfo = ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "LegacyWebApp",
                "LegacyWebApp",
                LanguageNames.CSharp,
                filePath: Path.Combine(tempDirectory, "LegacyWebApp", "LegacyWebApp", "LegacyWebApp", "LegacyWebApp.csproj"),
                metadataReferences: references);

            var solutionInfo = SolutionInfo.Create(
                SolutionId.CreateNewId(),
                VersionStamp.Create(),
                Path.Combine(tempDirectory, "LegacyWebApp", "LegacyWebApp.sln"),
                projects: new[] { projectInfo });

            workspace.AddSolution(solutionInfo);

            foreach (var relativePath in GetDocumentPaths())
            {
                var fullPath = Path.Combine(tempDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var text = SourceText.From(await File.ReadAllTextAsync(fullPath).ConfigureAwait(false));
                var documentInfo = DocumentInfo.Create(
                    DocumentId.CreateNewId(projectId),
                    Path.GetFileName(fullPath),
                    filePath: fullPath,
                    loader: TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create())));
                workspace.AddDocument(documentInfo);
            }

            return new LegacyWebAppTestEnvironment(workspace, tempDirectory);
        }

        public string CreateBranchName() => $"async-conversion/{Guid.NewGuid():N}";

        public string GetSampleRelativePath(params string[] segments)
        {
            var parts = new List<string> { "LegacyWebApp", "LegacyWebApp" };
            parts.AddRange(segments);
            return string.Join('/', parts);
        }

        public async Task<string> ReadFileFromBranchAsync(string branchName, string relativePath)
        {
            var pathArgument = relativePath.Replace('\\', '/');
            return await RunGitAsync(RepositoryPath, $"show {branchName}:{pathArgument}").ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            Workspace.Dispose();
            try
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures during test runs.
            }

            await Task.CompletedTask;
        }

        private static IEnumerable<string> GetDocumentPaths() => new[]
        {
            "LegacyWebApp/LegacyWebApp/Services/LegacyWeatherService.cs",
            "LegacyWebApp/LegacyWebApp/Services/LegacyWeatherReport.cs",
            "LegacyWebApp/LegacyWebApp/Services/LegacyTrivialService.cs",
            "LegacyWebApp/LegacyWebApp/Data/LegacyWeatherRepository.cs",
            "LegacyWebApp/LegacyWebApp/Data/LegacyWeatherDatabase.cs",
            "LegacyWebApp/LegacyWebApp/Data/LegacyWeatherObservation.cs",
        };

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory is not null)
            {
                var solutionPath = Path.Combine(directory.FullName, "RoslynRunner.sln");
                if (File.Exists(solutionPath))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Unable to locate repository root.");
        }

        private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);

            foreach (var file in Directory.GetFiles(sourceDirectory))
            {
                var destination = Path.Combine(destinationDirectory, Path.GetFileName(file));
                File.Copy(file, destination, overwrite: true);
            }

            foreach (var directory in Directory.GetDirectories(sourceDirectory))
            {
                var destination = Path.Combine(destinationDirectory, Path.GetFileName(directory));
                CopyDirectory(directory, destination);
            }
        }

        private static async Task<string> RunGitAsync(string workingDirectory, string arguments)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                throw new InvalidOperationException("Failed to start git process.");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync().ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"git {arguments} failed with exit code {process.ExitCode}:{Environment.NewLine}{error}");
            }

            return output.ReplaceLineEndings("\n");
        }
    }
}
