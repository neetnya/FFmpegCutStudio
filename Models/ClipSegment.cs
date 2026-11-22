using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FFmpegCutStudio.Models;

public class ClipSegment : INotifyPropertyChanged
{
    private string _name = "片段";
    private double _startSeconds;
    private double _endSeconds;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); }
    }

    public double StartSeconds
    {
        get => _startSeconds;
        set
        {
            _startSeconds = Math.Max(0, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DurationSeconds));
            OnPropertyChanged(nameof(Summary));
        }
    }

    public double EndSeconds
    {
        get => _endSeconds;
        set
        {
            _endSeconds = Math.Max(0, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DurationSeconds));
            OnPropertyChanged(nameof(Summary));
        }
    }

    public double DurationSeconds => Math.Max(0, EndSeconds - StartSeconds);

    public string Summary =>
        $"{Name}  |  {FormatTime(StartSeconds)} → {FormatTime(EndSeconds)}  ({DurationSeconds:F2}s)";

    public static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class SubtitleStreamInfo
{
    public int RelativeIndex { get; set; }
    public int GlobalIndex { get; set; }
    public string CodecName { get; set; } = "";
    public string Language { get; set; } = "und";
    public string Title { get; set; } = "";

    public string DisplayLabel =>
        $"字幕轨 #{RelativeIndex} (流:{GlobalIndex}) [{Language}] {CodecName} {Title}".Trim();
}

public class MediaProbeInfo
{
    public string FilePath { get; set; } = "";
    public double DurationSeconds { get; set; }
    public bool HasVideo { get; set; }
    public string VideoCodec { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string PixelFormat { get; set; } = "";
    public double Fps { get; set; }

    public bool HasAudio { get; set; }
    public string AudioCodec { get; set; } = "";
    public int SampleRate { get; set; }
    public int Channels { get; set; }

    public List<SubtitleStreamInfo> SubtitleStreams { get; set; } = new();
    public List<double> Keyframes { get; set; } = new();
}
