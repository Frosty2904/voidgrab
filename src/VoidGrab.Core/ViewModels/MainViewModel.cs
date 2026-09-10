using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using VoidGrab.Models;
using VoidGrab.Platform;
using VoidGrab.Services;

namespace VoidGrab.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly ToolProvisioner _tools = new();
    private readonly SpotifyResolver _spotify = new();
    private readonly YtDlpRunner _runner;
    private readonly AppSettings _settings;
    private readonly IPlatformServices _platform;

    private CancellationTokenSource? _cancellation;
    private Task _pump = Task.CompletedTask;

    private string _urlInput = "";
    private string _status = "Paste a link to begin.";
    private bool _isBusy;
    private bool _toolsReady;
    private MediaFormat _format;
    private QualityOption _quality;

    public MainViewModel(IPlatformServices platform)
    {
        _platform = platform;
        _runner = new YtDlpRunner(_tools);
        _settings = SettingsStore.Load();

        _format = MediaFormats.ById(_settings.FormatId);
        _quality = QualitiesFor(_format)
                       .FirstOrDefault(q => q.Label == QualityLabelFor(_format))
                   ?? QualitiesFor(_format)[0];

        AddCommand = new RelayCommand(async () => await AddAsync(), () => !string.IsNullOrWhiteSpace(UrlInput));
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        ClearFinishedCommand = new RelayCommand(ClearFinished, () => Jobs.Any(j => j.IsFinished));
        OpenFolderCommand = new RelayCommand(OpenFolder);
        BrowseCommand = new RelayCommand(async () => await BrowseAsync());
        UpdateToolCommand = new RelayCommand(async () => await UpdateToolAsync(), () => !IsBusy);

        // Without WPF's ambient requery, the queue has to say when it changed.
        Jobs.CollectionChanged += (_, _) => RefreshCommands();
    }

    private void RefreshCommands()
    {
        AddCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ClearFinishedCommand.RaiseCanExecuteChanged();
        UpdateToolCommand.RaiseCanExecuteChanged();
    }

    // ---- collections -----------------------------------------------------
    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    public ObservableCollection<string> Log { get; } = [];

    public IReadOnlyList<MediaFormat> Formats => MediaFormats.All;

    public ObservableCollection<QualityOption> Qualities { get; } = [];

    // ---- bound state -----------------------------------------------------
    public string UrlInput
    {
        get => _urlInput;
        set
        {
            if (Set(ref _urlInput, value)) AddCommand.RaiseCanExecuteChanged();
        }
    }

    public MediaFormat SelectedFormat
    {
        get => _format;
        set
        {
            if (!Set(ref _format, value)) return;
            _settings.FormatId = value.Id;
            RefreshQualities();
            OnPropertyChanged(nameof(FormatNote));
        }
    }

    public QualityOption SelectedQuality
    {
        get => _quality;
        set
        {
            if (!Set(ref _quality, value) || value is null) return;
            if (SelectedFormat.IsAudio) _settings.AudioQualityLabel = value.Label;
            else _settings.VideoQualityLabel = value.Label;
        }
    }

    public string FormatNote => SelectedFormat.Note;

    public string OutputDirectory
    {
        get => _settings.OutputDirectory;
        set
        {
            if (_settings.OutputDirectory == value) return;
            _settings.OutputDirectory = value;
            OnPropertyChanged();
        }
    }

    public bool EmbedMetadata
    {
        get => _settings.EmbedMetadata;
        set { _settings.EmbedMetadata = value; OnPropertyChanged(); }
    }

    public bool EmbedThumbnail
    {
        get => _settings.EmbedThumbnail;
        set { _settings.EmbedThumbnail = value; OnPropertyChanged(); }
    }

    public bool ExpandPlaylists
    {
        get => _settings.ExpandPlaylists;
        set { _settings.ExpandPlaylists = value; OnPropertyChanged(); }
    }

    public string SpotifyClientId
    {
        get => _settings.SpotifyClientId;
        set { _settings.SpotifyClientId = value; OnPropertyChanged(); }
    }

    public string SpotifyClientSecret
    {
        get => _settings.SpotifyClientSecret;
        set { _settings.SpotifyClientSecret = value; OnPropertyChanged(); }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) RefreshCommands();
        }
    }

    // ---- commands --------------------------------------------------------
    public RelayCommand AddCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearFinishedCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand UpdateToolCommand { get; }

    // ---- lifecycle -------------------------------------------------------
    public async Task InitialiseAsync()
    {
        RefreshQualities();

        var log = new Progress<string>(Write);
        try
        {
            if (!_tools.HasYtDlp || !_tools.HasFfmpeg)
            {
                Status = "Setting up tools…";
                IsBusy = true;
            }

            await _tools.EnsureAsync(log);
            _toolsReady = true;
            Status = "Ready.";
        }
        catch (Exception ex)
        {
            Write($"Tool setup failed: {ex.Message}");
            Status = "Tool setup failed — see the log.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Persist() => SettingsStore.Save(_settings);

    // ---- queueing --------------------------------------------------------
    private async Task AddAsync()
    {
        var links = LinkClassifier.ExtractLinks(UrlInput).ToList();
        if (links.Count == 0)
        {
            Status = "No usable link found in that text.";
            return;
        }

        if (!_toolsReady)
        {
            Status = "Tools are still being set up.";
            return;
        }

        UrlInput = "";
        var added = 0;

        foreach (var link in links)
        {
            try
            {
                added += await EnqueueAsync(link);
            }
            catch (SpotifyException ex)
            {
                Write($"Spotify: {ex.Message}");
                Status = ex.Message;
            }
            catch (Exception ex)
            {
                Write($"Could not queue {link}: {ex.Message}");
            }
        }

        if (added > 0)
        {
            Status = $"Queued {added} item{(added == 1 ? "" : "s")}.";
            StartPump();
        }
    }

    /// <summary>
    /// Turns one pasted link into queue rows.
    /// </summary>
    /// <remarks>
    /// Every multi-item source is expanded here rather than inside yt-dlp, so a
    /// row always maps to exactly one output file. A Spotify album becomes N
    /// search-backed rows; a YouTube playlist becomes N URL rows.
    /// </remarks>
    private async Task<int> EnqueueAsync(string link)
    {
        var source = LinkClassifier.Classify(link);

        if (source == SourceKind.Spotify)
        {
            if (!SelectedFormat.IsAudio)
            {
                Write("Spotify links are audio only — switching that item to MP3.");
            }

            Status = "Reading the Spotify catalogue…";
            var tracks = await _spotify.ResolveAsync(
                link, _settings.SpotifyClientId, _settings.SpotifyClientSecret, CancellationToken.None);

            var audioFormat = SelectedFormat.IsAudio ? SelectedFormat : MediaFormats.ById("mp3");
            var audioQuality = SelectedFormat.IsAudio ? SelectedQuality : QualityOptions.Audio[0];

            foreach (var track in tracks)
            {
                var job = NewJob(link, SourceKind.Spotify, audioFormat, audioQuality);
                job.SearchQuery = track.SearchQuery;
                job.Title = track.SearchQuery;
                Jobs.Add(new JobViewModel(job));
            }

            Write($"Spotify: matched {tracks.Count} track(s) to search on YouTube.");
            return tracks.Count;
        }

        if (ExpandPlaylists && LooksLikeCollection(link))
        {
            Status = "Expanding playlist…";
            var entries = await _runner.ResolvePlaylistAsync(link, CancellationToken.None);
            if (entries.Count > 1)
            {
                foreach (var entry in entries)
                {
                    var job = NewJob(entry.Url, source, SelectedFormat, SelectedQuality);
                    job.Title = entry.Title;
                    Jobs.Add(new JobViewModel(job));
                }

                Write($"Playlist expanded into {entries.Count} items.");
                return entries.Count;
            }
        }

        Jobs.Add(new JobViewModel(NewJob(link, source, SelectedFormat, SelectedQuality)));
        return 1;
    }

    private DownloadJob NewJob(string url, SourceKind source, MediaFormat format, QualityOption quality) =>
        new()
        {
            Url = url,
            Source = source,
            Format = format,
            Quality = quality,
            OutputDirectory = OutputDirectory,
            EmbedMetadata = EmbedMetadata,
            EmbedThumbnail = EmbedThumbnail,
        };

    private static bool LooksLikeCollection(string url) =>
        url.Contains("list=", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("/playlist", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("/sets/", StringComparison.OrdinalIgnoreCase);

    // ---- the pump --------------------------------------------------------
    private void StartPump()
    {
        if (!_pump.IsCompleted) return;

        _cancellation = new CancellationTokenSource();
        _pump = ProcessQueueAsync(_cancellation.Token);
    }

    /// <summary>
    /// Works the queue one job at a time.
    /// </summary>
    /// <remarks>
    /// Sequential on purpose. Parallel downloads would race for the same
    /// bandwidth, make per-item speed readings meaningless, and give sites a
    /// far better reason to rate limit. One at a time is also what makes the
    /// single cancel button unambiguous.
    /// </remarks>
    private async Task ProcessQueueAsync(CancellationToken token)
    {
        IsBusy = true;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var next = Jobs.FirstOrDefault(j => j.State == JobState.Queued);
                if (next is null) break;

                await RunOneAsync(next, token);
            }
        }
        finally
        {
            IsBusy = false;
            Status = Jobs.Any(j => j.State == JobState.Failed)
                ? "Finished, with failures — see the log."
                : "All done.";
        }
    }

    private async Task RunOneAsync(JobViewModel vm, CancellationToken token)
    {
        vm.State = vm.Job.SearchQuery is null ? JobState.Downloading : JobState.Resolving;
        vm.Detail = "Starting…";
        Status = $"{vm.StateLabel}: {vm.Title}";

        // Both created here, on the UI thread, so their callbacks marshal back
        // to it — the process events they relay fire on the thread pool.
        var progress = new Progress<DownloadProgress>(tick =>
        {
            if (tick.Fraction is { } fraction)
            {
                vm.Progress = fraction;
                vm.State = JobState.Downloading;
            }
            else if (tick.Status == "Converting")
            {
                vm.State = JobState.Converting;
            }

            vm.Detail = tick.Detail;
        });

        var log = new Progress<string>(line =>
        {
            if (line.StartsWith("[download] Destination:", StringComparison.OrdinalIgnoreCase))
            {
                vm.Title = Path.GetFileName(line[(line.IndexOf(':') + 1)..].Trim());
            }

            Write(line);
        });

        try
        {
            var result = await _runner.RunAsync(vm.Job, progress, log, token);

            if (result.Success)
            {
                vm.State = JobState.Done;
                vm.Progress = 1;
                vm.Detail = result.FilePath is { Length: > 0 } path
                    ? Path.GetFileName(path)
                    : "Saved.";
                if (result.FilePath is { Length: > 0 } saved) vm.Title = Path.GetFileName(saved);
            }
            else
            {
                vm.State = JobState.Failed;
                vm.Detail = result.Error ?? "Failed.";
            }
        }
        catch (OperationCanceledException)
        {
            vm.State = JobState.Cancelled;
            vm.Detail = "Cancelled.";
        }
        catch (Exception ex)
        {
            vm.State = JobState.Failed;
            vm.Detail = ex.Message;
            Write($"Error: {ex.Message}");
        }
    }

    private void Cancel()
    {
        _cancellation?.Cancel();
        Status = "Cancelling…";

        foreach (var queued in Jobs.Where(j => j.State == JobState.Queued))
        {
            queued.State = JobState.Cancelled;
            queued.Detail = "Cancelled before starting.";
        }
    }

    private void ClearFinished()
    {
        foreach (var finished in Jobs.Where(j => j.IsFinished).ToList()) Jobs.Remove(finished);
        RefreshCommands();
    }

    private async Task UpdateToolAsync()
    {
        IsBusy = true;
        Status = "Updating yt-dlp…";
        try
        {
            await _tools.UpdateYtDlpAsync(new Progress<string>(Write));
            Status = "yt-dlp updated.";
        }
        catch (Exception ex)
        {
            Write($"Update failed: {ex.Message}");
            Status = "Update failed — see the log.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            _platform.OpenFolder(OutputDirectory);
        }
        catch (Exception ex)
        {
            Write($"Could not open the folder: {ex.Message}");
        }
    }

    private async Task BrowseAsync()
    {
        try
        {
            var chosen = await _platform.PickFolderAsync(
                Directory.Exists(OutputDirectory) ? OutputDirectory : null);

            if (!string.IsNullOrWhiteSpace(chosen)) OutputDirectory = chosen;
        }
        catch (Exception ex)
        {
            Write($"Could not open the folder picker: {ex.Message}");
        }
    }

    // ---- helpers ---------------------------------------------------------
    private void RefreshQualities()
    {
        var wanted = QualitiesFor(SelectedFormat);
        Qualities.Clear();
        foreach (var option in wanted) Qualities.Add(option);

        var remembered = QualityLabelFor(SelectedFormat);
        SelectedQuality = wanted.FirstOrDefault(q => q.Label == remembered) ?? wanted[0];
        OnPropertyChanged(nameof(SelectedQuality));
    }

    private static IReadOnlyList<QualityOption> QualitiesFor(MediaFormat format) =>
        format.IsAudio ? QualityOptions.Audio : QualityOptions.Video;

    private string QualityLabelFor(MediaFormat format) =>
        format.IsAudio ? _settings.AudioQualityLabel : _settings.VideoQualityLabel;

    private void Write(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        Log.Add(line);

        // Keep the log bounded; a long playlist would otherwise grow it without limit.
        while (Log.Count > 500) Log.RemoveAt(0);
    }
}
