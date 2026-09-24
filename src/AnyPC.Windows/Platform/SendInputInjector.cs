using System.Runtime.InteropServices;
using AnyPC.Core.Abstractions;
using static AnyPC.Windows.Native;

namespace AnyPC.Windows.Platform;

/// <summary>Injects mouse and keyboard input with SendInput.</summary>
internal sealed class SendInputInjector : IInputInjector
{
    const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D;
    const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B;

    static readonly HashSet<ushort> ExtendedKeys =
    [
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // PgUp PgDn End Home arrows
        0x2C, 0x2D, 0x2E, // PrintScreen Insert Delete
        0x5B, 0x5C, 0x5D, // LWin RWin Apps
        0x6F, 0x90, // Divide NumLock
        0xA3, 0xA5, // RCtrl RAlt
    ];

    static readonly int InputSize = Marshal.SizeOf<INPUT>();
    readonly object _lock = new();

    static void Send(params INPUT[] inputs)
    {
        if (inputs.Length > 0) SendInput((uint)inputs.Length, inputs, InputSize);
    }

    static INPUT Mouse(uint flags, int dx = 0, int dy = 0, int data = 0) => new()
    {
        type = INPUT_MOUSE,
        u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = unchecked((uint)data), dwFlags = flags } },
    };

    static INPUT KeyInput(ushort vk, bool up)
    {
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        if (ExtendedKeys.Contains(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC), dwFlags = flags } },
        };
    }

    static INPUT Unicode(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } },
    };

    static void MoveToPixel(int px, int py)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN)), vh = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        px = Math.Clamp(px, vx, vx + vw - 1);
        py = Math.Clamp(py, vy, vy + vh - 1);
        // Absolute coordinates are normalized 0..65535 across the virtual desktop.
        int nx = (int)Math.Round((px - vx) * 65535.0 / (vw - 1));
        int ny = (int)Math.Round((py - vy) * 65535.0 / (vh - 1));
        Send(Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny));
    }

    public void MoveAbsolute(MonitorInfo monitor, double x, double y)
    {
        lock (_lock)
            MoveToPixel(monitor.X + (int)Math.Round(x * (monitor.Width - 1)), monitor.Y + (int)Math.Round(y * (monitor.Height - 1)));
    }

    public void MoveRelative(int dx, int dy)
    {
        lock (_lock)
        {
            if (!GetCursorPos(out var p)) return;
            MoveToPixel(p.X + dx, p.Y + dy);
        }
    }

    public void Button(MouseButton button, ButtonAction action)
    {
        var (down, up) = button switch
        {
            MouseButton.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            MouseButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        };
        lock (_lock)
        {
            switch (action)
            {
                case ButtonAction.Down: Send(Mouse(down)); break;
                case ButtonAction.Up: Send(Mouse(up)); break;
                case ButtonAction.Click: Send(Mouse(down), Mouse(up)); break;
                case ButtonAction.DoubleClick: Send(Mouse(down), Mouse(up), Mouse(down), Mouse(up)); break;
            }
        }
    }

    public void Wheel(int dx, int dy)
    {
        lock (_lock)
        {
            if (dy != 0) Send(Mouse(MOUSEEVENTF_WHEEL, data: dy));
            if (dx != 0) Send(Mouse(MOUSEEVENTF_HWHEEL, data: dx));
        }
    }

    static IEnumerable<ushort> ModifierKeys(Modifiers m)
    {
        if (m.HasFlag(Modifiers.Ctrl)) yield return VK_CONTROL;
        if (m.HasFlag(Modifiers.Alt)) yield return VK_MENU;
        if (m.HasFlag(Modifiers.Shift)) yield return VK_SHIFT;
        if (m.HasFlag(Modifiers.Win)) yield return VK_LWIN;
    }

    public void Key(ushort vk, KeyAction action, Modifiers modifiers)
    {
        var mods = ModifierKeys(modifiers).ToList();
        var inputs = new List<INPUT>();
        if (action is KeyAction.Press or KeyAction.Down)
        {
            inputs.AddRange(mods.Select(m => KeyInput(m, false)));
            inputs.Add(KeyInput(vk, false));
        }
        if (action is KeyAction.Press or KeyAction.Up)
        {
            inputs.Add(KeyInput(vk, true));
            inputs.AddRange(Enumerable.Reverse(mods).Select(m => KeyInput(m, true)));
        }
        lock (_lock) Send([.. inputs]);
    }

    public void Text(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            ushort? vk = c switch
            {
                '\r' => VK_RETURN,
                '\n' => i > 0 && text[i - 1] == '\r' ? null : VK_RETURN,
                '\t' => VK_TAB,
                '\b' => VK_BACK,
                _ => (ushort)0,
            };
            if (vk is null) continue;
            if (vk != 0)
            {
                inputs.Add(KeyInput(vk.Value, false));
                inputs.Add(KeyInput(vk.Value, true));
            }
            else
            {
                inputs.Add(Unicode(c, false));
                inputs.Add(Unicode(c, true));
            }
        }
        lock (_lock) Send([.. inputs]);
    }
}
