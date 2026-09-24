namespace AnyPC.Core.Abstractions;

/// <summary>A physical display, in physical (non DPI-scaled) virtual-desktop pixels.</summary>
public sealed record MonitorInfo(int Index, string Name, int X, int Y, int Width, int Height, bool Primary);

/// <summary>An encoded screen frame. Cursor coordinates are in frame pixels, or -1 when off-screen.</summary>
public sealed record EncodedFrame(byte[] Jpeg, int Width, int Height, int CursorX, int CursorY);

public interface IScreenSource
{
    IReadOnlyList<MonitorInfo> GetMonitors();

    /// <summary>
    /// Captures and encodes a monitor. Returns null when the screen is unchanged since the last
    /// call (unless <paramref name="force"/> is set) or when capture is impossible (e.g. the
    /// secure desktop is showing).
    /// </summary>
    EncodedFrame? Capture(int monitor, int maxWidth, int quality, bool force);
}

public enum MouseButton { Left, Right, Middle }

public enum ButtonAction { Click, DoubleClick, Down, Up }

public enum KeyAction { Press, Down, Up }

[Flags]
public enum Modifiers { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

public interface IInputInjector
{
    /// <summary>Moves the cursor to a normalized (0–1) point on the given monitor.</summary>
    void MoveAbsolute(MonitorInfo monitor, double x, double y);
    void MoveRelative(int dx, int dy);
    void Button(MouseButton button, ButtonAction action);
    void Wheel(int dx, int dy);
    void Key(ushort vk, KeyAction action, Modifiers modifiers);
    void Text(string text);
}

public interface ISystemActions
{
    /// <summary>Returns false when the action name is unknown.</summary>
    bool Execute(string action);
}

public sealed record FsEntry(string Name, bool IsDirectory, long Size, long ModifiedUnix);

public sealed record FsListing(string Path, string Parent, IReadOnlyList<FsEntry> Entries);

public interface IFileService
{
    FsListing List(string path);
    Stream OpenRead(string path, out string name, out long size);

    /// <summary>Creates a new file in <paramref name="directory"/>, never overwriting an existing one.</summary>
    Stream Create(string directory, string name, out string finalName);
}

public interface IPlatform
{
    string MachineName { get; }
    IScreenSource Screen { get; }
    IInputInjector Input { get; }
    IFileService Files { get; }
    ISystemActions System { get; }
}
