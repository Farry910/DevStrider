using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;
using Microsoft.Win32;
using MongoDB.Driver;

namespace DevStrider.Desktop.ViewModels;

public partial class InterviewPanelViewModel : ViewModelBase
{
    private readonly InterviewService _service;
    private readonly ResumeService _resumes;
    private readonly MongoContext _db;

    public ObservableCollection<Interview> Items { get; } = new();

    private DateTime _from = DateTime.Today.AddDays(-7);
    public DateTime From { get => _from; set { if (SetProperty(ref _from, value)) _ = ReloadAsync(); } }

    private DateTime _to = DateTime.Today.AddDays(14);
    public DateTime To { get => _to; set { if (SetProperty(ref _to, value)) _ = ReloadAsync(); } }

    public InterviewPanelViewModel(InterviewService service, ResumeService resumes, MongoContext db)
    {
        _service = service;
        _resumes = resumes;
        _db = db;
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
        await _service.DeleteAsync(iv.Id);
        await ReloadAsync();
    }

    /// <summary>
    /// Find the resume the user submitted for this interview's bid and save it to disk.
    /// Lookup path: Interview → BidId → UserBid.ResumeId (the resume's UID) → Resume bytes.
    /// </summary>
    [RelayCommand]
    public async Task DownloadResumeAsync(object? param)
    {
        if (param is not Interview iv)
        {
            StatusMessage = "No interview selected.";
            return;
        }
        var bid = await _db.Bids.Find(b => b.Id == iv.BidId).FirstOrDefaultAsync();
        if (bid == null || string.IsNullOrWhiteSpace(bid.ResumeId))
        {
            StatusMessage = "This interview's bid has no UID set.";
            return;
        }
        var resume = await _resumes.GetByUidAsync(bid.ResumeId);
        if (resume == null)
        {
            StatusMessage = $"No resume uploaded with UID '{bid.ResumeId}'.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = resume.FileName,
            Filter = resume.ContentType == "application/pdf"
                ? "PDF (*.pdf)|*.pdf|All files|*.*"
                : "All files|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            await _resumes.SaveToFileAsync(resume, dialog.FileName);
            StatusMessage = $"Saved {resume.FileName}.";
        }
    }
}
