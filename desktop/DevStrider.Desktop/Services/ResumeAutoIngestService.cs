using System.Diagnostics;
using System.IO;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Watches the user-configured output folder (where the Word macro saves generated resumes)
/// and auto-imports every new .pdf / .docx / .doc into the local Resumes collection. The file
/// is parsed by <see cref="ResumeService.ParseFileName"/> using the same
/// "UID, Company, Role, Stack1, Stack2, …" convention as the rest of the app.
///
/// Deduped via UID — a second file with the same UID overwrites the bytes of the first row
/// rather than creating a duplicate (handy when the macro re-runs for the same bid).
/// </summary>
public sealed class ResumeAutoIngestService : IDisposable
{
    private readonly ResumeService _resumes;
    private readonly SettingsService _settings;
    private FileSystemWatcher? _watcher;
    private string _watchedFolder = "";
    private static readonly HashSet<string> AcceptedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx"
    };

    public ResumeAutoIngestService(ResumeService resumes, SettingsService settings)
    {
        _resumes = resumes;
        _settings = settings;
    }

    public async Task StartAsync()
    {
        var s = await _settings.GetAsync();
        Restart(s.ResumeOutputFolder);
    }

    /// <summary>Swap to a different folder (called from Settings save).</summary>
    public void Restart(string folder)
    {
        StopWatcher();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        _watchedFolder = folder;
        _watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            // Each pattern is OR-ed; FileSystemWatcher's Filter only takes one, so we register
            // separately for each accepted extension via NotifyFilter + a single permissive
            // filter and then check the extension in the handler.
            Filter = "*.*",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        _watcher.Created += OnChanged;
        _watcher.Renamed += OnChanged;
    }

    private void StopWatcher()
    {
        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnChanged;
        _watcher.Renamed -= OnChanged;
        _watcher.Dispose();
        _watcher = null;
        _watchedFolder = "";
    }

    private async void OnChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            var ext = Path.GetExtension(e.FullPath);
            if (!AcceptedExtensions.Contains(ext)) return;

            // Word writes the file in stages — wait until it's stable + readable.
            if (!await WaitForFileReadyAsync(e.FullPath, timeoutMs: 8_000)) return;

            await _resumes.AddFromFileAsync(e.FullPath);
            Debug.WriteLine($"[ResumeAutoIngest] Imported {Path.GetFileName(e.FullPath)}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ResumeAutoIngest] {Path.GetFileName(e.FullPath)} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Poll the file until two consecutive size reads match (means the writer's done) AND it
    /// opens read-only (no exclusive lock). Bounded by <paramref name="timeoutMs"/>.
    /// </summary>
    private static async Task<bool> WaitForFileReadyAsync(string path, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        long lastLen = -1;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var len = new FileInfo(path).Length;
                if (len == lastLen && len > 0)
                {
                    using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    return true;
                }
                lastLen = len;
            }
            catch (IOException) { /* still being written */ }
            catch (FileNotFoundException) { return false; }
            await Task.Delay(300);
        }
        return false;
    }

    public void Dispose() => StopWatcher();
}
