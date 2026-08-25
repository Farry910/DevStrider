using System.Text.RegularExpressions;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Scrubbing for text on its way to the Activity log or the UI.
///
/// <para>
/// This is what is left of <c>SharedDbCredentials</c>. That class existed to assemble a PostgreSQL
/// connection string from a host, a user and a password held on this machine, and it went when the
/// app stopped having a database credential to assemble one from. Its redaction outlived it: a
/// bearer token in a header dump, or a URL somebody typed a password into, is still not something
/// to write into a log pane and screenshot.
/// </para>
/// </summary>
public static class Safe
{
    private static readonly Regex UriCredentials =
        new(@"(\w+://)[^:@/\s]+:[^@\s]*@", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BearerToken =
        new(@"(Bearer\s+)[A-Za-z0-9._~+/-]+=*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PasswordAssignment =
        new(@"(password\s*[=:]\s*)[^;,\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The message with anything credential-shaped replaced. Never throws and never returns null —
    /// this runs on error paths, where a redactor that could itself fail would replace a bad
    /// message with no message.
    /// </summary>
    public static string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message)) return message ?? "";
        var text = UriCredentials.Replace(message, "$1***:***@");
        text = BearerToken.Replace(text, "$1***");
        return PasswordAssignment.Replace(text, "$1***");
    }
}
