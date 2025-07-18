using System.Data;
using System.Linq;
using Dapper;

namespace ModernWebApi.Services;

public class RecursiveService : IRecursiveService
{
    private readonly IDbConnection _connection;

    public RecursiveService(IDbConnection connection)
    {
        _connection = connection;
    }

    public IEnumerable<int> GetValues(int id)
    {
        return GetPrimary(id);
    }

    private IEnumerable<int> GetPrimary(int id)
    {
        var result = _connection.Query<int>("select value from Data where Id=@id", new { id });
        if (result.Any())
        {
            return result;
        }
        return GetSecondary(id);
    }

    private IEnumerable<int> GetSecondary(int id)
    {
        if (id <= 0)
        {
            return Enumerable.Empty<int>();
        }
        return GetPrimary(id - 1);
    }
}
