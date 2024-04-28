# Running the tool

## General info
The purpose of the tool is to improve the development experience and reduce cycle time in working with Roslyn. Despite being a web project, it is intended to be used for iterating on the same solution by a single developer. The web server offers flexibility in integrating with it that a CLI doesn't. The project takes some inspiration from the [.NET AWS Lambda testing tool](https://github.com/aws/aws-lambda-dotnet/tree/master/Tools/LambdaTestTool). There will likely eventually be a Blazor UI for it, but for the time being the Swagger UI (reachable at http://localhost:5188/swagger by default) is sufficient to be usable.

You can either run the server directly with the `RoslynRunner` project or preferably using [.NET Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/get-started/aspire-overview) with the `RoslyRunner.AppHost` project. Aspire could be used to attach useful utilities like Redis or Postgres, but for now it's convenient to have a transient dashboard for watching the logs and having the queue operation tied to the execution logs.

In the future this may be a .NET tool in NuGet, but for now, clone and run. 

## Getting started
It is useful to run the server in debugging mode. Once the server is running we need a project to run. There are a few ways to do it. Remember to attach the debugger to the RoslynRunner from the instance of your editor with the code you're debugging before using the `/run` API. Each run goes into the queue and is picked up by the background worker which will compile your code and load it dynamically based on the configuration passed in each time, but the debugger can remain attached through all iterations.

### Analyzers
For analyzers or incremental generators, build them as you normally would and then you can debug them with this tool.

You can find reasonable references for [how to write Roslyn analyzers](https://devblogs.microsoft.com/dotnet/how-to-write-a-roslyn-analyzer/) and there are a bunch of examples out there like [Roslynator](https://github.com/dotnet/roslynator).

There was a great [dotnet video](https://www.youtube.com/watch?v=KTsyS3rDUgg) recently about incremental generators. I wish I had more resources on writing them well.

#### Running
Both analyzers and incremental generators can be run with the `/run` route with a body that looks roughly like the following.

```
{
    "primarySolution": "{{fullPathToTargetCsprojOrSln}}",
    "persistSolution": true,
    "processorName": "AnalyzerRunner",
    "context": {
        "AnalyzerNames": ["{{fullyQualifiedNameOfFirstAnalyzer}}"],
        "AnalyzerProject": "{{fullPathToAnalyzerCsproj}},
        "TargetProject": "{{nameOfProjectInTheSolutionToTestAgainst}}"
    }
}
```

You can of course set persistSolution to false, but it's significantly faster to iterate if you keep the solution loaded.

### Custom code

If you want to use the workspaces APIs then you want to create a project with a reference to the `RoslynRunner.Abstractions` project. It is a good idea to put that in a different solution than the `RoslynRunner`. It is then possible to create a class implementing `ISolutionProcessor` or `ISolutionProcessor<T>`. This processor will then be executable by using the `/run` command. Keep in mind that it only compiles and loads this project by default. The `ISolutionProcessor` already exists on the server so it is capable of resolving the type and running it, but other dependencies outside of your processor library are more complicated so read the `API Overview` below for more information if you need to load dependencies that aren't already available in the server.

#### Running
```
{
    "primarySolution": "{{fullPathToTargetCsprojOrSln}}",
    "persistSolution": true,
    "processorName": "{{fullyQualifiedNameOfTheCustomISolutionProcessor}}",
    "processorProjectName": "{{assemblyNameOfTheProjectWithTheISolutionProcessor}}",
    "processorSolution": "{{fullPathToISolutionProcessorCsprojOrSln}}",
    "context": {
       {{processorSpecificContext}}
    }
}
```

## Overview of APIs
The APIs should later be documented with OpenAPI and should also be easier to work with using a UI in the future, but for now this reference will indicate features available that may not be obvious from the above instructions.

### `GET` `/run`

#### `assemblyLoadContextPath`
This value is an optional parameter for use with custom `ISolutionProcessor` code to specify a folder where to find additional dependencies just for the given run. The easy way to use this is to publish the processor's project and point to that folder. 

If you need to change the dependencies after iterating then you should publish to another folder and change this value to that folder, as unloading the assembly has not been sufficient to remove the file lock on the assemblies. 

If you're using a library with native bindings like `LibGit2Sharp` used by `RoslynRunner.Git` then you need to publish for the OS you're executing on.

#### `context`

The context is specific to the processor and will deserialize to the nullable generic type specified to the `ISolutionProcessor`. The AnalyzerRunner is just an `ISolutionProcessor` with a custom context so you can [use that as an example](../RoslynRunner/SolutionProcessors/AnalyzerRunner.cs).

### `DELETE` `/run`
This causes the current run to break at the next opportunity to be forced to by the `CancellationToken`.

### `POST` `/assemblies/global`
This should be used for very carefully, but is potentially quite valuable. You can post something like the following.

```
{
    "path": "{{fullyQualifiedPathToCsproj}}"
}
```
This will load a single project into the [AssemblyLoadContext.Default](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.loader.assemblyloadcontext.default?view=net-8.0). A given library can only be loaded once into it and it persists for the lifetime of the project. To use a project like this you want a project only with types you want to persist between runs and reference it from your processor project. It cannot be changed after loading so you need to use a new project if you need to change a data type. Despite the difficulty in using this effectively, it can be very helpful for caching results in a custom type between runs. 

### `/solutions`
Used to fetch and remove persistent solutions.

### `/analyze`
This was originally a convenience method to make it simpler to run analyzers and have the context exposed to the API definition. Since it takes the same parameters as `/run` it may be removed.





