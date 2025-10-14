namespace ModernWebApi.Services;

public interface IRecursiveService
{
    IEnumerable<int> GetValues(int id);
}
