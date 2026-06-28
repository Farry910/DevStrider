using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;
using MongoDB.Bson;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// Drives the Resume tab: paste a batch of job URLs, Start, and walk away. The merged Chrome
/// extension polls the app for each queued URL, scrapes the JD, drives ChatGPT (no clipboard,
/// background-safe), and POSTs the result back — which runs the Word macro and auto-records
/// the bid. This VM just manages the queue + shows live status.
/// </summary>
public partial class ResumeViewModel : ViewModelBase
{
    private readonly ResumeQueueService _queue;
    private readonly ProfileContext _profileContext;

    public ObservableCollection<ResumeJob> Jobs { get; } = new();

    private string _pasteText = "";
    public string PasteText { get => _pasteText; set => SetProperty(ref _pasteText, value); }

    public bool IsRunning => _queue.IsRunning;

    private string _counts = "";
    public string Counts { get => _counts; set => SetProperty(ref _counts, value); }

    public ResumeViewModel(ResumeQueueService queue, ProfileContext profileContext)
    {
        _queue = queue;
        _profileContext = profileContext;

        _queue.Changed += OnQueueChanged;
        _profileContext.ProfileChanged += OnQueueChanged;
    }

    private void OnQueueChanged() =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            new Action(async () => { try { await ReloadAsync(); } catch { /* ignore */ } }));

    [RelayCommand]
    public async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            var jobs = await _queue.ListAsync();
            Jobs.Clear();
            foreach (var j in jobs) Jobs.Add(j);

            int q = jobs.Count(j => j.Status == ResumeJobStatuses.Queued);
            int gen = jobs.Count(j => j.Status is ResumeJobStatuses.Generating or ResumeJobStatuses.Fetching or ResumeJobStatuses.ResumeReceived);
            int done = jobs.Count(j => j.Status == ResumeJobStatuses.Done);
            int fail = jobs.Count(j => j.Status == ResumeJobStatuses.Failed);
            Counts = $"{q} queued · {gen} in progress · {done} done · {fail} failed";
            OnPropertyChanged(nameof(IsRunning));
        }
        finally { IsBusy = false; }
    }

    /// <summary>Parse the paste box into URLs, enqueue them for the active profile, and start.</summary>
    [RelayCommand]
    public async Task StartAsync()
    {
        if (_profileContext.Current == null)
        {
            StatusMessage = "Pick a profile first (title-bar switcher).";
            return;
        }

        var urls = (PasteText ?? "")
            .Replace("\r\n", "\n").Split('\n', ' ', '\t')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0);

        var (added, skipped) = await _queue.EnqueueAsync(urls);
        PasteText = "";
        _queue.Start();
        OnPropertyChanged(nameof(IsRunning));
        await ReloadAsync();
        StatusMessage = $"Queued {added} URL{(added == 1 ? "" : "s")}" +
                        (skipped > 0 ? $" ({skipped} skipped — duplicates/invalid)" : "") +
                        ". Keep a logged-in ChatGPT tab open; you can work in other apps.";
    }

    [RelayCommand]
    public void Stop()
    {
        _queue.Stop();
        OnPropertyChanged(nameof(IsRunning));
        StatusMessage = "Batch paused. Queued jobs stay; press Start to resume.";
    }

    [RelayCommand]
    public async Task RetryFailedAsync()
    {
        var n = await _queue.RetryFailedAsync();
        _queue.Start();
        OnPropertyChanged(nameof(IsRunning));
        await ReloadAsync();
        StatusMessage = n > 0 ? $"Re-queued {n} failed job{(n == 1 ? "" : "s")}." : "No failed jobs to retry.";
    }

    [RelayCommand]
    public async Task ClearFinishedAsync()
    {
        var n = await _queue.ClearFinishedAsync();
        await ReloadAsync();
        StatusMessage = n > 0 ? $"Cleared {n} finished job{(n == 1 ? "" : "s")}." : "Nothing to clear.";
    }

    /// <summary>Parameter is <c>object?</c> to tolerate WPF UnsetValue; see BidBoardViewModel.</summary>
    [RelayCommand]
    public async Task DeleteJobAsync(object? param)
    {
        if (param is not ResumeJob job) return;
        await _queue.DeleteAsync(job.Id);
        await ReloadAsync();
    }
}
