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
    [property:JsonConverter(typeof(JsonRawStringConverter))]
    string? Context);

public record RunCommand<T>(
    string PrimarySolution,
    bool PersistSolution,
    string ProcessorName,
    string? AssemblyLoadContextPath,
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
            JsonSerializer.Serialize(Context));
    }
}