using System.Diagnostics;
using ChestCounter.Core;
using Microsoft.Win32;

namespace ChestCounter.App;

/// <summary>
/// Optional "start with Windows": a per-user entry under HKCU\...\Run (no admin, no system change).
/// Only written when the user turns the option on in Settings; removed when turned off.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ChestCounter";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (enabled && exe is not null && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var value = $"\"{exe}\" --autostart";
                if (!string.Equals(key.GetValue(ValueName) as string, value, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(ValueName, value);
                }
            }
            else if (!enabled && key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not update the start-with-Windows setting: " + ex.Message);
        }
    }
}
