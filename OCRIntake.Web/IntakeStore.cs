using System.Collections.Concurrent;
using System.Text.Json;

namespace OCRIntake.Web;

public sealed class IntakeEntry(Intake intake, byte[] image, string mediaType)
{
    public Intake Intake { get; set; } = intake;
    public byte[] Image { get; } = image;
    public string MediaType { get; } = mediaType;
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

public class IntakeStore(IWebHostEnvironment environment, IConfiguration configuration)
{
    private readonly ConcurrentDictionary<Guid, IntakeEntry> drafts = new();
    private readonly object capacityGate = new();
    private readonly string directory = Path.GetFullPath(
        configuration["Storage:Directory"] ?? "App_Data", environment.ContentRootPath);

    public bool TryAdd(IntakeEntry entry)
    {
        lock (capacityGate)
        {
            foreach (var old in drafts.Where(x => x.Value.Intake.CreatedAt < DateTimeOffset.UtcNow.AddHours(-2)))
                drafts.TryRemove(old.Key, out _);
            return drafts.Count < 20 && drafts.TryAdd(entry.Intake.Id, entry);
        }
    }

    public IntakeEntry? Get(Guid id) => drafts.GetValueOrDefault(id);

    public async Task<Intake?> ReadApproved(Guid id)
    {
        var path = Path.Combine(directory, $"{id}.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<Intake>(await File.ReadAllTextAsync(path))
            : null;
    }

    public async Task SaveApproved(Intake intake)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{intake.Id}.json");
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(intake, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
