using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LegacyWebApp.Services;

public class LegacyOrderRepository
{
    public IEnumerable<string> GetOrderNumbers(int customerId)
    {
        yield return $"ORD-{customerId:D4}";
        yield return $"ORD-{customerId + 1:D4}";
    }

    public Task<IEnumerable<string>> GetOrderNumbersAsync(int customerId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GetOrderNumbers(customerId));
    }

    public string GetPrimaryOrder(int customerId)
    {
        return GetOrderNumbers(customerId).First();
    }

    public Task<string> GetPrimaryOrderAsync(int customerId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GetPrimaryOrder(customerId));
    }
}
