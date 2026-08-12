using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

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
/// <para><b>Why there is no PowerShell here any more.</b> This used to write the resume to a
/// temp file, write a .ps1 next to it, and spawn <c>powershell.exe</c> to drive Word over COM.
/// The COM calls are identical from C#, so the whole hop bought nothing but latency: a process
/// spawn, a script parse, and — because the script could not keep anything alive between runs —
/// a cold <c>WINWORD.EXE</c> launch on every single bid. The template is one page of 87 words,
/// so launching and tearing down Word cost several times more than the work it did.</para>
///
/// <para><b>The instance is kept warm.</b> One hidden Word, launched once and reused, owned by
/// the dedicated STA thread below. <see cref="PrewarmAsync"/> starts it while ChatGPT is still
/// writing the reply, so by the time the text arrives the only work left is the macro itself.
/// Word is released after <see cref="IdleShutdown"/> without a bid so an idle DevStrider isn't
/// sitting on a hidden Office process.</para>
///
/// <para><b>The user's own Word is never taken over.</b> Word's automation server is
/// single-instance: if the user has Word open, <c>CoCreateInstance</c> hands back *their*
/// instance rather than a private one. Hiding it would strand their window and holding it warm
/// would strand it for the rest of the session, so an attached instance is used for exactly one
/// run, restored to visible, and dropped — and <see cref="PrewarmAsync"/> declines to run at
/// all while a foreign Word is alive.</para>
/// </summary>
public sealed class WordMacroService : IDisposable
{
    /// <summary>
    /// Macro invoked when a profile doesn't name one. Every template ships with this entry point.
    /// </summary>
    public const string DefaultMacroName = "UpdateResumeAndSwitchOriginal";

    /// <summary>Hard wall on a single macro run before Word is presumed wedged.</summary>
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Prewarm is best-effort; it must never outlive the generation it overlaps.</summary>
    private static readonly TimeSpan PrewarmTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Idle time after which the warm instance is closed and the memory handed back.</summary>
    private static readonly TimeSpan IdleShutdown = TimeSpan.FromMinutes(10);

    /// <summary>Where the VBA error handler appends its reason. Word is invisible, so this file
    /// is the only witness a failing macro leaves behind.</summary>
    private static readonly string MacroLogPath =
        Path.Combine(Path.GetTempPath(), "devstrider_macro_error.log");

    private readonly ActivityLogService _activity;

    /// <summary>
    /// Every COM call in this class runs here. A single thread is both the apartment COM wants
    /// and the serialization the macro needs — Word reuses one instance of an already-open
    /// document, so two overlapping runs would stomp each other.
    /// </summary>
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _sta;
    private readonly Timer _idleTimer;

    // ---- STA-thread-only state. Never touch these from anywhere else. -----------------------
    private object? _word;
    private object? _doc;
    private string _openDocPath = "";
    /// <summary>PID of the Word we launched, or 0 when attached to one the user already had.</summary>
    private int _ourWordPid;

    private long _lastUseTicks = DateTime.UtcNow.Ticks;
    private volatile bool _disposed;

    /// <summary>
    /// Set when a run times out against a Word we do not own and therefore may not kill. The STA
    /// thread is stuck inside that COM call forever, so every later request would queue behind it
    /// and time out too — failing fast with the real reason beats 90 seconds of silence each time.
    /// </summary>
    private volatile bool _wedged;

    public WordMacroService(ActivityLogService activity)
    {
        _activity = activity;

        _sta = new Thread(PumpQueue)
        {
            IsBackground = true,
            Name = "DevStrider Word COM",
        };
        _sta.SetApartmentState(ApartmentState.STA);
        _sta.Start();

        _idleTimer = new Timer(_ => CloseIfIdle(), null, IdleShutdown, IdleShutdown);
    }

    public record Result(bool Success, string Message);

    // =========================================================================================
    // Public API
    // =========================================================================================

    /// <summary>
    /// Get Word up and the template open *before* the resume text exists, so the run itself is
    /// only the macro. Called the moment the prompt is submitted to ChatGPT — the 30-60 seconds
    /// it spends writing is otherwise dead time on this side.
    ///
    /// <para>Best-effort by design: every failure is swallowed, because a prewarm that didn't
    /// work costs the bid nothing — <see cref="RunAsync"/> opens whatever is still missing.</para>
    /// </summary>
    public async Task PrewarmAsync(string documentPath)
    {
        if (_disposed || _wedged) return;
        if (string.IsNullOrWhiteSpace(documentPath) || !File.Exists(documentPath)) return;

        try
        {
            var full = Path.GetFullPath(documentPath);
            await RunOnStaAsync(() =>
            {
                // Attaching would mean hiding the user's own Word for the whole generation.
                // Their Word being open is exactly when prewarming is not worth it.
                if (_word == null && ForeignWordIsRunning()) return false;
                EnsureDocumentOpen(full);
                return true;
            }, PrewarmTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WordMacro] prewarm skipped: {ex.Message}");
        }
    }

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
        if (_disposed)
            return new Result(false, "DevStrider is shutting down.");
        if (_wedged)
            return new Result(false, "Word is not responding — restart DevStrider (and close Word) to recover.");

        var macro = string.IsNullOrWhiteSpace(macroName) ? DefaultMacroName : macroName.Trim();
        var full = Path.GetFullPath(documentPath);

        try
        {
            return await RunOnStaAsync(
                () => RunOnSta(resumeText, full, macro, profileName), RunTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The STA thread is still inside the COM call. Killing the Word we launched is what
            // unblocks it; if it isn't ours we may not kill it, and we're stuck for good.
            var owned = KillOurWordFromAnyThread();
            if (!owned)
            {
                _wedged = true;
                _activity.Error("Resume", "Word wedged",
                    "A macro run hung inside Word. Close Word and restart DevStrider.");
                return new Result(false, "Word stopped responding and it isn't ours to kill — close Word, then restart DevStrider.");
            }
            return new Result(false, $"Macro timed out after {RunTimeout.TotalSeconds:0}s and Word was closed.");
        }
        catch (Exception ex)
        {
            var real = Unwrap(ex);
            _activity.Error("Resume", "Macro crashed", real.Message);
            return new Result(false, real.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _idleTimer.Dispose(); } catch { /* ignore */ }

        // The warm instance is invisible: leaving it behind orphans a WINWORD.EXE the user can
        // only find in Task Manager. App.OnExit kills the process a moment later, so this gets a
        // short budget and no more.
        try
        {
            var closed = RunOnStaAsync(() => { CloseWord(); return true; }, TimeSpan.FromSeconds(2));
            // Observe it either way: App.OnStartup turns unobserved task exceptions into a modal
            // error box, and a shutdown is the last moment to show the user one of those.
            _ = closed.ContinueWith(t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            closed.Wait(TimeSpan.FromSeconds(2));
        }
        catch { /* falls through to the kill below */ }

        // Whatever the graceful path managed, our process must not outlive us.
        KillOurWordFromAnyThread();

        try { _queue.CompleteAdding(); } catch { /* ignore */ }
    }

    // =========================================================================================
    // The STA thread
    // =========================================================================================

    private void PumpQueue()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); }
            catch (Exception ex) { Debug.WriteLine($"[WordMacro] queue item threw: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Queue <paramref name="work"/> onto the COM thread and await it with a ceiling. On timeout
    /// the work item is abandoned, not cancelled — it is sitting in a blocking COM call and only
    /// the server going away will release it, which is the caller's job to arrange.
    /// </summary>
    private async Task<T> RunOnStaAsync<T>(Func<T> work, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _queue.Add(() =>
            {
                try
                {
                    Interlocked.Exchange(ref _lastUseTicks, DateTime.UtcNow.Ticks);
                    tcs.TrySetResult(work());
                }
                catch (Exception ex) { tcs.TrySetException(Unwrap(ex)); }
                finally { Interlocked.Exchange(ref _lastUseTicks, DateTime.UtcNow.Ticks); }
            });
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException("Word service is shut down.");
        }

        var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (finished != tcs.Task)
        {
            // App.OnStartup turns unobserved task exceptions into a modal error box, so the
            // abandoned task must have its eventual failure read by someone.
            _ = tcs.Task.ContinueWith(t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            throw new TimeoutException($"Word did not answer within {timeout.TotalSeconds:0}s.");
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    // =========================================================================================
    // The run itself — STA thread only
    // =========================================================================================

    private Result RunOnSta(string resumeText, string documentPath, string macroName, string profileName)
    {
        EnsureDocumentOpen(documentPath);

        // Baseline the macro's log so we only ever read entries from *this* run.
        var logBefore = SafeLogLength();

        Debug.WriteLine($"[WordMacro] running {macroName} for [{profileName}]");

        // The resume text is the macro's single argument — no clipboard, no bridge file.
        string? runError = null;
        try
        {
            // Word.Application.Run declares all thirty of its Arg parameters ByRef. PowerShell
            // refused to marshal a plain value into one and needed an explicit [ref]; the CLR's
            // IDispatch binder handles it, so the whole retry dance that used to live here is gone.
            Invoke(_word!, "Run", macroName, resumeText);
        }
        catch (Exception ex)
        {
            // Do NOT treat this as a failure yet. A macro that still ends in Application.Quit
            // tears down the RPC channel while this very call is on the stack, so a *successful*
            // run surfaces here as a COM error. The evidence below, not the HRESULT, decides.
            runError = Unwrap(ex).Message;
            if (IsDispatchFailure(Unwrap(ex)))
            {
                // Raised before the macro body was entered: a name Word can't find, a Sub whose
                // signature doesn't take the single string argument, or a disabled project.
                AfterRun();
                return new Result(false, $"Macro call failed: {runError}");
            }
        }

        // Run is a synchronous COM call, so reaching this line means the macro has finished and
        // its error handler — if it ran at all — has already written to the log.
        var macroError = ReadLogTail(logBefore);
        AfterRun();

        if (!string.IsNullOrEmpty(macroError))
            return new Result(false, $"Macro reported: {macroError}");

        return new Result(true, "Resume document produced.");
    }

    /// <summary>
    /// Settle the session after a macro ran. The document is gone either way — the macro
    /// <c>SaveAs2</c>s it to the output folder and then either closes it or quits Word — so the
    /// handle we hold is stale and the template gets reopened next time (cheap, on a live Word).
    /// </summary>
    private void AfterRun()
    {
        ReleaseDocument();

        if (!WordIsAlive())
        {
            // A macro still ending in Application.Quit. Supported, just slower: the next bid pays
            // for a fresh launch unless a prewarm gets there first.
            ReleaseWord();
            return;
        }

        // Attached to the user's Word — hand it back the way we found it rather than holding it
        // hidden for the rest of the session.
        if (_ourWordPid == 0)
        {
            TrySet(_word!, "Visible", true);
            TrySet(_word!, "DisplayAlerts", -1);
            ReleaseWord();
        }
    }

    // =========================================================================================
    // Word session — STA thread only
    // =========================================================================================

    private void EnsureDocumentOpen(string documentPath)
    {
        if (_word != null && !WordIsAlive()) ReleaseAll();
        if (_word == null) LaunchWord();

        if (_doc != null && string.Equals(_openDocPath, documentPath, StringComparison.OrdinalIgnoreCase))
            return;   // already prewarmed with this template

        if (_doc != null) ReleaseDocument();

        var documents = Get(_word!, "Documents")
            ?? throw new InvalidOperationException("Word exposed no Documents collection.");

        // Positional, because the CLR's IDispatch binder takes named arguments only as a trailing
        // block and this signature is mostly optional:
        //   Open(FileName, ConfirmConversions, ReadOnly, AddToRecentFiles, PasswordDocument,
        //        PasswordTemplate, Revert, WritePasswordDocument, WritePasswordTemplate, Format, …)
        //
        // The document deliberately opens *visible* — the application is what's hidden. Opening it
        // with Visible:=False leaves it without a window, and the macro addresses its work through
        // ActiveDocument, which a windowless document is not reliably the answer to.
        _doc = Invoke(documents, "Open",
            documentPath,
            false,          // ConfirmConversions
            false,          // ReadOnly
            false)          // AddToRecentFiles
            ?? throw new InvalidOperationException($"Word could not open {documentPath}.");

        _openDocPath = documentPath;
    }

    private void LaunchWord()
    {
        var type = Type.GetTypeFromProgID("Word.Application", throwOnError: false)
            ?? throw new InvalidOperationException(
                "Microsoft Word isn't installed, or its COM automation class isn't registered.");

        var before = WinwordPids();
        _word = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Word.Application could not be created.");

        // Which process did we get? Word's automation server is single-instance, so this is also
        // how we learn whether we launched one or attached to the user's. The old code slept a
        // flat 800ms here; polling costs what it actually takes, which is usually a fraction.
        _ourWordPid = 0;
        var budgetMs = before.Count == 0 ? 3000 : 400;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            var fresh = WinwordPids().FirstOrDefault(pid => !before.Contains(pid));
            if (fresh != 0) { _ourWordPid = fresh; break; }
            Thread.Sleep(25);
        }

        if (_ourWordPid > 0)
        {
            // Ours: invisible for its whole life.
            TrySet(_word, "Visible", false);
            TrySet(_word, "DisplayAlerts", 0);
        }
        else
        {
            // Theirs. Hiding the application is what makes the macro's work invisible, and it is
            // restored in AfterRun — but their documents stay open and the instance is never held
            // past this one run.
            TrySet(_word, "Visible", false);
            TrySet(_word, "DisplayAlerts", 0);
            _activity.Warning("Resume", "Using your open Word",
                "Word was already running, so this bid borrowed it. It'll be visible again in a moment.",
                silent: true);
        }
    }

    private bool WordIsAlive()
    {
        if (_word == null) return false;

        // Prefer the process handle: once Word has quit, probing the COM proxy is unreliable —
        // some members answer with null instead of throwing, which reports a dead instance as
        // alive and makes every successful run look like a timeout.
        if (_ourWordPid > 0) return ProcessAlive(_ourWordPid);

        try { return Get(_word, "Version") != null; }
        catch { return false; }
    }

    /// <summary>A Word that isn't the one we launched. Prewarm stays out of its way.</summary>
    private bool ForeignWordIsRunning() =>
        WinwordPids().Any(pid => pid != _ourWordPid);

    private void CloseWord()
    {
        if (_word == null) return;

        ReleaseDocument();

        if (_ourWordPid > 0)
        {
            // Ours and invisible — nobody is looking at it and it holds no unsaved work of the
            // user's, so Quit is safe. Never call Quit on a borrowed instance: with DisplayAlerts
            // off and SaveChanges 0 that is silent loss of their open documents.
            try { Invoke(_word, "Quit", 0 /* wdDoNotSaveChanges */); } catch { /* going away anyway */ }
        }
        else
        {
            TrySet(_word, "Visible", true);
            TrySet(_word, "DisplayAlerts", -1);
        }

        ReleaseWord();
    }

    private void CloseIfIdle()
    {
        if (_disposed || _wedged) return;
        var idleFor = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastUseTicks), DateTimeKind.Utc);
        if (idleFor < IdleShutdown) return;

        try
        {
            _queue.Add(() =>
            {
                // Re-check on the owning thread: a bid may have landed while this was queued.
                var stillIdle = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastUseTicks), DateTimeKind.Utc);
                if (stillIdle < IdleShutdown || _word == null) return;
                Debug.WriteLine("[WordMacro] closing idle Word instance");
                CloseWord();
            });
        }
        catch (InvalidOperationException) { /* queue closed — shutting down */ }
    }

    private void ReleaseAll()
    {
        ReleaseDocument();
        ReleaseWord();
    }

    private void ReleaseDocument()
    {
        if (_doc == null) return;
        try { if (Marshal.IsComObject(_doc)) Marshal.ReleaseComObject(_doc); } catch { /* ignore */ }
        _doc = null;
        _openDocPath = "";
    }

    private void ReleaseWord()
    {
        if (_word == null) return;
        try { if (Marshal.IsComObject(_word)) Marshal.ReleaseComObject(_word); } catch { /* ignore */ }
        _word = null;
        _ourWordPid = 0;
    }

    /// <summary>
    /// Kill the Word we launched, callable when the STA thread is stuck inside a COM call and so
    /// cannot do it itself. Returns false when the instance isn't ours to kill.
    /// </summary>
    private bool KillOurWordFromAnyThread()
    {
        var pid = Volatile.Read(ref _ourWordPid);
        if (pid <= 0) return false;
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }
        return true;
    }

    // =========================================================================================
    // Macro error log
    // =========================================================================================

    private static long SafeLogLength()
    {
        try { return File.Exists(MacroLogPath) ? new FileInfo(MacroLogPath).Length : 0; }
        catch { return 0; }
    }

    /// <summary>
    /// Whatever the VBA error handler appended since this run started. A failing macro logs and
    /// deliberately stays put, so this file is the only place its reason exists — without it every
    /// failure looks identical from out here.
    /// </summary>
    private static string ReadLogTail(long fromOffset)
    {
        try
        {
            if (!File.Exists(MacroLogPath)) return "";
            var all = File.ReadAllText(MacroLogPath);
            return all.Length <= fromOffset ? "" : all[(int)fromOffset..].Trim();
        }
        catch { return ""; }
    }

    // =========================================================================================
    // Late-bound COM plumbing
    // =========================================================================================

    private static object? Get(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);

    private static void TrySet(object target, string name, object value)
    {
        try { target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value }); }
        catch { /* best effort — a dead or busy instance isn't worth failing the run over */ }
    }

    /// <summary>
    /// <see cref="Type.InvokeMember(string, BindingFlags, Binder, object, object[])"/> wraps
    /// everything the callee threw in a <see cref="TargetInvocationException"/>, and the HRESULT
    /// we need to classify is on the inner one.
    /// </summary>
    private static Exception Unwrap(Exception ex) =>
        ex is TargetInvocationException { InnerException: { } inner } ? Unwrap(inner)
        : ex is AggregateException agg && agg.InnerExceptions.Count == 1 ? Unwrap(agg.InnerExceptions[0])
        : ex;

    /// <summary>
    /// Errors raised BEFORE the macro body is entered. Those are real failures, and they identify
    /// themselves. Everything else that threw has already run the macro to completion —
    /// overwhelmingly the channel collapsing because the macro called Application.Quit while this
    /// very call was still on the stack.
    /// </summary>
    private static bool IsDispatchFailure(Exception ex)
    {
        if (ex is MissingMethodException or MissingMemberException) return true;

        if (ex is COMException com)
        {
            switch ((uint)com.HResult)
            {
                case 0x80020003:   // DISP_E_MEMBERNOTFOUND — no Sub by that name
                case 0x80020005:   // DISP_E_TYPEMISMATCH   — wrong argument type
                case 0x80020006:   // DISP_E_UNKNOWNNAME
                case 0x8002000E:   // DISP_E_BADPARAMCOUNT  — Sub takes no string argument
                    return true;
            }
        }

        return Regex.IsMatch(ex.Message,
            "Number of parameters|does not match the expected|member not found|DISP_E_MEMBERNOTFOUND|cannot be found|has been disabled",
            RegexOptions.IgnoreCase);
    }

    private static HashSet<int> WinwordPids()
    {
        try { return Process.GetProcessesByName("WINWORD").Select(p => p.Id).ToHashSet(); }
        catch { return new HashSet<int>(); }
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch { return false; }
    }
}
