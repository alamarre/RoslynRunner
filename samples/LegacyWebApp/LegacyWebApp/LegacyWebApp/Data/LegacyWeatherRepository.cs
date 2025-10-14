namespace LegacyWebApp.Data;

public class LegacyWeatherRepository
{
    private readonly LegacyWeatherDatabase _database;

    public LegacyWeatherRepository(LegacyWeatherDatabase database)
    {
        _database = database;
    }

    public LegacyWeatherObservation GetPrimaryObservation(int locationId)
    {
        return _database.GetLatestObservation(locationId);
    }

    public LegacyWeatherObservation GetSecondaryObservation(int locationId)
    {
        var adjustedId = locationId + 1;
        return _database.GetLatestObservation(adjustedId);
    }
}
