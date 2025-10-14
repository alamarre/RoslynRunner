namespace ModernWebApi.Services;

public class ValuesService : IValuesService
{
    private readonly IRecursiveService _recursive;

    public ValuesService(IRecursiveService recursive)
    {
        _recursive = recursive;
    }

    public IEnumerable<int> GetValues(int id)
    {
        return _recursive.GetValues(id);
    }
}
