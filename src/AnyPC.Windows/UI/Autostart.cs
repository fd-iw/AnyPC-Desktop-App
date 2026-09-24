using Microsoft.Win32;

namespace AnyPC.Windows.UI;

/// <summary>Per-user "start when I sign in" via the HKCU Run key.</summary>
internal static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "AnyPC";

    static string Command => $"\"{Environment.ProcessPath}\" --minimized";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, false);
    }

    /// <summary>Keeps the registered path current if the exe moved (e.g. reinstalled elsewhere).</summary>
    public static void Refresh()
    {
        if (IsEnabled) Set(true);
    }
}
