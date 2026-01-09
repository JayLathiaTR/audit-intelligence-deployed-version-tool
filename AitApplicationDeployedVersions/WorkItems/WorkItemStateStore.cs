using System.Text.Json;

namespace AitApplicationDeployedVersions.WorkItems;

public sealed class WorkItemStateStore
{
    private readonly string filePath;

    public WorkItemStateStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("filePath is required", nameof(filePath));
        this.filePath = filePath;
    }

    public WorkItemState Load()
    {
        try
        {
            if (!File.Exists(filePath))
                return new WorkItemState();

            var json = File.ReadAllText(filePath);
            var state = JsonSerializer.Deserialize<WorkItemState>(json, SerializerOptions());
            return state ?? new WorkItemState();
        }
        catch
        {
            // If state is corrupt/unreadable, start fresh.
            return new WorkItemState();
        }
    }

    public void Save(WorkItemState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(state, SerializerOptions(indented: true));

        // Atomic-ish write
        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Copy(tmp, filePath, overwrite: true);
        File.Delete(tmp);
    }

    private static JsonSerializerOptions SerializerOptions(bool indented = false)
        => new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = indented
        };
}

public sealed class WorkItemState
{
    // Key: "{appName}||{env}"
    public Dictionary<string, WorkItemEnvEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string MakeKey(string appName, string env)
        => $"{appName}||{env}";
}

public sealed class WorkItemEnvEntry
{
    // Keep the most recent 2 snapshots.
    public List<WorkItemSnapshot> Snapshots { get; set; } = new();
}

public sealed class WorkItemSnapshot
{
    public required string BaselineSha { get; set; }
    public required string CurrentSha { get; set; }

    public DateTimeOffset FetchedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public string? Note { get; set; }

    public List<WorkItemLink> WorkItems { get; set; } = new();
    public List<UnlinkedPullRequest> UnlinkedPullRequests { get; set; } = new();
}

public sealed class WorkItemLink
{
    public required int WorkItemId { get; set; }

    public required int PullRequestNumber { get; set; }
    public required string PullRequestTitle { get; set; }
    public required string PullRequestUrl { get; set; }
}

public sealed class UnlinkedPullRequest
{
    public required int PullRequestNumber { get; set; }
    public required string PullRequestTitle { get; set; }
    public required string PullRequestUrl { get; set; }
}
