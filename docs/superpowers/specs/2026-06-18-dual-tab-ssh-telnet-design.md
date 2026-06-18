# 双 Tab SSH 会话工具（串口 + Telnet）— 设计文档

## Overview

在现有 WPF 工具中加入第二个 `TabItem`（Telnet 会话），与现有"串口" Tab **对称**但**独立**。两个 Tab 各自维护自己的 SSH 连接、循环逻辑、异常计数、设置和文件日志。Telnet Tab 通过 SSH 通道内嵌套 telnet 子会话（`telnet 0 2323`）登录到设备的 NE_name shell，循环执行用户自定义命令并解析返回。

## Goals & Non-Goals

### Goals
- 主窗口提供 `TabControl` 切换「串口会话」和「Telnet 会话」两个 Tab
- 两个 Tab 完全独立：各自 SSH 客户端、各自设置、各自日志、各自异常计数
- Telnet Tab 支持 `telnet 0 2323` 嵌套子会话登录
- Telnet Tab 循环结束 / 取消 / 异常时直接关闭 SSH 连接
- 保持现有串口 Tab 行为不变
- 不引入新的 NuGet 包

### Non-Goals
- 不做"独立 TCP 直连 2323 端口"（SSH 通道外开新连接）
- 不做 SSH 公钥免密登录
- 不做多设备并行（只支持两 Tab 各连一台设备）
- 不做 Tab 拖拽 / 关闭
- 不做设置持久化（每次启动手工填）

## Architecture

### 命名空间
- 现有控件名加 `Serial` 前缀（如 `SerialIpTextBox`、`SerialSshClient`）
- 新增 `Telnet` 前缀同名控件（`TelnetIpTextBox`、`TelnetSshClient`）

### 类结构
- `MainWindow.xaml`：双 Tab 布局
- `MainWindow.xaml.cs`：
  - 现有字段复制为 `Serial*` / `Telnet*` 两套
  - 公共方法（不依赖具体 Tab）：
    - `ConnectSshAsync(SshClient, string ip, int port, string user, string pwd)` → bool
    - `ReadShellClean(ShellStream, int readMaxWaitMs)` → string
    - `WriteLogToFile(string fileName, string content)` → void
    - `ParseResult(string raw, string keyword, int position)` → (string currentValue, bool isOk)
  - 串口 Tab 特有：
    - `SerialPreLoop(ShellStream)` → 写 `bsp redir_off` + `bsp redir_on` + 等 idle
    - `SerialPostLoop(ShellStream)` → 写 `bsp redir_off`
  - Telnet Tab 特有：
    - `TelnetLogin(ShellStream)` → 写 `telnet 0 2323` + 200ms + `login:root` + `Changeme_123`
    - Telnet 退出：直接 `client.Disconnect()`（不发送 exit）
  - 事件处理器：`SerialSend_Click` / `SerialStop_Click` / `TelnetSend_Click` / `TelnetStop_Click`
  - 后台任务：`SerialRunTask` / `TelnetRunTask`

### 数据流
1. 用户点「连接」 → `ConnectSshAsync` 建立 SSH 客户端 → 创建 `ShellStream`
2. 用户点「开始」 → 启动 `*RunTask`
3. `*RunTask`：
   - 调用 Tab 特有的 `*PreLoop`（串口：redir；Telnet：登录）
   - 进入主循环（while 条件 + 异常计数）
   - 每次循环：写命令 → 等待 → 读取 → 解析 → 写日志 → 写文件
   - 退出条件：达到总时间 / 取消 / 5 次异常
   - `finally`：调用 Tab 特有的清理（串口：redir_off；Telnet：直接断 SSH）
4. 用户点「停止」 → `cancellationTokenSource.Cancel()`

## Design Details

### 1. UI 布局
```
┌─ TabControl ─────────────────────────────────────────────┐
│ ┌ 串口会话 ┐ ┌ Telnet 会话 ┐                                │
│ ├──────────┴──────────────────────────────────────────┤ │
│ │ 【SSH 连接区】                                       │ │
│ │   IP: [____] 端口: [__] 用户: [____] 密码: [____]    │ │
│ │   [ 连接 ]  [ 断开 ]                                  │ │
│ │                                                     │ │
│ │ 【设置区】                                           │ │
│ │   间隔(秒): [_]  总时间(分): [_]                     │ │
│ │   命令: [________________________]                   │ │
│ │   关键字: [____]  位置: [_]                          │ │
│ │                                                     │ │
│ │ 【双栏】                                             │ │
│ │   ┌ 操作日志 ───────┐ ┌ 解析结果 ───────┐             │ │
│ │   │  ListView        │ │  ListView        │             │ │
│ │   └──────────────────┘ └──────────────────┘             │ │
│ │                                                     │ │
│ │ [ 开始 ]  [ 停止 ]   状态: ●已连接   执行: 0          │ │
│ └─────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────┘
```

### 2. 串口 Tab 主循环（保留现有逻辑）
```csharp
private async Task SerialRunTask(CancellationToken ct)
{
    var shell = _serialShell;
    await SerialPreLoop(shell);  // redir_off → redir_on → 等 idle
    var endTime = DateTime.Now.AddMinutes(SerialTotalMinutes);
    int currentCount = 1;
    int exceptionCount = 0;
    while (DateTime.Now < endTime && !ct.IsCancellationRequested)
    {
        // 发命令 → 等 → 读 → 解析 → 写日志
        ...
        if (parseFailed) { exceptionCount++; if (exceptionCount >= 5) break; }
        else exceptionCount = 0;
        currentCount++;
        await Task.Delay(SerialIntervalSec * 1000, ct);
    }
    if (ct.IsCancellationRequested || exceptionCount >= 5)
        await SerialPostLoop(shell);  // 最后一次 redir_off
    _serialClient.Disconnect();
}
```

### 3. Telnet Tab 主循环
```csharp
private async Task TelnetRunTask(CancellationToken ct)
{
    var shell = _telnetShell;
    var loginOk = await TelnetLogin(shell);
    if (!loginOk) { /* 错误提示，断 SSH */ return; }
    var endTime = DateTime.Now.AddMinutes(TelnetTotalMinutes);
    int currentCount = 1;
    int exceptionCount = 0;
    while (DateTime.Now < endTime && !ct.IsCancellationRequested)
    {
        // 发命令 → 等 → 读 → 解析 → 写日志
        ...
        if (parseFailed) { exceptionCount++; if (exceptionCount >= 5) break; }
        else exceptionCount = 0;
        currentCount++;
        await Task.Delay(TelnetIntervalSec * 1000, ct);
    }
    // 退出：直接关闭 SSH（不发送 exit）
    _telnetClient.Disconnect();
}
```

### 4. Telnet 登录流程
```csharp
private async Task<bool> TelnetLogin(ShellStream shell)
{
    AppendLog("Telnet 登录开始：telnet 0 2323");
    shell.WriteLine("telnet 0 2323");
    await Task.Delay(200);  // 固定 200ms
    AppendLog("发送登录凭据：login:root / Changeme_123");
    shell.WriteLine("login:root");
    shell.WriteLine("Changeme_123");
    // 轮询等待 [root@NE_name] # 提示符
    var sw = Stopwatch.StartNew();
    var sb = new StringBuilder();
    while (sw.ElapsedMilliseconds < 5000)
    {
        var data = ReadShellData(shell, 200);
        sb.Append(data);
        if (sb.ToString().Contains("[root@") && sb.ToString().Contains("]#"))
        {
            AppendLog($"Telnet 登录成功（耗时 {sw.ElapsedMilliseconds} ms）");
            return true;
        }
        await Task.Delay(100);
    }
    AppendLog("Telnet 登录超时（5s 未检测到 [root@...# 提示符）");
    return false;
}
```

### 5. 串口 Pre/Post 循环（抽取现有逻辑）
```csharp
private async Task SerialPreLoop(ShellStream shell)
{
    AppendLog("发送 bsp redir_off");
    shell.WriteLine("bsp redir_off");
    await Task.Delay(500);
    ReadShellClean(shell, 2000);
    AppendLog("发送 bsp redir_on");
    shell.WriteLine("bsp redir_on");
    await Task.Delay(500);
    ReadShellClean(shell, 2000);
    AppendLog("串口 PreLoop 完成");
}

private async Task SerialPostLoop(ShellStream shell)
{
    AppendLog("发送 bsp redir_off（清理）");
    shell.WriteLine("bsp redir_off");
    await Task.Delay(500);
    ReadShellClean(shell, 2000);
}
```

### 6. 公共方法
```csharp
// SSH 连接
private async Task<bool> ConnectSshAsync(
    SshClient client, string ip, int port, string user, string pwd,
    Action<string> appendLog, TextBlock statusBlock, Button connectBtn, Button disconnectBtn)
{
    try
    {
        client.Connect();
        var shell = client.CreateShellStream("xterm", 80, 24, 800, 600, 65536);
        await Task.Delay(300);
        var init = ReadShellClean(shell, 1000);
        appendLog($"SSH 已连接，初始返回: {init.Length} 字节");
        statusBlock.Text = "● 已连接";
        connectBtn.IsEnabled = false;
        disconnectBtn.IsEnabled = true;
        return true;
    }
    catch (Exception ex)
    {
        appendLog($"SSH 连接失败: {ex.Message}");
        statusBlock.Text = "● 未连接";
        return false;
    }
}

// 解析
private (string currentValue, bool isOk) ParseResult(string raw, string keyword, int position)
{
    // 现有逻辑保持不变
}
```

### 7. 文件日志
- 串口 Tab：`SensorPosLog.txt`（保留）
- Telnet Tab：`TelnetLog.txt`（新增）
- 写入路径：`AppDomain.CurrentDomain.BaseDirectory`

### 8. 5 次异常逻辑
- 两个 Tab **独立计数**（各自的 `_serialExceptionCount` / `_telnetExceptionCount`）
- 触发条件：解析失败 或 解析值 == "0.00"
- 触发后立即退出循环
- 串口 Tab：触发后仍执行 `bsp redir_off`（用户已确认）
- Telnet Tab：触发后直接关闭 SSH

### 9. 取消/清理
- 用户点「停止」：
  - 设置 `cancellationTokenSource.Cancel()`
  - 串口 Tab：`finally` 块执行 `SerialPostLoop`（redir_off）+ `client.Disconnect()`
  - Telnet Tab：`finally` 块直接 `client.Disconnect()`（不发送 exit）
- 用户点「断开」：
  - 如果正在跑：先取消，再断
  - 如果空闲：直接 `client.Disconnect()`

## Implementation Plan

### 阶段 1：UI 重构（保留串口功能）
1. `MainWindow.xaml`：将现有顶层 `Grid` 改为 `TabControl`
2. 把现有所有控件移入 `TabItem Header="串口会话"`，重命名为 `Serial*` 前缀
3. 保留 SSH 连接、设置、双栏、按钮、状态

### 阶段 2：复制 Telnet Tab UI
1. 在 `TabItem Header="Telnet 会话"` 中复制所有 UI
2. 重命名为 `Telnet*` 前缀
3. 日志 / 解析结果 ListView 独立

### 阶段 3：抽取公共方法
1. 把现有 SSH 连接、ReadShellClean、WriteLogToFile、ParseResult 改为通用
2. 接受参数化（不依赖具体控件名）

### 阶段 4：实现 Telnet 特有逻辑
1. `TelnetLogin` 方法
2. `TelnetRunTask` 主循环
3. `TelnetSend_Click` / `TelnetStop_Click` 事件
4. 文件日志切换到 `TelnetLog.txt`

### 阶段 5：测试 & 调整
1. 串口 Tab：完整跑一遍，确认行为不变
2. Telnet Tab：填 SSH 信息 → 点连接 → 点开始 → 确认 `telnet 0 2323` + 登录 + 循环
3. 取消 / 异常：确认清理逻辑

## Open Questions

无。全部已与用户确认。

## Success Criteria

1. 串口 Tab 行为与当前**完全一致**（无回归）
2. Telnet Tab 能成功 `telnet 0 2323` + 登录 + 循环
3. 两个 Tab **可同时**连接 / 跑（互不干扰）
4. Telnet Tab 退出 / 异常 / 取消时 SSH 连接被关闭
5. 5 次异常停循环逻辑在 Telnet Tab 也生效
6. 文件日志按 Tab 分文件（`SensorPosLog.txt` / `TelnetLog.txt`）
7. 编译 0 错误 0 警告
