using AitApplicationDeployedVersions.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AitApplicationDeployedVersions.Core;

public readonly record struct FetchProgress(int Completed, int Total, string CurrentApp);

public static class AppCore
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static string NormalizeCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return "AIT";
        return category.Trim().ToUpperInvariant();
    }

    public static List<AppInfo> LoadApps()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "apps.json");
        string json;

        if (File.Exists(configPath))
        {
            json = File.ReadAllText(configPath);
        }
        else
        {
            var assembly = typeof(AppCore).Assembly;
            using var stream = assembly.GetManifestResourceStream("apps.json");
            if (stream is null)
                throw new InvalidOperationException("Embedded resource 'apps.json' was not found.");

            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());

        var apps = JsonSerializer.Deserialize<List<AppInfo>>(json, options);
        if (apps is null || apps.Count == 0)
            throw new InvalidDataException("apps.json did not contain any apps.");

        return apps;
    }

    public static async Task<VersionResult[]> FetchAllAsync(
        List<AppInfo> apps,
        string env,
        int maxConcurrency,
        IProgress<FetchProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (apps is null) throw new ArgumentNullException(nameof(apps));
        if (string.IsNullOrWhiteSpace(env)) throw new ArgumentException("env is required", nameof(env));

        var results = apps
            .Select(a => new VersionResult
            {
                AppName = a.Name,
                Environment = env,
                Category = NormalizeCategory(a.Category),
                Version = "Pending"
            })
            .ToArray();

        var completedCount = 0;

        try
        {
            await Parallel.ForEachAsync(
                apps.Select((app, index) => (app, index)),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxConcurrency,
                    CancellationToken = cancellationToken
                },
                async (item, token) =>
                {
                    var (app, index) = item;

                    string version;
                    if (app.EnvUrls.TryGetValue(env, out _))
                    {
                        version = await FetchVersionAsync(app, env, token);
                    }
                    else
                    {
                        version = "URL not configured";
                    }

                    results[index].Version = version;

                    var done = Interlocked.Increment(ref completedCount);
                    progress?.Report(new FetchProgress(done, apps.Count, app.Name));
                });
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected.
        }

        if (cancellationToken.IsCancellationRequested)
        {
            for (var i = 0; i < results.Length; i++)
            {
                if (string.Equals(results[i].Version, "Pending", StringComparison.Ordinal))
                    results[i].Version = "Error: Cancelled";
            }
        }

        return results;
    }

    private static async Task<string> FetchVersionAsync(AppInfo app, string env, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!app.EnvUrls.TryGetValue(env, out var url))
            return "URL not configured";

        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase) ? "" : $" {response.ReasonPhrase}";
                return $"Error: {(int)response.StatusCode}{reason}";
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            var version = TryGetJsonPathString(root, app.VersionJsonPath);
            return version is null ? "Version not found" : ExtractCommitSha(version);
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested ? "Error: Cancelled" : "Error: Timeout";
        }
        catch (JsonException)
        {
            return "Error: Invalid JSON";
        }
        catch (HttpRequestException ex)
        {
            return $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static string? TryGetJsonPathString(JsonElement root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind != JsonValueKind.Object)
                return null;

            if (!current.TryGetProperty(segment, out var next))
                return null;

            current = next;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => current.ToString()
        };
    }

    private static string ExtractCommitSha(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "Invalid";

        var parts = version.Split('+');
        if (parts.Length > 1 && parts[1].Length >= 7)
        {
            return parts[1][..7];
        }

        return "Invalid SHA";
    }
}
