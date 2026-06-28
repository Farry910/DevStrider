using System.Diagnostics;
using System.IO;
using System.Text;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Runs a Word VBA macro by name against a .docm, headless, in the background — the exact
/// mechanism ResumeAuto used, replicated in C# so existing macros work unchanged.
///
/// <para>Contract (so author macros keep working):</para>
/// <list type="number">
///   <item>Resume text is written to a temp .txt file.</item>
///   <item>The <b>path</b> of that temp file is written to the bridge file
///         <c>%TEMP%\resume_bridge_path.txt</c>.</item>
///   <item>PowerShell opens Word via COM with <c>Visible = $false</c>, opens the .docm,
///         and calls <c>$word.Run(MacroName)</c>.</item>
///   <item>The macro reads the bridge file → temp .txt → resume text, populates the doc,
///         saves the named output, and <b>closes Word itself</b>. The script waits for Word
///         to close (polling), force-killing after a timeout.</item>
/// </list>
///
/// No clipboard, no foreground focus — safe to run while the user does other things.
/// A process-wide lock serializes runs so the fixed-name bridge file can't race.
/// </summary>
public sealed class WordMacroService
{
    private static readonly SemaphoreSlim MacroLock = new(1, 1);
    private const int MacroTimeoutSeconds = 90;

    private readonly ActivityLogService _activity;

    public WordMacroService(ActivityLogService activity)
    {
        _activity = activity;
    }

    public record Result(bool Success, string Message);

    /// <summary>
    /// Invoke <paramref name="macroName"/> in <paramref name="docmPath"/> with the given resume
    /// text. Returns success + a short message. Never throws — failures come back in the Result.
    /// </summary>
    public async Task<Result> RunAsync(string resumeText, string docmPath, string macroName, string profileName)
    {
        if (string.IsNullOrWhiteSpace(docmPath) || !File.Exists(docmPath))
            return new Result(false, $"Word document not found: {docmPath}");
        if (string.IsNullOrWhiteSpace(macroName))
            return new Result(false, "No macro name set for this profile.");

        await MacroLock.WaitAsync();
        try
        {
            return await Task.Run(() => RunInternal(resumeText, docmPath, macroName, profileName));
        }
        finally
        {
            MacroLock.Release();
        }
    }

    private Result RunInternal(string resumeText, string docmPath, string macroName, string profileName)
    {
        var tempTxt = Path.Combine(Path.GetTempPath(), $"devstrider_resume_{Guid.NewGuid():N}.txt");
        var psScriptPath = Path.Combine(Path.GetTempPath(), $"devstrider_macro_{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(tempTxt, resumeText ?? "", new UTF8Encoding(false));
            File.WriteAllText(psScriptPath, BuildPowerShell(), new UTF8Encoding(false));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in new[]
            {
                "-ExecutionPolicy", "Bypass", "-File", psScriptPath,
                "-TempTextPath", tempTxt,
                "-DocmPath", Path.GetFullPath(docmPath),
                "-MacroName", macroName,
                "-ProfileName", string.IsNullOrWhiteSpace(profileName) ? "profile" : profileName,
                "-BridgeFileName", "resume_bridge_path.txt",
            })
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = Process.Start(psi);
            if (proc == null) return new Result(false, "Couldn't start PowerShell.");

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            // Hard wall well past the macro's own timeout so a wedged process can't hang us.
            if (!proc.WaitForExit((MacroTimeoutSeconds + 30) * 1000))
            {
                try { proc.Kill(true); } catch { /* ignore */ }
                return new Result(false, "Macro process timed out and was killed.");
            }

            if (proc.ExitCode == 0 && stdout.Contains("SUCCESS"))
                return new Result(true, "Macro ran; Word document produced.");

            var detail = stderr.Trim();
            if (string.IsNullOrEmpty(detail)) detail = stdout.Trim();
            if (detail.Length > 300) detail = detail[..300];
            return new Result(false, string.IsNullOrEmpty(detail) ? "Macro failed (no output)." : detail);
        }
        catch (Exception ex)
        {
            _activity.Error("Resume", "Macro crashed", ex.Message);
            return new Result(false, ex.Message);
        }
        finally
        {
            TryDelete(tempTxt);
            TryDelete(psScriptPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    /// <summary>
    /// The PowerShell body — a faithful port of ResumeAuto's macro runner. Word is invisible;
    /// the macro is expected to close Word when done (we poll until it does).
    /// </summary>
    private static string BuildPowerShell() => """
param ([string]$TempTextPath, [string]$DocmPath, [string]$MacroName, [string]$ProfileName, [string]$BridgeFileName)
$word = $null
try {
    if (-not (Test-Path $TempTextPath)) { throw "Temp text file not found." }

    $bridgeFile = Join-Path $env:TEMP $BridgeFileName
    [System.IO.File]::WriteAllText($bridgeFile, $TempTextPath, [System.Text.Encoding]::UTF8)
    Start-Sleep -Milliseconds 500

    $word = New-Object -ComObject Word.Application
    $word.Visible = $false
    $word.DisplayAlerts = 0
    Start-Sleep -Seconds 2

    $doc = $word.Documents.Open($DocmPath)
    Start-Sleep -Seconds 2
    $doc.Repaginate()
    Start-Sleep -Seconds 1

    Write-Host "Running macro for [$ProfileName]: $MacroName"
    $word.Run($MacroName)

    $startTime = Get-Date
    while ($true) {
        Start-Sleep -Milliseconds 500
        try {
            $null = $word.Name
            $elapsed = ((Get-Date) - $startTime).TotalSeconds
            if ($elapsed -gt 90) {
                $word.Quit([ref]0)
                [System.Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null
                Start-Process -FilePath "taskkill" -ArgumentList "/F /IM WINWORD.EXE" -Wait -NoNewWindow
                exit 1
            }
        } catch { break }
    }
    Write-Output "SUCCESS"
    exit 0
} catch {
    Write-Host "POWERSHELL ERROR: $($_.Exception.Message)"
    if ($word) {
        try {
            $word.Quit([ref]0)
            [System.Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null
        } catch {}
    }
    Start-Process -FilePath "taskkill" -ArgumentList "/F /IM WINWORD.EXE" -Wait -NoNewWindow -ErrorAction SilentlyContinue
    exit 1
} finally {
    if ($bridgeFile -and (Test-Path $bridgeFile)) { Remove-Item $bridgeFile -Force -ErrorAction SilentlyContinue }
    [System.GC]::Collect(); [System.GC]::WaitForPendingFinalizers()
}
""";
}
