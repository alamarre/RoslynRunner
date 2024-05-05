using System.Reflection;
using System.Runtime.Loader;

namespace RoslynRunner.Core;

// based on https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability
public class TestAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver? _resolver;
    private readonly string? _libDirectory;

    public TestAssemblyLoadContext(string? mainAssemblyToLoadPath) : base(true)
    {
        if (Directory.Exists(mainAssemblyToLoadPath))
            _libDirectory = mainAssemblyToLoadPath;
        else if (mainAssemblyToLoadPath != null) _resolver = new AssemblyDependencyResolver(mainAssemblyToLoadPath);
    }

    protected override Assembly? Load(AssemblyName name)
    {
        if (Default.Assemblies.Any(a => a.GetName().Name == name.Name)) return null;
        if (_libDirectory != null)
        {
            var path = Path.Combine(_libDirectory, name.Name + ".dll");
            if (File.Exists(path)) return LoadFromAssemblyPath(path);
        }

        if (_resolver == null) return null;
        var assemblyPath = _resolver.ResolveAssemblyToPath(name);
        if (assemblyPath != null) return LoadFromAssemblyPath(assemblyPath);

        return null;
    }
}
