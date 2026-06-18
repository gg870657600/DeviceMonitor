using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using SkiaSharp;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace DeviceMonitor
{
    public partial class ChartWindow : Window
    {
        private readonly ObservableCollection<DateTimePoint> _dataPoints;

        public ChartWindow(string title, string keyword, ObservableCollection<DateTimePoint> dataPoints)
        {
            InitializeComponent();

            Title = title;
            _dataPoints = dataPoints;

            // ── 系列：折线图 ──
            var series = new LineSeries<DateTimePoint>
            {
                Values = _dataPoints,
                GeometrySize = 0,
                GeometryStroke = null,
                GeometryFill = null,
                LineSmoothness = 0,
                Stroke = new SolidColorPaint(new SKColor(0x00, 0x78, 0xD4), 3),
                Fill = null,
                AnimationsSpeed = TimeSpan.FromMilliseconds(100),
                EasingFunction = null,
            };

            ChartControl.Series = new ISeries[] { series };

            // ── X 轴：时间 ──
            ChartControl.XAxes = new Axis[]
            {
                new DateTimeAxis(TimeSpan.FromSeconds(1), date => date.ToString("HH:mm:ss"))
                {
                    Name = "时间",
                    NameTextSize = 13,
                    TextSize = 11,
                    LabelsRotation = 45,
                }
            };

            // ── Y 轴：数值 ──
            ChartControl.YAxes = new Axis[]
            {
                new Axis
                {
                    Name = string.IsNullOrEmpty(keyword) ? "解析值" : keyword,
                    NameTextSize = 13,
                    TextSize = 11,
                }
            };

            // 数据变化时更新统计
            _dataPoints.CollectionChanged += OnDataChanged;
            RefreshStats();

            // 阈值框按 Enter 触发应用
            ThresholdBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                    ApplyThreshold();
            };
        }

        // ══════════════════════════════════════════════════════════
        // 统计信息
        // ══════════════════════════════════════════════════════════
        private void OnDataChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.InvokeAsync(RefreshStats, DispatcherPriority.Background);
        }

        private void RefreshStats()
        {
            int count = _dataPoints.Count;
            TbCount.Text = count.ToString();

            if (count == 0)
            {
                TbCurrent.Text = "-";
                TbMax.Text = "-";
                TbMin.Text = "-";
                TbAvg.Text = "-";
                TbStddev.Text = "-";
                return;
            }

            // 当前值
            var last = _dataPoints[count - 1];
            TbCurrent.Text = last.Value?.ToString("F4") ?? "-";

            // 单次遍历 -> 最大/最小/平均/方差
            double sum = 0, sumSq = 0;
            double min = double.MaxValue, max = double.MinValue;
            DateTime minTime = DateTime.MinValue, maxTime = DateTime.MinValue;

            for (int i = 0; i < count; i++)
            {
                double v = _dataPoints[i].Value ?? 0.0;
                sum += v;
                sumSq += v * v;
                if (v < min) { min = v; minTime = _dataPoints[i].DateTime; }
                if (v > max) { max = v; maxTime = _dataPoints[i].DateTime; }
            }

            double avg = sum / count;
            double variance = sumSq / count - avg * avg;
            double stddev = variance > 0.0 ? Math.Sqrt(variance) : 0.0;

            TbMax.Text = $"{max:F4} @ {maxTime:HH:mm:ss}";
            TbMin.Text = $"{min:F4} @ {minTime:HH:mm:ss}";
            TbAvg.Text = avg.ToString("F4");
            TbStddev.Text = stddev.ToString("F4");
        }

        // ══════════════════════════════════════════════════════════
        // 阈值线
        // ══════════════════════════════════════════════════════════
        private void ApplyThreshold_Click(object sender, RoutedEventArgs e)
        {
            ApplyThreshold();
        }

        private void ClearThreshold_Click(object sender, RoutedEventArgs e)
        {
            ThresholdBox.Text = "";
            ChartControl.Sections = null;
        }

        private void ApplyThreshold()
        {
            if (double.TryParse(ThresholdBox.Text.Trim(), out var value))
            {
                ChartControl.Sections = new SectionsCollection
                {
                    new RectangularSection
                    {
                        Yi = value,
                        Yj = value,
                        Stroke = new SolidColorPaint(new SKColor(0xFF, 0x32, 0x32), 2),
                    }
                };
            }
        }
    }
}
