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
  - **system**：直接写 HKCU 的 `Internet Settings`（`ProxyEnable` / `ProxyServer` / `ProxyOverride`），
    再用 `wininet.dll` 的 `InternetSetOption`（`SETTINGS_CHANGED` / `REFRESH`）通知系统刷新；
    `ProxyOverride` 与上游保持一致。写入前会记录原值，退出时**还原原值**而不是清空；
  - **pac**：同一注册表位置写 `AutoConfigURL=http://<ip>:<port>/proxy.pac`。PAC 脚本由引擎自带的中间件在同一端口按需返回，无需额外托管；
  - **hosts**：把规则里的监听域名（`ListenDomainNames`，支持 `ip domain` 形式）指向 `127.0.0.1`，
    写在 `# --- Steam++Accelerator BEGIN/END ---` 标记之间，停止时只删这段；
    文件按字节保真读写（Latin-1 映射），不会剥掉 BOM、改写用户已有的非 ASCII 注释或吞掉末尾空行；
  - **dns**：WinDivert 由引擎自己 P/Invoke，不再需要插件里的 `WinDivertInitHelper`（那依赖插件工程的嵌入资源）；
  - **proxyonly**：不改动任何系统设置，只提示客户端把代理指过来。
- 端口语义：引擎只在 `System` / `PAC` / `VPN` 三种模式下监听正向代理端口（`ProxyPort`）。
  因此 **proxyonly 模式在引擎侧按 System 处理**（否则 `--port` 会被静默忽略，客户端连不上），
  而系统副作用仍由宿主按模式决定，proxyonly 一个都不施加。
  `hosts` / `dns` 走引擎的 `else` 分支，固定监听 `443` / `80` 做反向代理，`--port` 对这两种模式无效。
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
- **这 5 个默认项全部是 Steam 相关**，GitHub 的 12 条规则服务端标记的都是 `Checked == false`。
  要加速 GitHub 必须加 `--all`；
- `--all` 会带上一条有害规则：`Google(Recaptcha) 验证码` 的 `MatchDomainNames` 是 `google.com`、
  `ForwardDomainNames` 是 `www.recaptcha.net`，命中后 Google 首页会变成 404。
  新增 `--exclude <项目名>` 选项按项目名排除规则（不区分大小写，可重复指定），默认用法：
  ```
  Steam++.Accelerator.exe --mode dns --all --exclude "Google(Recaptcha) 验证码"
  ```
- 原始 JSON 缓存在 `<数据目录>/accelerate-projects.json`，接口不可用时用缓存兜底。
- 规则的匹配字段是 `MatchDomainNames`，`DomainPattern` 用 `^` 锚定的正则匹配，
  因此 `raw.githubusercontent.com` 这类域名不在匹配范围内，走直连。

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

### 6.1 Linux（裁剪完成时）

在本机（Linux arm64，.NET SDK 11.0.100-rc.3）验证：

| 项目 | 结果 |
|---|---|
| `dotnet build WattToolkit.slnx -c Debug` | 0 错误（警告来自 `ref/` 子模块） |
| 运行 `--mode system --no-doh`（Linux） | 加载规则 → `Listened http://127.0.0.1:26501` → 引擎启动 → `Ctrl+C` 正常停止并清理 |
| 运行 `--mode system --no-doh --all`（Linux） | 加载 58 条规则 |
| `dotnet publish -c Release -f net11.0 -r win-x64 --self-contained true` | 成功产出 `Steam++.Accelerator.exe`（PE32+ x86-64），并附带 `WinDivert.dll` / `WinDivert64.sys` / `WinDivertSharp.dll` |

### 6.2 Windows（x64，Windows 11 26300，.NET SDK 11.0.100-rc.3）

| 项目 | 结果 |
|---|---|
| `dotnet build`（Debug / Release） | 0 错误 |
| `dotnet publish -c Release -f net11.0 -r win-x64 --self-contained true` | 成功，产出 `Steam++.Accelerator.exe` + `WinDivert.dll` / `WinDivert64.sys` |
| 官方规则接口 | HTTP 200、28 503 字节；默认 5 个项目启用，`--all` 启用 58 个 |
| `--mode proxyonly --port 26590` | 监听 `http://127.0.0.1:26590`；系统代理未被改动；经该端口访问命中域名 200 |
| `--mode system --port 26592` | 写入 `ProxyEnable=1` / `ProxyServer=127.0.0.1:26592` / `ProxyOverride`；经该端口访问命中域名 200；`Ctrl+C` 退出后注册表逐项还原为原值 |
| `--mode pac --port 26591` | 写入 `AutoConfigURL=http://127.0.0.1:26591/proxy.pac`；`/proxy.pac` 返回 PAC 脚本；`Ctrl+C` 退出后该项被删除 |
| `--mode hosts --port 26554` | 在 hosts 标记块内写入 15 个域名，保留文件 BOM 与 CRLF；`Ctrl+C` 退出后 hosts 文件与运行前**字节级一致** |
| `--mode dns`（非管理员） | 引擎按预期报 `Win32Exception(5) 拒绝访问` 并中止启动（加载驱动需要管理员权限） |
| `--mode dns`（管理员） | 成功加载 WinDivert；`store.steampowered.com` / `api.github.com` / `steamcommunity.com` / `github.com` 的 DNS 应答被改写为 `127.0.0.1` / `::1`；经本机 443 反代访问上述站点均返回 200 |
| 加速命中验证 | 本机 DNS 对 `steamcommunity.com` 返回被污染的 `162.125.1.8`，直连失败（`code=000`）；经代理命中加速节点后返回 200 |

加速生效的直接证据：`steamcommunity.com` 的加速节点 `steamcommunity.rmbgame.net` 解析到 `23.200.230.67`，
而源站在本机被解析到 Facebook 的 `162.125.1.8`；只有命中规则并转发到加速节点才能拿到 200。

### 6.3 Windows 上发现并修复的问题

| 问题 | 现象 | 修复 |
|---|---|---|
| 系统代理 / PAC 写入静默失败 | 写 `.reg` 后调 `regedit /s`，但 regedit 路径取 `SpecialFolder.System`（`C:\WINDOWS\system32`）下并不存在该文件，异常被吞掉，注册表毫无变化 | 改为直接调用 `Microsoft.Win32.Registry` 写 HKCU，不再依赖外部 regedit 进程 |
| 退出时覆盖用户原有代理配置 | 撤销逻辑把 `ProxyServer` / `ProxyOverride` 一律清空 | 写入前记录原值，退出时逐项还原（原值不存在则删除该项） |
| 首次运行可能永久无法启动 | `SteamTools.Certificate.cer` 存在但 `.pfx` 缺失时不会重新生成根证书，启动直接返回 `GetCertificatePackableFail` | 生成条件改为「cer 或 pfx 任一缺失即重新生成」 |
| 非管理员无法信任根证书 | 只写 `LocalMachine\Root`，普通用户写入失败 | 写入失败时回退 `CurrentUser\Root`；检测安装状态时两个存储都算已安装 |
| `--port` 在 proxyonly 模式被忽略 | 引擎把 proxyonly 归入反向代理分支，固定监听 `443` / `80`，客户端按提示连 `--port` 必然失败 | 引擎侧把 proxyonly 按 System 处理，系统副作用仍一个都不施加 |
| hosts 写入破坏原文件 | `ReadAllTextAsync` 会剥掉 BOM、把用户已有的非 ASCII 注释写成乱码，并吞掉文件末尾空行；撤销后文件与原文件不一致 | 改为 Latin-1 字节保真读写，且只替换标记块；撤销后文件与原文件字节级一致 |
| hosts 域名取值错误 | `ListenDomainNames ?? MatchDomainNames` 中前者是非空字符串，`??` 永不生效；`ListenDomainNames` 为空时静默丢域名 | 改用上游同源的 `ListeningDomainNamesArray`，并支持 `ip domain` 形式 |
| `--all` 会弄坏 Google | 官方规则里 `Google(Recaptcha) 验证码` 的匹配域名是 `google.com`、转发目标是 `www.recaptcha.net`，命中后 Google 首页返回 404 | 新增 `--exclude <项目名>` 选项按项目名排除规则，可重复指定 |

## 7. 已知限制

- 强杀进程（任务管理器结束）不会撤销系统代理设置；
- 无 GUI，所有开关都在命令行；规则默认按服务端 `Checked` 启用，细粒度选择需要 `--all` 或后续补充本地规则文件；
- `ref/` 仍以子模块方式引入上游代码，仓库不是完全自包含的；
- 引擎自带的 SSH / Git 反向代理监听器沿用上游的「端口 22 / 9418 占用则顺延」策略；
- `dns` 模式只在 Windows 上可用且必须以管理员身份运行（WinDivert 驱动加载权限），本轮仅验证到非管理员下按预期报错并中止；
- 无管理员权限时根证书落在 `CurrentUser\Root`，只对该用户生效；升级到 `LocalMachine\Root` 需要管理员重跑一次。

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
