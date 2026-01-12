using AitApplicationDeployedVersions.Core;
using AitApplicationDeployedVersions.Models;
using System;
using System.Linq;
using System.Net.Http;
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

    public MainWindowViewModel()
    {
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
                    AiaHeaderText = $"AIA (Analyze) - {env}";
                else
                    AitHeaderText = $"AIT (Test) - {env}";

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
