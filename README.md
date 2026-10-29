# Steam++ Accelerator · 仅代理版

> 本仓库是 [Watt Toolkit（原 Steam++ / SteamTools）](https://github.com/BeyondDimension/SteamTools) 的**裁剪分支（fork）**，
> 只保留其中的「网络加速 / 反向代理」能力，删除了全部界面与 Steam 账号、令牌、游戏库等业务功能。
> 上游以 **GPL-3.0** 授权，本仓沿用同一许可，详见 [LICENSE](LICENSE)。

| 项 | 值 |
|---|---|
| 上游仓库 | https://github.com/BeyondDimension/SteamTools |
| 本仓（fork） | https://github.com/nxz1026/SteamTools |
| 上游基线 commit | `2195137ed9dd3db8cf0f5a056b7a0c33317fe2ed`（`develop`） |
| 许可 | GPL-3.0 |

## 这是什么

一个**没有界面、可以独立运行**的 Windows 控制台程序 `Steam++.Accelerator.exe`：

- 在本机启动一个 HTTP 代理端口（默认 `127.0.0.1:26501`）；
- 内置 YARP 反向代理与自签 CA（MITM 解密），按官方加速规则把命中域名的流量转发到加速节点；
- 可选地把系统流量引流过来：Windows 系统代理 / PAC / hosts / WinDivert 驱动级 DNS 拦截。

与上游客户端最大的结构差异：它**不再作为 GUI 主程序的 IPC 子进程**运行。
没有命名管道、没有父进程签名校验、不需要 `modules/Accelerator/` 目录布局，可以直接运行、写进计划任务或做成服务。

它**不是**：Steam 账号管理工具、令牌验证器、游戏库挂卡工具、ArchiSteamFarm 管理器——这些都在裁剪中删除。

## 与上游的差异

保留：

- `src/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy/` —— 代理引擎本体（YARP 反代、MITM 证书、HTTP/HTTPS/SSH/Git 中间件、DNS 分析与 DoH、流量统计、WinDivert 数据包拦截）。

删除：

- 全部 Avalonia 界面工程与 `src/BD.WTTS.Client.Avalonia.App`（主程序 `Steam++`）；
- `src/BD.WTTS.Client`（约 3.8 万行的客户端基础库）与全部业务插件（账号、令牌、游戏列表、挂卡、ASF、工具箱、更新器）；
- `src/BD.WTTS.Client.IPC` 与 `ref/dotnetCampus.Ipc`：改为在引擎项目内提供同名的 `IPCPlatformService` / `IPCToastService` 本地实现；
- 上游的 17 个子模块只保留 3 个（`ref/DirectoryPackages`、`ref/Common`、`ref/WTTS.MicroServices.ClientSDK`）；
- Windows AppCenter 遥测（`VisualStudioAppCenterSDK`）、迅游 SDK（`XunYouSDK`）、MSIX/发布工具链、macOS/Linux 打包脚本、多语言与站点相关文件。

改造：

- `Program.cs` 重写为独立宿主（命令行解析 → 初始化数据目录与日志 → `Ioc` 容器 → 拉取加速规则 → 组装 `ReverseProxySettings` → `StartProxyAsync` → 施加系统副作用 → 等待退出信号 → 反向清理）；
- 系统副作用（系统代理 / PAC / hosts）原在被删除的 `ProxyService.Operate.cs` 里，现在直接实现在 `Program.cs` 中；
- 加速规则改用官方接口匿名拉取（见下），离线时回落到本地 JSON 缓存。

## 构建

需要 **.NET 11 预览 SDK**（见 `global.json`，`rollForward: latestMajor`、`allowPrerelease: true`）。

```bash
git clone --recurse-submodules https://github.com/nxz1026/SteamTools.git
cd SteamTools

# Windows x64、自包含（目标机无需安装 .NET 运行时）
dotnet publish src/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy.csproj \
  -c Release -f net11.0 -r win-x64 --self-contained true -o dist/win-x64
```

框架依赖的构建把 `--self-contained true` 换成 `false` 即可；
只在本机调试时可以直接 `dotnet build WattToolkit.slnx -c Debug`。

> 子模块必须是完整的：`ref/WTTS.MicroServices.ClientSDK` 自身还有两个嵌套子模块，
> 所以要用 `--recurse-submodules`（或事后执行 `git submodule update --init --recursive`）。

## 使用

```
Steam++.Accelerator.exe [选项]

  --mode <system|hosts|pac|dns|proxyonly>  运行模式，默认 system
  --port <端口>                            代理监听端口，默认 26501（hosts 模式 443）
  --ip <地址>                              监听地址，默认 127.0.0.1
  --dns <地址>                             上游 DNS，默认 223.5.5.5
  --no-doh                                 关闭 DoH
  --all                                    启用官方规则中的全部项目（默认仅启用服务端已勾选项）
  --help                                   显示帮助
```

| 模式 | 作用 | 权限 |
|---|---|---|
| `system`（默认） | 设置 Windows 系统代理（`ProxyEnable` / `proxyServer` / `ProxyOverride`） | 普通用户 |
| `pac` | 写系统 `AutoConfigURL`，由引擎自带 PAC 脚本分流 | 普通用户 |
| `hosts` | 把规则里的监听域名指向本机并做反向代理（监听 443） | **管理员** |
| `dns` | WinDivert 驱动级 DNS 拦截 | **管理员** |
| `proxyonly` | 只监听代理端口，不改动任何系统设置 | 普通用户 |

`Ctrl+C` 停止：程序会先撤销自己写入的系统设置，再关闭代理引擎。

几点注意：

- `--ip` 默认只监听 `127.0.0.1`。改成 `0.0.0.0` 会把这个 MITM 代理暴露给同网段的所有设备，请谨慎。
- 首次以 `system` / `hosts` / `dns` 模式运行时会生成并信任一张自签根证书（写入 `LocalMachine\Root`），因此需要管理员权限。
- `dns` 模式依赖 `WinDivert.dll` / `WinDivert64.sys`，发布目录中已随程序附带。
- 程序退出时若被强杀（而非 `Ctrl+C`），`system` / `pac` 写下的注册表项不会被撤销，需要手动关闭系统代理。

## 加速规则从哪来

规则（域名 → 加速节点）来自官方接口，**匿名即可获取，不需要登录账号**：

```
POST https://api.steampp.net/accelerator/projectgroups
Content-Type: application/json
{}
```

成功后原始 JSON 会缓存到数据目录下的 `accelerate-projects.json`，接口不可用时用该缓存兜底。
接口默认只标记少数项目为启用（`Checked`），加 `--all` 可启用返回的全部项目。

## 数据目录与日志

| 平台 | 路径 |
|---|---|
| Windows | `%LOCALAPPDATA%\Steam++Accelerator` |
| 其它 | `~/.config/Steam++Accelerator` |

日志写在 `<数据目录>/Cache/Logs/accelerator-<日期>.log`（单文件 10 MB 轮转，保留 14 份）。

## 目录结构

```
src/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy/   代理引擎 + 独立宿主（Program.cs）
src/OSShuttingDownHelper/                              系统关机检测（共享项目）
src/*.props                                            目标框架与集中包版本
ref/Common/                                            上游基础库（子模块）
ref/WTTS.MicroServices.ClientSDK/                      加速规则 DTO（子模块）
ref/DirectoryPackages/                                 集中包版本（子模块）
ref/WinDivert.dll, ref/WinDivert64.sys                 DNS 拦截模式所需的驱动
doc/proxy-only.md                                      裁剪说明与实现细节
```

## 来源与许可

- 上游项目：**Watt Toolkit（Steam++ / SteamTools）** — https://github.com/BeyondDimension/SteamTools
  （Copyright © BeyondDimension / Steam++ contributors）
- 本仓为社区裁剪分支，与上游作者及团队无关，也不代表上游立场；**请勿向上游仓库反馈本仓的问题**。
- 上游与本仓均以 **GNU General Public License v3.0** 授权。你可以自由使用、修改、再分发，但**分发衍生作品时必须同样以 GPL-3.0 开源并提供完整源码**，且需保留版权与许可声明。
- 代理加速规则及 `api.steampp.net` 服务由上游/其运营方提供，本仓只是调用方。使用前请自行确认符合当地法律法规与服务条款，因使用本程序产生的任何后果由使用者自行承担。
