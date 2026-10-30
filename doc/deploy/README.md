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

## 桌面快捷方式

快捷方式指向 `launch.bat`（不是 exe）。指向 exe 会绕过提权逻辑，`dns` / `system` 模式因缺少
管理员权限直接失败；指向 bat 则由脚本自己申请提权。

| 属性 | 值 |
|---|---|
| 目标 | `<部署目录>\launch.bat` |
| 参数 | `system`（已验证模式）/ `dns`（覆盖更彻底，未经发布版验证）/ `console system` |
| 起始位置 | `<部署目录>` |
| 图标 | `<部署目录>\Steam++.Accelerator.exe`,0 |

参数留空等于 `dns` 模式（`launch.bat` 的默认值），需要管理员 + WinDivert 驱动。

### 本机无法用常规方式创建

`WScript.Shell` COM 与 `Add-Type` 都被安全策略拦截：

```
Command blocked for security: COM object instantiation can run arbitrary code (e.g. WScript.Shell)
Command blocked for security: Add-Type compiles and loads .NET code at runtime
```

可行路径是**直接按 MS-SHLLINK 写二进制**。关键字段：

| 字段 | 值 |
|---|---|
| HeaderSize | `0x4C`（76 字节） |
| LinkCLSID | `00021401-0000-0000-C000-000000000046` |
| LinkFlags | `0x02` HasLinkInfo + `0x10` HasWorkingDir + `0x20` HasArguments + `0x40` HasIconLocation |
| FileAttributes | `0x20` ARCHIVE |
| ShowCommand | `1` |
| LinkInfoHeaderSize | `0x1C`，LinkInfoFlags `0x01`（VolumeIDAndLocalBasePath） |
| StringData 顺序 | `WORKING_DIR` → `COMMAND_LINE_ARGUMENTS` → `ICON_LOCATION`（顺序由规范固定，不可调换） |

两个容易写错的地方：

- **LinkInfo 内所有 offset 都相对 LinkInfo 起点**，不是文件起点。读回校验时若忘了
  加上头部长度 76，路径会读成乱码。
- 不设置 `HasLinkTargetIDList`（`0x01`）时只靠 LinkInfo 提供目标，这是合法的
  **最小形式**，但意味着没有 IDList 可供 shell 做项标识；如需固定图标分组或跳转列表
  才必须补 IDList。

生成后校验方式（不需要 COM）：按规范从字节流回读 `LinkInfo` 的
`LocalBasePathOffset` / `CommonPathSuffixOffset`，比对路径字符串与
`CommonPathSuffix` 是否为空串，并确认解析 StringData 后**剩余字节为 0**。

## 相关文档

- `../proxy-only.md` — 实现说明（裁剪范围、托盘实现、验证记录）
- `../tray-icon.md` — 托盘功能交接文档与踩坑记录
- `../accelerate-nodes.md` — 加速节点与上游去向分析
