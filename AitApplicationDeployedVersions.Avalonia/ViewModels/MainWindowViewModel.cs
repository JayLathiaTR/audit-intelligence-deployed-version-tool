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
using System.Security.Cryptography;
using System.Text;
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

    private const string AdoWorkItemUrlTemplateEnvVarName = "AITVERS_ADO_WORKITEM_URL_TEMPLATE";
    private const string AdoWorkItemUrlTemplateDefault = "https://dev.azure.com/tr-tax/TaxProf/_workitems/edit/{id}";

    private static bool IsWorkItemsEnv(string env)
        => !string.Equals(env, "CI", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(env, "DEMO", StringComparison.OrdinalIgnoreCase);

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

    public ObservableCollection<LinkedWorkItemRow> LinkedWorkItemsAit { get; } = new();
    public ObservableCollection<UnlinkedPullRequestRow> UnlinkedPullRequestsAit { get; } = new();
    public ObservableCollection<LinkedWorkItemRow> LinkedWorkItemsAia { get; } = new();
    public ObservableCollection<UnlinkedPullRequestRow> UnlinkedPullRequestsAia { get; } = new();

    [ObservableProperty]
    private string workItemsStatusTextAit = "AIT work items: (not fetched)";

    [ObservableProperty]
    private string workItemsStatusTextAia = "AIA work items: (not fetched)";

    [ObservableProperty]
    private LinkedWorkItemRow? selectedLinkedWorkItemAit;

    [ObservableProperty]
    private LinkedWorkItemRow? selectedLinkedWorkItemAia;

    [ObservableProperty]
    private UnlinkedPullRequestRow? selectedUnlinkedPullRequestAit;

    [ObservableProperty]
    private UnlinkedPullRequestRow? selectedUnlinkedPullRequestAia;

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
    private Task FetchAitAsync() => FetchByCategoryAsync(category: "AIT");

    [RelayCommand(CanExecute = nameof(CanFetch))]
    private Task FetchAiaAsync() => FetchByCategoryAsync(category: "AIA");

    private async Task FetchByCategoryAsync(string category)
    {
        if (string.IsNullOrWhiteSpace(SelectedEnvironment))
        {
            StatusText = "Please select an environment.";
            return;
        }

        try
        {
            IsBusy = true;
            FetchAitCommand.NotifyCanExecuteChanged();
            FetchAiaCommand.NotifyCanExecuteChanged();

            fetchCts?.Cancel();
            fetchCts?.Dispose();
            fetchCts = new CancellationTokenSource();

            var env = SelectedEnvironment;
            var allApps = AppCore.LoadApps();
            var apps = FilterAppsByCategory(allApps, category);
            var total = apps.Count;

            ProgressValue = 0;
            StatusText = $"Fetching {env} ({category}) 0/{total}…";

            var progress = new Progress<FetchProgress>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ProgressValue = p.Total > 0 ? (double)p.Completed / p.Total * 100d : 0;
                    StatusText = $"Fetching {env} ({category}) {p.Completed}/{p.Total}: {p.CurrentApp}";
                });
            });

            var results = await AppCore.FetchAllAsync(apps, env, MaxConcurrency, progress, fetchCts.Token);
            await RefreshWorkItemsByCategoryAsync(apps, env, results, category, fetchCts.Token);

            Dispatcher.UIThread.Post(() =>
            {
                if (string.Equals(category, "AIA", StringComparison.OrdinalIgnoreCase))
                    ResultsAia.Clear();
                else
                    ResultsAit.Clear();

                foreach (var r in results)
                {
                    var row = new ResultRow(r.AppName, r.Version);
                    if (string.Equals(category, "AIA", StringComparison.OrdinalIgnoreCase))
                        ResultsAia.Add(row);
                    else
                        ResultsAit.Add(row);
                }

                ProgressValue = 100;
                StatusText = fetchCts.IsCancellationRequested ? $"Cancelled ({env})" : $"Completed ({env}) ({category})";
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            FetchAitCommand.NotifyCanExecuteChanged();
            FetchAiaCommand.NotifyCanExecuteChanged();
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
    private void OpenSelectedLinkedPullRequestAit()
    {
        var url = SelectedLinkedWorkItemAit?.PullRequestUrl;
        TryOpenUrl(url);
    }

    [RelayCommand]
    private void OpenSelectedLinkedPullRequestAia()
    {
        var url = SelectedLinkedWorkItemAia?.PullRequestUrl;
        TryOpenUrl(url);
    }

    [RelayCommand(CanExecute = nameof(CanOpenSelectedLinkedWorkItem))]
    private void OpenSelectedLinkedWorkItemAit()
    {
        var id = SelectedLinkedWorkItemAit?.WorkItemId;
        if (id is null) return;
        OpenWorkItemId(id.Value);
    }

    private bool CanOpenSelectedLinkedWorkItem()
        => SelectedLinkedWorkItemAit is not null || SelectedLinkedWorkItemAia is not null;

    [RelayCommand(CanExecute = nameof(CanOpenSelectedLinkedWorkItem))]
    private void OpenSelectedLinkedWorkItemAia()
    {
        var id = SelectedLinkedWorkItemAia?.WorkItemId;
        if (id is null) return;
        OpenWorkItemId(id.Value);
    }

    [RelayCommand]
    private void OpenSelectedUnlinkedPullRequestAit()
    {
        var url = SelectedUnlinkedPullRequestAit?.PullRequestUrl;
        TryOpenUrl(url);
    }

    [RelayCommand]
    private void OpenSelectedUnlinkedPullRequestAia()
    {
        var url = SelectedUnlinkedPullRequestAia?.PullRequestUrl;
        TryOpenUrl(url);
    }

    partial void OnSelectedLinkedWorkItemAitChanged(LinkedWorkItemRow? value)
        => OpenSelectedLinkedWorkItemAitCommand.NotifyCanExecuteChanged();

    partial void OnSelectedLinkedWorkItemAiaChanged(LinkedWorkItemRow? value)
        => OpenSelectedLinkedWorkItemAiaCommand.NotifyCanExecuteChanged();

    private void OpenWorkItemId(int id)
    {
        var template = GetAdoWorkItemUrlTemplate();
        if (string.IsNullOrWhiteSpace(template))
            template = AdoWorkItemUrlTemplateDefault;

        var url = template.Replace("{id}", id.ToString(), StringComparison.OrdinalIgnoreCase);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = $"Invalid ADO work item URL template in '{AdoWorkItemUrlTemplateEnvVarName}'.";
            return;
        }

        TryOpenUrl(url);
    }

    private static string? GetAdoWorkItemUrlTemplate()
    {
        var template = Environment.GetEnvironmentVariable(AdoWorkItemUrlTemplateEnvVarName);
        return string.IsNullOrWhiteSpace(template) ? null : template.Trim();
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

    private async Task RefreshWorkItemsByCategoryAsync(List<AppInfo> apps, string env, VersionResult[] versionResults, string category, CancellationToken cancellationToken)
    {
        if (!IsWorkItemsEnv(env))
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (string.Equals(category, "AIA", StringComparison.OrdinalIgnoreCase))
                {
                    LinkedWorkItemsAia.Clear();
                    UnlinkedPullRequestsAia.Clear();
                    WorkItemsStatusTextAia = "Work items and PRs are only retrieved for higher environments (QED/SBX/PROD and UK variants), not for CI or DEMO.";
                }
                else
                {
                    LinkedWorkItemsAit.Clear();
                    UnlinkedPullRequestsAit.Clear();
                    WorkItemsStatusTextAit = "Work items and PRs are only retrieved for higher environments (QED/SBX/PROD and UK variants), not for CI or DEMO.";
                }
            });
            return;
        }

        var token = GitHubWorkItemService.TryGetGitHubToken(GitHubCredentialTargetName, GitHubTokenEnvVarName);
        if (string.IsNullOrWhiteSpace(token))
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (string.Equals(category, "AIA", StringComparison.OrdinalIgnoreCase))
                {
                    LinkedWorkItemsAia.Clear();
                    UnlinkedPullRequestsAia.Clear();
                    WorkItemsStatusTextAia = $"Work items skipped: GitHub token not configured (CredMan '{GitHubCredentialTargetName}' or env var '{GitHubTokenEnvVarName}').";
                }
                else
                {
                    LinkedWorkItemsAit.Clear();
                    UnlinkedPullRequestsAit.Clear();
                    WorkItemsStatusTextAit = $"Work items skipped: GitHub token not configured (CredMan '{GitHubCredentialTargetName}' or env var '{GitHubTokenEnvVarName}').";
                }
            });
            return;
        }

        var service = new GitHubWorkItemService(GitHubHttp);

        var byAppName = versionResults.ToDictionary(v => v.AppName, StringComparer.OrdinalIgnoreCase);

        var didReset = EnsureServiceSetSignatureAndResetIfChanged(apps, env, category);

        var linkedRows = new List<LinkedWorkItemRow>();
        var unlinkedRows = new List<UnlinkedPullRequestRow>();
        var notes = new List<string>();

        var anyStateChanged = didReset;
        var baselinesInitialized = 0;
        var servicesFetched = 0;
        var eligibleServices = 0;

        if (didReset)
            notes.Add($"{category} cache reset (service list changed)");

        // Group-level invalidation: if any service in this category changed SHA, we don't show cached results.
        var anyServiceChangedSha = false;
        foreach (var app in apps)
        {
            if (string.IsNullOrWhiteSpace(app.GitHubRepo))
                continue;

            if (!byAppName.TryGetValue(app.Name, out var v0))
                continue;

            var sha0 = v0.FullCommitSha;
            if (string.IsNullOrWhiteSpace(sha0))
                continue;

            var key0 = WorkItemState.MakeKey(app.Name, env);
            if (workItemState.Entries.TryGetValue(key0, out var entry0))
            {
                var snap0 = entry0.Snapshots.FirstOrDefault();
                if (snap0 is not null && !string.Equals(snap0.CurrentSha, sha0, StringComparison.OrdinalIgnoreCase))
                {
                    anyServiceChangedSha = true;
                    break;
                }
            }
            else
            {
                // No cache exists yet; treat as change so we fetch fresh.
                anyServiceChangedSha = true;
                break;
            }
        }

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

            eligibleServices++;

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
                // First run (or baseline wiped): initialize baseline to current SHA so next deployment shows a delta.
                var init = new WorkItemSnapshot
                {
                    BaselineSha = currentSha!,
                    CurrentSha = currentSha!,
                    WorkItems = new List<WorkItemLink>(),
                    UnlinkedPullRequests = new List<UnlinkedPullRequest>(),
                    Note = "Baseline initialized"
                };

                entry.Snapshots.Insert(0, init);
                if (entry.Snapshots.Count > 2)
                    entry.Snapshots.RemoveRange(2, entry.Snapshots.Count - 2);

                workItemState.Entries[key] = entry;
                anyStateChanged = true;
                baselinesInitialized++;
                continue;
            }

            var hasChanged = latestSnapshot is null || !string.Equals(latestSnapshot.CurrentSha, currentSha, StringComparison.OrdinalIgnoreCase);

            // If nothing changed across the group, show cached rows.
            if (!anyServiceChangedSha && !hasChanged && latestSnapshot is not null)
            {
                foreach (var wi in latestSnapshot.WorkItems)
                    linkedRows.Add(new LinkedWorkItemRow(app.Name, wi.WorkItemId, wi.PullRequestNumber, wi.PullRequestTitle, wi.PullRequestUrl));

                foreach (var pr in latestSnapshot.UnlinkedPullRequests)
                    unlinkedRows.Add(new UnlinkedPullRequestRow(app.Name, pr.PullRequestNumber, pr.PullRequestTitle, pr.PullRequestUrl));

                continue;
            }

            // If any service changed, only fetch/show for services that changed; unchanged services contribute nothing.
            if (anyServiceChangedSha && !hasChanged)
                continue;

            // No cache match; fetch from GitHub.
            var fetched = await service.FetchAsync(app.GitHubRepo!, baselineSha!, currentSha!, token!, cancellationToken);
            if (!fetched.IsOk)
            {
                notes.Add($"{app.Name}: {fetched.Error}");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(fetched.Warning))
                notes.Add($"{app.Name}: {fetched.Warning}");

            servicesFetched++;

            var snapshot = new WorkItemSnapshot
            {
                BaselineSha = baselineSha!,
                CurrentSha = currentSha!,
                WorkItems = fetched.WorkItems.ToList(),
                UnlinkedPullRequests = fetched.UnlinkedPullRequests.ToList(),
                Note = fetched.Warning
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
            if (string.Equals(category, "AIA", StringComparison.OrdinalIgnoreCase))
            {
                LinkedWorkItemsAia.Clear();
                UnlinkedPullRequestsAia.Clear();

                foreach (var r in linkedRows.OrderBy(r => r.App).ThenBy(r => r.WorkItemId).ThenByDescending(r => r.PullRequestNumber))
                    LinkedWorkItemsAia.Add(r);

                foreach (var r in unlinkedRows.OrderBy(r => r.App).ThenByDescending(r => r.PullRequestNumber))
                    UnlinkedPullRequestsAia.Add(r);

                if (baselinesInitialized > 0 && servicesFetched == 0 && linkedCount == 0 && unlinkedCount == 0)
                {
                    var denom = eligibleServices <= 0 ? baselinesInitialized : eligibleServices;
                    WorkItemsStatusTextAia = $"AIA baseline initialized for {baselinesInitialized}/{denom} service(s). No delta yet.{noteText}";
                }
                else
                {
                    WorkItemsStatusTextAia = anyServiceChangedSha
                        ? $"AIA work items refreshed (delta): {linkedCount} linked; {unlinkedCount} unlinked PR(s).{noteText}"
                        : $"AIA work items (cached): {linkedCount} linked; {unlinkedCount} unlinked PR(s).{noteText}";
                }
            }
            else
            {
                LinkedWorkItemsAit.Clear();
                UnlinkedPullRequestsAit.Clear();

                foreach (var r in linkedRows.OrderBy(r => r.App).ThenBy(r => r.WorkItemId).ThenByDescending(r => r.PullRequestNumber))
                    LinkedWorkItemsAit.Add(r);

                foreach (var r in unlinkedRows.OrderBy(r => r.App).ThenByDescending(r => r.PullRequestNumber))
                    UnlinkedPullRequestsAit.Add(r);

                if (baselinesInitialized > 0 && servicesFetched == 0 && linkedCount == 0 && unlinkedCount == 0)
                {
                    var denom = eligibleServices <= 0 ? baselinesInitialized : eligibleServices;
                    WorkItemsStatusTextAit = $"AIT baseline initialized for {baselinesInitialized}/{denom} service(s). No delta yet.{noteText}";
                }
                else
                {
                    WorkItemsStatusTextAit = anyServiceChangedSha
                        ? $"AIT work items refreshed (delta): {linkedCount} linked; {unlinkedCount} unlinked PR(s).{noteText}"
                        : $"AIT work items (cached): {linkedCount} linked; {unlinkedCount} unlinked PR(s).{noteText}";
                }
            }
        });
    }

    private bool EnsureServiceSetSignatureAndResetIfChanged(List<AppInfo> apps, string env, string category)
    {
        // Signature should be stable regardless of ordering.
        var signatureSource = string.Join("|",
            apps
                .Where(a => !string.IsNullOrWhiteSpace(a.GitHubRepo))
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(a => $"{a.Name}:{a.GitHubRepo}"));

        var signature = ComputeSha256Hex(signatureSource);

        var setKey = WorkItemState.MakeSetKey(category, env);
        if (!workItemState.ServiceSetSignatures.TryGetValue(setKey, out var previous) || !string.Equals(previous, signature, StringComparison.OrdinalIgnoreCase))
        {
            // Reset cached per-service entries for this set so baseline init is consistent after config changes.
            foreach (var app in apps)
            {
                var key = WorkItemState.MakeKey(app.Name, env);
                workItemState.Entries.Remove(key);
            }

            workItemState.ServiceSetSignatures[setKey] = signature;

            // Persist via existing save path later (anyStateChanged).
            // Note: caller will append a user-facing note.
            return true;
        }

        return false;
    }

    private static string ComputeSha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static List<AppInfo> FilterAppsByCategory(List<AppInfo> apps, string category)
    {
        if (string.Equals(category, "AIA", StringComparison.OrdinalIgnoreCase))
            return apps.Where(a => string.Equals(a.Category, "AIA", StringComparison.OrdinalIgnoreCase)).ToList();

        // Treat missing/unknown as AIT.
        return apps.Where(a => !string.Equals(a.Category, "AIA", StringComparison.OrdinalIgnoreCase)).ToList();
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
