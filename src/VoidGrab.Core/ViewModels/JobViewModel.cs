using VoidGrab.Models;

namespace VoidGrab.ViewModels;

/// <summary>One row in the queue.</summary>
public sealed class JobViewModel(DownloadJob job) : ObservableObject
{
    private JobState _state = JobState.Queued;
    private double _progress;
    private string _detail = "Waiting…";
    private string _title = job.Title;

    public DownloadJob Job { get; } = job;

    public string Title
    {
        get => string.IsNullOrWhiteSpace(_title) ? ShortUrl : _title;
        set => Set(ref _title, value);
    }

    public string SourceLabel => LinkClassifier.DisplayName(Job.Source);

    public string FormatLabel => Job.Format.Id.ToUpperInvariant();

    public JobState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(StateLabel));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(IsFinished));
            OnPropertyChanged(nameof(IsIndeterminate));
        }
    }

    public string StateLabel => State switch
    {
        JobState.Queued => "Queued",
        JobState.Resolving => "Finding",
        JobState.Downloading => "Downloading",
        JobState.Converting => "Converting",
        JobState.Done => "Done",
        JobState.Failed => "Failed",
        JobState.Cancelled => "Cancelled",
        _ => "",
    };

    /// <summary>0 to 1. Bound to the row's progress bar.</summary>
    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }

    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }

    public bool IsActive => State is JobState.Resolving or JobState.Downloading or JobState.Converting;

    public bool IsFinished => State is JobState.Done or JobState.Failed or JobState.Cancelled;

    /// <summary>
    /// True while something is happening but no percentage is meaningful —
    /// resolving a link, or waiting on ffmpeg, which reports no byte counts.
    /// </summary>
    public bool IsIndeterminate => State is JobState.Resolving or JobState.Converting;

    private string ShortUrl =>
        Job.SearchQuery is { Length: > 0 } query
            ? query
            : Job.Url.Length > 60 ? Job.Url[..60] + "…" : Job.Url;
}
