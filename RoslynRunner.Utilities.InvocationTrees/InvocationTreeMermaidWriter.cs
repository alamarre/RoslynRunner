using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RoslynRunner.Utilities.InvocationTrees
{
	internal class InvocationTreeMermaidWriter
	{
		public static string GetMermaidDag(IEnumerable<InvocationMethod> methods)
		{
			StringBuilder sb = new StringBuilder();
			HashSet<string> written = new HashSet<string>();
			sb.AppendLine("```mermaid");
			sb.AppendLine("classDiagram");
			foreach (var method in methods)
			{
				string sourceClass = method.MethodSymbol.ContainingType.Name;
				string sourceMethodName = method.MethodSymbol.Name;
				foreach (var call in method.InvokedMethods)
				{
					var newMethod = call.Value.MethodSymbol;
					sb.AppendLine($"{sourceClass} --|> {newMethod.ContainingType.Name} : {newMethod.Name}");
				}

				foreach (var implementation in method.Implementations)
				{
					var newMethod = implementation.MethodSymbol;
					var relationship = $"{sourceClass} <|-- {newMethod.ContainingType.Name}";
					if (written.Add(relationship))
					{
						sb.AppendLine(relationship);
					}
				}
			}
			sb.AppendLine("```");
			return sb.ToString();
		}

		public static string GetMermaidDagForCallers(IEnumerable<InvocationMethod> methods)
		{
			StringBuilder sb = new StringBuilder();
			HashSet<string> written = new HashSet<string>();
			sb.AppendLine("```mermaid");
			sb.AppendLine("classDiagram");
			foreach (var method in methods)
			{
				string sourceClass = method.MethodSymbol.ContainingType.Name;
				string sourceMethodName = method.MethodSymbol.Name;
				foreach (var caller in method.Callers)
				{
					var newMethod = caller.MethodSymbol;
					sb.AppendLine($"{sourceClass} --|> {newMethod.ContainingType.Name} : {newMethod.Name}");
				}

			}
			sb.AppendLine("```");
			return sb.ToString();
		}
	}
}
