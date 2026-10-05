using System.Text.Json;
using System.Text.RegularExpressions;

namespace OCRIntake.Web;

public sealed class ProfileStore
{
    private readonly string directory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<ExtractionProfile> history = [];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ProfileStore(IWebHostEnvironment environment, IConfiguration configuration)
    {
        directory = Path.Combine(Path.GetFullPath(configuration["Storage:Directory"] ?? "App_Data",
            environment.ContentRootPath), "profiles");
        Directory.CreateDirectory(directory);
        var path = PathFor(Guid.Empty, 1);
        if (!File.Exists(path))
        {
            using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(environment.ContentRootPath,
                "product-label-analyzer.json")));
            var fields = schema.RootElement.GetProperty("fieldSchema").GetProperty("fields");
            var profile = new ExtractionProfile(Guid.Empty, 1, "Product labels",
                LabelSchema.Fields.Select(field => new ExtractionField(field.Key, field.Value,
                    fields.GetProperty(field.Key).GetProperty("description").GetString()!)).ToArray(),
                DateTimeOffset.UtcNow);
            Write(profile);
        }
        history.AddRange(ReadAll());
    }

    public static bool ValidKey(string? key) =>
        key is not null && Regex.IsMatch(key, @"\A[A-Za-z][A-Za-z0-9_]{0,63}\z", RegexOptions.CultureInvariant);

    public static string? Validate(ProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100)
            return "A profile name of 1–100 characters is required.";
        if (request.Fields is null || request.Fields.Length is < 1 or > 30)
            return "Define between 1 and 30 extraction fields.";
        if (request.Fields.Any(field => field is null || !ValidKey(field.Key) ||
                string.IsNullOrWhiteSpace(field.Name) || field.Name.Length > 100 ||
                string.IsNullOrWhiteSpace(field.Description) || field.Description.Length > 2000))
            return "Each field needs a key (letter followed by letters, numbers or underscores; up to 64 characters), a name (up to 100 characters) and extraction instructions (up to 2000 characters).";
        if (request.Fields.Select(field => field.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Fields.Length)
            return "Field keys must be unique (case-insensitive).";
        return null;
    }

    public async Task<ExtractionProfile[]> List()
    {
        await gate.WaitAsync();
        try { return history.GroupBy(profile => profile.Id).Select(group => group.MaxBy(profile => profile.Version)!)
            .OrderBy(profile => profile.Id != Guid.Empty).ThenBy(profile => profile.Name).ToArray(); }
        finally { gate.Release(); }
    }

    public async Task<ExtractionProfile?> Get(Guid id, int? version = null)
    {
        await gate.WaitAsync();
        try
        {
            if (version is not null)
            {
                return history.SingleOrDefault(profile => profile.Id == id && profile.Version == version);
            }
            return history.Where(profile => profile.Id == id).MaxBy(profile => profile.Version);
        }
        finally { gate.Release(); }
    }

    public async Task<ExtractionProfile?> Save(Guid? id, ProfileRequest request)
    {
        await gate.WaitAsync();
        try
        {
            var existing = history;
            var latest = id is null ? null : existing.Where(profile => profile.Id == id).MaxBy(profile => profile.Version);
            if (id is not null && (latest is null || request.Version != latest.Version))
                return null;
            if (id is null && (request.Version != 0 || existing.Select(profile => profile.Id).Distinct().Count() >= 50))
                return null;
            var fields = request.Fields.Select(field => field with
                { Name = field.Name.Trim(), Description = field.Description.Trim() }).ToArray();
            var profile = new ExtractionProfile(id ?? Guid.NewGuid(), (latest?.Version ?? 0) + 1,
                request.Name.Trim(), fields, DateTimeOffset.UtcNow);
            Write(profile);
            history.Add(profile);
            return profile;
        }
        finally { gate.Release(); }
    }

    private string PathFor(Guid id, int version) => Path.Combine(directory, $"{id:N}-v{version}.json");
    private ExtractionProfile[] ReadAll() => Directory.EnumerateFiles(directory, "*.json")
        .Select(path => JsonSerializer.Deserialize<ExtractionProfile>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Invalid stored extraction profile.")).ToArray();

    private void Write(ExtractionProfile profile)
    {
        var path = PathFor(profile.Id, profile.Version);
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(profile, JsonOptions));
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
