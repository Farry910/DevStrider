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
$doc = $null
$ourPid = 0
$macroLog = Join-Path $env:TEMP 'devstrider_macro_error.log'
$logBefore = 0

function Stop-OurWord {
    # Only the instance this script created. Never touches the user's own Word.
    if ($ourPid -gt 0) {
        try { Stop-Process -Id $ourPid -Force -ErrorAction SilentlyContinue } catch {}
    }
}

function Close-OurWork {
    # Word is a single-instance COM server: if it was already running we are holding the user's
    # own instance, not a private one. Killing it would take their open documents with it, and
    # leaving it hidden strands the window. So close only the document we opened, and hand the
    # instance back the way we found it.
    if ($doc) { try { $doc.Close([ref]0) } catch {} }
    if ($ourPid -gt 0) {
        Stop-OurWord
    } elseif ($word) {
        try { $word.Visible = $true } catch {}
        try { $word.DisplayAlerts = -1 } catch {}
    }
}

function Get-MacroErrorTail {
    # Whatever the VBA error handler appended since we started. A failing macro logs and
    # deliberately stays open, so this file is the only place its reason exists -- without it
    # every failure looks identical from out here.
    if (-not (Test-Path $macroLog)) { return '' }
    try {
        $all = [System.IO.File]::ReadAllText($macroLog)
        if ($all.Length -le $logBefore) { return '' }
        return $all.Substring($logBefore).Trim()
    } catch { return '' }
}

function Test-DispatchFailure([string]$message) {
    # Errors raised BEFORE the macro body is entered: a name Word can't find, a Sub whose
    # signature doesn't take the single string argument, or a disabled project. Those are real
    # failures, and they identify themselves by message.
    #
    # Everything else that threw has already run the macro to completion — overwhelmingly
    # 0x800A9C68, which is just Application.Quit collapsing the RPC channel while this very call
    # is still on the stack.
    return $message -match 'Number of parameters|does not match the expected|member not found|DISP_E_MEMBERNOTFOUND|0x80020003|cannot be found|has been disabled'
}

function Test-WordAlive {
    # Prefer the process handle. Probing the COM proxy is unreliable: once Word has quit,
    # PowerShell's adapter returns $null for $word.Name instead of throwing, so a try/catch
    # probe reports a dead instance as alive and every successful run looks like a timeout.
    if ($ourPid -gt 0) {
        return [bool](Get-Process -Id $ourPid -ErrorAction SilentlyContinue)
    }
    # Attached to a pre-existing instance, so there is no PID of ours to watch.
    try { return -not [string]::IsNullOrEmpty($word.Name) } catch { return $false }
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

    # Baseline the macro's log so we only ever read entries from *this* run.
    if (Test-Path $macroLog) {
        try { $logBefore = [System.IO.File]::ReadAllText($macroLog).Length } catch { $logBefore = 0 }
    }

    Write-Host "Running macro for [$ProfileName]: $MacroName"
    # The resume text is the macro's single argument — no clipboard, no bridge file.
    #
    # [ref] is required, not stylistic: Word.Application.Run declares all thirty of its Arg
    # parameters ByRef, and PowerShell refuses to marshal a plain value into one —
    # "Argument: '2' should be a System.Management.Automation.PSReference. Use [ref]."
    $runFailed = ''
    try {
        $word.Run($MacroName, [ref]$resumeText)
    } catch {
        $msg = $_.Exception.Message
        if ($msg -like '*PSReference*' -or $msg -like '*ByRef*') {
            # A binder failure happens before the macro is entered, so retrying is safe. Nothing
            # else is retried -- that would run the macro twice after it had already done its work.
            $null = $word.GetType().InvokeMember(
                'Run',
                [System.Reflection.BindingFlags]::InvokeMethod,
                $null,
                $word,
                @($MacroName, $resumeText))
        } else {
            # Do NOT treat this as a failure yet. The macro ends with Application.Quit, which
            # tears down the RPC channel while this very call is still on the stack -- so a
            # *successful* run surfaces here as a COM error (0x800A9C68). The evidence below,
            # not the HRESULT, decides the outcome.
            $runFailed = $msg
        }
    }

    # Run is a synchronous COM call, so reaching this line means the macro has already finished
    # and its error handler -- if it ran at all -- has already written to the log. There is
    # nothing to wait for, which is why this no longer polls for Word to disappear.
    $macroError = Get-MacroErrorTail
    if ($macroError) {
        Close-OurWork
        throw "Macro reported: $macroError"
    }

    # Run threw but the macro logged nothing, so the only question left is whether it ever
    # reached the macro. The message answers that; whether Word has finished exiting does not.
    #
    # This used to poll up to 12 seconds for the process to disappear and call the run failed if
    # it hadn't. That was a race, not a test: a longer resume means a slower PDF export and a
    # slower shutdown, and on a machine where Word was already running there is no PID of ours to
    # watch at all. It reported failure on runs that had produced the .docx, the .pdf and the
    # recorded bid — and cost up to twelve seconds doing it.
    if ($runFailed -and (Test-DispatchFailure $runFailed)) {
        Close-OurWork
        throw "Macro call failed: $runFailed"
    }

    # No-op when the macro already quit; tidies up when it did not.
    Close-OurWork
    Write-Output "SUCCESS"
    exit 0
} catch {
    Write-Host "POWERSHELL ERROR: $($_.Exception.Message)"
    # Never $word.Quit() here: on an attached instance that closes the user's Word and every
    # document in it, with DisplayAlerts off and SaveChanges 0 -- silent loss of their work.
    Close-OurWork
    if ($word) {
        try { [System.Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null } catch {}
    }
    exit 1
} finally {
    [System.GC]::Collect(); [System.GC]::WaitForPendingFinalizers()
}
""";
}
