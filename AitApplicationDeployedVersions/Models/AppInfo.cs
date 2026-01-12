namespace AitApplicationDeployedVersions.Models;

public enum AppType { Microservice, OfficeAddin, BlazorWasm }

public class AppInfo
{
    public required string Name { get; set; }
    public string Category { get; set; } = "AIT";
    public AppType Type { get; set; }
    public required Dictionary<string, string> EnvUrls { get; set; }

    public required string VersionJsonPath { get; set; }

    public string? GitHubRepo { get; set; }

    public Dictionary<string, string>? BaselineCommitByEnv { get; set; }

    // Optional: force the commit used for Work Items compare for a given env.
    // This is intended for ad-hoc validation (e.g., release-branch comparisons) and should be gated in UI logic.
    public Dictionary<string, string>? CurrentCommitOverrideByEnv { get; set; }
}


public class VersionResult
{
    public required string AppName { get; set; }
    public required string Environment { get; set; }
    public required string Category { get; set; }
    public required string Version { get; set; }

    public string? FullCommitSha { get; set; }
}
