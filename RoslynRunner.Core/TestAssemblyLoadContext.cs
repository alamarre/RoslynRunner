using System.Reflection;
using System.Runtime.Loader;

namespace RoslynRunner.Core;

// based on https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability
public class TestAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver? _resolver;

    public TestAssemblyLoadContext(string? mainAssemblyToLoadPath) : base(isCollectible: true)
    {
        if (mainAssemblyToLoadPath != null)
        {
            _resolver = new AssemblyDependencyResolver(mainAssemblyToLoadPath);
        }
    }

    protected override Assembly? Load(AssemblyName name)
    {
        if (_resolver == null)
        {
            return null;
        }
        string? assemblyPath = _resolver.ResolveAssemblyToPath(name);
        if (assemblyPath != null)
        {
            return LoadFromAssemblyPath(assemblyPath);
        }

        return null;
    }
}