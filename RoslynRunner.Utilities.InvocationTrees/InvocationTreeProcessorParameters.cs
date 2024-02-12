using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RoslynRunner.Utilities.InvocationTrees;

public record InvocationTreeProcessorParameters(string StartingSymbol, InvocationDiagram[]? Diagrams);

public record InvocationDiagram(string OutputPath, string Name, string DiagramType, bool SeparateDiagrams, string? Filter);

