using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynRunner.Abstractions;
using RoslynRunner.Core;
using RoslynRunner.Core.Extensions;
using RoslynRunner.Utilities.InvocationTrees;

namespace RoslynRunner.AsyncConversion.UnitTests;

public class AsyncConversionProcessorTests
{
    [Test]
    public async Task ProcessSolution_GeneratesAsyncRecursiveServiceFileWithAwaitedCalls()
    {
        var setup = CreateAsyncConversionSolution();
        using var workspace = setup.Workspace;
        var solution = setup.Solution;
        var tempDirectory = setup.TempDirectory;

        var outputDirectory = Path.Combine(Path.GetTempPath(), $"async-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var outputFile = Path.Combine(outputDirectory, "RecursiveServiceAsync.cs");

        var processor = new AsyncConversionProcessor();
        var parameters = new AsyncConversionParameters(outputFile, "Sample.RecursiveService");

        var runContext = new RunContext(Guid.NewGuid());
        RunContextAccessor.RunContext = runContext;

        List<string> outputMessages;
        List<string> errorMessages;
        try
        {
            await processor.ProcessSolution(solution, parameters, NullLogger.Instance, CancellationToken.None);
            outputMessages = runContext.Output.ToList();
            errorMessages = runContext.Errors.ToList();
        }
        finally
        {
            RunContextAccessor.Clear();
        }

        try
        {
            Assert.That(File.Exists(outputFile), Is.True, "Processor should create the async output file.");
            Assert.That(errorMessages, Is.Empty, "Processor should not report errors.");
            Assert.That(outputMessages, Does.Contain($"Created file: {Path.GetFullPath(outputFile)}"),
                "Run context output should include the generated file path.");

            var generatedCode = await File.ReadAllTextAsync(outputFile);
            var root = CSharpSyntaxTree.ParseText(generatedCode).GetCompilationUnitRoot();
            var classDeclaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Single(node => node.Identifier.Text == "RecursiveService");

            var getValues = classDeclaration.Members.OfType<MethodDeclarationSyntax>()
                .Single(method => method.Identifier.Text == "GetValues");
            Assert.That(getValues.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)),
                "GetValues should be marked async.");
            Assert.That(getValues.ReturnType.ToString(),
                Is.EqualTo("System.Threading.Tasks.Task<IEnumerable<int>>"));
            Assert.That(getValues.ParameterList.Parameters.Any(p => p.Identifier.Text == "cancellationToken"),
                "GetValues should accept a cancellation token.");

            var getPrimary = classDeclaration.Members.OfType<MethodDeclarationSyntax>()
                .Single(method => method.Identifier.Text == "GetPrimary");
            Assert.That(getPrimary.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)),
                "GetPrimary should be marked async.");
            Assert.That(getPrimary.ParameterList.Parameters.Any(p => p.Identifier.Text == "cancellationToken"),
                "GetPrimary should accept a cancellation token.");

            var queryAwait = getPrimary.DescendantNodes().OfType<AwaitExpressionSyntax>()
                .FirstOrDefault(expr => expr.Expression is InvocationExpressionSyntax invocation &&
                                         invocation.Expression.ToString().Contains("QueryAsync", StringComparison.Ordinal));
            Assert.That(queryAwait, Is.Not.Null, "GetPrimary should await the async query method.");

            var invocationExpression = (InvocationExpressionSyntax)queryAwait!.Expression;
            Assert.That(invocationExpression.ArgumentList.Arguments.Last().Expression.ToString(),
                Is.EqualTo("cancellationToken"),
                "The generated async call should forward the cancellation token.");
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }

            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Test]
    public async Task GenerateAsyncVersion_ReturnsNullWhenNoEligibleMethods()
    {
        var setup = CreateAsyncConversionSolution();
        using var workspace = setup.Workspace;
        var solution = setup.Solution;
        var tempDirectory = setup.TempDirectory;

        try
        {
            var cache = await CachedSymbolFinder.FromCache(solution);
            var serviceType = cache.GetSymbolByMetadataName("Sample.TrivialService") as INamedTypeSymbol;
            Assert.That(serviceType, Is.Not.Null);

            var engine = new AsyncConversionEngine(cache, solution);
            var conversionResult = await engine.GenerateAsyncVersion(serviceType!, methodName: null, CancellationToken.None);

            Assert.That(conversionResult, Is.Null);
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
    public async Task GenerateAsyncVersion_UsesProvidedInvocationTreeResult()
    {
        var setup = CreateAsyncConversionSolution();
        using var workspace = setup.Workspace;
        var solution = setup.Solution;
        var tempDirectory = setup.TempDirectory;

        try
        {
            var cache = await CachedSymbolFinder.FromCache(solution);
            var serviceType = cache.GetSymbolByMetadataName("Sample.RecursiveService") as INamedTypeSymbol;
            Assert.That(serviceType, Is.Not.Null);

            var treeResult = await InvocationTreeBuilder.BuildInvocationTreeWithCacheAsync(
                cache,
                serviceType!,
                solution,
                methodFilter: null,
                cancellationToken: CancellationToken.None);

            var engine = new AsyncConversionEngine(cache, solution);
            var conversionResult = await engine.GenerateAsyncVersion(
                serviceType!,
                methodName: null,
                CancellationToken.None,
                treeResult);

            Assert.That(conversionResult, Is.Not.Null);
            Assert.That(conversionResult!.ConvertedMethods, Is.Not.Empty);

            var asyncMethod = conversionResult.ConvertedMethods
                .Single(method => method.AsyncMethod.Identifier.Text == "GetPrimary");
            Assert.That(asyncMethod.AsyncMethod.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)), Is.True);
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
    public async Task ProcessSolution_CanAppendAsyncMethodsAlongsideOriginals()
    {
        var setup = CreateAsyncConversionSolution();
        using var workspace = setup.Workspace;
        var solution = setup.Solution;
        var tempDirectory = setup.TempDirectory;

        var outputDirectory = Path.Combine(Path.GetTempPath(), $"async-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var outputFile = Path.Combine(outputDirectory, "RecursiveServiceAsync.cs");

        var processor = new AsyncConversionProcessor();
        var parameters = new AsyncConversionParameters(
            outputFile,
            "Sample.RecursiveService",
            ReplaceExistingMethods: false);

        var runContext = new RunContext(Guid.NewGuid());
        RunContextAccessor.RunContext = runContext;

        try
        {
            await processor.ProcessSolution(solution, parameters, NullLogger.Instance, CancellationToken.None);

            Assert.That(File.Exists(outputFile), Is.True, "Processor should create the async output file.");

            var generatedCode = await File.ReadAllTextAsync(outputFile);
            var root = CSharpSyntaxTree.ParseText(generatedCode).GetCompilationUnitRoot();
            var classDeclaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Single(node => node.Identifier.Text == "RecursiveService");

            var getValuesMethods = classDeclaration.Members.OfType<MethodDeclarationSyntax>()
                .Where(method => method.Identifier.Text == "GetValues")
                .ToArray();

            Assert.That(getValuesMethods.Length, Is.EqualTo(2), "Both sync and async variants should be present.");

            var asyncVariant = getValuesMethods.Single(method => method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)));
            Assert.That(asyncVariant.ParameterList.Parameters.Any(p => p.Identifier.Text == "cancellationToken"),
                "Async method should accept a cancellation token.");
            Assert.That(asyncVariant.ReturnType.ToString(),
                Is.EqualTo("System.Threading.Tasks.Task<IEnumerable<int>>"));

            var syncVariant = getValuesMethods.Single(method => !method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)));
            Assert.That(syncVariant.ParameterList.Parameters.Count, Is.EqualTo(1),
                "Original method signature should remain unchanged.");
        }
        finally
        {
            RunContextAccessor.Clear();

            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }

            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static (AdhocWorkspace Workspace, Solution Solution, string TempDirectory) CreateAsyncConversionSolution()
    {
        var workspace = new AdhocWorkspace();
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"async-solution-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        var solutionPath = Path.Combine(tempDirectory, "Test.sln");
        var projectPath = Path.Combine(tempDirectory, "TestProject.csproj");
        File.WriteAllText(solutionPath, string.Empty);
        File.WriteAllText(projectPath, string.Empty);

        var projectId = ProjectId.CreateNewId();
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(CancellationToken).Assembly.Location)
        };

        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "TestProject",
            "TestProject",
            LanguageNames.CSharp,
            filePath: projectPath,
            metadataReferences: references);

        var solutionInfo = SolutionInfo.Create(
            SolutionId.CreateNewId(),
            VersionStamp.Create(),
            solutionPath,
            projects: new[] { projectInfo });

        workspace.AddSolution(solutionInfo);

        var documentId = DocumentId.CreateNewId(projectId);
        var sourceText = SourceText.From(GetTestSource(), Encoding.UTF8);
        var documentInfo = DocumentInfo.Create(
            documentId,
            "RecursiveService.cs",
            filePath: Path.Combine(tempDirectory, "RecursiveService.cs"),
            loader: TextLoader.From(TextAndVersion.Create(sourceText, VersionStamp.Create())));

        workspace.AddDocument(documentInfo);

        return (workspace, workspace.CurrentSolution, tempDirectory);
    }

    private static string GetTestSource() => @"using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sample;

public interface IRecursiveConnection
{
    IEnumerable<int> Query(int id);
    Task<IEnumerable<int>> QueryAsync(int id, CancellationToken cancellationToken = default);
}

public class RecursiveService
{
    private readonly IRecursiveConnection _connection;

    public RecursiveService(IRecursiveConnection connection)
    {
        _connection = connection;
    }

    public IEnumerable<int> GetValues(int id)
    {
        return GetPrimary(id);
    }

    private IEnumerable<int> GetPrimary(int id)
    {
        var result = _connection.Query(id);
        if (result.Any())
        {
            return result;
        }

        return GetSecondary(id);
    }

    private IEnumerable<int> GetSecondary(int id)
    {
        if (id <= 0)
        {
            return Enumerable.Empty<int>();
        }

        return GetPrimary(id - 1);
    }
}

public interface IValuesService
{
    IEnumerable<int> GetValues(int id);
}

public class ValuesService : IValuesService
{
    private readonly RecursiveService _recursiveService;

    public ValuesService(RecursiveService recursiveService)
    {
        _recursiveService = recursiveService;
    }

    public IEnumerable<int> GetValues(int id)
    {
        return _recursiveService.GetValues(id);
    }
}

public class TrivialService
{
    public int Echo(int value)
    {
        return value;
    }
}
";
}
