using Microsoft.Win32;

namespace SenkaSpeedMeasuringDevice.App;

internal static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SenkaKeisoku";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string value &&
               string.Equals(Unquote(value), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("実行ファイルのパスを取得できません。");
        key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
    }

    private static string Unquote(string value) => value.Trim().Trim('"');
}
