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
}


public class VersionResult
{
    public required string AppName { get; set; }
    public required string Environment { get; set; }
    public required string Category { get; set; }
    public required string Version { get; set; }

    public string? FullCommitSha { get; set; }
}
