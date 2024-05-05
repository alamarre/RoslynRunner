using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynRunner;

public record RunCommand(
    string PrimarySolution,
    bool PersistSolution,
    string? ProcessorSolution,
    string ProcessorName,
    string? ProcessorProjectName,
    string? AssemblyLoadContextPath,
    List<LibraryReference>? LibraryReferences,
    [property: JsonConverter(typeof(JsonRawStringConverter))]
    string? Context);

public record RunCommand<T>(
    string PrimarySolution,
    bool PersistSolution,
    string ProcessorName,
    string? AssemblyLoadContextPath,
    List<LibraryReference>? LibraryReferences,
    T? Context)
{
    public RunCommand ToRunCommand()
    {
        return new RunCommand(
            PrimarySolution,
            PersistSolution,
            null,
            ProcessorName,
            null,
            AssemblyLoadContextPath,
            LibraryReferences,
            JsonSerializer.Serialize(Context));
    }
}
