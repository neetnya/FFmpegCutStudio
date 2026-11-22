using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FFmpegCutStudio.Models;

namespace FFmpegCutStudio.Controls;

/// <summary>
/// 自绘多段选区时间轴控件：
/// - 拖拽中按 90ms 节流触发 ScrubRequested（走快速关键帧预览），松手后触发 SeekCommitted（走精准帧定位）
/// - 彻底避免拖拽松手后播放器排队快进的问题
/// </summary>
public class TimelineControl : FrameworkElement
{
    private const double PaddingX = 12.0;
    private const double TrackTop = 18.0;
    private const double TrackHeight = 32.0;
    private const double HandleHitRadius = 7.0;
    private const long ScrubThrottleMs = 90;

    private enum DragMode { None, Seek, ResizeStart, ResizeEnd }

    private DragMode _dragMode = DragMode.None;
    private readonly Stopwatch _scrubClock = Stopwatch.StartNew();
    private long _lastScrubMs;
    private double _durationSeconds;
    private double _currentSeconds;
    private IReadOnlyList<double> _keyframes = Array.Empty<double>();
    private ObservableCollection<ClipSegment>? _segments;
    private ClipSegment? _selectedSegment;

    public bool IsDragging => _dragMode != DragMode.None;

    /// <summary>拖拽过程中的节流预览定位（建议走快速关键帧 seek）。</summary>
    public event Action<double>? ScrubRequested;

    /// <summary>点击或松开鼠标后的最终精准定位（走 exact seek）。</summary>
    public event Action<double>? SeekCommitted;

    public event Action<ClipSegment>? SegmentSelected;
    public event Action<ClipSegment>? SegmentBoundsChanged;

    private static readonly Brush TrackBg = new SolidColorBrush(Color.FromRgb(32, 36, 45));
    private static readonly Brush TrackBorder = new SolidColorBrush(Color.FromRgb(64, 72, 88));
    private static readonly Pen KeyframePen = new(new SolidColorBrush(Color.FromArgb(110, 250, 204, 21)), 1.0);
    private static readonly Brush SegmentFill = new SolidColorBrush(Color.FromArgb(100, 59, 130, 246));
    private static readonly Brush SegmentSelectedFill = new SolidColorBrush(Color.FromArgb(150, 16, 185, 129));
    private static readonly Pen SegmentBorderPen = new(new SolidColorBrush(Color.FromRgb(96, 165, 250)), 1.2);
    private static readonly Pen SegmentSelectedBorderPen = new(new SolidColorBrush(Color.FromRgb(52, 211, 153)), 2.0);
    private static readonly Brush HandleBrush = new SolidColorBrush(Color.FromRgb(236, 253, 245));
    private static readonly Pen PlayheadPen = new(new SolidColorBrush(Color.FromRgb(239, 68, 68)), 2.0);
    private static readonly Brush PlayheadHeadBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
    private static readonly Brush RulerTextBrush = new SolidColorBrush(Color.FromRgb(154, 162, 178));

    public TimelineControl()
    {
        Height = 62;
        ClipToBounds = true;
        Cursor = Cursors.Hand;
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        set { _durationSeconds = Math.Max(0, value); InvalidateVisual(); }
    }

    public double CurrentSeconds
    {
        get => _currentSeconds;
        set { _currentSeconds = Math.Max(0, value); InvalidateVisual(); }
    }

    public IReadOnlyList<double> Keyframes
    {
        get => _keyframes;
        set { _keyframes = value ?? Array.Empty<double>(); InvalidateVisual(); }
    }

    public ObservableCollection<ClipSegment>? Segments
    {
        get => _segments;
        set { _segments = value; InvalidateVisual(); }
    }

    public ClipSegment? SelectedSegment
    {
        get => _selectedSegment;
        set { _selectedSegment = value; InvalidateVisual(); }
    }

    public void Refresh() => InvalidateVisual();

    private double TrackWidth => Math.Max(1.0, ActualWidth - PaddingX * 2);

    private double TimeToX(double seconds)
    {
        if (_durationSeconds <= 0) return PaddingX;
        double ratio = Math.Clamp(seconds / _durationSeconds, 0, 1);
        return PaddingX + ratio * TrackWidth;
    }

    private double XToTime(double x)
    {
        if (_durationSeconds <= 0) return 0;
        double ratio = Math.Clamp((x - PaddingX) / TrackWidth, 0, 1);
        return ratio * _durationSeconds;
    }

    private void FireThrottledScrub(double seconds)
    {
        long now = _scrubClock.ElapsedMilliseconds;
        if (now - _lastScrubMs >= ScrubThrottleMs)
        {
            _lastScrubMs = now;
            ScrubRequested?.Invoke(seconds);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (_durationSeconds > 0)
        {
            int ticks = 6;
            var typeface = new Typeface("Consolas");
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (int i = 0; i <= ticks; i++)
            {
                double t = _durationSeconds * i / ticks;
                double x = TimeToX(t);
                var ts = TimeSpan.FromSeconds(t);
                string label = ts.TotalHours >= 1
                    ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                    : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
                var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, 10, RulerTextBrush, dpi);
                double drawX = Math.Clamp(x - ft.Width / 2, PaddingX, ActualWidth - PaddingX - ft.Width);
                dc.DrawText(ft, new Point(drawX, 2));
            }
        }

        var trackRect = new Rect(PaddingX, TrackTop, TrackWidth, TrackHeight);
        dc.DrawRoundedRectangle(TrackBg, new Pen(TrackBorder, 1), trackRect, 4, 4);

        if (_durationSeconds > 0 && _keyframes.Count > 0)
        {
            double lastX = -10;
            foreach (double kf in _keyframes)
            {
                double x = TimeToX(kf);
                if (x - lastX < 2.0) continue;
                dc.DrawLine(KeyframePen, new Point(x, TrackTop + 2), new Point(x, TrackTop + 10));
                lastX = x;
            }
        }

        if (_durationSeconds > 0 && _segments != null)
        {
            var segTypeface = new Typeface("Microsoft YaHei UI");
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            foreach (var seg in _segments)
            {
                double x1 = TimeToX(seg.StartSeconds);
                double x2 = TimeToX(seg.EndSeconds);
                if (x2 < x1) (x1, x2) = (x2, x1);
                double w = Math.Max(3.0, x2 - x1);
                bool isSelected = ReferenceEquals(seg, _selectedSegment);

                var rect = new Rect(x1, TrackTop + 1, w, TrackHeight - 2);
                dc.DrawRectangle(isSelected ? SegmentSelectedFill : SegmentFill,
                    isSelected ? SegmentSelectedBorderPen : SegmentBorderPen, rect);

                if (w > 28)
                {
                    var ft = new FormattedText(seg.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        segTypeface, 10.5, Brushes.White, dpi);
                    ft.MaxTextWidth = w - 6;
                    ft.MaxLineCount = 1;
                    dc.DrawText(ft, new Point(x1 + 4, TrackTop + (TrackHeight - ft.Height) / 2));
                }

                if (isSelected)
                {
                    dc.DrawRectangle(HandleBrush, null, new Rect(x1 - 2, TrackTop, 4, TrackHeight));
                    dc.DrawRectangle(HandleBrush, null, new Rect(x2 - 2, TrackTop, 4, TrackHeight));
                }
            }
        }

        if (_durationSeconds > 0)
        {
            double px = TimeToX(_currentSeconds);
            dc.DrawLine(PlayheadPen, new Point(px, TrackTop - 4), new Point(px, TrackTop + TrackHeight + 4));
            var tri = new StreamGeometry();
            using (var ctx = tri.Open())
            {
                ctx.BeginFigure(new Point(px - 5, TrackTop - 6), true, true);
                ctx.LineTo(new Point(px + 5, TrackTop - 6), true, false);
                ctx.LineTo(new Point(px, TrackTop + 1), true, false);
            }
            dc.DrawGeometry(PlayheadHeadBrush, null, tri);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_durationSeconds <= 0) return;

        Point pos = e.GetPosition(this);
        CaptureMouse();
        _lastScrubMs = _scrubClock.ElapsedMilliseconds;

        if (_selectedSegment != null)
        {
            double sx1 = TimeToX(_selectedSegment.StartSeconds);
            double sx2 = TimeToX(_selectedSegment.EndSeconds);
            if (Math.Abs(pos.X - sx1) <= HandleHitRadius)
            {
                _dragMode = DragMode.ResizeStart;
                return;
            }
            if (Math.Abs(pos.X - sx2) <= HandleHitRadius)
            {
                _dragMode = DragMode.ResizeEnd;
                return;
            }
        }

        double clickedTime = XToTime(pos.X);
        if (_segments != null)
        {
            foreach (var seg in _segments)
            {
                if (clickedTime >= seg.StartSeconds && clickedTime <= seg.EndSeconds && !ReferenceEquals(seg, _selectedSegment))
                {
                    _selectedSegment = seg;
                    SegmentSelected?.Invoke(seg);
                    break;
                }
            }
        }

        _dragMode = DragMode.Seek;
        _currentSeconds = clickedTime;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_durationSeconds <= 0) return;

        Point pos = e.GetPosition(this);
        if (_dragMode == DragMode.None)
        {
            if (_selectedSegment != null)
            {
                double sx1 = TimeToX(_selectedSegment.StartSeconds);
                double sx2 = TimeToX(_selectedSegment.EndSeconds);
                Cursor = (Math.Abs(pos.X - sx1) <= HandleHitRadius || Math.Abs(pos.X - sx2) <= HandleHitRadius)
                    ? Cursors.SizeWE
                    : Cursors.Hand;
            }
            return;
        }

        double t = XToTime(pos.X);
        if (_dragMode == DragMode.Seek)
        {
            _currentSeconds = t;
            InvalidateVisual();
            FireThrottledScrub(t);
        }
        else if (_dragMode == DragMode.ResizeStart && _selectedSegment != null)
        {
            _selectedSegment.StartSeconds = Math.Min(t, Math.Max(0, _selectedSegment.EndSeconds - 0.1));
            _currentSeconds = _selectedSegment.StartSeconds;
            InvalidateVisual();
            SegmentBoundsChanged?.Invoke(_selectedSegment);
            FireThrottledScrub(_selectedSegment.StartSeconds);
        }
        else if (_dragMode == DragMode.ResizeEnd && _selectedSegment != null)
        {
            _selectedSegment.EndSeconds = Math.Max(t, Math.Min(_durationSeconds, _selectedSegment.StartSeconds + 0.1));
            _currentSeconds = _selectedSegment.EndSeconds;
            InvalidateVisual();
            SegmentBoundsChanged?.Invoke(_selectedSegment);
            FireThrottledScrub(_selectedSegment.EndSeconds);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragMode != DragMode.None)
        {
            double commitTime = _currentSeconds;
            _dragMode = DragMode.None;
            ReleaseMouseCapture();
            SeekCommitted?.Invoke(commitTime);
        }
    }
}
