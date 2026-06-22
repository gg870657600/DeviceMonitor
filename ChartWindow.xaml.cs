using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using Microsoft.Win32;
using OfficeOpenXml;
using SkiaSharp;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace DeviceMonitor
{
    public partial class ChartWindow : Window
    {
        private readonly ObservableCollection<DateTimePoint> _dataPoints;
        private readonly string _yAxisName;

        public ChartWindow(string title, string keyword, ObservableCollection<DateTimePoint> dataPoints)
        {
            InitializeComponent();

            Title = title;
            _dataPoints = dataPoints;
            _yAxisName = string.IsNullOrEmpty(keyword) ? "解析值" : keyword;

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
                    Name = _yAxisName,
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

        // ══════════════════════════════════════════════════════════
        // 导出 Excel（EPPlus，后台线程避免 UI 卡死）
        // ══════════════════════════════════════════════════════════
        private async void ExportExcel_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. 选保存路径
                var dialog = new SaveFileDialog
                {
                    FileName = $"{SanitizeFileName(Title)}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx",
                    Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
                    DefaultExt = ".xlsx",
                    Title = "导出到 Excel"
                };
                if (dialog.ShowDialog(this) != true) return;

                string filePath = dialog.FileName;

                // 2. 在 UI 线程抓取数据快照（线程安全）
                var snapshot = _dataPoints
                    .Select(dp => (T: dp.DateTime, V: dp.Value ?? 0.0))
                    .ToArray();

                if (snapshot.Length == 0)
                {
                    MessageBox.Show(this, "当前没有数据可以导出。", "提示",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string windowTitle = Title;
                string yAxisName = string.IsNullOrWhiteSpace(_yAxisName) ? "数值" : _yAxisName;

                // 禁用按钮防重复点击
                ExportExcelBtn.IsEnabled = false;

                // 3. 后台线程生成 Excel 文件
                string? errorMsg = null;
                await Task.Run(() =>
                {
                    try
                    {
                        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

                        using var package = new ExcelPackage();

                        // ── Sheet1: 数据 ──
                        var wsData = package.Workbook.Worksheets.Add("数据");
                        wsData.Cells[1, 1].Value = "时间";
                        wsData.Cells[1, 2].Value = yAxisName;
                        using (var header = wsData.Cells[1, 1, 1, 2])
                        {
                            header.Style.Font.Bold = true;
                            header.Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;
                        }

                        for (int i = 0; i < snapshot.Length; i++)
                        {
                            wsData.Cells[i + 2, 1].Value = snapshot[i].T;
                            wsData.Cells[i + 2, 1].Style.Numberformat.Format = "yyyy-MM-dd HH:mm:ss";
                            wsData.Cells[i + 2, 2].Value = snapshot[i].V;
                        }

                        wsData.Column(1).Width = 22;
                        wsData.Column(2).AutoFit();

                        // ── Sheet2: 统计 ──
                        var wsStats = package.Workbook.Worksheets.Add("统计");
                        wsStats.Cells["A1"].Value = "项";
                        wsStats.Cells["B1"].Value = "值";
                        using (var header = wsStats.Cells[1, 1, 1, 2])
                        {
                            header.Style.Font.Bold = true;
                        }

                        double currentVal = snapshot.Last().V;
                        double maxVal = snapshot.Max(dp => dp.V);
                        double minVal = snapshot.Min(dp => dp.V);
                        double avgVal = snapshot.Average(dp => dp.V);
                        double stdVal = Math.Sqrt(snapshot.Select(dp => (dp.V - avgVal) * (dp.V - avgVal)).Average());
                        int countVal = snapshot.Length;

                        wsStats.Cells["A2"].Value = "当前值"; wsStats.Cells["B2"].Value = currentVal;
                        wsStats.Cells["A3"].Value = "最大值"; wsStats.Cells["B3"].Value = maxVal;
                        wsStats.Cells["A4"].Value = "最小值"; wsStats.Cells["B4"].Value = minVal;
                        wsStats.Cells["A5"].Value = "平均值"; wsStats.Cells["B5"].Value = avgVal;
                        wsStats.Cells["A6"].Value = "标准差"; wsStats.Cells["B6"].Value = stdVal;
                        wsStats.Cells["A7"].Value = "数据点"; wsStats.Cells["B7"].Value = countVal;

                        wsStats.Cells["B2:B6"].Style.Numberformat.Format = "0.0000";
                        wsStats.Column(1).AutoFit();
                        wsStats.Column(2).AutoFit();

                        // ── Sheet3: 图表 ──
                        var wsChart = package.Workbook.Worksheets.Add("图表");
                        int lastRow = snapshot.Length + 1;

                        // 复制数据到图表 Sheet
                        wsChart.Cells[1, 1].Value = "时间";
                        wsChart.Cells[1, 2].Value = yAxisName;
                        using (var header = wsChart.Cells[1, 1, 1, 2])
                        {
                            header.Style.Font.Bold = true;
                        }
                        for (int i = 0; i < snapshot.Length; i++)
                        {
                            wsChart.Cells[i + 2, 1].Value = snapshot[i].T;
                            wsChart.Cells[i + 2, 1].Style.Numberformat.Format = "yyyy-MM-dd HH:mm:ss";
                            wsChart.Cells[i + 2, 2].Value = snapshot[i].V;
                        }
                        wsChart.Column(1).Width = 22;
                        wsChart.Column(2).AutoFit();

                        // 创建散点折线图（X=时间，Y=数值）
                        var chart = wsChart.Drawings.AddChart(
                            "DataChart",
                            OfficeOpenXml.Drawing.Chart.eChartType.XYScatterSmooth);
                        var series = chart.Series.Add(
                            $"图表!$B$2:$B${lastRow}",
                            $"图表!$A$2:$A${lastRow}");
                        series.Header = yAxisName;
                        chart.SetPosition(3, 0, 3, 0);
                        chart.SetSize(900, 500);
                        chart.Title.Text = windowTitle;

                        // X 轴时间格式
                        chart.XAxis.Format = "HH:mm:ss";
                        chart.YAxis.Format = "0.00";

                        // 保存文件
                        package.SaveAs(new FileInfo(filePath));
                    }
                    catch (Exception ex)
                    {
                        errorMsg = ex.Message;
                    }
                });

                // 4. 回到 UI 线程，弹出结果
                if (errorMsg != null)
                {
                    MessageBox.Show(this, $"导出失败：{errorMsg}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    var result = MessageBox.Show(this,
                        $"已导出到：\n{filePath}\n\n是否立即打开？",
                        "导出成功", MessageBoxButton.YesNo, MessageBoxImage.Information);

                    if (result == MessageBoxResult.Yes)
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(this, $"无法自动打开文件：{ex.Message}\n请手动打开：\n{filePath}",
                                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"发生错误：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ExportExcelBtn.IsEnabled = true;
            }
        }

        // 去掉文件名中不允许的字符
        private static string SanitizeFileName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "导出数据";
            foreach (var c in Path.GetInvalidFileNameChars())
                raw = raw.Replace(c, '_');
            return raw;
        }
    }
}
