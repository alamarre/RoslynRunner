using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynRunner.Abstractions;
using RoslynRunner.Utilities.InvocationTrees;

namespace RoslynRunner.SerializationTests;

public class RunCommandSerializationTests
{
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task CallChainsContext_WritesD3DiagramForDbContextCalls(bool useCache, bool separateDiagrams)
    {
        using var workspace = new AdhocWorkspace();
        var outputPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(),
            filePath: Path.Combine(outputPath, "Sample.sln")));
        var project = workspace.AddProject("Sample", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        var document = project.AddDocument("Sample.cs", SourceText.From("""
            namespace Microsoft.EntityFrameworkCore
            {
                public class DbContext { public void SaveChanges() { } public void Read() { } }
            }
            namespace Jellyfin.Api.Controllers
            {
                public class PlaylistsController
                {
                    public void UpdatePlaylist(Microsoft.EntityFrameworkCore.DbContext db) { ViaBranch(db); Save(db); }
                    private void ViaBranch(Microsoft.EntityFrameworkCore.DbContext db) { Save(db); }
                    private void Save(Microsoft.EntityFrameworkCore.DbContext db)
                    {
                        Unrelated();
                        UpdatePlaylist(db);
                        db.SaveChanges();
                        db.Read();
                        db.SaveChanges();
                    }
                    public void Unrelated() { }
                }
            }
            """), filePath: Path.Combine(outputPath, "Sample.cs"));
        var parameters = new InvocationTreeProcessorParameters(
            "Jellyfin.Api.Controllers.PlaylistsController",
            [new InvocationDiagram(outputPath, "UpdatePlaylist.DbContextCalls", "d3", separateDiagrams, null, true,
                "MethodSymbol.ContainingType.MetadataName == \"DbContext\" && MethodSymbol.ContainingType.ContainingNamespace.Name == \"EntityFrameworkCore\" && MethodSymbol.ContainingType.ContainingNamespace.ContainingNamespace.Name == \"Microsoft\"")],
            MethodFilter: "UpdatePlaylist", UseCache: useCache);
        var runContext = new RunContext(Guid.NewGuid());
        RunContextAccessor.RunContext = runContext;
        try
        {
            await new InvocationTreeProcessor().ProcessSolution(document.Project.Solution,
                JsonSerializer.Serialize(parameters), NullLogger.Instance, CancellationToken.None);

            var files = Directory.GetFiles(outputPath, "*.html");
            Assert.That(files, Has.Length.EqualTo(separateDiagrams ? 2 : 1));
            if (separateDiagrams)
            {
                Assert.That(files.Select(Path.GetFileName), Is.EquivalentTo(new[]
                {
                    "Jellyfin.Api.Controllers.PlaylistsController.Save.html",
                    "Jellyfin.Api.Controllers.PlaylistsController.Save_2.html"
                }));
            }
            foreach (var file in files)
            {
                var html = await File.ReadAllTextAsync(file);
                var jsonStart = html.IndexOf("const jsonData = ", StringComparison.Ordinal) + "const jsonData = ".Length;
                var jsonEnd = html.IndexOf("  // Embedded JSON data", jsonStart, StringComparison.Ordinal);
                using var graph = JsonDocument.Parse(html[jsonStart..jsonEnd]);
                var methods = graph.RootElement.EnumerateArray()
                    .DistinctBy(m => m.GetProperty("identifier").GetString()).ToArray();
                var expectedMethods = separateDiagrams
                    ? new[] { "UpdatePlaylist", "Save", Path.GetFileNameWithoutExtension(file).EndsWith("_2") ? "Read" : "SaveChanges" }
                    : new[] { "UpdatePlaylist", "Save", "ViaBranch", "SaveChanges", "Read" };
                Assert.That(methods.Select(m => m.GetProperty("methodName").GetString()),
                    Is.EquivalentTo(expectedMethods));
                var controller = methods.Single(m => m.GetProperty("methodName").GetString() == "UpdatePlaylist");
                var save = methods.Single(m => m.GetProperty("methodName").GetString() == "Save");
                var dbCall = methods.First(m => m.GetProperty("containingTypeFqdn").GetString() == "Microsoft.EntityFrameworkCore.DbContext");
                Assert.That(controller.GetProperty("invokedMethodIdentifiers").EnumerateArray().Select(i => i.GetString()),
                    Does.Contain(save.GetProperty("identifier").GetString()));
                Assert.That(save.GetProperty("invokedMethodIdentifiers").EnumerateArray().Select(i => i.GetString()),
                    Does.Contain(dbCall.GetProperty("identifier").GetString()));
                var rootsStart = html.IndexOf("const startingMethodIds = ", StringComparison.Ordinal) + "const startingMethodIds = ".Length;
                var rootsEnd = html.IndexOf(';', rootsStart);
                var rootIds = JsonSerializer.Deserialize<string[]>(html[rootsStart..rootsEnd]);
                Assert.That(rootIds, Is.EqualTo(new[] { controller.GetProperty("identifier").GetString() }));
                if (separateDiagrams)
                {
                    Assert.That(graph.RootElement.GetArrayLength(), Is.EqualTo(methods.Length));
                    Assert.That(methods[0].GetProperty("methodName").GetString(), Is.EqualTo("UpdatePlaylist"));
                    for (var i = 0; i < methods.Length; i++)
                    {
                        var children = methods[i].GetProperty("invokedMethodIdentifiers").EnumerateArray()
                            .Concat(methods[i].GetProperty("implementationIdentifiers").EnumerateArray()).Select(v => v.GetString());
                        Assert.That(children, Is.EqualTo(i + 1 < methods.Length
                            ? new[] { methods[i + 1].GetProperty("identifier").GetString() } : Array.Empty<string>()));
                        Assert.That(methods[i].GetProperty("callerIdentifiers").EnumerateArray().Select(v => v.GetString()),
                            Is.EqualTo(i > 0 ? new[] { methods[i - 1].GetProperty("identifier").GetString() } : Array.Empty<string>()));
                    }
                }
            }
            Assert.That(runContext.Output, Has.Count.EqualTo(files.Length));
        }
        finally
        {
            RunContextAccessor.Clear();
            if (Directory.Exists(outputPath))
            {
                Directory.Delete(outputPath, recursive: true);
            }
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CallChainsContext_AcceptsObjectAndJsonString(bool encodeAsString)
    {
        const string filter = "MethodSymbol.ContainingType.MetadataName == \"DbContext\" && MethodSymbol.ContainingType.ContainingNamespace.Name == \"EntityFrameworkCore\" && MethodSymbol.ContainingType.ContainingNamespace.ContainingNamespace.Name == \"Microsoft\"";
        var parameters = new InvocationTreeProcessorParameters(
            "Jellyfin.Api.Controllers.PlaylistsController",
            [new InvocationDiagram("diagrams", "UpdatePlaylist.DbContextCalls", "d3", true, null, true, filter)],
            MethodFilter: "UpdatePlaylist", UseCache: true);
        var context = JsonSerializer.Serialize(parameters);
        var json = $$"""
            {
              "PrimarySolution": "Jellyfin.slnx",
              "PersistSolution": false,
              "ProcessorSolution": null,
              "ProcessorName": "CallChains",
              "Context": {{(encodeAsString ? JsonSerializer.Serialize(context) : context)}}
            }
            """;

        var command = JsonSerializer.Deserialize<RunCommand>(json)!;
        var parsed = JsonSerializer.Deserialize<InvocationTreeProcessorParameters>(command.Context!)!;

        Assert.That(parsed.StartingSymbol, Is.EqualTo(parameters.StartingSymbol));
        Assert.That(parsed.MethodFilter, Is.EqualTo("UpdatePlaylist"));
        Assert.That(parsed.UseCache, Is.True);
        Assert.That(parsed.Diagrams, Is.EqualTo(parameters.Diagrams));

        // Saved runs must serialize the context as an object and survive reloading.
        var serialized = JsonSerializer.Serialize(command);
        using var document = JsonDocument.Parse(serialized);
        Assert.That(document.RootElement.GetProperty("Context").ValueKind, Is.EqualTo(JsonValueKind.Object));
        var reloaded = JsonSerializer.Deserialize<RunCommand>(serialized)!;
        Assert.That(reloaded.Context, Is.EqualTo(command.Context));
    }

    [TestCase("null")]
    [TestCase("{\"value\":42}")]
    [TestCase("[1,2,3]")]
    public void RawContext_RoundTrips(string context)
    {
        var command = new RunCommand("sample.sln", false, null, "CustomProcessor", Context: context);
        var serialized = JsonSerializer.Serialize(command);
        var reloaded = JsonSerializer.Deserialize<RunCommand>(serialized)!;

        Assert.That(reloaded.Context, Is.EqualTo(context == "null" ? null : context));
    }
}
