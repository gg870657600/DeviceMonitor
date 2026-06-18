# 设备 SSH 命令工具

基于 WPF (.NET 8) 的桌面工具，通过 SSH 远程登录调制解调器（或其他 Linux 嵌入式设备），定时循环执行用户自定义的 shell 命令，并解析命令的返回结果，统计正常/异常次数。

主窗口提供 **两个 Tab**：

- **串口会话**：SSH 连入设备后用 `bsp redir_on` / `bsp redir_off` 把命令重定向到本地串口外设，循环执行用户命令。
- **Telnet 会话**：在同一 SSH 通道内 `telnet 0 2323` 嵌套登录到设备的 NE_name 子 shell，循环执行用户命令。

两个 Tab **完全独立**：各自的 SSH 连接、设置、日志、异常计数、文件日志，互不干扰。

适用场景：长时间稳定性测试、定时数据采集、设备状态监测等。

## 核心功能

1. 通过 SSH 协议连接远程设备（用户名/密码认证）
2. **双 Tab** 架构：串口会话 / Telnet 会话
3. 在 SSH 会话内定时循环执行用户自定义的 shell 命令
4. 通过「关键字 + 位置」方式从命令返回结果中解析目标字段
5. 实时统计解析值的正常（绿色）/ 异常（红色）次数
6. 连续 5 次异常自动终止任务
7. **每轮解析值绘制实时折线图**（独立窗口，50 万点不卡顿）
8. 详细日志输出（连接/发送/原始返回/解析过程/判定）
9. 日志同时写入程序目录下的文件：
   - 串口 Tab → `SensorPosLog.txt`
   - Telnet Tab → `TelnetLog.txt`
10. 串口 Tab 任何退出路径（任务结束/取消/关闭窗口）都会执行收尾的 `bsp redir_off`

## 关键命令说明

### 串口 Tab（bsp redir_on / bsp redir_off）
- `bsp redir_on`：把网口（SSH）的输入输出重定向到设备的本地串口
- `bsp redir_off`：关闭上述重定向，恢复正常的 SSH 控制台交互

```
bsp redir_off   (确保干净起点)
  → bsp redir_on (重定向到串口外设)
  → 循环执行用户自定义命令（如：bsp cmd st sensor_pos）
  → bsp redir_off (收尾，恢复正常 SSH 控制台)
```

### Telnet Tab（telnet 0 2323 + login）
```
telnet 0 2323
  → 等待 200ms
  → login:root
  → Changeme_123
  → 轮询等待 [root@NE_name]# 提示符（5s 超时）
  → 循环执行用户自定义命令
  → 退出：直接 Disconnect（不发送 exit）
```

## 前置条件

1. PC 能 ping 通目标设备
2. 目标设备已开启 SSH 服务（默认 22 端口）
3. 已知 SSH 登录的用户名和密码
4. .NET 8 桌面运行时

## 使用说明

1. 启动程序，顶部 Tab 切换「串口会话」 / 「Telnet 会话」
2. 在当前 Tab 中填入 SSH 连接信息（IP/端口/用户名/密码），点「连接」
3. 填入运行参数（间隔秒、总时间分钟）、命令、关键字、位置
4. 点「开始」启动循环任务，按钮变为「停止」
5. 点「开始」按钮右侧的**图表按钮**（柱条+折线 icon）→ 弹出独立的实时折线图窗口
6. 任务结束情况（每个 Tab 独立）：
   - 总时间到 → 正常结束
   - 手动停止 → 弹出取消提示
   - 连续 5 次异常 → 自动终止
   - 关闭窗口 → 同步执行收尾
   - 串口 Tab 任何情况下都会自动执行收尾的 `bsp redir_off` 还原设备状态

## 解析逻辑说明

```
1. 把命令的原始返回按行拆分
2. 找到第一行包含「关键字」的行
3. 把该行按空格/Tab 拆分
4. 取第「位置」段作为解析值
5. 解析失败 或 解析值 == "0.00" 判定为异常，否则正常
```

**示例**：

```
原始返回：
  current pos = 12.34.
关键字 = "current pos"   位置 = 4
→ 解析值 = 12.34   → 正常
```

```
原始返回：(空)
→ 解析失败   → 异常
```

## 实时趋势图

- 点击「开始」右侧的图表按钮 → 弹出独立窗口（900×500）
- 横轴：执行时间（HH:mm:ss）
- 纵轴：解析值（文本自动映射为数字：disable/off/失败 → 0/-1 等）
- 支持鼠标滚轮缩放 + 拖拽平移
- 50 万数据点流畅渲染（SkiaSharp GPU）
- 两个 Tab 数据独立，窗口可多次开关
- 关闭窗口 ≠ 停止收集，只关闭视图

技术：LiveCharts2（`LiveChartsCore.SkiaSharpView.WPF`），通过 `ObservableCollection<ChartPoint>` + 后台 500ms Flush 增量更新。

## 项目结构

```
device_uart/
├─ MainWindow.xaml        # UI 布局（双 Tab）
├─ MainWindow.xaml.cs     # SSH 主逻辑、解析、日志、图表数据收集
├─ ChartWindow.xaml       # 实时折线图窗口
├─ ChartWindow.xaml.cs    # 图表窗口逻辑
├─ App.xaml / App.xaml.cs # 应用入口
├─ AssemblyInfo.cs
├─ DeviceMonitor.csproj       # .NET 8 WPF 项目
├─ DeviceMonitor.sln
├─ README.md              # 本文件
├─ 图表.svg               # iconfont 下载的图表按钮图标
├─ .gitignore
├─ docs/
│  ├─ design/
│  │  └─ 2026-06-17-device-ssh-tool-design.md
│  └─ superpowers/
│     └─ specs/
│        ├─ 2026-06-18-dual-tab-ssh-telnet-design.md
│        └─ 2026-06-18-chart-design.md
└─ Properties/
   └─ PublishProfiles/
```

## 技术栈

- .NET 8 / WPF
- SSH.NET (Renci.SshNet 2025.1.0)
- LiveCharts2 + SkiaSharpView.WPF（实时趋势图）
- OpenTK / OpenTK.GLWpfControl（LiveCharts2 渲染依赖）
- SkiaSharp.Views.WPF

## 注意事项

- SSH 凭据在每个 Tab 的「密码」输入框中填写（不再硬编码）
- Telnet Tab 登录凭据 `login:root` / `Changeme_123` 硬编码在 `TelnetLogin` 方法中
- 程序只在 Windows 上运行（WPF 应用）
- 长任务期间请勿关闭 SSH 客户端或目标设备
- 如需调试解析问题，查看「操作日志」中的"原始返回"和"解析过程"段落
- 图表按钮图标从 iconfont（阿里巴巴矢量图标库）下载，`MainWindow.xaml` 中以 `Canvas + Path` 内联
