# SteamTools 仅代理裁剪方案（Windows，无 GUI）

| 项 | 值 |
|---|---|
| 上游 | https://github.com/BeyondDimension/SteamTools |
| 本仓（fork） | https://github.com/nxz1026/SteamTools |
| 基线 commit | `2195137ed9dd3db8cf0f5a056b7a0c33317fe2ed`（develop） |
| 许可 | **GPL-3.0** — 分发衍生必须同许可开源 |
| 分析方式 | 静态阅读 + grep 取证；**未编译、未运行、未初始化 submodule** |

## 0. 已定决策

| # | 决策 | 结论 |
|---|---|---|
| ① | 路线 | **B：抽出代理引擎为独立程序**（非官方客户端换皮） |
| ⑤ | 界面 | **不要 GUI**（不带 Avalonia，去掉主程序与 UI 插件） |
| ② | 规则来源 | **未定** |
| ③ | 运行形态 | **未定**（命令行 / Windows 服务 / 托盘） |
| ④ | DNS 拦截模式 | **未定**（要则需管理员 + WinDivert 驱动） |

## 1. 目标产物

一个无 GUI 的 Windows 可执行程序，仅提供：HTTP 代理端口 + YARP 反向代理 + 自签 MITM 证书 + 域名规则加速。

## 2. 保留 / 删除

### 保留（编译必需）

| 项 | 说明 |
|---|---|
| `src/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy/` | 引擎主体，7400 行 / 75 文件，几乎不动 |
| `src/BD.WTTS.Client.IPC/` | 636 行 / 3 文件，含 `IPCSubProcessService` |
| `ref/Common` 的 `BD.Common`、`BD.Common.Repositories.SQLitePCL` | 基础库 |
| `ref/dotnetCampus.Ipc` | IPC 框架 + 源生成器 |
| `ref/WTTS.MicroServices.ClientSDK` 的 `BD.WTTS.MicroServices.Primitives.Models` | **只为 2 个 DTO**：`AccelerateProjectDTO`、`ScriptIPCDTO`，可 vendor 后断开 |
| 源码级共享文件（被引擎 Include） | `CertificateConstants.cs`、`ProxyMode.cs`、`Os_Release.cs`、`LogConsoleService.Logger.cs`、`VisualStudioAppCenterSDK(.AppSecret).cs`、`OSShuttingDownHelper.projitems` |
| `ref/WinDivert.dll`、`ref/WinDivert64.sys` | 仅 DNS 拦截模式需要 |

### 删除

| 类别 | 项 |
|---|---|
| GUI | `src/BD.WTTS.Client.Avalonia.App`、`src/BD.WTTS.Client.Avalonia`、`src/BD.WTTS.Client`、`src/BD.WTTS.Client.AppHost`、`src/BD.WTTS.Client.AppHost.Bridge`、`src/Avalonia.Ref`（10 项目） |
| 加速 UI 插件 | `src/BD.WTTS.Client.Plugins.Accelerator/`（含 `XunYouSDK/` 迅游 SDK，Windows DLL `xunyoucall64.dll`） |
| 其它业务插件 | ArchiSteamFarmPlus、Authenticator、GameAccount、GameList、GameTools、SteamIdleCard |
| 工具/测试 | `src/BD.WTTS.Client.Tools.Publish`、`src/BD.WTTS.UnitTest`、`src/BD.WTTS.VisualStudio.Designer.HostApp` |
| 无关 submodule | `ref/SteamClient`、`ref/WinAuth`、`ref/Facepunch.Steamworks`、`ref/ArchiSteamFarm`、`ref/Steam4NET`、`ref/SteamAchievementManager`、`ref/Gameloop.Vdf`、`ref/Titanium-Web-Proxy`、`ref/Avalonia.Image2`、`ref/Avalonia8`、`ref/appcenter-sdk-dotnet`、`ref/dotnet-packaging`、`ref/Fusillade` |
| 无 GUI 后多余常量 | `ProxyMode.Hosts/System/PAC` 的副作用代码留在被删项目里；引擎侧只保留 `DNSIntercept`、`ProxyOnly`、`Hosts` 三种 |

体量参考：全仓 `src/` 约 12.97 万行 → 裁剪后目标约 1 万行量级（点亮 16% 里的引擎核心）。

## 3. 改造点

### 3.1 三个 IPC 耦合点

| 耦合点 | 现状 | 改造 |
|---|---|---|
| `IPCSubProcessService.MainAsync` | `Program.cs` 靠它引导：args[0]=pipeName、args[1]=父 PID、args[2]=Base64Url MessagePack 的 AppData/Cache 目录；且 `CheckBePid` 强校验父进程必须是 `Steam++.exe`/`Steam++.Accelerator.exe` 或通过强签名 `AssemblyInfo.ValidateAssembly` | **换成自写 host**：直接 `Ioc.ConfigureServices(...)` → 读本地配置 → 组装 → 调 `StartProxyAsync`；完全不走命名管道，`CheckBePid` 一并去掉 |
| `IPCPlatformService` | `YarpReverseProxyServiceImpl.cs:23`、`CertificateManagerImpl.cs:17` 由 `ipc.GetService<T>()` 取 | **给 stub**：Windows 分支下 `IsCertificateInstalled` 直读 `X509Store(StoreName.Root, LocalMachine)`（`ICertificateManager.cs:35-37`），`TrustRootCertificate` 直写同一 store（同文件 62-74 行），**根本不调 platformService** |
| `IPCToastService` | `DomainResolver`、`CertificateManagerImpl` 用于弹通知 | 换 no-op 实现；`YarpReverseProxyServiceImpl.cs:235` 本就已注释 |

### 3.2 唯一启动入口

```csharp
// ReverseProxyServiceImpl.cs:150
public async Task<StartProxyResult> StartProxyAsync(byte[] reverseProxySettings_)
//   :155  Serializable.DMP2<ReverseProxySettings>(reverseProxySettings_)
//   :190  reverseProxySettings.SetValue(this)
//   :192  StartProxyImpl()
```

`ReverseProxySettings` 是 `readonly partial record struct`，定义在引擎项目内（`Services/IReverseProxyService.Constants.cs:188`），**不依赖 UI 插件**。

### 3.3 必须自己实现的 Windows 副作用

引擎只负责监听代理端口，"把流量引过去"原本在父进程 `ProxyService.Operate.cs` 里做：

| 模式 | 原实现（已随 GUI 删除） | Windows 替代做法 |
|---|---|---|
| Hosts | `hostsFileService.UpdateHosts()` | 把 `ListenDomainNames` 映射到 `127.0.0.1` 写 hosts；启动前查 443 占用 |
| System | `IPlatformService.SetAsSystemProxyAsync` | 写 `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings` 的 `ProxyEnable`/`proxyServer`/`ProxyOverride`（`WindowsPlatformServiceImpl.SystemProxy.cs` 有现成 .reg 模板，可直接 `reg import`） |
| PAC | `SetAsSystemPACProxyAsync` | 同键写 `AutoConfigURL` |
| DNSIntercept | `Mobius.Helpers.WinDivertInitHelper.InitializeAsync()` | 需**管理员** + WinDivert 驱动（`ref/` 已带） |
| ProxyOnly | 枚举存在，但**只在 Android 暴露**给用户（`ProxySettings.ProxyMode.cs:19-21`） | 建议自己放开：只监听本机代理端口，不改系统设置；**这是"只用代理"最干净的形态** |

## 4. 规则数据源（未定，关键决策）

现状：`ProxyService.InitializeAccelerateAsync()` → `IMicroServiceClient.Instance.Accelerate.All()`（20s 超时），失败回落本地缓存。

- 缓存文件：`<AppData>/LOCAL_ACCELERATE.json` — **扩展名骗人，实为 MessagePack + lz4**（`MessagePackSerializer` + `Serializable.lz4Options`），内容是 `List<AccelerateProjectGroupDTO>`
- 仓库内**不含任何 bundled 规则表**

| 方案 | 代价 |
|---|---|
| ① 沿用官方后端 | 需保留 `MicroServices.ClientSDK` + 登录/令牌链路，等于没真正独立 |
| ② 复用本地缓存 | 需先从官方客户端拷出 `LOCAL_ACCELERATE.json`，规则会过期 |
| ③ **自维护规则表**（倾向） | 自写域名清单，格式自定 json，彻底离线 |

引擎需要的规则字段：`Name`、`Port`、`MatchDomainNames`、`ForwardDomainNames`、`ListenDomainNames`、`ProxyType`、`Order`、`IgnoreSSLCertVerification`、`FakeServerName`、`FakeUserAgent`、`ThreeStateEnable`。

## 5. 硬约束

| # | 约束 | 证据 |
|---|---|---|
| 1 | **GPL-3.0**：分发衍生必须同许可开源 | `LICENSE` 首行 |
| 2 | 需 **.NET 11 预览 SDK**（`DotNet_Version=11.0`） | `src/Directory.Build.props:3`、`global.json` |
| 3 | **17 个 submodule 全部未初始化**，且均为 BeyondDimension 的 fork，NuGet 上没有 | `.gitmodules` |
| 4 | 引擎原以**管理员权限**启动 | `.../Accelerator/Plugins/Plugin.cs:128` |
| 5 | 原部署路径硬编码 `modules/Accelerator/Steam++.Accelerator.exe` | `.../Tools.Publish/Helpers/MSIXHelper.cs:234` |
| 6 | 引擎单 TFM，无平台后缀；Windows 常量靠条件 `DefineConstants`：`WINDOWS7_0_OR_GREATER;WINDOWS`；DNS 拦截另受 `REMOVE_DNS_INTERCEPT` 控制 | `.../Accelerator.ReverseProxy.csproj` |
| 7 | 无 Windows 专用构建脚本（原走 VS/msbuild + MSIX） | `build/` 只有 linux/macos |
| 8 | 本机（Linux）**没有 dotnet SDK**，且目标平台是 Windows ⇒ 编译验证必须在 Windows 上做 | `which dotnet` 失败 |

## 6. 证据索引

| 事实 | 文件:行 |
|---|---|
| 引擎启动入口 | `src/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy/Services.Implementation/ReverseProxyServiceImpl.cs:150` |
| 配置反序列化 | 同上 `:155` |
| `ReverseProxySettings` 定义 | `.../Services/IReverseProxyService.Constants.cs:188` |
| IPC 耦合注入 | `.../Services.Implementation/YarpReverseProxyServiceImpl.cs:12-37` |
| Windows 证书直用 X509Store | `.../Services/Certificate/ICertificateManager.cs:35-37`、`:62-74` |
| 子进程引导与父进程校验 | `src/BD.WTTS.Client.IPC/Services/IPCSubProcessService.cs` |
| 系统代理注册表模板 | `src/BD.WTTS.Client/Services.Implementation/Platform/Windows/WindowsPlatformServiceImpl.SystemProxy.cs` |
| 代理模式平台矩阵 | `src/BD.WTTS.Client.Plugins.Accelerator/Settings/ProxySettings.ProxyMode.cs:10-33` |
| 规则在线拉取 + 本地缓存 | `src/BD.WTTS.Client.Plugins.Accelerator/Services/Mvvm/ProxyService.cs:422-500` |
| 子进程启动（管理员） | `src/BD.WTTS.Client.Plugins.Accelerator/Plugins/Plugin.cs:124-130`、`:199-231` |

> 注：以上 `src/BD.WTTS.Client*` 路径均为**裁剪前**的路径，用于取证溯源；裁剪后不复存在。

## 7. 待拍板

| # | 问题 | 影响 |
|---|---|---|
| ② | 规则表：沿用后端 / 复用缓存 / 自维护？ | 决定是否保留账号链路 |
| ③ | 运行形态：命令行 / Windows 服务 / 托盘？ | 决定宿主实现 |
| ④ | 要不要 DNS 拦截模式？ | 不要则可砍掉整个 `PacketIntercept/` 与 WinDivert 依赖 |
