using System.Collections.Generic;

namespace LegacyWebApp.Services;

public class LegacyOrderCoordinator
{
    private readonly LegacyOrderFormatter _formatter;

    public LegacyOrderCoordinator(LegacyOrderFormatter formatter)
    {
        _formatter = formatter;
    }

    public IEnumerable<string> PrepareOrders(int customerId)
    {
        return _formatter.FormatOrders(customerId);
    }
}
