namespace ModernWebApi.Services;

public interface IValuesService
{
    IEnumerable<int> GetValues(int id);
}
