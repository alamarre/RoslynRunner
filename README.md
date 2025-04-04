# RoslynRunner

This is a set of tools to make it easier to build and debug with Roslyn. 
It is intended to reduce cycle time and complexity of automated 
refactoring, and to make it reasonably easy to debug Roslyn analyzers and 
incremental generators.

The tool is primarily useful for refactoring if you need to make a large number of changes consistently in C# that are not already supported by your IDE, so you're using Roslyn workspaces, and you wish to iterate on your refactoring code quickly. It can also be helpful if you're trying to debug why an analyzer or incremental generator isn't working as expected on a project and help with iterating by enabling dynamic recompilation and running of the analysis with debugging symbols.

The basic concept is that you can load your solution once, but that the Roslyn code gets recompiled for each run. You can attach the debugger to the RoslynRunner processer in the editor for your Roslyn code and debug and rerun as necessary. Since the solution loading and compilations can be cached between runs, subsequent runs can save minutes in iteration time between runs on large solutions. Analyzers and generators can be hard to debug without using tests, so although it will likely not save you as much time, it does make it simpler to debug and ensure the latest version is ran after each modification. 

## Usage
See the [running the tool](./documentation/running-the-tool.md) documentation, or look at the [LegacyWebApp sample documentation](./samples/LegacyWebApp), for how to work with the tool.
