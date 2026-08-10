using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;
using DevStrider.Desktop.Views;

namespace DevStrider.Desktop.ViewModels;

public partial class InterviewPanelViewModel : ViewModelBase
{
    private readonly InterviewService _service;
    private readonly R2StorageService _storage;
    private readonly ProfileService _profile;

    public ObservableCollection<Interview> Items { get; } = new();

    private DateTime _from = DateTime.Today.AddDays(-7);
    public DateTime From { get => _from; set { if (SetProperty(ref _from, value)) _ = ReloadAsync(); } }

    private DateTime _to = DateTime.Today.AddDays(14);
    public DateTime To { get => _to; set { if (SetProperty(ref _to, value)) _ = ReloadAsync(); } }

    public InterviewPanelViewModel(
        InterviewService service, ProfileContext profileContext,
        R2StorageService storage, ProfileService profile)
    {
        _service = service;
        _storage = storage;
        _profile = profile;
        profileContext.ProfileChanged += () =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                new Action(async () => { try { await ReloadAsync(); } catch { /* ignore */ } }));
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            var fromUtc = From.ToUniversalTime();
            var toUtc = To.ToUniversalTime();
            var rows = await _service.ListAsync(fromUtc, toUtc);
            Items.Clear();
            foreach (var i in rows) Items.Add(i);
            StatusMessage = $"{rows.Count} interviews.";
        }
        finally { IsBusy = false; }
    }

    /// <summary>Parameter is <c>object?</c> to tolerate WPF passing <c>UnsetValue</c>; see BidBoardViewModel.</summary>
    [RelayCommand]
    public async Task SaveAsync(object? param)
    {
        if (param is not Interview iv) return;
        if (iv.Id == default) await _service.CreateAsync(iv);
        else await _service.UpdateAsync(iv);
        await ReloadAsync();
    }

    [RelayCommand]
    public async Task DeleteAsync(object? param)
    {
        if (param is not Interview iv) return;

        var label = $"{iv.Company} · {iv.Role} · {iv.InterviewType}".Trim(' ', '·');
        if (string.IsNullOrWhiteSpace(label)) label = "this interview";
        var when = iv.ScheduledDate?.ToString("MMM dd yyyy") ?? "(no date)";

        var ok = ConfirmDialog.Ask(
            System.Windows.Application.Current?.MainWindow,
            "Delete interview?",
            $"{label}\nScheduled: {when}\n\nThis can't be undone.");
        if (!ok) return;

        await _service.DeleteAsync(iv.Id);
        await ReloadAsync();
        StatusMessage = $"Deleted: {label}";
    }

    // ── Resume file in Cloudflare R2 ────────────────────────────────────────

    /// <summary>
    /// Attach a resume document to this interview and upload it to R2.
    ///
    /// <para>
    /// The macro already writes a .docx and .pdf to disk when a bid is generated, but they only
    /// ever existed on the machine that produced them. Putting the file behind the interview it
    /// belongs to means it is there when the interview actually happens — and, once synced, a
    /// teammate can pull the same document.
    /// </para>
    /// </summary>
    [RelayCommand]
    public async Task UploadResumeAsync(object? param)
    {
        if (param is not Interview iv) return;
        if (!await _storage.IsConfiguredAsync())
        {
            StatusMessage = "Cloud storage isn't configured — Settings → Cloud storage (Cloudflare R2).";
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Resume for {iv.Company} · {iv.InterviewType}",
            Filter = "Resume documents|*.pdf;*.docx;*.doc;*.rtf;*.odt;*.txt|All files|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        IsBusy = true;
        try
        {
            StatusMessage = "Uploading…";
            var username = (await _profile.GetAsync()).Username ?? "";
            var result = await _storage.UploadAsync(iv, dlg.FileName, username);
            StatusMessage = result.Message;
            if (!result.Ok) return;

            // Persist the pointer, and bump UpdatedAt so the next sync carries it to peers.
            await _service.UpdateAsync(iv);
            await ReloadAsync();
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Download the attached resume and hand it to the shell. Works for a peer's interview too —
    /// the object key is all that's needed, so nothing extra is required to read someone else's.
    /// </summary>
    [RelayCommand]
    public async Task OpenResumeAsync(object? param)
    {
        var (key, name) = param switch
        {
            Interview iv => (iv.ResumeObjectKey, iv.ResumeFileName),
            PeerInterview pv => (pv.ResumeObjectKey, pv.ResumeFileName),
            _ => ("", ""),
        };
        if (string.IsNullOrWhiteSpace(key)) { StatusMessage = "No resume attached."; return; }

        IsBusy = true;
        try
        {
            StatusMessage = "Downloading…";
            var (path, message) = await _storage.DownloadToTempAsync(key, name);
            StatusMessage = message;
            if (path is null) return;

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex) { StatusMessage = $"Couldn't open the file: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>Delete the object from R2 and clear the interview's pointer.</summary>
    [RelayCommand]
    public async Task RemoveResumeAsync(object? param)
    {
        if (param is not Interview iv) return;
        if (string.IsNullOrWhiteSpace(iv.ResumeObjectKey)) return;

        var ok = ConfirmDialog.Ask(
            System.Windows.Application.Current?.MainWindow,
            "Remove resume?",
            $"{iv.ResumeFileName}\n\nDeletes the file from cloud storage. Peers will lose access to it too.");
        if (!ok) return;

        IsBusy = true;
        try
        {
            var result = await _storage.DeleteAsync(iv);
            StatusMessage = result.Message;
            if (!result.Ok) return;
            await _service.UpdateAsync(iv);
            await ReloadAsync();
        }
        finally { IsBusy = false; }
    }

    /// <summary>Open a modal with the interview's attached JD text.</summary>
    public string GetJdFor(Interview iv) =>
        (iv?.AttachedJobDescription ?? "").Trim();

    /// <summary>
    /// Schedule a NEXT-step interview chained from this one. New interview captures the same
    /// company/role/resumeId/JD and points <c>ParentInterviewId</c> at the source.
    /// </summary>
    public async Task ScheduleNextStepAsync(
        Interview parent, DateTime? date, string time, string interviewType,
        string recruiter, string meetingLink)
    {
        if (parent == null) return;
        await _service.CreateAsync(new Interview
        {
            BidId = parent.BidId,
            ParentInterviewId = parent.Id,
            // Same hiring process as the round it follows — this is what keeps a pipeline
            // together for interviews that never came from a bid.
            ProcessId = parent.ProcessId,
            ScheduledDate = date,
            ScheduledTime = time,
            InterviewType = string.IsNullOrWhiteSpace(interviewType) ? InterviewTypes.Interview : interviewType,
            Recruiter = recruiter,
            MeetingLink = meetingLink,
            Company = parent.Company,
            Role = parent.Role,
            ResumeId = parent.ResumeId,
            AttachedJobDescription = parent.AttachedJobDescription,
            Status = InterviewStatuses.Scheduled,
            Origin = "NextStep"
        });
        await ReloadAsync();
        StatusMessage = $"Next-step {interviewType} scheduled for {parent.Company}.";
    }
}
