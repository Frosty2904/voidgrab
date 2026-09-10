namespace VoidGrab.Platform;

/// <summary>
/// The few things the view model needs that only a UI toolkit can provide.
/// </summary>
/// <remarks>
/// Kept deliberately tiny. Everything else in Core runs identically everywhere;
/// this interface exists so that "pick a folder" and "show me that folder" — the
/// two operations with no portable answer — can be supplied by the WPF head on
/// Windows and the Avalonia head on macOS and Linux without Core knowing which.
/// </remarks>
public interface IPlatformServices
{
    /// <summary>Asks the user for a directory, or null if they cancelled.</summary>
    Task<string?> PickFolderAsync(string? startingAt);

    /// <summary>Reveals a directory in the system file manager.</summary>
    void OpenFolder(string path);
}

/// <summary>
/// Opens folders through the platform's own file manager.
/// </summary>
/// <remarks>
/// Folder picking is left abstract because it needs a parent window, but
/// revealing a directory is just a process launch and the same three lines serve
/// every head — so it lives here rather than being written twice.
/// </remarks>
public static class ShellFolder
{
    public static void Open(string path)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            UseShellExecute = false,
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "explorer.exe";
            startInfo.ArgumentList.Add(path);
        }
        else if (OperatingSystem.IsMacOS())
        {
            startInfo.FileName = "open";
            startInfo.ArgumentList.Add(path);
        }
        else
        {
            startInfo.FileName = "xdg-open";
            startInfo.ArgumentList.Add(path);
        }

        using var process = System.Diagnostics.Process.Start(startInfo);
    }
}
