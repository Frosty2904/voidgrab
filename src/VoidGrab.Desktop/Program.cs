using Avalonia;

namespace VoidGrab.Desktop;

internal static class Program
{
    /// <remarks>
    /// Must stay <c>[STAThread]</c> and free of any Avalonia call before
    /// <see cref="BuildAvaloniaApp"/> — the toolkit is not initialised until the
    /// lifetime starts, and touching it earlier fails in ways that are hard to
    /// read from a crash log on a machine you cannot attach a debugger to.
    /// </remarks>
    [STAThread]
    public static int Main(string[] args)
    {
        // Handled before Avalonia starts so the diagnostics work headlessly —
        // over SSH, in CI, or on a Mac with no display attached.
        if (CommandLine.WantsHeadless(args)) return CommandLine.Run(args);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
