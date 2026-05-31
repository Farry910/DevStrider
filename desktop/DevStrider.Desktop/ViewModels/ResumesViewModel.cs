using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;
using Microsoft.Win32;

namespace DevStrider.Desktop.ViewModels;

public partial class ResumesViewModel : ViewModelBase
{
    private readonly ResumeService _service;
    public ObservableCollection<Resume> Items { get; } = new();

    public ResumesViewModel(ResumeService service)
    {
        _service = service;
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            Items.Clear();
            foreach (var r in await _service.ListAsync()) Items.Add(r);
            StatusMessage = $"{Items.Count} resume{(Items.Count == 1 ? "" : "s")} uploaded.";
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Multi-file picker → ingest each. The filename is parsed (UID, Company, Role, stacks)
    /// — the user keeps using their own naming convention, we just store the parsed fields
    /// so the bid board / interview row can resolve the resume by UID.
    /// </summary>
    [RelayCommand]
    public async Task UploadAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Resume files|*.pdf;*.docx;*.doc;*.txt;*.md|All files|*.*",
            Multiselect = true,
            Title = "Pick one or more resume files to upload"
        };
        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        try
        {
            var added = 0;
            foreach (var path in dialog.FileNames)
            {
                try
                {
                    await _service.AddFromFileAsync(path);
                    added++;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Upload failed for {path}: {ex.Message}");
                }
            }
            await ReloadAsync();
            StatusMessage = $"Added {added} resume{(added == 1 ? "" : "s")}.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task DeleteAsync(object? param)
    {
        if (param is not Resume r) return;
        await _service.DeleteAsync(r.Id);
        Items.Remove(r);
        StatusMessage = $"Removed {r.FileName}.";
    }

    /// <summary>Save the picked resume to a user-chosen location — handy outside of an interview row.</summary>
    [RelayCommand]
    public async Task DownloadAsync(object? param)
    {
        if (param is not Resume r) return;
        var dialog = new SaveFileDialog
        {
            FileName = r.FileName,
            Filter = "All files|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        await _service.SaveToFileAsync(r, dialog.FileName);
        StatusMessage = $"Saved {r.FileName}.";
    }
}
