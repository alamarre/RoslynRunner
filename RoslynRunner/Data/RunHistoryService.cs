using Microsoft.EntityFrameworkCore;
using RoslynRunner.Abstractions;
using System.Text.Json;

namespace RoslynRunner.Data;

public interface IRunHistoryService
{
    Task RecordRunAsync(RunParameters runParameters, RunContext runContext, bool succeeded, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RunHistoryItem>> GetRecentRunsAsync(CancellationToken cancellationToken = default);
    Task SaveRunAsync(string name, RunCommand runCommand, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SavedRunItem>> GetSavedRunsAsync(CancellationToken cancellationToken = default);
    Task DeleteSavedRunAsync(string name, CancellationToken cancellationToken = default);
    Task<RunCommand?> GetSavedRunCommandAsync(string name, CancellationToken cancellationToken = default);
}

public class RunHistoryService(RunHistoryDbContext dbContext, ILogger<RunHistoryService> logger) : IRunHistoryService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public async Task RecordRunAsync(RunParameters runParameters, RunContext runContext, bool succeeded, CancellationToken cancellationToken = default)
    {
        try
        {
            var entity = new RunRecordEntity
            {
                RunId = runParameters.RunId,
                RunCommandJson = JsonSerializer.Serialize(runParameters.RunCommand, SerializerOptions),
                CreatedAt = DateTime.UtcNow,
                Succeeded = succeeded,
                OutputJson = JsonSerializer.Serialize(runContext.Output ?? new List<string>(), SerializerOptions),
                ErrorsJson = JsonSerializer.Serialize(runContext.Errors ?? new List<string>(), SerializerOptions)
            };

            await dbContext.RunRecords.AddAsync(entity, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            var recordsToRemove = await dbContext.RunRecords
                .OrderByDescending(r => r.CreatedAt)
                .Skip(RunHistoryDbContext.MaxRecentRuns)
                .ToListAsync(cancellationToken);

            if (recordsToRemove.Count > 0)
            {
                dbContext.RunRecords.RemoveRange(recordsToRemove);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record run {RunId}", runParameters.RunId);
        }
    }

    public async Task<IReadOnlyList<RunHistoryItem>> GetRecentRunsAsync(CancellationToken cancellationToken = default)
    {
        var records = await dbContext.RunRecords
            .AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(RunHistoryDbContext.MaxRecentRuns)
            .ToListAsync(cancellationToken);

        return records
            .Select(r =>
            {
                var command = DeserializeCommand(r.RunCommandJson);
                return command is null
                    ? null
                    : new RunHistoryItem(
                        r.RunId,
                        DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc),
                        command,
                        r.Succeeded,
                        DeserializeList(r.OutputJson),
                        DeserializeList(r.ErrorsJson));
            })
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();
    }

    public async Task SaveRunAsync(string name, RunCommand runCommand, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var normalizedName = name.Trim();
        var serializedCommand = JsonSerializer.Serialize(runCommand, SerializerOptions);

        var lowerName = normalizedName.ToLowerInvariant();

        var existing = await dbContext.SavedRuns
            .FirstOrDefaultAsync(r => r.Name.ToLower() == lowerName, cancellationToken);

        if (existing is null)
        {
            var entity = new SavedRunEntity
            {
                Name = normalizedName,
                RunCommandJson = serializedCommand,
                CreatedAt = DateTime.UtcNow
            };

            await dbContext.SavedRuns.AddAsync(entity, cancellationToken);
        }
        else
        {
            existing.RunCommandJson = serializedCommand;
            existing.CreatedAt = DateTime.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SavedRunItem>> GetSavedRunsAsync(CancellationToken cancellationToken = default)
    {
        var saved = await dbContext.SavedRuns
            .AsNoTracking()
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);

        return saved
            .Select(s =>
            {
                var command = DeserializeCommand(s.RunCommandJson);
                return command is null
                    ? null
                    : new SavedRunItem(s.Id, s.Name, command, DateTime.SpecifyKind(s.CreatedAt, DateTimeKind.Utc));
            })
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();
    }

    public async Task DeleteSavedRunAsync(string name, CancellationToken cancellationToken = default)
    {
        var lowerName = name.ToLowerInvariant();

        var entity = await dbContext.SavedRuns
            .FirstOrDefaultAsync(r => r.Name.ToLower() == lowerName, cancellationToken);

        if (entity is null)
        {
            return;
        }

        dbContext.SavedRuns.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<RunCommand?> GetSavedRunCommandAsync(string name, CancellationToken cancellationToken = default)
    {
        var lowerName = name.ToLowerInvariant();

        var entity = await dbContext.SavedRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Name.ToLower() == lowerName, cancellationToken);

        return entity is null ? null : DeserializeCommand(entity.RunCommandJson);
    }

    private static RunCommand? DeserializeCommand(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RunCommand>(json, SerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> DeserializeList(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, SerializerOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }
}

public record RunHistoryItem(
    Guid RunId,
    DateTime CreatedAt,
    RunCommand RunCommand,
    bool Succeeded,
    IReadOnlyList<string> Output,
    IReadOnlyList<string> Errors);

public record SavedRunItem(
    int Id,
    string Name,
    RunCommand RunCommand,
    DateTime CreatedAt);
