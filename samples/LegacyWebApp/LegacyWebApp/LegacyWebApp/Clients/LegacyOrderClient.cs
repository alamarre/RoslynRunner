using System.Collections.Generic;
using LegacyWebApp.Services;

namespace LegacyWebApp.Clients;

public class LegacyOrderClient
{
    private readonly LegacyOrderCoordinator _coordinator;

    public LegacyOrderClient(LegacyOrderCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public IEnumerable<string> GetFormattedOrders(int customerId)
    {
        return _coordinator.PrepareOrders(customerId);
    }
}
