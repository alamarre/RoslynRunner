using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace RoslynRunner.Core;

public class TestAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver? _resolver;
    private readonly string? _libDirectory;
    private readonly AssemblyLoadContext _secondaryContext;

    public TestAssemblyLoadContext(string? mainAssemblyToLoadPath, AssemblyLoadContext? secondaryContext = null)
        : base(isCollectible: true)
    {
        AssemblyLoadContext localSecondaryContext = secondaryContext ?? AssemblyLoadContext.Default;
        _secondaryContext = localSecondaryContext;

        if (mainAssemblyToLoadPath != null && Directory.Exists(mainAssemblyToLoadPath))
        {
            string localLibDirectory = mainAssemblyToLoadPath;
            _libDirectory = localLibDirectory;
        }
        else if (mainAssemblyToLoadPath != null)
        {
            AssemblyDependencyResolver localResolver = new AssemblyDependencyResolver(mainAssemblyToLoadPath);
            _resolver = localResolver;
        }
    }
    
    public string? LibDirectory => _libDirectory;

    protected override Assembly? Load(AssemblyName name)
    {
        Assembly? localAssembly = null;

        Assembly[] defaultAssemblies = Default.Assemblies.ToArray();
        bool isFound = defaultAssemblies.Any((Assembly assembly) => assembly.GetName().Name == name.Name);
        if (isFound)
        {
            return null;
        }

        if (_libDirectory != null)
        {
            string assemblyFileName = name.Name + ".dll";
            string assemblyPath = Path.Combine(_libDirectory, assemblyFileName);
            if (File.Exists(assemblyPath))
            {
                localAssembly = LoadFromAssemblyPath(assemblyPath);
                if (localAssembly != null)
                {
                    return localAssembly;
                }
            }
        }

        if (_resolver != null)
        {
            string? resolvedPath = _resolver.ResolveAssemblyToPath(name);
            if (resolvedPath != null)
            {
                localAssembly = LoadFromAssemblyPath(resolvedPath);
                if (localAssembly != null)
                {
                    return localAssembly;
                }
            }
        }
        
        localAssembly = _secondaryContext.LoadFromAssemblyName(name);
        if (localAssembly != null)
        {
            return localAssembly;
        }
        

        return null;
    }
}
