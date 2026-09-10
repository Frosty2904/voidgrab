using VoidGrab.Platform;

namespace VoidGrab;

/// <summary>The Windows half of <see cref="IPlatformServices"/>.</summary>
public sealed class WpfPlatformServices : IPlatformServices
{
    public Task<string?> PickFolderAsync(string? startingAt)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Where should downloads go?",
            InitialDirectory = startingAt ?? "",
        };

        // WPF's dialog is synchronous and must run on the UI thread, which is
        // already where commands are invoked — so there is nothing to await.
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FolderName : null);
    }

    public void OpenFolder(string path) => ShellFolder.Open(path);
}
