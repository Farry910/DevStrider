using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace BidAssistantApp;

// Partial class to hold the refresh-word endpoint logic
partial class HttpServer
{
    private static IResult HandleRefreshWord(HttpContext context, string body)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        
        JsonElement data;
        try
        {
            data = JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch (JsonException ex)
        {
            Logger.Error("Invalid JSON in refresh-word request", ex);
            return Results.Json(new { success = false, error = "Invalid JSON in request body" }, statusCode: 400);
        }
        
        var wordDocPath = data.TryGetProperty("word_doc_path", out var p1) ? p1.GetString()?.Trim() ?? "" : "";
        var wordHotkey = data.TryGetProperty("word_hotkey", out var p2) ? p2.GetString()?.Trim() ?? "F9" : "F9";

        var (valid, pathError) = PathValidator.ValidateWordPath(wordDocPath);
        if (!valid)
        {
            Logger.Warning($"Invalid Word path: {pathError}");
            var status = pathError?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true ? 404 : 400;
            return Results.Json(new { success = false, error = pathError }, statusCode: status);
        }

        var parsed = KeyboardHelper.ParseHotkey(wordHotkey);
        if (parsed == null)
        {
            Logger.Warning($"Invalid hotkey: {wordHotkey}");
            return Results.Json(new { success = false, error = $"Invalid hotkey: {wordHotkey}" }, statusCode: 400);
        }

        try
        {
            var (mods, keyVk) = parsed.Value;
            var chromeHwnd = KeyboardHelper.GetForegroundWindow();
            var wordHwnd = KeyboardHelper.OpenWordDocument(wordDocPath!);

            if (wordHwnd == IntPtr.Zero)
            {
                Logger.Error("Failed to open Word document");
                return Results.Json(new { success = false, error = "Failed to open Word. Ensure Microsoft Word is installed and the path is correct." }, statusCode: 500);
            }

            Thread.Sleep(Constants.WORD_OPEN_DELAY_MS);

            // Try sending hotkey directly to window first (faster)
            if (mods.Count == 0 && KeyboardHelper.PostSingleKeyToWindow(wordHwnd, mods, keyVk))
            {
                if (KeyboardHelper.WaitForWordClose(5))
                {
                    KeyboardHelper.ReturnToChrome(chromeHwnd);
                    RecordMetric("refresh-word", sw.ElapsedMilliseconds);
                    Logger.Info($"Word refreshed successfully in {sw.ElapsedMilliseconds}ms");
                    return Results.Json(new { success = true, message = "Word document refreshed" });
                }
            }

            // Fallback: bring Word to foreground and send hotkey
            wordHwnd = KeyboardHelper.FindWordWindow();
            if (wordHwnd == IntPtr.Zero)
            {
                KeyboardHelper.ReturnToChrome(chromeHwnd);
                RecordMetric("refresh-word", sw.ElapsedMilliseconds);
                Logger.Info($"Word closed (no window found) in {sw.ElapsedMilliseconds}ms");
                return Results.Json(new { success = true, message = "Word document refreshed" });
            }

            if (KeyboardHelper.SetForegroundWindow(wordHwnd) || KeyboardHelper.AltTabToWindow(wordHwnd))
            {
                Thread.Sleep(Constants.HOTKEY_DELAY_MS);
                KeyboardHelper.PressHotkey(mods, keyVk);
                if (KeyboardHelper.WaitForWordClose(Constants.WORD_CLOSE_TIMEOUT_SECONDS))
                {
                    KeyboardHelper.ReturnToChrome(chromeHwnd);
                    RecordMetric("refresh-word", sw.ElapsedMilliseconds);
                    Logger.Info($"Word refreshed successfully in {sw.ElapsedMilliseconds}ms");
                    return Results.Json(new { success = true, message = "Word document refreshed" });
                }
            }

            KeyboardHelper.ReturnToChrome(chromeHwnd);
            RecordMetric("refresh-word", sw.ElapsedMilliseconds);
            Logger.Warning($"Hotkey sent but Word didn't close in {sw.ElapsedMilliseconds}ms");
            return Results.Json(new { success = false, error = "Hotkey sent, but Word didn't close. The macro may not have executed." });
        }
        catch (Exception ex)
        {
            Logger.Error("refresh_word failed", ex);
            return Results.Json(new { success = false, error = "Word refresh failed. Check that Word is installed and the document path is valid." }, statusCode: 500);
        }
    }
}
