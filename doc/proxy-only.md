# 仅代理裁剪：实现说明

本文记录从 [Watt Toolkit（SteamTools）](https://github.com/BeyondDimension/SteamTools) 裁剪出「仅代理」版本的
决策、改动点与验证结果。上游基线 commit：`2195137ed9dd3db8cf0f5a056b7a0c33317fe2ed`（`develop`）。

| # | 决策项 | 结论 |
|---|---|---|
| ① | 路线 | 抽出代理引擎为独立程序（不做官方客户端换皮） |
| ② | 规则来源 | 官方接口 `POST https://api.steampp.net/accelerator/projectgroups`，匿名可用；离线回落本地 JSON 缓存 |
| ③ | 运行形态 | 命令行程序（可自行服务化 / 计划任务） |
| ④ | DNS 拦截 | 保留，需管理员 + 随程序附带的 WinDivert 驱动 |
| ⑤ | 界面 | 无 GUI，删除全部 Avalonia 工程 |

## 1. 裁剪结果

`src/` 从 36 个项目收敛到 **1 个可执行项目**，行数从约 13 万行降到约 1 万行量级（引擎本体 7400 行）。

保留：

| 项 | 说明 |
|---|---|
| `src/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy/` | 引擎本体 + 重写的独立宿主 `Program.cs` |
| `src/AssemblyInfo{,.Constants}.cs`、`src/ImplicitUsings.{BCL,Common,...}.cs`、`src/Directory.*.props`、`src/TFM_NETX.props`、`src/.editorconfig` | 编译必需的共享源码与 MSBuild 配置 |
| `src/OSShuttingDownHelper/` | 关机检测（`Windows.Win32.OSShuttingDownHelper`） |
| `ref/Common`、`ref/WTTS.MicroServices.ClientSDK`、`ref/DirectoryPackages` | 三个子模块 |
| `ref/WinDivert.dll`、`ref/WinDivert64.sys` | DNS 拦截模式所需驱动 |
| `res/icons/app/v3/` | 程序图标 |
| `LICENSE`、`README.md`、`doc/proxy-only.md` | 许可与文档 |

删除（要点）：

- 界面与主程序：`BD.WTTS.Client.Avalonia.App`（`Steam++`）、`BD.WTTS.Client.Avalonia`、`BD.WTTS.Client`、
  `AppHost` / `AppHost.Bridge`、10 个 `Avalonia.*` 定制工程、`BD.Avalonia8.Image2.Compat`、`Designer.HostApp`；
- 业务插件：Accelerator（UI 插件，含 `XunYouSDK` 迅游 SDK）、Authenticator、GameAccount、GameList、
  GameTools、SteamIdleCard、ArchiSteamFarmPlus、Update；
- 工具与测试：`Tools.Publish`、`Tools.HostsTest`、`Tools.OpenSourceLibraryList`、`UnitTest`、`BD.WTTS.Generators`；
- 其它：`BD.WTTS.Client.IPC`、`ref/dotnetCampus.Ipc`、14 个无关子模块、`ref/Interop.IWshRuntimeLibrary.dll`、
  `VisualStudioAppCenterSDK*`、`packaging/`、`build/`（仅 Linux/macOS 脚本）、`.github/`、`crowdin.yml`、
  两个 `.snk` 签名文件、`ref/WinDivert.mpo`、`res/` 中除图标外的全部资源。

> 说明：`ref/WTTS.MicroServices.ClientSDK` 只用到了 `AccelerateProjectDTO` / `AccelerateProjectGroupDTO` /
> `ApiRsp<T>` 这几个类型。它自身还带有 `ref/DirectoryPackages`、`ref/WTTS.Public` 两个嵌套子模块，
> 所以拉取子模块必须用 `--recurse-submodules`，否则 `BD.WTTS.Primitives` 会因缺少 `BD.WTTS.R` 命名空间编译失败。

## 2. 解耦 IPC

引擎原本是 GUI 的子进程，靠命名管道接受指令。裁剪后保留引擎业务代码不动，只在**同名接口**上提供本地实现。

| 原耦合点 | 改造 |
|---|---|
| `Program.cs` 中的 `IPCSubProcessService.MainAsync(pipeName, pid, ...)` | 删除。改为自写宿主：`Ioc.ConfigureServices(...)` → `Ioc.Get<IReverseProxyService>()` → `StartProxyAsync(...)`；不再校验父进程、不再用命名管道 |
| `YarpReverseProxyServiceImpl` / `CertificateManagerImpl` 构造函数里的 `IPCSubProcessService ipc` + `GetIPCService<T>()` | 改为直接注入 `IPCPlatformService` / `IPCToastService` |
| `IPCPlatformServiceImpl`（新增，156 行） | Win 用 `X509Store(StoreName.Root, LocalMachine)` 按指纹查/增/删证书；macOS 走 `security add-trusted-cert`；Linux 拷入 `/usr/local/share/ca-certificates` 并 `update-ca-certificates` |
| `IPCToastServiceImpl`（新增） | 把原本弹给用户的通知写进日志与 `Console` |
| `LazyReverseProxyServiceImpl` / `LazyCertificateManager`、`[IpcPublic]` 标记 | 删除 |
| Yarp 内部 DI 容器原本靠 `ipcAddSingletonServices` 注入 IPC 代理 | 改为 `services.AddSingleton(platformService); services.AddSingleton(toast);`（`DomainResolver` 需要在 Yarp 容器的中解析 `IPCToastService`） |

`YarpReverseProxyServiceImpl.Startup.cs` 中原有一条注释值得保留：
*不可把 `IPCSubProcessService ipc` 放进 YarpHost 的 DI 容器，因为停止加速时释放它会导致进程退出。*

## 3. 独立宿主设计

`src/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy/Program.cs`（约 750 行）的执行顺序：

1. 关机检测 `Windows.Win32.OSShuttingDownHelper.IsSystemShuttingDown()`；
2. 解析命令行（`--mode/--port/--ip/--dns/--no-doh/--all/--help`）；
3. `IOPath.InitFileSystem(GetAppDataDirectory, GetCacheDirectory)` —— 引擎里所有 `IOPath.AppDataDirectory` 都会在未初始化时抛异常；
4. `InitNLog()` —— 控制台 + 文件两个 target（引擎的 Yarp 宿主会调用 `builder.Host.UseNLog()`）；
5. `Ioc.ConfigureServices(ConfigureServices)` —— 引擎内部有 `Ioc.Get<T>()` 的硬依赖（`IReverseProxyService.Constants.Instance`、`ICertificateManager.Instance`）；
6. `LoadAccelerateProjectsAsync()` 拉规则（在线失败读本地缓存）；
7. 组装 `ReverseProxySettings` → `Serializable.SMP2(...)` → `StartProxyAsync(byte[])`；
8. 按模式施加系统副作用；
9. 等待 `Ctrl+C` / 进程退出信号；
10. 先撤销副作用，再 `StopProxyAsync()`，最后 `await Ioc.DisposeAsync()`。

几点实现注意：

- `ReverseProxySettings` 是引擎内定义的 `readonly partial record struct`（22 个字段），是引擎唯一入口，宿主只需按顺序填参。
- `ProxyIp` 上游默认 `IPAddress.Any`；这里默认 `127.0.0.1`（只用于 Kestrel 绑定与随机端口探测）。
- Windows 副作用原在已删除的 `ProxyService.Operate.cs` 中，现直接实现在 `Program.cs`：
  - **system**：写 `.reg` 文本后 `regedit /s` 导入 HKCU 的 `Internet Settings`（`ProxyEnable` / `proxyServer` / `ProxyOverride`），再用 `wininet.dll` 的 `InternetSetOption`（`SETTINGS_CHANGED` / `REFRESH`）通知系统刷新；`ProxyOverride` 与上游保持一致；
  - **pac**：同一注册表位置写 `AutoConfigURL=http://<ip>:<port>/proxy.pac`。PAC 脚本由引擎自带的中间件在同一端口按需返回，无需额外托管；
  - **hosts**：把 `ListenDomainNames ?? MatchDomainNames` 指向 `127.0.0.1`，写在 `# --- Steam++Accelerator BEGIN/END ---` 标记之间，停止时只删这段；
  - **dns**：WinDivert 由引擎自己 P/Invoke，不再需要插件里的 `WinDivertInitHelper`（那依赖插件工程的嵌入资源）；
  - **proxyonly**：不改动任何系统设置，只提示客户端把代理指过来。
- 退出信号用 `Console.CancelKeyPress` + `AppDomain.ProcessExit`。`CancellationTokenSource` **不能**用 `using`——
  `ProcessExit` 处理器可能在它释放之后才被回调，会抛 `ObjectDisposedException`。

## 4. 加速规则

```
POST https://api.steampp.net/accelerator/projectgroups
Content-Type: application/json

{}
```

- 匿名可用（未登录时只是不带 `Authorization` 头），实测返回 HTTP 200、约 28 KB；
- 响应是 emoji 键的包装结构：`{"🦓": [...], "🦄": 200, "🐴": null}`，对应 `ApiRsp<T>.Content / Code / Message`，
  正文为 PascalCase，`System.Text.Json` 默认配置即可反序列化；
- 按组拍平后取 `Checked == true` 的项目，默认约 58 个项目中只有 5 个被标记启用，加 `--all` 可全部启用；
- 原始 JSON 缓存在 `<数据目录>/accelerate-projects.json`，接口不可用时用缓存兜底。

## 5. 跨平台条件编译

引擎项目使用**裸 TFM**（`net11.0`，不用 `-windows` 后缀）：

```xml
<!-- Windows 目标：本机在 Windows 上，或以 win-* RID 交叉编译 -->
<PropertyGroup Condition="$([MSBuild]::IsOSPlatform('windows')) OR $(RuntimeIdentifier.StartsWith('win-'))">
  <DefineConstants>WINDOWS7_0_OR_GREATER;WINDOWS;$(DefineConstants)</DefineConstants>
</PropertyGroup>
```

上游只判断 `IsOSPlatform('windows')`，导致在 Linux/macOS 上**无法**交叉编译 Windows 版本（`WINDOWS` 不会被定义，
WinDivert 包也不会被引入）。改成「或 RID 以 `win-` 开头」后，`-r win-x64` 就能在非 Windows 主机上产出完整功能版本。
`Aigio.WinDivertSharp` 包与 WinDivert 驱动文件的引入条件做了同样修改。

## 6. 验证记录

在本机（Linux arm64，.NET SDK 11.0.100-rc.3）验证：

| 项目 | 结果 |
|---|---|
| `dotnet build WattToolkit.slnx -c Debug` | 0 错误（警告来自 `ref/` 子模块） |
| 运行 `--mode system --no-doh`（Linux） | 加载规则 → `Listened http://127.0.0.1:26501` → 引擎启动 → `Ctrl+C` 正常停止并清理 |
| 运行 `--mode system --no-doh --all`（Linux） | 加载 58 条规则 |
| `dotnet publish -c Release -f net11.0 -r win-x64 --self-contained true` | 成功产出 `Steam++.Accelerator.exe`（PE32+ x86-64），并附带 `WinDivert.dll` / `WinDivert64.sys` / `WinDivertSharp.dll` |

**尚未在真实 Windows 上运行过**：注册表系统代理、hosts 写入、`X509Store(LocalMachine\Root)` 证书安装、
WinDivert 拦截这四条路径只经过编译验证。首次在 Windows 上使用请以 `--mode proxyonly` 先行确认代理本身可用，
再逐个尝试需要管理员权限的模式。

## 7. 已知限制

- 强杀进程（任务管理器结束）不会撤销系统代理设置；
- 无 GUI，所有开关都在命令行；规则默认按服务端 `Checked` 启用，细粒度选择需要 `--all` 或后续补充本地规则文件；
- `ref/` 仍以子模块方式引入上游代码，仓库不是完全自包含的；
- 引擎自带的 SSH / Git 反向代理监听器沿用上游的「端口 22 / 9418 占用则顺延」策略。

## 8. 证据索引（均指上游裁剪前的路径）

| 事实 | 位置 |
|---|---|
| 引擎唯一启动入口 | `.../Accelerator.ReverseProxy/Services.Implementation/ReverseProxyServiceImpl.cs:150` |
| 配置反序列化 / 应用 | 同上 `:155`、`:190` |
| `ReverseProxySettings` 定义（22 字段） | `.../Services/IReverseProxyService.Constants.cs:188` |
| IPC 注入原貌 | `.../Services.Implementation/YarpReverseProxyServiceImpl.cs:12-37` |
| Windows 证书直用 `X509Store` | `.../Services/Certificate/ICertificateManager.cs:35-37`、`:62-74` |
| 监听器选择逻辑 | `.../Services.Implementation/YarpReverseProxyServiceImpl.cs` `StartProxyCore()` |
| PAC 中间件 | `.../Services.Implementation/HttpServer/Middleware/HttpProxyPacMiddleware.cs` |
| 子进程引导与父进程校验 | `src/BD.WTTS.Client.IPC/Services/IPCSubProcessService.cs` |
| 系统代理注册表模板 | `src/BD.WTTS.Client/Services.Implementation/Platform/Windows/WindowsPlatformServiceImpl.SystemProxy.cs` |
| 规则在线拉取 + 本地缓存 | `src/BD.WTTS.Client.Plugins.Accelerator/Services/Mvvm/ProxyService.cs:422-500` |
| 代理模式平台矩阵 | `src/BD.WTTS.Client.Plugins.Accelerator/Settings/ProxySettings.ProxyMode.cs` |
| 规则接口实现 | `ref/WTTS.MicroServices.ClientSDK/src/BD.WTTS.MicroServices.ClientSDK/Services/Implementation/MicroServiceClientBase.Clients.cs:341-353` |
