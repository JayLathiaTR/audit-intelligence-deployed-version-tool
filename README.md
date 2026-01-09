# Audit Intelligence Deployed Version Tool

Windows desktop app that fetches and displays deployed versions (commit SHAs) for Audit Intelligence services by environment.

## What it does

- Pick an environment (CI/DEMO/QED/SBX/PROD + UK variants)
- Click **Fetch**
- The app calls each configured endpoint and shows the deployed version/short SHA in a table

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

This repo uses GitHub Actions to automatically build and publish the single-file EXE.

### PR / push builds

- Every push and PR to `master` runs the CI workflow.
- It publishes the `win-x64` single-file EXE and uploads it as a workflow artifact.
- You can download it from **GitHub → Actions → (the run) → Artifacts**.

### Releases (tag-based)

- When you push a tag that starts with `v` (example: `v1.0.0`), the Release workflow runs.
- It publishes the `win-x64` single-file EXE and attaches it to a GitHub Release.
- You can download it from **GitHub → Releases → (your tag) → Assets**.

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
