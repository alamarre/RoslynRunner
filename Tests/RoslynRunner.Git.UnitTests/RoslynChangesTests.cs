using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;

namespace RoslynRunner.Git.UnitTests;

public class RoslynChangesTests
{
    private const string InitialSource = """
using System.Threading.Tasks;

namespace Sample;

public class SomeClass
{
    public Task MethodA()
    {
        return Task.CompletedTask;
    }

    public Task MethodB()
    {
        return Task.CompletedTask;
    }
}
""";

    [Test]
    public async Task ApplyEachAsync_CommitsChangesToIndividualBranches()
    {
        await using var repository = await TestRepository.CreateAsync(InitialSource).ConfigureAwait(false);
        using var workspace = CreateWorkspace(repository);
        var document = workspace.CurrentSolution.Projects.Single().Documents.Single();

        var methods = await GetMethodDeclarationsAsync(document).ConfigureAwait(false);

        var changes = new RoslynChanges(repository.RepositoryPath);
        var firstChange = changes.NewChangeSet("a", "Add CancellationToken to MethodA");
        firstChange.ReplaceMethod(document, methods.MethodA, AddCancellationToken(methods.MethodA));
        var secondChange = changes.NewChangeSet("b", "Add CancellationToken to MethodB");
        secondChange.ReplaceMethod(document, methods.MethodB, AddCancellationToken(methods.MethodB));

        var branchPrefix = $"test/{Guid.NewGuid():N}-";
        await changes.ApplyEachAsync(branchPrefix, CancellationToken.None).ConfigureAwait(false);

        using var repo = new Repository(repository.RepositoryPath);
        var branchA = repo.Branches[branchPrefix + "a"];
        var branchB = repo.Branches[branchPrefix + "b"];

        Assert.That(branchA, Is.Not.Null, "Expected branch for first change to exist.");
        Assert.That(branchB, Is.Not.Null, "Expected branch for second change to exist.");
        Assert.That(branchA!.Tip.Message.Trim(), Is.EqualTo("Add CancellationToken to MethodA"));
        Assert.That(branchB!.Tip.Message.Trim(), Is.EqualTo("Add CancellationToken to MethodB"));

        Commands.Checkout(repo, branchA);
        var branchAContent = await File.ReadAllTextAsync(repository.DocumentPath).ConfigureAwait(false);
        Assert.That(branchAContent, Does.Contain("CancellationToken cancellationToken = default"));
        Assert.That(branchAContent, Does.Contain("MethodB()"), "MethodB should remain unchanged on branch A.");

        Commands.Checkout(repo, branchB);
        var branchBContent = await File.ReadAllTextAsync(repository.DocumentPath).ConfigureAwait(false);
        Assert.That(branchBContent, Does.Contain("CancellationToken cancellationToken = default"));
        Assert.That(branchBContent, Does.Contain("MethodA()"), "MethodA should remain unchanged on branch B.");

        Commands.Checkout(repo, repo.Branches[repository.DefaultBranchName]);
        var baseContent = await File.ReadAllTextAsync(repository.DocumentPath).ConfigureAwait(false);
        Assert.That(baseContent, Is.EqualTo(InitialSource.Replace("\r\n", "\n")));
    }

    [Test]
    public async Task ApplyAllAsync_CommitsAllChangesTogether()
    {
        await using var repository = await TestRepository.CreateAsync(InitialSource).ConfigureAwait(false);
        using var workspace = CreateWorkspace(repository);
        var document = workspace.CurrentSolution.Projects.Single().Documents.Single();
        var methods = await GetMethodDeclarationsAsync(document).ConfigureAwait(false);

        var changes = new RoslynChanges(repository.RepositoryPath);
        changes.NewChangeSet("a", "Update MethodA").ReplaceMethod(document, methods.MethodA, AddCancellationToken(methods.MethodA));
        changes.NewChangeSet("b", "Update MethodB").ReplaceMethod(document, methods.MethodB, AddCancellationToken(methods.MethodB));

        var branchName = $"feature/{Guid.NewGuid():N}";
        await changes.ApplyAllAsync(branchName, "Add CancellationToken to both methods", CancellationToken.None).ConfigureAwait(false);

        using var repo = new Repository(repository.RepositoryPath);
        var branch = repo.Branches[branchName];
        Assert.That(branch, Is.Not.Null);
        Assert.That(branch!.Tip.Message.Trim(), Is.EqualTo("Add CancellationToken to both methods"));

        Commands.Checkout(repo, branch);
        var content = await File.ReadAllTextAsync(repository.DocumentPath).ConfigureAwait(false);
        Assert.That(content, Does.Contain("CancellationToken cancellationToken = default"));
        Assert.That(content, Does.Contain("using System.Threading;"));

        Commands.Checkout(repo, repo.Branches[repository.DefaultBranchName]);
        var baseContent = await File.ReadAllTextAsync(repository.DocumentPath).ConfigureAwait(false);
        Assert.That(baseContent, Is.EqualTo(InitialSource.Replace("\r\n", "\n")));
    }

    [Test]
    public async Task GetDiffAsync_ReturnsUnifiedDiffWithoutModifyingWorkingTree()
    {
        await using var repository = await TestRepository.CreateAsync(InitialSource).ConfigureAwait(false);
        using var workspace = CreateWorkspace(repository);
        var document = workspace.CurrentSolution.Projects.Single().Documents.Single();
        var methods = await GetMethodDeclarationsAsync(document).ConfigureAwait(false);

        var changes = new RoslynChanges(repository.RepositoryPath);
        changes.NewChangeSet("a", "Update MethodA").ReplaceMethod(document, methods.MethodA, AddCancellationToken(methods.MethodA));
        var diff = await changes.GetDiffAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.That(diff, Does.Contain("+    public Task MethodA("));
        Assert.That(diff, Does.Contain("CancellationToken cancellationToken = default"));

        using var repo = new Repository(repository.RepositoryPath);
        var workingTreeChanges = repo.Diff.Compare<TreeChanges>();
        Assert.That(workingTreeChanges.Count, Is.EqualTo(0), "GetDiffAsync should not leave changes in the working tree.");
    }

    private static async Task<(MethodDeclarationSyntax MethodA, MethodDeclarationSyntax MethodB)> GetMethodDeclarationsAsync(Document document)
    {
        var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var methods = root!.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        return (methods[0], methods[1]);
    }

    private static MethodDeclarationSyntax AddCancellationToken(MethodDeclarationSyntax method)
    {
        var cancellationTokenType = SyntaxFactory.ParseTypeName("System.Threading.CancellationToken")
            .WithAdditionalAnnotations(Simplifier.Annotation, Simplifier.AddImportsAnnotation);

        var parameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier("cancellationToken"))
            .WithType(cancellationTokenType)
            .WithDefault(SyntaxFactory.EqualsValueClause(SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression)))
            .WithAdditionalAnnotations(Simplifier.Annotation, Simplifier.AddImportsAnnotation, Formatter.Annotation);

        var updatedParameterList = method.ParameterList ?? SyntaxFactory.ParameterList();
        updatedParameterList = updatedParameterList.AddParameters(parameter);

        return method.WithParameterList(updatedParameterList);
    }

    private static AdhocWorkspace CreateWorkspace(TestRepository repository)
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("SampleProject", LanguageNames.CSharp);

        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(CancellationToken).Assembly.Location),
        };

        foreach (var reference in references)
        {
            project = project.AddMetadataReference(reference);
        }

        workspace.TryApplyChanges(project.Solution);

        var sourceText = SourceText.From(File.ReadAllText(repository.DocumentPath));
        var documentId = DocumentId.CreateNewId(project.Id);
        var documentInfo = DocumentInfo.Create(
            documentId,
            Path.GetFileName(repository.DocumentPath),
            filePath: repository.DocumentPath,
            loader: TextLoader.From(TextAndVersion.Create(sourceText, VersionStamp.Create())));

        workspace.AddDocument(documentInfo);
        return workspace;
    }

    private sealed class TestRepository : IAsyncDisposable
    {
        private TestRepository(string repositoryPath, string documentPath, string defaultBranchName)
        {
            RepositoryPath = repositoryPath;
            DocumentPath = documentPath;
            DefaultBranchName = defaultBranchName;
        }

        public string RepositoryPath { get; }

        public string DocumentPath { get; }

        public string DefaultBranchName { get; }

        public static async Task<TestRepository> CreateAsync(string source)
        {
            var repositoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(repositoryPath);
            var documentPath = Path.Combine(repositoryPath, "SomeClass.cs");
            await File.WriteAllTextAsync(documentPath, source.Replace("\r\n", "\n"));

            Repository.Init(repositoryPath);
            using var repo = new Repository(repositoryPath);
            var defaultBranch = repo.Head.FriendlyName;
            repo.Config.Set("user.name", "RoslynRunnerTests");
            repo.Config.Set("user.email", "roslynrunner@example.com");
            Commands.Stage(repo, Path.GetFileName(documentPath));
            var signature = new Signature("RoslynRunner", "roslynrunner@example.com", DateTimeOffset.Now);
            repo.Commit("Initial commit", signature, signature);

            return new TestRepository(repositoryPath, documentPath, defaultBranch);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(RepositoryPath))
            {
                Directory.Delete(RepositoryPath, true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
