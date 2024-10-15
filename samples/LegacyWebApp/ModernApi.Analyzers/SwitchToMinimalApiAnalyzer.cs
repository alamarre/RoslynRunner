using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ModernApi.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class SwitchToMinimalApiAnalyzer : DiagnosticAnalyzer
{
    private const string DiagnosticId = "Web0001";
    private static readonly LocalizableString Title = "Use Minimal APIs";
    private static readonly LocalizableString MessageFormat = "Consider using Minimal APIs instead of {0}";
    private static readonly LocalizableString Description = "ApiController attribute detected. Consider using Minimal APIs.";
    private const string Category = "Design";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        DiagnosticId, Title, MessageFormat, Category,
        DiagnosticSeverity.Warning, isEnabledByDefault: true, description: Description);
    
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeSymbol, SymbolKind.NamedType); 
    }
    
    private static void AnalyzeSymbol(SymbolAnalysisContext context)
    {
        var namedTypeSymbol = (INamedTypeSymbol)context.Symbol;

        // Check if the type has the ApiControllerAttribute
        foreach (var attribute in namedTypeSymbol.GetAttributes())
        {
            if (attribute.AttributeClass?.ToString() == "Microsoft.AspNetCore.Mvc.ApiControllerAttribute")
            {
                var diagnostic = Diagnostic.Create(Rule, namedTypeSymbol.Locations[0], namedTypeSymbol.Name);
                context.ReportDiagnostic(diagnostic);
                break; 
            }
        }
    }
}
