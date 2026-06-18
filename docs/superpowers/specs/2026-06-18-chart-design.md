# 实时解析值折线图 — 设计文档

## 概述

在下发按钮右侧新增图表按钮，点击后弹出独立窗口，展示每轮解析值的实时折线图。支持 50 万数据点不卡顿，横轴为时间，与日志时间对应。

## 目标与非目标

**目标**
- 每个 Tab 独立图表（串口一个、Telnet 一个）
- 横轴为执行时间（DateTime），与日志条目时间一致
- 每解析一个值立即追加到图表
- 支持 50 万+ 数据点流畅交互（缩放、平移）
- 文本值自动映射为数字（如 disable=0, enable=1）

**非目标**
- 不保存历史图表数据（关闭窗口即释放）
- 不做多序列对比（每个图表只有一条线）

## 技术选型

- **LiveCharts2**（`LiveChartsCore.SkiaSharpView.WPF`）
  - SkiaSharp GPU 渲染，大数据量性能好
  - 内置 `DateTimeAxis`，支持时间横轴
  - 自动采样降噪（`MaxToKeep` 控制）
  - 鼠标滚轮缩放 + 拖拽平移

## 架构

```
AppendResult() / ExecuteOnce()
   │ 解析出 parsed value (string)
   ▼
ChartDataCollector (每个 Tab 一个)
   │  double.TryParse → double?
   │  失败则查文本映射表 → double
   │  构造 ChartPoint(Time, Value) push 到 ConcurrentQueue
   ▼
后台 Flush Task (每 500ms)
   │  ObservableCollection<ChartPoint> → CartesianChart
   │  LiveCharts2 自动增量更新 UI
   ▼
ChartWindow (独立 Window)
   ┌──────────────────────────┐
   │  CartesianChart          │
   │  X: DateTimeAxis         │
   │  Y: LinearAxis           │
   │  鼠标缩放 + 平移         │
   └──────────────────────────┘
```

## 数据模型

```csharp
public class ChartPoint
{
    public DateTime Time { get; set; }
    public double Value { get; set; }
}
```

文本→数字映射表（可扩展）：
| 文本 | 映射值 |
|------|--------|
| disable | 0 |
| enable | 1 |
| off | 0 |
| on | 1 |
| 失败 | -1 |

## 文件变更

| 文件 | 操作 | 说明 |
|------|------|------|
| `DeviceMonitor.csproj` | 修改 | 添加 LiveCharts2 NuGet 引用 |
| `ChartWindow.xaml` | 新增 | 图表窗口布局 |
| `ChartWindow.xaml.cs` | 新增 | 图表窗口逻辑（接收 ObservableCollection） |
| `MainWindow.xaml` | 修改 | 两个 Tab 各添加"图表"按钮 |
| `MainWindow.xaml.cs` | 修改 | 数据收集逻辑、窗口打开事件 |

## 数据流

1. `ExecuteOnce` 中解析出 `parsed` 字符串
2. `ChartDataCollector.Add(DateTime.Now, parsed)` 转换为 `ChartPoint`
3. 后台 Flush Task 将新点追加到 `ObservableCollection<ChartPoint>`
4. LiveCharts2 订阅到该 `ObservableCollection`，UI 自动更新
5. 窗口关闭时取消 Flush Task

## 性能

- **LiveCharts2 采样降噪**：`MaxToKeep = 500000`，超过时丢弃最旧点
- **后台线程追加**：Flush 在 `Task.Run` 中，UI 批处理更新
- **SkiaSharp 渲染**：GPU 加速，只绘可见区域
- **清理时机**：任务重新下发时清空数据（`Clear()` + 重置索引）

## UI 交互

- 图表按钮图标：从 iconfont（阿里巴巴矢量图标库）下载的"图表"图标，viewBox 1280×1024，含 3 个 path（4 根灰色柱条 #4A4A4A + L 形灰色坐标轴 #4A4A4A + 顶部青色折线 #10BCAE），以 `Canvas + Path` 内联到按钮 `Viewbox` 中（22×18）。背景透明、无边框，点击热区 34×30。
- 点击图表按钮 → 弹出 `ChartWindow`，大小 900×500
- 窗口标题：`串口-解析值趋势图` / `Telnet-解析值趋势图`
- 横轴：时间格式化 "HH:mm:ss"
- 纵轴：自动范围
- 鼠标滚轮缩放 + 拖拽平移（LiveCharts2 内置）
- 关闭窗口 = 只是关闭视图，数据继续收集
- 再次打开窗口 = 显示已有的全部数据

## 成功标准

1. 50 万数据点流畅渲染（FPS ≥ 30）
2. 每轮解析后 1 秒内图表更新
3. 缩放/平移无卡顿
4. 串口和 Telnet 数据独立不串
5. 窗口可多次打开/关闭
