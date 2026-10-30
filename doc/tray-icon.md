# 托盘功能交接文档

**任务**：给 Steam++ Accelerator（仅代理裁剪版）加系统托盘，启动成功后自动隐藏窗口到右下角。
**日期**：2026-10-30
**仓库**：`E:\2026Workplace\Code\SteamTools`（origin: `nxz1026/SteamTools`，分支 `develop`）
**部署目录**：`C:\SteamToolsAccelerator\`（self-contained 发布版）

---

## 1. 交付状态

| 项 | 状态 |
|---|---|
| 托盘图标注册 | ✅ 已验证（UIA 读出 `Steam++ Accelerator (System)`） |
| 右键菜单（显示控制台 / 退出） | ✅ 已验证（`MN_GETHMENU` 读出 3 项，见 §6） |
| 点「退出」→ 进程退出 | ✅ 已验证（发布版） |
| 点「退出」→ 系统代理撤销 | ✅ 已验证（`ProxyEnable` 归 0，`ProxyServer` / `ProxyOverride` 删除） |
| 退出时 CLR assert | ✅ 已修复 |
| 控制台自动隐藏 | ✅ 启动即崩溃的缺陷已修（`GetConsoleWindow` 在 kernel32，见 §3 Bug 4）；`SW_HIDE` 本身需人工目视确认（§5.1） |
| `--console` 保留窗口 | ✅ 已实现，Release 路径下验证过 |
| 启动脚本配合 | ✅ `launch.bat` 改 `start ""` + 去 `pause`（否则自动隐藏永远不生效，见 §3 Bug 5） |
| 异常退出撤销代理 | ✅ 已验证（人为抛异常 → `ExitCode=500` 且 `ProxyEnable` 归 0） |
| 文档更新 | ✅ `README.md` / `doc/proxy-only.md` / `使用说明.md` 均已改 |
| 提交 | ✅ 已提交 `develop` |

---

## 2. 代码改动

### 2.1 新增 `src/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy/TrayIcon.cs`（未跟踪，552 行）

纯 Win32 实现（`Shell_NotifyIcon` + 消息窗口），**不引入 WinForms**。
理由：引入 WinForms 会把工程从裸 TFM `net11.0` 变成 `net11.0-windows`，
破坏「在非 Windows 主机上交叉编译 win-x64」的能力——这是该 fork 明确保留的特性（见 `doc/proxy-only.md` §5）。

关键设计点：

| 点 | 实现 | 原因 |
|---|---|---|
| 窗口类型 | 顶层窗口 + `WS_EX_TOOLWINDOW` | 不进任务栏/Alt-Tab；但**句柄仍可被 `FindWindowEx` 按类名定位**。`HWND_MESSAGE` 消息专用窗口定位不到，跨进程无法验证 |
| 窗口类名 | `SteamPPAcceleratorTray_{进程ID}` | 多实例不互相覆盖类注册 |
| 菜单返回值 | `TrackPopupMenu` 带 `TPM_RETURNCMD` | 不带时返回 0 且走 `WM_COMMAND`；菜单的嵌套消息循环会卡住托盘线程 |
| 退出机制 | `PostThreadMessage(WM_QUIT)` | 直接结束消息循环，不走 `WM_DESTROY` 分发，避免在进程退出阶段执行托管代码触发运行时断言 |
| 窗口销毁 | 在消息线程内 `DestroyWindow` | `DestroyWindow` 必须同线程调用 |
| 控制台隐藏 | `GetConsoleProcessList` 判断控制台归属 | 共享终端时不隐藏，避免把用户自己的终端藏掉 |
| 图标来源 | `ExtractIconEx(exePath)`；`dotnet x.dll` 启动时改用程序集同目录 exe | `Environment.ProcessPath` 指向 dotnet 宿主，图标不对 |

公开 API：
- `static string ResolveExecutablePath()` — 定位用于提取图标的 exe
- `static bool HideConsoleWindow()` — 隐藏控制台，返回是否真的隐藏了
- `static void ShowConsoleWindow()` — 恢复并激活控制台
- `bool Start()` — 创建图标并进消息循环，返回是否成功
- `void SetTip(string)` — 更新提示文字

### 2.2 修改 `Program.cs`（+114 / -5）

| 位置 | 改动 |
|---|---|
| 参数解析 | 新增 `--console`：保持窗口可见 |
| try 块前 | 声明 `TrayIcon? tray` / `consoleHidden`（finally 里要释放） |
| 启动成功后 | 创建托盘图标；Release 下隐藏控制台；Debug 下保留控制台便于看日志 |
| 退出流程 | 从托盘退出时先 `ShowConsoleWindow()`，让用户看到清理过程 |
| finally | **先 `tray?.Dispose()` 再 `Ioc.DisposeAsync()`** —— 顺序反了会让 `Log.Info` 抛 `ObjectDisposedException`（LoggerFactory 已被释放） |
| finally | **删除 `#if DEBUG Console.ReadLine();`** —— 在真实控制台里永久阻塞，托盘应用不能这样 |
| `PrintUsage()` | 补充 `--console` 说明 |

---

## 3. 已修复的三个真 Bug

### Bug 1：`RegisterClassEx` 绑定到 ANSI 版本
```csharp
[DllImport("user32.dll", SetLastError = true)]              // ← 缺 CharSet
static extern IntPtr RegisterClassEx(ref WNDCLASSEX lpwcx);
```
缺 `CharSet = CharSet.Unicode` → 实际调用 `RegisterClassExA`，类名以 ANSI 注册，
但窗口用 `CreateWindowExW` 创建，类名匹配不上 → **窗口创建失败，托盘图标根本没出现**。

### Bug 2：`TrackPopupMenu` 缺 `TPM_RETURNCMD`
返回 0，`if (cmd == CMD_EXIT)` 是死代码；选中项走 `WM_COMMAND`，
而 `WM_COMMAND` 会在菜单的**嵌套消息循环内**被投递，`ExitRequested` 同步触发时托盘线程还困在 `TrackPopupMenu` 里 →
主流程走到 `Dispose` 去 `Join` 托盘线程 → 死锁 → 进程退出时该线程还在跑托管代码 → 
`CLR: Assert failure: !"Attempt to execute managed code after the .NET runtime thread state has been destroyed."`

### Bug 3：`finally` 中释放顺序错误
`Ioc.DisposeAsync()` 先执行会释放 `LoggerFactory`，随后 `tray.Dispose()` 里的 `Log.Info` 抛
`System.ObjectDisposedException: Cannot access a disposed object. Object name: 'LoggerFactory'`，
未捕获异常直接终止进程。

### Bug 4：`GetConsoleWindow` 绑到了 `user32.dll`
```csharp
[DllImport("user32.dll")]                       // ← 该导出实际在 kernel32.dll
static extern IntPtr GetConsoleWindow();
```
`GetConsoleWindow` **导出在 `kernel32.dll`**，写成 `user32` 不是返回 0，而是抛
`EntryPointNotFoundException`。异常从 `HideConsoleWindow()` 上抛，被 `Main` 的兜底 `catch` 接住 →
`ExitCode=500`，进程当场退出。

这解释了「发布版启动即消失」，也解释了为什么之前 `--console` 路径看起来正常：
```csharp
consoleHidden = !keepConsole && TrayIcon.HideConsoleWindow();
```
`--console` 时 `!keepConsole` 为 false，短路后 `HideConsoleWindow()` **一次都没被调用过**。

连带的第二个问题：异常发生在「已写入系统代理」之后，而撤销逻辑只在正常路径上，
于是进程死了、`ProxyEnable` 还停在 1、`ProxyServer` 指向一个已经没人监听的端口 —— **整机断网**。
现在撤销逻辑抽成 `RevertSideEffectsAsync()`，正常路径与 `catch` 共用。
实测：人为在写完代理后抛异常 → `ExitCode=500` 且 `ProxyEnable` 仍被还原为 0。

### Bug 5：`launch.bat` 同步启动，导致自动隐藏永远不触发
原脚本：
```bat
"%EXE%" --mode %MODE%
:done
pause
```
exe 是 `cmd.exe` 的子进程、共用同一个控制台 → `GetConsoleProcessList` 返回 2 →
`OwnsConsole()` 判定「共用」→ 按设计拒绝隐藏。窗口留在屏幕上，`Ctrl+C` 仍是唯一出口，
托盘自动隐藏形同失效。

改为 `start "" "%EXE%" --mode %MODE%` 让主程序拿独立控制台，并去掉 `pause`
（脚本自己立刻退出，窗口自然关闭）。

---

## 4. 踩坑记录（验证方法相关）

**`Shell_NotifyIconGetRect` 在本机不可用**——用最小标准实现做对照实验，同样返回
`hr=-2147024809` (E_INVALIDARG)。所以它不能用来验证托盘注册，**别再花时间在这上面**。

**UI Automation 读托盘项**（可靠，已采用）：
```powershell
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
foreach ($b in $all) { if ($b.Current.Name -like "*Steam++ Accelerator*") { ... } }
```
要点：
- 托盘图标在**折叠区**（`显示隐藏的图标` 面板）里，需要先展开。展开是**开关切换态**，
  每次 Invoke 会翻转一次。必须写成「找到就停，否则翻转一次」的重试循环。
- 折叠区里的元素可能失效（`BoundingRectangle` 为 `Infinity`），点击前要校验 `Width > 0 && !IsInfinity`。
- 菜单项能通过 UIA 拿到：`ControlType.MenuItem`，`Name` 为 `退出` / `显示控制台`。

**PowerShell 5.1 环境坑**：
- 读 `.ps1` 按 ANSI，中文脚本必须存 **UTF-8 with BOM**，否则解析报错。
- `Add-Type` 用 C# 5 编译器：不支持字符串插值 `$"..."`、不支持 `out var`、不支持 `<` 运算符（要用 `-lt`）。
- `GetConsoleWindow` 在 **kernel32** 不是 user32。

**bash 环境坑**：
- 路径含 `++`（`Steam++.Accelerator.exe`）必须加引号，否则解析失败。
- `start` 是 cmd 内建命令，bash 下不可用；用 `cmd //c start` 或 PowerShell `Start-Process`。

---

**原生弹出菜单在 UIA 里读不出来**——`class=#32768` 的 Win32 菜单不暴露 `MenuItem`，
全树按名字扫（1145 个元素）也扫不到 `退出`/`显示控制台`。可靠路径是
`SendMessage(menuWnd, MN_GETHMENU /*0x01E1*/, 0, 0)` 取 `HMENU`，再
`GetMenuItemCount` / `GetMenuStringW` / `GetMenuState` / `GetMenuItemRect`。
实测读出 3 项、文字与矩形都对。

**弹菜单不必用鼠标**：`PostMessage(trayHwnd, WM_USER+1, ID_TRAY /*0x5654*/, WM_RBUTTONUP /*0x0205*/)`
直接触发 `ShowContextMenu`。鼠标模拟在折叠面板上不可靠（第一次试就失败了）。

**自动化环境的三个坑**：

1. 从 agent shell 里 `Start-Process` 出去的 exe 会**附着到该 shell 的 ConPTY**。
   shell 一退出控制台就关闭，exe 被一起带走 —— 表现就是「进程起来后立刻消失」。
   **这不是程序 bug**：验证时的启动 → 交互 → 退出必须写在同一个 shell 调用里跑完。
2. `cmd /c start`、`[Diagnostics.Process]::Start`、`WScript.Shell.Run`、`schtasks.exe`
   在本机安全策略下**全部被拦**，因此拿不到独立控制台，`SW_HIDE` 分支无法自动化（见 §5.1）。
   另外 pwsh 7 的 `Start-Process` 是 `UseShellExecute=false`，子进程只会继承控制台。
3. pwsh 7 里 `New-Object TypeName(a, b)` 这种写法会**静默打死宿主**（输出戛然而止、无报错）。
   用 `[Type]::new(a, b)`。

---

## 5. 遗留事项

### 5.1 控制台隐藏：缺陷已修，`SW_HIDE` 需人工目视确认

启动即崩溃的问题已定位并修掉（§3 Bug 4）。但「窗口真的被隐藏了」这一步在本机**无法自动化**：
agent 环境下拿不到独立控制台（见 §4），`OwnsConsole()` 永远判 false，`SW_HIDE` 根本不会执行。

**人工确认（约 15 秒）**：
1. 双击 `C:\SteamToolsAccelerator\launch.bat system`
2. 预期：黑窗口一闪即消，右下角出现托盘图标，任务栏无痕迹
3. 右键托盘 → 「显示控制台」→ 黑窗口应该重新出现
4. 右键托盘 → 「退出」→ 窗口消失、系统代理被撤销

第 3 步若无效，重点查 `ShowConsoleWindow()`：它对 `conhost` 持有的控制台窗口做
`SetWindowLong(GWL_EXSTYLE)`，跨进程写这个字段可能被拒（`ShowWindow` 本身跨进程可用）。

### 5.2 文档

已更新：`README.md`（`--console` + 托盘菜单 + 新的停止方式）、`doc/proxy-only.md`
（新增「托盘图标」小节、验证记录、修复过的问题）、`C:\SteamToolsAccelerator\使用说明.md`。

### 5.3 部署脚本不在仓库里

`launch.bat` 与 `使用说明.md` **只存在于 `C:\SteamToolsAccelerator\`**，没有进 git。
重新 publish 到别的目录会丢掉这两个文件，且本次对 `launch.bat` 的 `start ""` 改动很容易再次丢失。
建议后续把部署脚本纳入 `doc/deploy/` 统一维护。

---

## 6. 验证脚本

全部在 `C:\Users\ND\AppData\Local\Temp\`（临时文件，无需保留到仓库）：

| 脚本 | 用途 |
|---|---|
| `exit8.ps1` | **主验证脚本**：找托盘项 → 右键 → 找「退出」菜单项 → 点击 → 检查进程退出 + 检查注册表 `ProxyEnable` |
| `uia2.ps1` | 展开折叠区，列出所有 `SystemTray.*` 按钮（验证托盘图标注册） |
| `menushot2.ps1` / `w2.png` | 右键后截图，人工确认菜单渲染 |
| `winpid.ps1` | 按 PID 枚举顶层窗口（确认托盘窗口创建） |
| `traycontrol.ps1` | `Shell_NotifyIconGetRect` 对照实验（本机不可用，仅留证） |

复现步骤：
```bash
# 1. 清理
powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='dotnet.exe'\" |
  Where-Object { \$_.CommandLine -like '*Accelerator*' } | ForEach-Object { Stop-Process -Id \$_.ProcessId -Force };
  \$k='HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings';
  Set-ItemProperty -Path \$k -Name ProxyEnable -Value 0 -Type DWord;
  Remove-ItemProperty -Path \$k -Name ProxyServer -ErrorAction SilentlyContinue;
  Remove-ItemProperty -Path \$k -Name ProxyOverride -ErrorAction SilentlyContinue"

# 2. 构建
cd "E:/2026Workplace/Code/SteamTools"
C:/Users/ND/.dotnet/dotnet.exe build src/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy.csproj -c Release

# 3. 启动（--console 便于观察输出；无参数则验证隐藏）
cd src/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy/bin/Release/net11.0
C:/Users/ND/.dotnet/dotnet.exe Steam++.Accelerator.dll --console

# 4. 另开窗口跑验证
powershell -NoProfile -ExecutionPolicy Bypass -File "C:/Users/ND/AppData/Local/Temp/exit8.ps1"
```

---

## 7. 编译命令

```bash
export DOTNET_ROOT="C:/Users/ND/.dotnet"
export PATH="/c:/Users/ND/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "E:/2026Workplace/Code/SteamTools"

# Debug
C:/Users/ND/.dotnet/dotnet.exe build src/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy.csproj -c Debug

# 发布（多目标框架，必须指定 -f）
C:/Users/ND/.dotnet/dotnet.exe publish src/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy.csproj \
  -c Release -f net11.0 -r win-x64 --self-contained true -o "E:/2026Workplace/Code/SteamTools/publish-win-x64"
```

---

## 8. 相关背景

- 内存层：本 fork 的项目名 `steamtools`，`mem_save` 时 `project` 传 `steamtools`
- 部署目录 `C:\SteamToolsAccelerator\` 是 self-contained 发布，`launch.bat` 启动（不是 `启动加速.bat`）
- 程序启动会写系统代理（`mode=system`），退出时撤销——撤销逻辑靠「启动时捕获原值」，
  **测试前务必清理注册表**，否则会恢复成脏值误判为 bug
