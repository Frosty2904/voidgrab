using Avalonia.Controls;
using Avalonia.Platform.Storage;
using VoidGrab.Platform;

namespace VoidGrab.Desktop;

/// <summary>
/// The macOS/Linux half of <see cref="IPlatformServices"/>.
/// </summary>
/// <remarks>
/// Folder picking goes through Avalonia's StorageProvider rather than any
/// path-based API, because on macOS the picker is a sandbox-aware system dialog
/// and its result arrives as a storage item, not a string. It also needs the
/// owning window, which is why this takes one.
/// </remarks>
public sealed class AvaloniaPlatformServices(Window owner) : IPlatformServices
{
    public async Task<string?> PickFolderAsync(string? startingAt)
    {
        var storage = owner.StorageProvider;
        if (!storage.CanPickFolder) return null;

        IStorageFolder? start = null;
        if (!string.IsNullOrWhiteSpace(startingAt))
        {
            try
            {
                start = await storage.TryGetFolderFromPathAsync(startingAt);
            }
            catch
            {
                // A remembered folder that has since been deleted or unmounted
                // is not worth failing the picker over; open at the default.
            }
        }

        var chosen = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Where should downloads go?",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        return chosen.Count > 0 ? chosen[0].Path.LocalPath : null;
    }

    public void OpenFolder(string path) => ShellFolder.Open(path);
}
