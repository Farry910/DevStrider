using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
// Disambiguate from System.Drawing.Color, which lands in implicit usings via
// UseWindowsForms=true (the tray icon's NotifyIcon lives there).
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace DevStrider.Desktop.Services;

public enum ThemePreference { System, Light, Dark }

/// <summary>
/// Runtime theme switcher. Mutates the <c>SolidColorBrush.Color</c> of the named brushes
/// defined in <c>Themes/Theme.xaml</c> in place — because all the rest of the app uses
/// <c>StaticResource</c> against those same brush instances, every visual that depends on
/// the palette repaints automatically.
///
/// <para>
/// "System" reads <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize
/// \AppsUseLightTheme</c> + subscribes to <see cref="SystemEvents.UserPreferenceChanged"/>
/// so when the user flips Windows' light/dark switch the app follows.
/// </para>
/// </summary>
public sealed class ThemeService
{
    private readonly SettingsService _settings;
    private ThemePreference _userPreference = ThemePreference.System;

    private static readonly Dictionary<string, Color> LightPalette = new()
    {
        ["Bg"]          = Color.FromRgb(0xF8, 0xFA, 0xFC),
        ["Surface"]     = Color.FromRgb(0xFF, 0xFF, 0xFF),
        ["SurfaceAlt"]  = Color.FromRgb(0xF1, 0xF5, 0xF9),
        ["Border"]      = Color.FromRgb(0xE5, 0xE7, 0xEB),
        ["BorderSoft"]  = Color.FromRgb(0xEE, 0xF2, 0xF6),
        ["Text"]        = Color.FromRgb(0x0F, 0x17, 0x2A),
        ["Muted"]       = Color.FromRgb(0x64, 0x74, 0x8B),
        ["Primary"]     = Color.FromRgb(0x25, 0x63, 0xEB),
        ["PrimaryHv"]   = Color.FromRgb(0x1D, 0x4E, 0xD8),
        ["PrimaryPr"]   = Color.FromRgb(0x1E, 0x40, 0xAF),
        ["PrimarySoft"] = Color.FromRgb(0xDB, 0xEA, 0xFE),
        ["Warning"]     = Color.FromRgb(0xF5, 0x9E, 0x0B),
        ["Danger"]      = Color.FromRgb(0xDC, 0x26, 0x26),
        ["OK"]          = Color.FromRgb(0x16, 0xA3, 0x4A),
        ["ChromeHv"]    = Color.FromRgb(0xE5, 0xE7, 0xEB),
        ["ChromeClose"] = Color.FromRgb(0xE8, 0x11, 0x23),
    };

    private static readonly Dictionary<string, Color> DarkPalette = new()
    {
        ["Bg"]          = Color.FromRgb(0x0F, 0x17, 0x2A),
        ["Surface"]     = Color.FromRgb(0x1E, 0x29, 0x3B),
        ["SurfaceAlt"]  = Color.FromRgb(0x33, 0x41, 0x55),
        ["Border"]      = Color.FromRgb(0x47, 0x55, 0x69),
        ["BorderSoft"]  = Color.FromRgb(0x33, 0x41, 0x55),
        ["Text"]        = Color.FromRgb(0xF1, 0xF5, 0xF9),
        ["Muted"]       = Color.FromRgb(0x94, 0xA3, 0xB8),
        ["Primary"]     = Color.FromRgb(0x3B, 0x82, 0xF6),
        ["PrimaryHv"]   = Color.FromRgb(0x60, 0xA5, 0xFA),
        ["PrimaryPr"]   = Color.FromRgb(0x25, 0x63, 0xEB),
        ["PrimarySoft"] = Color.FromRgb(0x1E, 0x3A, 0x8A),
        ["Warning"]     = Color.FromRgb(0xFB, 0xBF, 0x24),
        ["Danger"]      = Color.FromRgb(0xF8, 0x71, 0x71),
        ["OK"]          = Color.FromRgb(0x4A, 0xDE, 0x80),
        ["ChromeHv"]    = Color.FromRgb(0x33, 0x41, 0x55),
        ["ChromeClose"] = Color.FromRgb(0xE8, 0x11, 0x23),
    };

    public ThemeService(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Apply System default synchronously — reads the registry only, no Mongo. Call this from
    /// <c>App.OnStartup</c> before <c>window.Show()</c> so the very first paint is correct;
    /// <see cref="InitAsync"/> later refines if the user has a saved override.
    /// </summary>
    public void ApplySystemDefault()
    {
        _userPreference = ThemePreference.System;
        Apply();
    }

    /// <summary>
    /// Load the user's saved <see cref="AppSettings.ThemePreference"/> from Mongo and apply
    /// it. Also subscribes to <see cref="SystemEvents.UserPreferenceChanged"/> so a system
    /// theme flip refreshes the app live (when the preference is "System").
    /// </summary>
    public async Task InitAsync()
    {
        var s = await _settings.GetAsync();
        _userPreference = ParsePreference(s.ThemePreference);
        Apply();

        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General &&
                _userPreference == ThemePreference.System)
            {
                Application.Current?.Dispatcher.BeginInvoke(new Action(Apply));
            }
        };
    }

    public async Task SetPreferenceAsync(ThemePreference pref)
    {
        _userPreference = pref;
        var s = await _settings.GetAsync();
        s.ThemePreference = pref.ToString();
        await _settings.SaveAsync(s);
        Apply();
    }

    private void Apply()
    {
        if (Application.Current is null) return;
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            // Resource mutation must happen on the UI thread. The async chain in
            // SetPreferenceAsync resumes on whichever context Mongo's I/O happened on;
            // bounce here defensively.
            dispatcher.BeginInvoke(new Action(Apply));
            return;
        }

        var effective = _userPreference == ThemePreference.System ? GetSystemTheme() : _userPreference;
        var palette = effective == ThemePreference.Dark ? DarkPalette : LightPalette;

        // Theme.xaml binds each SolidColorBrush.Color via DynamicResource to a sibling
        // Color resource keyed "<X>Color". We rewrite those Color values; the brushes
        // pick the change up via DynamicResource, raise their own Changed event, and
        // every visual using them invalidates and redraws.
        foreach (var (key, color) in palette)
        {
            Application.Current.Resources[key + "Color"] = color;
        }
        System.Diagnostics.Debug.WriteLine(
            $"[ThemeService] applied effective={effective} (user pref={_userPreference})");
    }

    private static ThemePreference GetSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = key?.GetValue("AppsUseLightTheme");
            return v is int i && i == 0 ? ThemePreference.Dark : ThemePreference.Light;
        }
        catch
        {
            return ThemePreference.Light;
        }
    }

    private static ThemePreference ParsePreference(string raw) =>
        Enum.TryParse<ThemePreference>(raw, ignoreCase: true, out var p)
            ? p
            : ThemePreference.System;
}
