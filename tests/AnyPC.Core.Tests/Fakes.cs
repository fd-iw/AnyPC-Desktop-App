using System.Collections.Concurrent;
using AnyPC.Core.Abstractions;
using AnyPC.Core.Files;

namespace AnyPC.Core.Tests;

sealed class FakeScreen : IScreenSource
{
    public int Captures;
    public bool Changed = true;

    public IReadOnlyList<MonitorInfo> GetMonitors() =>
    [
        new MonitorInfo(0, "Primary", 0, 0, 1920, 1080, true),
        new MonitorInfo(1, "Second", 1920, 0, 1280, 1024, false),
    ];

    public EncodedFrame? Capture(int monitor, int maxWidth, int quality, bool force)
    {
        Interlocked.Increment(ref Captures);
        if (!Changed && !force) return null;
        return new EncodedFrame([0xFF, 0xD8, (byte)monitor, (byte)quality, 0xFF, 0xD9], Math.Min(maxWidth, 1920), 600, 10, 20);
    }
}

sealed class FakeInput : IInputInjector
{
    public readonly ConcurrentQueue<string> Events = new();

    public void MoveAbsolute(MonitorInfo monitor, double x, double y) => Events.Enqueue($"move {monitor.Index} {x:0.##} {y:0.##}");
    public void MoveRelative(int dx, int dy) => Events.Enqueue($"moverel {dx} {dy}");
    public void Button(MouseButton button, ButtonAction action) => Events.Enqueue($"button {button} {action}");
    public void Wheel(int dx, int dy) => Events.Enqueue($"wheel {dx} {dy}");
    public void Key(ushort vk, KeyAction action, Modifiers modifiers) => Events.Enqueue($"key {vk} {action} {modifiers}");
    public void Text(string text) => Events.Enqueue($"text {text}");
}

sealed class FakeSystem : ISystemActions
{
    public readonly ConcurrentQueue<string> Actions = new();

    public bool Execute(string action)
    {
        if (action is not ("lock" or "volume_up")) return false;
        Actions.Enqueue(action);
        return true;
    }
}

sealed class FakePlatform : IPlatform
{
    public string MachineName => "TEST-PC";
    public FakeScreen FakeScreen { get; } = new();
    public FakeInput FakeInput { get; } = new();
    public FakeSystem FakeSystem { get; } = new();
    public IScreenSource Screen => FakeScreen;
    public IInputInjector Input => FakeInput;
    public IFileService Files { get; } = new FileService();
    public ISystemActions System => FakeSystem;
}
