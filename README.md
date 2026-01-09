# Audit Intelligence Suite - Deployed Versions

Windows desktop app that fetches and displays deployed versions (commit SHAs) for Audit Intelligence services by environment.

## What it does

- Pick an environment (CI/DEMO/QED/SBX/PROD + UK variants)
- Click **Fetch**
- The app calls each configured endpoint and shows the deployed version/short SHA in a table
- For QED/SBX/PROD (+ UK variants), the **Work Items** tab can also list `AB#123456` work item references found in GitHub PR descriptions for the deployment delta

## Where to change things

### Update services/endpoints (most common)

Edit the embedded config file:

- `AitApplicationDeployedVersions/apps.json`

This file controls:

- App display name (`name`)
- Per-environment URL map (`envUrls`)
- Where the version lives in JSON (`versionJsonPath`, e.g. `info.version`)

### Update core fetch logic (HTTP/JSON/progress)

- `AitApplicationDeployedVersions/Core/AppCore.cs`

### Update the UI

- Layout/styling: `AitApplicationDeployedVersions.Avalonia/Views/MainWindow.axaml`
- ViewModel/behavior: `AitApplicationDeployedVersions.Avalonia/ViewModels/MainWindowViewModel.cs`

## CI/CD builds + Releases (recommended)

This repo uses GitHub Actions to build on PRs and `master`, and to publish the single-file EXE only for releases.

### PR / push builds

- Every PR targeting `master` and every push to `master` runs the CI workflow automatically (so merges to `master` trigger CI).
- CI is build-only (no EXE is produced/uploaded).

### Releases (tag-based)

- When you push a tag that starts with `v` (example: `v1.0.0`), the Release workflow runs.
- It publishes the `win-x64` single-file EXE and attaches it to a GitHub Release.
- You can download it from **GitHub → Releases → (your tag) → Assets**.
  <img width="818" height="557" alt="image" src="https://github.com/user-attachments/assets/2a42b2f0-9260-4a7e-8a2a-f85c1b9972f1" />

## Work Items (optional)

For services that have `gitHubRepo` configured in `apps.json`, the app can query GitHub to find PRs between the last baseline and the currently deployed commit, then extract work items from PR descriptions using a regex like `AB#4058190`.

To access private GitHub repos, configure a read-only GitHub token on the machine running the tool.

- Preferred: Windows Credential Manager
  - Credential name: `AuditIntelligenceDeployedVersion-GitHubToken`
  - Create/update it via PowerShell:

```powershell
cmdkey /generic:AuditIntelligenceDeployedVersion-GitHubToken /user:token /pass:<YOUR_GITHUB_PAT>
```

- Fallback: environment variable

```powershell
setx AITVERS_GITHUB_TOKEN "<YOUR_GITHUB_PAT>"
```

## Build / Run (dev) (manual)

In Visual Studio, set `AitApplicationDeployedVersions.Avalonia` as the Startup Project and run.

Or from the repo root:

- `dotnet build`

## Publish single-file EXE (win-x64) (manual)

From the repo root:

```powershell
dotnet publish "AitApplicationDeployedVersions.Avalonia/AitApplicationDeployedVersions.Avalonia.csproj" -c Release -r win-x64 --self-contained true -o "artifacts/publish/win-x64" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
```

Output:

- `artifacts/publish/win-x64/AitApplicationDeployedVersions.exe`
