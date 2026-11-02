using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Easing;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System.Windows;
using System.Windows.Input;

namespace NMEA2000Analyzer
{
    public partial class TrafficGraphWindow : Window
    {
        private const double BarWidthDip = 6;
        private const double LabelWidthDip = 84;
        private static readonly int[] ZoomStepsSeconds = [1, 5, 10, 30, 60, 300, 600, 1800, 3600, 10800, 21600, 43200, 86400];

        private readonly List<DateTimeOffset> _timestamps;
        private readonly DateTimeOffset _fullStart;
        private readonly DateTimeOffset _fullEnd;
        private readonly string _graphSubtitleBase;
        private int _currentSecondsPerBar;
        private int _maxSecondsPerBar;
        private DateTimeOffset _visibleStart;
        private DateTimeOffset _visibleEnd;
        private Point _panStartPoint;
        private DateTimeOffset _panStartVisibleStart;
        private DateTimeOffset _panStartVisibleEnd;
        private bool _isPanning;

        public IEnumerable<ISeries> Series { get; }
        public Axis[] XAxes { get; }
        public Axis[] YAxes { get; }
        public string GraphTitle { get; }
        public string GraphSubtitle { get; private set; }

        public TrafficGraphWindow(
            string graphTitle,
            string graphSeriesName,
            string graphSubtitle,
            IReadOnlyList<DateTimeOffset> timestamps,
            DateTimeOffset fullStart,
            DateTimeOffset fullEnd)
        {
            _timestamps = timestamps.OrderBy(value => value).ToList();
            _fullStart = fullStart;
            _fullEnd = fullEnd <= fullStart ? fullStart.AddSeconds(1) : fullEnd;
            _currentSecondsPerBar = 1;
            _maxSecondsPerBar = 1;
            _visibleStart = _fullStart;
            _visibleEnd = _fullEnd;

            GraphTitle = graphTitle;
            _graphSubtitleBase = string.IsNullOrWhiteSpace(graphSubtitle) ? string.Empty : graphSubtitle;
            GraphSubtitle = _graphSubtitleBase;

            var fillColor = new SKColor(0x3B, 0x82, 0xF6, 0x99);
            var strokeColor = new SKColor(0x60, 0xA5, 0xFA);
            var labelColor = new SKColor(0x7A, 0x86, 0x94);

            Series =
            [
                new ColumnSeries<ObservablePoint>
                {
                    Values = Array.Empty<ObservablePoint>(),
                    Name = graphSeriesName,
                    Fill = new SolidColorPaint(fillColor),
                    Stroke = new SolidColorPaint(strokeColor, 1),
                    AnimationsSpeed = TimeSpan.Zero,
                    EasingFunction = EasingFunctions.Lineal,
                    XToolTipLabelFormatter = _ => string.Empty,
                    YToolTipLabelFormatter = chartPoint => chartPoint.Coordinate.PrimaryValue.ToString("0"),
                    Rx = 0,
                    Ry = 0,
                    Padding = 0,
                    MaxBarWidth = double.PositiveInfinity
                }
            ];

            XAxes =
            [
                new Axis
                {
                    Labels = Array.Empty<string>(),
                    AnimationsSpeed = TimeSpan.Zero,
                    EasingFunction = EasingFunctions.Lineal,
                    LabelsRotation = 0,
                    LabelsPaint = new SolidColorPaint(labelColor),
                    TextSize = 12,
                    SeparatorsPaint = null,
                    MinStep = 1,
                    MinLimit = 0,
                    MaxLimit = 0,
                    ForceStepToMin = true
                }
            ];

            YAxes =
            [
                new Axis
                {
                    Name = "Packets/s",
                    AnimationsSpeed = TimeSpan.Zero,
                    EasingFunction = EasingFunctions.Lineal,
                    NamePaint = new SolidColorPaint(labelColor),
                    NameTextSize = 12,
                    LabelsPaint = new SolidColorPaint(labelColor),
                    TextSize = 12,
                    MinLimit = 0,
                    MinStep = 1,
                    SeparatorsPaint = null
                }
            ];

            InitializeComponent();
            DataContext = this;
            Width = SystemParameters.WorkArea.Width * 0.9;
            Height = Math.Max(390, SystemParameters.WorkArea.Height * 0.54);
            Loaded += (_, _) =>
            {
                RecalculateZoomBounds();
                _currentSecondsPerBar = _maxSecondsPerBar;
                _visibleStart = _fullStart;
                _visibleEnd = _fullEnd;
                UpdateChart();
            };
        }

        private void TrafficChart_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged && IsLoaded)
            {
                RecalculateZoomBounds();
                var nextEnd = _visibleStart + TimeSpan.FromSeconds(GetVisibleDurationSeconds());
                ClampVisibleRange(ref _visibleStart, ref nextEnd);
                _visibleEnd = nextEnd;
                UpdateChart();
            }
        }

        private void TrafficChart_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var nextSecondsPerBar = e.Delta > 0
                ? GetNextFinerStep(_currentSecondsPerBar)
                : GetNextCoarserStep(_currentSecondsPerBar);

            if (nextSecondsPerBar == _currentSecondsPerBar)
            {
                e.Handled = true;
                return;
            }

            var currentVisibleSeconds = GetVisibleDurationSeconds();
            var nextVisibleSeconds = GetVisibleDurationSeconds(nextSecondsPerBar);
            var centerRatio = TrafficChart.ActualWidth <= 0 ? 0.5 : Math.Clamp(e.GetPosition(TrafficChart).X / TrafficChart.ActualWidth, 0, 1);
            var center = _visibleStart + TimeSpan.FromSeconds(currentVisibleSeconds * centerRatio);
            var nextStart = center - TimeSpan.FromSeconds(nextVisibleSeconds * centerRatio);
            var nextEnd = nextStart + TimeSpan.FromSeconds(nextVisibleSeconds);

            ClampVisibleRange(ref nextStart, ref nextEnd);
            _currentSecondsPerBar = nextSecondsPerBar;
            _visibleStart = nextStart;
            _visibleEnd = nextEnd;
            UpdateChart();
            e.Handled = true;
        }

        private void TrafficChart_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isPanning = true;
            _panStartPoint = e.GetPosition(TrafficChart);
            _panStartVisibleStart = _visibleStart;
            _panStartVisibleEnd = _visibleEnd;
            TrafficChart.CaptureMouse();
            Cursor = Cursors.SizeWE;
        }

        private void TrafficChart_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isPanning || TrafficChart.ActualWidth <= 0)
            {
                return;
            }

            var currentPoint = e.GetPosition(TrafficChart);
            var deltaX = currentPoint.X - _panStartPoint.X;
            var visibleSeconds = (_panStartVisibleEnd - _panStartVisibleStart).TotalSeconds;
            var deltaSeconds = -deltaX / TrafficChart.ActualWidth * visibleSeconds;

            var nextStart = _panStartVisibleStart.AddSeconds(deltaSeconds);
            var nextEnd = _panStartVisibleEnd.AddSeconds(deltaSeconds);
            ClampVisibleRange(ref nextStart, ref nextEnd);

            _visibleStart = nextStart;
            _visibleEnd = nextEnd;
            UpdateChart();
        }

        private void TrafficChart_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            EndPan();
        }

        private void UpdateChart()
        {
            if (TrafficChart.ActualWidth <= 0)
            {
                return;
            }

            var barCount = GetBarCount();
            var secondsPerBar = _currentSecondsPerBar;
            var labels = new string[barCount];
            var points = new ObservablePoint[barCount];
            var labelEvery = Math.Max(1, (int)Math.Ceiling(LabelWidthDip / BarWidthDip));

            var scanIndex = FindFirstVisibleTimestamp(_visibleStart);
            for (var barIndex = 0; barIndex < barCount; barIndex++)
            {
                var bucketStart = _visibleStart.AddSeconds(secondsPerBar * barIndex);
                var bucketEnd = barIndex == barCount - 1
                    ? _visibleEnd
                    : _visibleStart.AddSeconds(secondsPerBar * (barIndex + 1));

                var count = 0;
                while (scanIndex < _timestamps.Count)
                {
                    var timestamp = _timestamps[scanIndex];
                    if (timestamp < bucketStart)
                    {
                        scanIndex++;
                        continue;
                    }

                    if (barIndex == barCount - 1 ? timestamp > bucketEnd : timestamp >= bucketEnd)
                    {
                        break;
                    }

                    count++;
                    scanIndex++;
                }

                points[barIndex] = new ObservablePoint(barIndex, count);
                labels[barIndex] = barIndex % labelEvery == 0
                    ? bucketStart.LocalDateTime.ToString("HH:mm:ss")
                    : string.Empty;
            }

            if (Series.FirstOrDefault() is ColumnSeries<ObservablePoint> series)
            {
                series.Values = points;
            }

            XAxes[0].Labels = labels;
            XAxes[0].MaxLimit = Math.Max(0, barCount - 1);
            GraphSubtitle = BuildSubtitle(secondsPerBar);
            DataContext = null;
            DataContext = this;
        }

        private int FindFirstVisibleTimestamp(DateTimeOffset visibleStart)
        {
            var index = _timestamps.BinarySearch(visibleStart);
            if (index < 0)
            {
                index = ~index;
            }

            return Math.Clamp(index, 0, _timestamps.Count);
        }

        private int GetBarCount()
        {
            var width = Math.Max(1, TrafficChart.ActualWidth);
            return Math.Max(1, (int)Math.Floor(width / BarWidthDip));
        }

        private void RecalculateZoomBounds()
        {
            var requiredSecondsPerBar = (_fullEnd - _fullStart).TotalSeconds / GetBarCount();
            _maxSecondsPerBar = ZoomStepsSeconds.FirstOrDefault(step => step >= requiredSecondsPerBar);
            if (_maxSecondsPerBar == 0)
            {
                _maxSecondsPerBar = ZoomStepsSeconds[^1];
            }

            _currentSecondsPerBar = Math.Min(_currentSecondsPerBar, _maxSecondsPerBar);
            if (_currentSecondsPerBar <= 0)
            {
                _currentSecondsPerBar = ZoomStepsSeconds[0];
            }
        }

        private int GetVisibleDurationSeconds()
        {
            return GetVisibleDurationSeconds(_currentSecondsPerBar);
        }

        private int GetVisibleDurationSeconds(int secondsPerBar)
        {
            return Math.Max(secondsPerBar, GetBarCount() * secondsPerBar);
        }

        private int GetNextFinerStep(int currentSecondsPerBar)
        {
            for (var index = ZoomStepsSeconds.Length - 1; index >= 0; index--)
            {
                if (ZoomStepsSeconds[index] < currentSecondsPerBar)
                {
                    return ZoomStepsSeconds[index];
                }
            }

            return ZoomStepsSeconds[0];
        }

        private int GetNextCoarserStep(int currentSecondsPerBar)
        {
            foreach (var step in ZoomStepsSeconds)
            {
                if (step > currentSecondsPerBar)
                {
                    return Math.Min(step, _maxSecondsPerBar);
                }
            }

            return _maxSecondsPerBar;
        }

        private void ClampVisibleRange(ref DateTimeOffset start, ref DateTimeOffset end)
        {
            var duration = end - start;
            var fullDuration = _fullEnd - _fullStart;
            if (duration > fullDuration)
            {
                start = _fullStart;
                end = _fullEnd;
                return;
            }

            if (start < _fullStart)
            {
                start = _fullStart;
                end = start + duration;
            }

            if (end > _fullEnd)
            {
                end = _fullEnd;
                start = end - duration;
            }
        }

        private string BuildSubtitle(double secondsPerBar)
        {
            var resolution = secondsPerBar <= 1.0001
                ? "1 second per bar"
                : $"{secondsPerBar:0.##} seconds per bar";
            var prefix = string.IsNullOrWhiteSpace(_graphSubtitleBase) ? string.Empty : $"{_graphSubtitleBase} | ";
            return $"{prefix}{resolution} | {_visibleStart.LocalDateTime:HH:mm:ss} to {_visibleEnd.LocalDateTime:HH:mm:ss}";
        }

        protected override void OnClosed(EventArgs e)
        {
            EndPan();
            base.OnClosed(e);
        }

        private void EndPan()
        {
            if (!_isPanning)
            {
                return;
            }

            _isPanning = false;
            TrafficChart.ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
        }
    }
}
