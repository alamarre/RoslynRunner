using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using RoslynRunner.Core;

public class IMethodSymbolExtensionsTests
{
    private static Compilation CreateCompilation(string code)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(code);
        var refs = new[] {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location)
        };
        return CSharpCompilation.Create("TestAssembly", new[] { syntaxTree }, refs);
    }

    private static IMethodSymbol? GetMethodSymbol(Compilation compilation, string typeName, string methodName)
    {
        var type = compilation.GlobalNamespace.GetNamespaceMembers()
            .SelectMany(ns => ns.GetTypeMembers())
            .FirstOrDefault(t => t.MetadataName == typeName);
        return type?.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == methodName);
    }

    [Test]
    public void GetMethodId_ReturnsExpectedId_ForRegularMethod()
    {
        var code = @"
namespace Namespace
{
    public class Type
    {
        public void TestMethod() { }
    }
}";
        var compilation = CreateCompilation(code);
        var methodSymbol = GetMethodSymbol(compilation, "Type", "TestMethod");

        var id = methodSymbol?.GetMethodId();

        Assert.That(id, Is.EqualTo("Namespace.Type.TestMethod()"));
    }

    [Test]
    public void GetMethodId_ReturnsCtor_ForConstructor()
    {
        var code = @"
namespace Namespace
{
    public class Type
    {
        public Type() { }
    }
}";
        var compilation = CreateCompilation(code);
        var type = compilation.GlobalNamespace.GetNamespaceMembers()
            .SelectMany(ns => ns.GetTypeMembers())
            .FirstOrDefault(t => t.Name == "Type");
        var ctor = type?.Constructors.FirstOrDefault();

        var id = ctor?.GetMethodId();

        Assert.That(id, Is.EqualTo("Namespace.Type..ctor()"));
    }

    [Test]
    public void GetMethodId_IncludesParameterTypes()
    {
        var code = @"
namespace Namespace
{
    public class Type
    {
        public void Add(int x) { }
    }
}";
        var compilation = CreateCompilation(code);
        var methodSymbol = GetMethodSymbol(compilation, "Type", "Add");

        var id = methodSymbol?.GetMethodId();

        Assert.That(id, Is.EqualTo("Namespace.Type.Add(int)"));
    }

    [Test]
    public void GetMethodId_IncludesFullyQualifiedParameterTypes()
    {
        var code = @"
namespace Namespace
{
    public class Type
    {
        public int Compare(Type x) { }
    }
}";
        var compilation = CreateCompilation(code);
        var methodSymbol = GetMethodSymbol(compilation, "Type", "Compare");

        var id = methodSymbol?.GetMethodId();

        Assert.That(id, Is.EqualTo("Namespace.Type.Compare(Namespace.Type)"));
    }

    [Test]
    public void GetMethodId_IncludesGenericTypeArguments()
    {
        var code = @"
namespace Namespace
{
    public class Type<TType>
    {
        public void GenericMethod(TType param) { }
    }
}";
        var compilation = CreateCompilation(code);
        var methodSymbol = GetMethodSymbol(compilation, "Type`1", "GenericMethod");

        var id = methodSymbol?.GetMethodId();

        Assert.That(id, Is.EqualTo("Namespace.Type<TType>.GenericMethod(TType)"));
    }

}
