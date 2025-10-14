using System.Threading;
using System.Threading.Tasks;

namespace LegacyWebApp.Data;

public class LegacyWeatherDatabase
{
    public LegacyWeatherObservation GetLatestObservation(int locationId)
    {
        var temperature = 20 + (locationId % 5);
        return new LegacyWeatherObservation(temperature, "Sunny");
    }

    public Task<LegacyWeatherObservation> GetLatestObservationAsync(
        int locationId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GetLatestObservation(locationId));
    }
}
