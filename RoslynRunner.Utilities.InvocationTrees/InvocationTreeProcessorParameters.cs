using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RoslynRunner.Utilities.InvocationTrees;

public record InvocationTreeProcessorParameters(
    string StartingSymbol,
    InvocationDiagram[]? Diagrams,
    string? MethodFilter = null,
    int? MaxImplementations = null);

public record InvocationDiagram(
    string OutputPath,
    string Name,
    string DiagramType,
    bool SeparateDiagrams,
    string? InclusivePruneFilter,
    bool WriteAllMethods,
    string? Filter);
