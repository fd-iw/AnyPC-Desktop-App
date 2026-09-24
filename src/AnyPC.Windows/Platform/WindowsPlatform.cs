using AnyPC.Core.Abstractions;
using AnyPC.Core.Files;

namespace AnyPC.Windows.Platform;

internal sealed class WindowsPlatform : IPlatform, IDisposable
{
    readonly GdiScreenSource _screen = new();

    public WindowsPlatform()
    {
        Input = new SendInputInjector();
        System = new WindowsSystemActions(Input);
    }

    public string MachineName => Environment.MachineName;
    public IScreenSource Screen => _screen;
    public IInputInjector Input { get; }
    public IFileService Files { get; } = new FileService();
    public ISystemActions System { get; }

    public void Dispose() => _screen.Dispose();
}
