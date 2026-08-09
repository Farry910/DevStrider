using System.Diagnostics;
using System.IO;
using System.Text;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Runs a Word VBA macro by name against a profile's template, headless and in the background.
///
/// <para><b>Contract with the macro:</b> the resume text is handed over as a single string
/// argument —</para>
/// <code>Sub UpdateResumeAndSwitchOriginal(ByVal ClipText As String)</code>
/// <para>
/// No clipboard and no bridge file. The macro used to read the Windows clipboard, which meant
/// every bid quietly overwrote whatever the user had copied — unacceptable when the whole point
/// is that they keep working in a job application while this runs. Passing a COM argument also
/// fixes a silent corruption: the clipboard read used <c>CF_TEXT</c> (ANSI), so em-dashes and
/// smart quotes — which ChatGPT emits constantly — arrived as <c>?</c>. A COM <c>BSTR</c> is
/// Unicode end to end.
/// </para>
///
/// <para>
/// The temp file below is <i>not</i> a bridge file: it exists only so the resume text reaches
/// PowerShell without going through a command line (which caps out around 32K and would mangle
/// newlines). The macro never sees it.
/// </para>
///
/// <para>
/// The macro is expected to save its output and call <c>Application.Quit</c>; the script polls
/// until Word disappears. A process-wide lock serializes runs, since Word reuses one instance of
/// an already-open document.
/// </para>
/// </summary>
public sealed class WordMacroService
{
    private static readonly SemaphoreSlim MacroLock = new(1, 1);
    private const int MacroTimeoutSeconds = 90;

    /// <summary>
    /// Macro invoked when a profile doesn't name one. Every template ships with this entry point.
    /// </summary>
    public const string DefaultMacroName = "UpdateResumeAndSwitchOriginal";

    private readonly ActivityLogService _activity;

    public WordMacroService(ActivityLogService activity)
    {
        _activity = activity;
    }

    public record Result(bool Success, string Message);

    /// <summary>
    /// Invoke <paramref name="macroName"/> in <paramref name="documentPath"/>, passing the resume
    /// text as its argument. Never throws — failures come back in the Result.
    /// </summary>
    public async Task<Result> RunAsync(string resumeText, string documentPath, string macroName, string profileName)
    {
        if (string.IsNullOrWhiteSpace(documentPath) || !File.Exists(documentPath))
            return new Result(false, $"Word template not found: {documentPath}");
        if (string.IsNullOrWhiteSpace(resumeText))
            return new Result(false, "No resume text to place into the template.");

        var macro = string.IsNullOrWhiteSpace(macroName) ? DefaultMacroName : macroName.Trim();

        await MacroLock.WaitAsync();
        try
        {
            return await Task.Run(() => RunInternal(resumeText, documentPath, macro, profileName));
        }
        finally
        {
            MacroLock.Release();
        }
    }

    private Result RunInternal(string resumeText, string documentPath, string macroName, string profileName)
    {
        var tempTxt = Path.Combine(Path.GetTempPath(), $"devstrider_resume_{Guid.NewGuid():N}.txt");
        var psScriptPath = Path.Combine(Path.GetTempPath(), $"devstrider_macro_{Guid.NewGuid():N}.ps1");
        try
        {
            // UTF-8 *with* BOM so PowerShell's Get-Content reads it back as Unicode without
            // guessing at the code page — the whole point of dropping CF_TEXT was to stop
            // mangling non-ASCII.
            File.WriteAllText(tempTxt, resumeText, new UTF8Encoding(true));
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
                "-ResumeTextPath", tempTxt,
                "-DocumentPath", Path.GetFullPath(documentPath),
                "-MacroName", macroName,
                "-ProfileName", string.IsNullOrWhiteSpace(profileName) ? "profile" : profileName,
            })
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = Process.Start(psi);
            if (proc == null) return new Result(false, "Couldn't start PowerShell.");

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            // Hard wall past the script's own timeout so a wedged process can't hang us.
            if (!proc.WaitForExit((MacroTimeoutSeconds + 30) * 1000))
            {
                try { proc.Kill(true); } catch { /* ignore */ }
                return new Result(false, "Macro process timed out and was killed.");
            }

            if (proc.ExitCode == 0 && stdout.Contains("SUCCESS"))
                return new Result(true, "Resume document produced.");

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
    /// The PowerShell body. Word runs invisible; the macro receives the resume text as its single
    /// argument and is expected to close Word when it's finished.
    ///
    /// <para>
    /// Cleanup only ever targets the Word process this script started. The previous version ran
    /// <c>taskkill /F /IM WINWORD.EXE</c>, which killed <i>every</i> Word on the machine — so a
    /// stalled macro would destroy unsaved work in an unrelated document the user happened to
    /// have open.
    /// </para>
    /// </summary>
    private static string BuildPowerShell() => """
param ([string]$ResumeTextPath, [string]$DocumentPath, [string]$MacroName, [string]$ProfileName)
$word = $null
$ourPid = 0

function Stop-OurWord {
    # Only the instance this script created. Never touches the user's own Word.
    if ($ourPid -gt 0) {
        try { Stop-Process -Id $ourPid -Force -ErrorAction SilentlyContinue } catch {}
    }
}

try {
    if (-not (Test-Path $ResumeTextPath)) { throw "Resume text file not found." }
    $resumeText = [System.IO.File]::ReadAllText($ResumeTextPath, [System.Text.Encoding]::UTF8)
    if ([string]::IsNullOrWhiteSpace($resumeText)) { throw "Resume text was empty." }

    # Snapshot existing Word PIDs so the one we create can be told apart afterwards.
    $before = @(Get-Process WINWORD -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)

    $word = New-Object -ComObject Word.Application
    $word.Visible = $false
    $word.DisplayAlerts = 0
    Start-Sleep -Milliseconds 800

    $after = @(Get-Process WINWORD -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    $new = @($after | Where-Object { $before -notcontains $_ })
    if ($new.Count -gt 0) { $ourPid = $new[0] }

    $doc = $word.Documents.Open($DocumentPath)
    Start-Sleep -Seconds 1
    $doc.Repaginate()

    Write-Host "Running macro for [$ProfileName]: $MacroName"
    # The resume text is the macro's single argument — no clipboard, no bridge file.
    $word.Run($MacroName, $resumeText)

    # The macro ends with Application.Quit, so Word going away is the success signal.
    $startTime = Get-Date
    while ($true) {
        Start-Sleep -Milliseconds 500
        try {
            $null = $word.Name
            if (((Get-Date) - $startTime).TotalSeconds -gt 90) {
                Stop-OurWord
                throw "Macro did not finish within 90 seconds."
            }
        } catch [System.Runtime.InteropServices.COMException] {
            break        # RPC failed = Word exited = the macro completed
        } catch {
            if ($_.Exception.Message -like "*did not finish*") { throw }
            break
        }
    }
    Write-Output "SUCCESS"
    exit 0
} catch {
    Write-Host "POWERSHELL ERROR: $($_.Exception.Message)"
    if ($word) {
        try { $word.Quit([ref]0) } catch {}
        try { [System.Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null } catch {}
    }
    Stop-OurWord
    exit 1
} finally {
    [System.GC]::Collect(); [System.GC]::WaitForPendingFinalizers()
}
""";
}
