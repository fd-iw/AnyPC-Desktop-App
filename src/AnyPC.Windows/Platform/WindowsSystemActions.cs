using System.Diagnostics;
using AnyPC.Core.Abstractions;

namespace AnyPC.Windows.Platform;

internal sealed class WindowsSystemActions(IInputInjector input) : ISystemActions
{
    const ushort VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;
    const ushort VK_MEDIA_NEXT = 0xB0, VK_MEDIA_PREV = 0xB1, VK_MEDIA_PLAY_PAUSE = 0xB3;
    const ushort VK_D = 0x44, VK_ESCAPE = 0x1B;

    public bool Execute(string action)
    {
        switch (action)
        {
            case "volume_up": input.Key(VK_VOLUME_UP, KeyAction.Press, Modifiers.None); return true;
            case "volume_down": input.Key(VK_VOLUME_DOWN, KeyAction.Press, Modifiers.None); return true;
            case "mute": input.Key(VK_VOLUME_MUTE, KeyAction.Press, Modifiers.None); return true;
            case "play_pause": input.Key(VK_MEDIA_PLAY_PAUSE, KeyAction.Press, Modifiers.None); return true;
            case "next_track": input.Key(VK_MEDIA_NEXT, KeyAction.Press, Modifiers.None); return true;
            case "prev_track": input.Key(VK_MEDIA_PREV, KeyAction.Press, Modifiers.None); return true;
            case "show_desktop": input.Key(VK_D, KeyAction.Press, Modifiers.Win); return true;
            case "task_manager": input.Key(VK_ESCAPE, KeyAction.Press, Modifiers.Ctrl | Modifiers.Shift); return true;
            case "lock": Native.LockWorkStation(); return true;
            // Delay power actions slightly so the confirmation reaches the phone first.
            case "sleep": Later(() => Native.SetSuspendState(false, false, false)); return true;
            case "restart": Later(() => Shutdown("/r /t 0")); return true;
            case "shutdown": Later(() => Shutdown("/s /t 0")); return true;
            case "signout": Later(() => Shutdown("/l")); return true;
            default: return false;
        }
    }

    static void Later(Action a) => Task.Delay(750).ContinueWith(_ => a(), TaskScheduler.Default);

    static void Shutdown(string args) =>
        Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false });
}
