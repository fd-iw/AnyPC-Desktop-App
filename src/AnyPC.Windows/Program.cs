using AnyPC.Core;
using AnyPC.Windows.Platform;
using AnyPC.Windows.UI;

namespace AnyPC.Windows;

internal static class Program
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnyPC");

    [STAThread]
    static int Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\AnyPC-Desktop", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("AnyPC is already running. Look for its icon in the notification area (system tray).", "AnyPC", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var log = new AppLog(Path.Combine(DataDir, "anypc.log"));
        using var platform = new WindowsPlatform();
        AnyPCHost host;
        try
        {
            host = new AnyPCHost(platform, DataDir, log.Write);
            host.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.Write("Startup failed: " + ex);
            var hint = ex is IOException || ex.InnerException is IOException || ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                ? $"\n\nPort {Core.Protocol.Wire.DefaultPort} may be used by another program."
                : "";
            MessageBox.Show("AnyPC could not start:\n\n" + ex.Message + hint, "AnyPC", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        var startMinimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        using (var tray = new TrayApp(host, log, startMinimized))
            Application.Run(tray);

        host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }
}

/// <summary>Small rolling log file plus an in-memory tail for the UI.</summary>
internal sealed class AppLog(string path)
{
    readonly object _lock = new();
    readonly LinkedList<string> _tail = new();
    public event Action<string>? Line;

    public void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        lock (_lock)
        {
            _tail.AddLast(line);
            while (_tail.Count > 200) _tail.RemoveFirst();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Line?.Invoke(line);
    }

    public string[] Tail()
    {
        lock (_lock) return [.. _tail];
    }
}
