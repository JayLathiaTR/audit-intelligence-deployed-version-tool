using AitApplicationDeployedVersions.Core;
using AitApplicationDeployedVersions.Models;
using AitApplicationDeployedVersions.WorkItems;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace AitApplicationDeployedVersions.Avalonia.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private const int MaxConcurrency = 6;

    private const string GitHubCredentialTargetName = "AuditIntelligenceDeployedVersion-GitHubToken";
    private const string GitHubTokenEnvVarName = "AITVERS_GITHUB_TOKEN";

    private static readonly HashSet<string> WorkItemEnvs = new(StringComparer.OrdinalIgnoreCase)
    {
        "QED", "UKQED", "SBX", "UKSBX", "PROD", "UKPROD"
    };

    private static readonly HttpClient GitHubHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private readonly WorkItemStateStore workItemStore;
    private WorkItemState workItemState;

    public string[] Environments { get; } = ["CI", "DEMO", "QED", "UKQED", "SBX", "UKSBX", "PROD", "UKPROD"];

    [ObservableProperty]
    private string? selectedEnvironment;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusText = "Idle";

    [ObservableProperty]
    private double progressValue;

    public bool IsNotBusy => !IsBusy;

    public ObservableCollection<ResultRow> ResultsAit { get; } = new();
    public ObservableCollection<ResultRow> ResultsAia { get; } = new();

    public ObservableCollection<LinkedWorkItemRow> LinkedWorkItems { get; } = new();
    public ObservableCollection<UnlinkedPullRequestRow> UnlinkedPullRequests { get; } = new();

    [ObservableProperty]
    private string workItemsStatusText = "Work items: (not fetched)";

    [ObservableProperty]
    private LinkedWorkItemRow? selectedLinkedWorkItem;

    [ObservableProperty]
    private UnlinkedPullRequestRow? selectedUnlinkedPullRequest;

    private CancellationTokenSource? fetchCts;

    public MainWindowViewModel()
    {
        SelectedEnvironment = Environments.FirstOrDefault();

        workItemStore = new WorkItemStateStore(GetWorkItemStatePath());
        workItemState = workItemStore.Load();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotBusy));
    }

    [RelayCommand(CanExecute = nameof(CanFetch))]
    private async Task FetchAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedEnvironment))
        {
            StatusText = "Please select an environment.";
            return;
        }

        try
        {
            IsBusy = true;
            FetchCommand.NotifyCanExecuteChanged();

            fetchCts?.Cancel();
            fetchCts?.Dispose();
            fetchCts = new CancellationTokenSource();

            var env = SelectedEnvironment;
            var apps = AppCore.LoadApps();
            var total = apps.Count;

            ResultsAit.Clear();
            ResultsAia.Clear();
            ProgressValue = 0;
            StatusText = $"Fetching {env} 0/{total}…";

            var progress = new Progress<FetchProgress>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ProgressValue = p.Total > 0 ? (double)p.Completed / p.Total * 100d : 0;
                    StatusText = $"Fetching {env} {p.Completed}/{p.Total}: {p.CurrentApp}";
                });
            });

            var results = await AppCore.FetchAllAsync(apps, env, MaxConcurrency, progress, fetchCts.Token);

            await RefreshWorkItemsAsync(apps, env, results, fetchCts.Token);

            Dispatcher.UIThread.Post(() =>
            {
                ResultsAit.Clear();
                ResultsAia.Clear();

                foreach (var r in results)
                {
                    var row = new ResultRow(r.AppName, r.Version);
                    if (string.Equals(r.Category, "AIA", StringComparison.OrdinalIgnoreCase))
                        ResultsAia.Add(row);
                    else
                        ResultsAit.Add(row);
                }

                ProgressValue = 100;
                StatusText = fetchCts.IsCancellationRequested ? $"Cancelled ({env})" : $"Completed ({env})";
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            FetchCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanFetch() => IsNotBusy;

    public void CancelFetch()
    {
        try
        {
            fetchCts?.Cancel();
        }
        catch
        {
            // ignore
        }
    }

    [RelayCommand]
    private void OpenSelectedLinkedPullRequest()
    {
        var url = SelectedLinkedWorkItem?.PullRequestUrl;
        TryOpenUrl(url);
    }

    [RelayCommand]
    private void OpenSelectedUnlinkedPullRequest()
    {
        var url = SelectedUnlinkedPullRequest?.PullRequestUrl;
        TryOpenUrl(url);
    }

    private static void TryOpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    private async Task RefreshWorkItemsAsync(List<AppInfo> apps, string env, VersionResult[] versionResults, CancellationToken cancellationToken)
    {
        if (!WorkItemEnvs.Contains(env))
        {
            Dispatcher.UIThread.Post(() =>
            {
                LinkedWorkItems.Clear();
                UnlinkedPullRequests.Clear();
                WorkItemsStatusText = "Work items are only tracked for QED/SBX/PROD (+ UK variants).";
            });
            return;
        }

        var token = GitHubWorkItemService.TryGetGitHubToken(GitHubCredentialTargetName, GitHubTokenEnvVarName);
        if (string.IsNullOrWhiteSpace(token))
        {
            Dispatcher.UIThread.Post(() =>
            {
                LinkedWorkItems.Clear();
                UnlinkedPullRequests.Clear();
                WorkItemsStatusText = $"Work items skipped: GitHub token not configured (CredMan '{GitHubCredentialTargetName}' or env var '{GitHubTokenEnvVarName}').";
            });
            return;
        }

        var service = new GitHubWorkItemService(GitHubHttp);

        var byAppName = versionResults.ToDictionary(v => v.AppName, StringComparer.OrdinalIgnoreCase);

        var linkedRows = new List<LinkedWorkItemRow>();
        var unlinkedRows = new List<UnlinkedPullRequestRow>();
        var notes = new List<string>();

        var anyStateChanged = false;

        foreach (var app in apps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(app.GitHubRepo))
                continue;

            if (!byAppName.TryGetValue(app.Name, out var v))
                continue;

            var currentSha = v.FullCommitSha;
            if (string.IsNullOrWhiteSpace(currentSha))
            {
                notes.Add($"{app.Name}: no commit SHA found in version");
                continue;
            }

            var key = WorkItemState.MakeKey(app.Name, env);
            if (!workItemState.Entries.TryGetValue(key, out var entry))
                entry = new WorkItemEnvEntry();

            var latestSnapshot = entry.Snapshots.FirstOrDefault();

            // Baseline priority: last cached current -> per-env baseline override.
            var baselineSha = latestSnapshot?.CurrentSha;
            if (string.IsNullOrWhiteSpace(baselineSha)
                && app.BaselineCommitByEnv is not null
                && app.BaselineCommitByEnv.TryGetValue(env, out var configuredBaseline))
            {
                baselineSha = configuredBaseline;
            }

            if (string.IsNullOrWhiteSpace(baselineSha))
            {
                notes.Add($"{app.Name}: baseline not set");
                continue;
            }

            if (latestSnapshot is not null && string.Equals(latestSnapshot.CurrentSha, currentSha, StringComparison.OrdinalIgnoreCase))
            {
                // Use cached snapshot.
                foreach (var wi in latestSnapshot.WorkItems)
                {
                    linkedRows.Add(new LinkedWorkItemRow(app.Name, wi.WorkItemId, wi.PullRequestNumber, wi.PullRequestTitle, wi.PullRequestUrl));
                }

                foreach (var pr in latestSnapshot.UnlinkedPullRequests)
                {
                    unlinkedRows.Add(new UnlinkedPullRequestRow(app.Name, pr.PullRequestNumber, pr.PullRequestTitle, pr.PullRequestUrl));
                }

                continue;
            }

            // No cache match; fetch from GitHub.
            var fetched = await service.FetchAsync(app.GitHubRepo!, baselineSha!, currentSha!, token!, cancellationToken);
            if (!fetched.IsOk)
            {
                notes.Add($"{app.Name}: {fetched.Error}");
                continue;
            }

            var snapshot = new WorkItemSnapshot
            {
                BaselineSha = baselineSha!,
                CurrentSha = currentSha!,
                WorkItems = fetched.WorkItems.ToList(),
                UnlinkedPullRequests = fetched.UnlinkedPullRequests.ToList(),
                Note = null
            };

            entry.Snapshots.Insert(0, snapshot);
            if (entry.Snapshots.Count > 2)
                entry.Snapshots.RemoveRange(2, entry.Snapshots.Count - 2);

            workItemState.Entries[key] = entry;
            anyStateChanged = true;

            foreach (var wi in snapshot.WorkItems)
            {
                linkedRows.Add(new LinkedWorkItemRow(app.Name, wi.WorkItemId, wi.PullRequestNumber, wi.PullRequestTitle, wi.PullRequestUrl));
            }

            foreach (var pr in snapshot.UnlinkedPullRequests)
            {
                unlinkedRows.Add(new UnlinkedPullRequestRow(app.Name, pr.PullRequestNumber, pr.PullRequestTitle, pr.PullRequestUrl));
            }
        }

        if (anyStateChanged)
        {
            // Save once per Fetch.
            try
            {
                workItemStore.Save(workItemState);
            }
            catch
            {
                // ignore
            }
        }

        var linkedCount = linkedRows.Count;
        var unlinkedCount = unlinkedRows.Count;
        var noteText = notes.Count == 0 ? "" : $" Notes: {string.Join(" | ", notes)}";

        Dispatcher.UIThread.Post(() =>
        {
            LinkedWorkItems.Clear();
            UnlinkedPullRequests.Clear();

            foreach (var r in linkedRows.OrderBy(r => r.App).ThenBy(r => r.WorkItemId).ThenByDescending(r => r.PullRequestNumber))
                LinkedWorkItems.Add(r);

            foreach (var r in unlinkedRows.OrderBy(r => r.App).ThenByDescending(r => r.PullRequestNumber))
                UnlinkedPullRequests.Add(r);

            WorkItemsStatusText = $"Work items: {linkedCount} linked entries; {unlinkedCount} unlinked PR(s).{noteText}";
        });
    }

    private static string GetWorkItemStatePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "AuditIntelligenceDeployedVersionTool", "workitems_state.json");
    }
}

public readonly record struct ResultRow(string App, string DeployedVersion);

public readonly record struct LinkedWorkItemRow(string App, int WorkItemId, int PullRequestNumber, string PullRequestTitle, string PullRequestUrl);

public readonly record struct UnlinkedPullRequestRow(string App, int PullRequestNumber, string PullRequestTitle, string PullRequestUrl);
