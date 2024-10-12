using System;
using System.Net.Http;

namespace ModernWebApi.Endpoints;

public enum HttpVerb
{
    Get,
    Post,
    Put,
    Delete,
    Patch,
    Options,
    Head
}

[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class ApiAttribute(HttpVerb verb, string path) : Attribute
{
    public HttpVerb HttpVerb { get; } = verb;
    public string Path { get; } = path;
}

