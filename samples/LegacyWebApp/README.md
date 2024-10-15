# Legacy Web App Sample

This legacy web app sample is still a work in progress, but can be used to demonstrate the utility of the tool. 

This sample demonstrates debugging an analyzer, an incremental source generator and altering a solution with the workspaces API. Note: these examples are more of a starting point in working with the tooling and the code itself is mostly and Roslyn written by ChatGPT. This is not a good reference for making things with Roslyn, but the tool can be quite helpful for experimenting with it. 

This sample is about porting an application from using ASP.NET Core MVC Api controllers to using minimal APIs that are structured similarly, but bound using an incremental generator. 

## Walkthrough

### Getting started with the tool
Although this tool may make sense to be a dotnet tool, and is inspired in part by the [AWS Lambda .NET Test Tool](https://github.com/aws/aws-lambda-dotnet/blob/master/Tools/LambdaTestTool/README.md), it is currently required to clone this repo and run it.

You can run the RoslynRunner directly, but the RoslynRunner.AppHost leverages Aspire to put the logs into the Aspire dashboard and will also be useful if you want to add dependencies to your solution changes.

The RoslynRunner loads a target solution and a Roslyn project against the target. A primary use case for the tool is quick iteration on potentially large solutions. Roslyn works based on an immutable representation of the solution so if the solution is persisted between runs, even if the initial run takes double digit minutes, subsequent runs can be as quick as to be measured in milliseconds. To take advantage of this performance the source code to be analyzed or altered, of course, cannot change between runs since it will execute against the original cached model.

It is advised to run the tool itself normally and then in the editor containing the Roslyn code attach the debugger to the `RoslynRunner` processor. This enables setting break points in the Roslyn code to debug and the code can be edited between runs and executed by triggering a new run. Note: so far debugging in Visual Studio or Visual Studio code has been effective for debugging, whereas Rider has not identified the code changes when debugging so viewing the variable values doesn't work as well.  The runs can be triggered via the UI or the API, the latter of which could be triggered by a launch configuration in VS code for instance.

The RoslynRunner has a Blazor UI that has two tabs: RUN and JSON. The JSON tab represents the current state of the RUN tab as JSON and allows that JSON to copied and later reloaded or used to call the minimal API that triggers runs separately.

The RUN tab enables enqueuing the next set of details to run.

The command type can be set to `Analyzer` or `Custom Processor`. Analyzer is used for both analyzers and incremental generators. Custom processors are a name for using the Roslyn workspace API by extending `ISolutionProcessor<T>`. 

For all the examples here `Primary Solution` should be set to the absolute file path of the `LegacyWebApp.sln`.

`Persist Solution` is useful for performance reasons, but is also useful for custom processors as the output of them will not become input to subsequent runs as long as it's using the previously cached load of the solution data.

`Assembly Load Context Path` is useful for loading libraries referenced by the libraries being worked on, but is unnecessary for these samples. If you want to use it you should `dotnet publish` the library you're working on to a directory. If you change the referenced libraries, but want to keep the persisted solution then you must publish to another directory and change this value as the files get locked since unloading the context has not been successful thus far.

The remaining parameters will vary by the command type.

### Analyzer

Analyzers are likely how most people are introduced to Roslyn and there are numerous resources on writing them, but they can be hard to debug beyond using unit testing frameworks.

The [SwitchToMinimalApiAnalyzer](./ModernApi.Analyzers/SwitchToMinimalApiAnalyzer.cs) looks for APIs defined using MVC controllers and suggests moving to minimal APIs.

The `Analyzer Project Path` should be set to the absolute path of the `ModerApi.Analyzers.csproj`.

The `Target Project Name` should be set to `ModernWebApi`.

The `Analyzer Names` should be set to `ModernApi.Analyzers.SwitchToMinimalApiAnalyzer`.

Hitting submit will enqueue the run, which will then compile and execute the current version of the analyzer. Due to attaching to the `RoslynRunner` it is possible to debug when the diagnostic is fired and perhaps look to analyze other attributes or patterns. 

### Incremental generator

Source generators fall into two categories, the original source generators which ran at each compile and the more complex, but efficient incremental generators. Incremental generators are favored so they are the only ones handled by this tool.

They are a great alternative to reflection, but developing them can be tricky. See more [here](https://www.youtube.com/watch?v=KTsyS3rDUgg).
Jetbrains Rider also has a [way of debugging them](https://blog.jetbrains.com/dotnet/2023/07/13/debug-source-generators-in-jetbrains-rider/) and they are currently better than Visual Studio and Visual Studio code for examining the generated code since it has a button to reload the generators. 

On the command line it can even cache with the build server so when running `dotnet build` it can be useful to use `--disable-build-servers` when building projects referencing a generator to be able to force changes made to the generators. You may also wish to set `EmitCompilerGeneratedFiles` and look at the output. Some more information can be found [here](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/source-generators-overview).

Generators are run similarly to analyzers so the key change is just that `Analyzer Names` should now be set to `ModernApi.Analyzers.MinimalApiGenerator`.

### Custom processor

Automatically refactoring of solutions is why this tooling's closed source predecessor was  created. By leveraging [Roslyn workspaces](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/work-with-workspace), this tool compiles the custom processor, which then itself uses the workspace to work with the target solution. Insert yo dawg meme here. 

The power of workspaces is immense as you have full ability to process the code from [SyntaxNode](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.syntaxnode?view=roslyn-dotnet-4.9.0) to [ISymbol](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.isymbol?view=roslyn-dotnet-4.9.0) to [IOperation](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.ioperation?view=roslyn-dotnet-4.9.0), and can add or edit documents by creating or manipulating syntax trees. This is useful for visualizing call chains like is done with the `RoslynRunner.Utilities.InvocationTrees`, or can be used to make changes like done in this sample.

Working with a custom processor it is often useful to persist the solution, particularly if modifying the solution, since it keeps the solution in memory and the compilations are cached automatically and you can optionally choose to cache semantic models as well. This is helpful for performance, but also enables tweaking code that has output without it automatically incorporating its output in subsequent runs. 

Set `Command Type` to `Custom Processor`.

Set `Processor Solution` to the absolute path to `LegacyWebAppConverter.csproj`

Set `Processor Name` to `LegacyWebAppConverter.ConvertToMinimalApi`.

Set `Process Project Name` to `LegacyWebAppConverter`.

For now the custom processor creates a `SampleEndpoint.cs` file, but leaves the existing Web API intact and doesn't deal with converting constructors, which will be important if we want to deal with dependency injection. In the future there could be a second iteration of the processor in the same project which requires using the `Assembly Load Context Path` that does those things, converts from the legacy project and converts chains of synchronous functions to async, all while committing small changes using LibGit2Sharp. You could even switch from the original to the new one while using the same persisted target solution and just change the `Processor Name` to switch from one to the other. For the time being experiment and enjoy working with Roslyn.
