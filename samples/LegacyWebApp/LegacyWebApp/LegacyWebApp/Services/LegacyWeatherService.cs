using LegacyWebApp.Data;

namespace LegacyWebApp.Services;

public class LegacyWeatherService
{
    private readonly LegacyWeatherRepository _repository;

    public LegacyWeatherService(LegacyWeatherRepository repository)
    {
        _repository = repository;
    }

    public LegacyWeatherReport GetWeatherReport(int locationId)
    {
        var primary = _repository.GetPrimaryObservation(locationId);
        var secondary = _repository.GetSecondaryObservation(locationId);

        var average = (primary.TemperatureCelsius + secondary.TemperatureCelsius) / 2;
        return new LegacyWeatherReport(primary.Condition, average);
    }
}
