using AitApplicationDeployedVersions.Core;
using AitApplicationDeployedVersions.Models;
using Avalonia.Input.Platform;
using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace AitApplicationDeployedVersions.Avalonia.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private const int MaxConcurrency = 6;

    private readonly IClipboard? clipboard;

    private string? lastFetchedAitEnv;
    private string? lastFetchedAiaEnv;

    [ObservableProperty]
    private string aitHeaderText = "AIT (Test)";

    [ObservableProperty]
    private string aiaHeaderText = "AIA (Analyze)";

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

    private CancellationTokenSource? fetchCts;

    public MainWindowViewModel(IClipboard? clipboard = null)
    {
        this.clipboard = clipboard;
        SelectedEnvironment = Environments.FirstOrDefault();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotBusy));
    }

    [RelayCommand(CanExecute = nameof(CanFetch))]
    private Task FetchAitAsync() => FetchByCategoryAsync(category: "AIT");

    [RelayCommand(CanExecute = nameof(CanFetch))]
    private Task FetchAiaAsync() => FetchByCategoryAsync(category: "AIA");

    [RelayCommand]
    private Task CopyAitMarkdownAsync() => CopyMarkdownAsync(label: "AIT", ResultsAit, lastFetchedAitEnv);

    [RelayCommand]
    private Task CopyAiaMarkdownAsync() => CopyMarkdownAsync(label: "AIA", ResultsAia, lastFetchedAiaEnv);

    [RelayCommand]
    private Task CopyBothMarkdownAsync() => CopyBothMarkdownAsyncCore();

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

            // Populate Deployed Versions immediately after the HTTP fetch completes.
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

                if (fetchCts.IsCancellationRequested)
                {
                    StatusText = $"Cancelled {category} ({env}).";
                    return;
                }

                if (string.Equals(category, "AIA", StringComparison.OrdinalIgnoreCase))
                {
                    AiaHeaderText = $"AIA (Analyze) - {env}";
                    lastFetchedAiaEnv = env;
                }
                else
                {
                    AitHeaderText = $"AIT (Test) - {env}";
                    lastFetchedAitEnv = env;
                }

                StatusText = $"Fetched {category} ({env}).";
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

    private async Task CopyMarkdownAsync(string label, IReadOnlyCollection<ResultRow> rows, string? env)
    {
        if (string.IsNullOrWhiteSpace(env))
        {
            StatusText = $"Please fetch {label} first.";
            return;
        }

        if (rows.Count == 0)
        {
            StatusText = $"Nothing to copy for {label}.";
            return;
        }

        var md = FormatMarkdownSingle(label, env, rows);
        await CopyToClipboardAsync(md, $"Copied {label} ({rows.Count} rows) for {env}.");
    }

    private async Task CopyBothMarkdownAsyncCore()
    {
        if (ResultsAit.Count == 0 && ResultsAia.Count == 0)
        {
            StatusText = "Nothing to copy.";
            return;
        }

        var aitEnv = ResultsAit.Count > 0 ? lastFetchedAitEnv : null;
        var aiaEnv = ResultsAia.Count > 0 ? lastFetchedAiaEnv : null;

        if (ResultsAit.Count > 0 && string.IsNullOrWhiteSpace(aitEnv))
        {
            StatusText = "Please fetch AIT first.";
            return;
        }

        if (ResultsAia.Count > 0 && string.IsNullOrWhiteSpace(aiaEnv))
        {
            StatusText = "Please fetch AIA first.";
            return;
        }

        var md = FormatMarkdownBoth(ResultsAit, aitEnv, ResultsAia, aiaEnv);
        var totalRows = ResultsAit.Count + ResultsAia.Count;
        await CopyToClipboardAsync(md, $"Copied both tables ({totalRows} rows).");
    }

    private static string FormatMarkdownSingle(string category, string env, IEnumerable<ResultRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### {EscapeMarkdownInline(category)} ({EscapeMarkdownInline(env)})");
        sb.AppendLine();
        sb.AppendLine("| Environment | App | Deployed Version |");
        sb.AppendLine("|---|---|---|");
        foreach (var row in rows)
            sb.AppendLine($"| {EscapeMarkdownCell(env)} | {EscapeMarkdownCell(row.App)} | {EscapeMarkdownCell(row.DeployedVersion)} |");
        return sb.ToString().TrimEnd();
    }

    private static string FormatMarkdownBoth(
        IEnumerable<ResultRow> aitRows,
        string? aitEnv,
        IEnumerable<ResultRow> aiaRows,
        string? aiaEnv)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Category | Environment | App | Deployed Version |");
        sb.AppendLine("|---|---|---|---|");

        foreach (var row in aitRows)
            sb.AppendLine($"| AIT | {EscapeMarkdownCell(aitEnv)} | {EscapeMarkdownCell(row.App)} | {EscapeMarkdownCell(row.DeployedVersion)} |");

        foreach (var row in aiaRows)
            sb.AppendLine($"| AIA | {EscapeMarkdownCell(aiaEnv)} | {EscapeMarkdownCell(row.App)} | {EscapeMarkdownCell(row.DeployedVersion)} |");

        return sb.ToString().TrimEnd();
    }

    private static string EscapeMarkdownInline(string? value)
    {
        // Basic escaping for headings/inline contexts.
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    }

    private static string EscapeMarkdownCell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        // Keep it readable in table cells: remove newlines, escape pipe and backslash.
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
    }

    private async Task CopyToClipboardAsync(string text, string successStatus)
    {
        if (clipboard is null)
        {
            StatusText = "Clipboard is not available.";
            return;
        }

        await clipboard.SetTextAsync(text);

        StatusText = successStatus;
    }

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

    private static List<AppInfo> FilterAppsByCategory(List<AppInfo> apps, string category)
    {
        if (string.Equals(category, "AIA", StringComparison.OrdinalIgnoreCase))
            return apps.Where(a => string.Equals(a.Category, "AIA", StringComparison.OrdinalIgnoreCase)).ToList();

        // Treat missing/unknown as AIT.
        return apps.Where(a => !string.Equals(a.Category, "AIA", StringComparison.OrdinalIgnoreCase)).ToList();
    }
}

public readonly record struct ResultRow(string App, string DeployedVersion);
