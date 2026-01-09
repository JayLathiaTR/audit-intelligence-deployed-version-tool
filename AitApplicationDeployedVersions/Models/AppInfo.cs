namespace AitApplicationDeployedVersions.Models;

public enum AppType { Microservice, OfficeAddin, BlazorWasm }

public class AppInfo
{
    public required string Name { get; set; }
    public AppType Type { get; set; }
    public required Dictionary<string, string> EnvUrls { get; set; }

    public required string VersionJsonPath { get; set; }
}


public class VersionResult
{
    public required string AppName { get; set; }
    public required string Environment { get; set; }
    public required string Version { get; set; }
}
