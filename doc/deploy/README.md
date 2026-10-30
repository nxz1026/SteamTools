# 部署包（proxy-only 版本）

这个目录存放**不属于编译产物、但部署时必须一起放到目标机器**的文件。
`dotnet publish` 只会覆盖二进制，不会带上这些；重新发布到新目录时必须手工拷贝，
否则会出现「程序能跑但启动方式退化成 Ctrl+C」的问题。

## 目录内容

| 文件 | 作用 | 目标位置 |
|---|---|---|
| `launch.bat` | 启动器：模式参数、自动提权、`start ""` 脱离控制台 | `<部署目录>\launch.bat` |
| `使用说明.md` | 面向使用者的说明（托盘菜单、规则配置、停止清理） | `<部署目录>\使用说明.md` |

## 发布步骤

```bash
dotnet publish src/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy \
  -c Release -f net11.0 -r win-x64 --self-contained true \
  -o "<部署目录>"
cp doc/deploy/launch.bat "<部署目录>/"
cp doc/deploy/使用说明.md "<部署目录>/"
```

`launch.bat` **必须是 CRLF 行尾**，LF 会让 `goto` 分支出错。校验：

```bash
grep -c $'\r' launch.bat   # 应等于总行数
```

## `launch.bat` 的两个非显而易见之处

### 1. 必须用 `start ""` 异步拉起

```bat
start "" "%EXE%" --mode %MODE% %KEEP%
```

同步调用（`"%EXE%" --mode %MODE%`）会让 exe 与 `cmd.exe` **共用同一个控制台**，
程序内部 `GetConsoleProcessList` 返回 2，`OwnsConsole()` 判定「共享终端」，
于是**按设计拒绝隐藏窗口**——黑窗口留在屏幕上，自动隐藏永远不生效。

`start` 出去后，程序获得独立控制台，`GetConsoleProcessList` 返回 1，才会隐藏。

### 2. 提权分支不要再 `start`

```bat
powershell ... "Start-Process -FilePath '%EXE%' -ArgumentList '--mode','%MODE%','%KEEP%' -Verb RunAs"
```

`-Verb RunAs` 本身就会创建新控制台，再加 `start` 只会多套一层。

## 发布后校验清单

以 `--mode system` 启动，按顺序确认：

| 步骤 | 期望 |
|---|---|
| 双击 `launch.bat system` | 黑窗口一闪即消失 |
| 托盘区 | 出现 `Steam++ Accelerator (System)` 图标 |
| `ProxyEnable` | 为 `1`，`ProxyServer` 为 `127.0.0.1:26501` |
| 右键托盘 → 显示控制台 | 黑窗口重新出现 |
| 右键托盘 → 退出 | 进程退出、`ProxyEnable` 归 `0`、26501 端口释放 |

托盘自动隐藏（`SW_HIDE`）这一项**难以自动化验证**：本机安全策略会拦截
`cmd /c start`、`Process::Start`、`WScript.Shell`、`schtasks.exe`，拿不到独立控制台；
而 `Start-Process` 拉起的进程会附着调用方 ConPTY，**调用方 shell 一退，控制台关闭，
exe 跟着被带走**，很容易误判成程序崩溃。这一项只能人工确认。

## 桌面启动器（用 .bat，不要用 .lnk）

双击启动用 **`.bat`** 而不是 `.lnk`。本机 `WScript.Shell` COM 与 `Add-Type` 都被安全策略拦截，
手写 MS-SHLLINK 二进制虽然能写出，但 Explorer 经常解析不了，**双击毫无反应**（已踩坑：
之前放到桌面的手写 `.lnk` 双击完全不启动）。`.bat` 双击必执行，不依赖 COM、不依赖二进制格式，最可靠。

桌面 `Steam++ 加速.bat` 内容（**纯 ASCII**，见下方注意事项）：

```bat
@echo off
REM Steam++ Accelerator launcher (system mode)
REM Double-click to run; no UAC needed (root cert already installed).
REM Black window auto-hides; tray icon appears at bottom-right.
REM To stop: right-click tray icon -> Exit.
REM For dns mode (deeper coverage, needs admin): change --mode system to --mode dns
set "EXE=C:\SteamToolsAccelerator\Steam++.Accelerator.exe"
if not exist "%EXE%" (
    echo Cannot find %EXE%
    pause
    exit /b 1
)
start "" "%EXE%" --mode system
exit /b 0
```

### 关键注意：.bat 必须纯 ASCII

本机 cmd 的活动代码页是 GBK（936）。`.bat` 若是 UTF-8 无 BOM 且含中文，cmd 按 GBK 读取会
变成乱码，乱码字节可能破坏 `start` 那一行的解析，报
`'--mode' 不是内部或外部命令`，甚至把 `--mode` 当成独立命令执行。
**所有注释用纯英文**，或保存为 GBK / UTF-8 with BOM。这里统一纯 ASCII 最省心。

### 为什么直接 `start` exe，不走 `launch.bat`

`launch.bat` 的提权分支依赖 `net session` 检测 + `Start-Process -Verb RunAs`，双击会弹 UAC。
但 system 模式的核心（写 HKCU 代理 + 监听 26501）**不需要管理员**——根证书 `SteamTools Certificate`
已装入 `LocalMachine\Root`，所以桌面启动器直接 `start "" "%EXE%" --mode system`，
双击即跑、无 UAC。需要 dns 模式（WinDivert，需提权）时再改用 `launch.bat` 或显式 `--mode dns`。

实测（双击等效：在 cmd 中执行该 .bat）：进程存活、`ProxyEnable=1`、`ProxyServer=127.0.0.1:26501`、
26501 端口监听均成立。

## 相关文档

- `../proxy-only.md` — 实现说明（裁剪范围、托盘实现、验证记录）
- `../tray-icon.md` — 托盘功能交接文档与踩坑记录
- `../accelerate-nodes.md` — 加速节点与上游去向分析
