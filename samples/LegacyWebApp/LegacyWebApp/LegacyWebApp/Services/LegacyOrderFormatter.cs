using System.Collections.Generic;
using System.Linq;

namespace LegacyWebApp.Services;

public class LegacyOrderFormatter
{
    private readonly LegacyOrderRepository _repository;

    public LegacyOrderFormatter(LegacyOrderRepository repository)
    {
        _repository = repository;
    }

    public IEnumerable<string> FormatOrders(int customerId)
    {
        var orders = _repository.GetOrderNumbers(customerId);
        if (orders.Any())
        {
            return orders.Select(order => $"Order:{order}");
        }

        return new[] { $"Order:{_repository.GetPrimaryOrder(customerId)}" };
    }
}
