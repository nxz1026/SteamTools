# 仅代理裁剪：实现说明

面向维护者的笔记。**用户向的说明在 [README](../README.md)**，这里只记录从
[Watt Toolkit（SteamTools）](https://github.com/BeyondDimension/SteamTools) 裁剪时的决策、
非显而易见的实现约束与验证结果。上游基线 commit：`2195137ed9dd3db8cf0f5a056b7a0c33317fe2ed`（`develop`）。

## 1. 裁剪范围

`src/` 从 36 个项目收敛到 **1 个可执行项目**，代码量从约 13 万行降到约 1 万行（引擎本体 7400 行）。

| 决策 | 结论 |
|---|---|
| 路线 | 抽出代理引擎做独立程序，不做官方客户端换皮 |
| 运行形态 | 命令行程序（可自行服务化 / 计划任务） |
| 界面 | 全删（全部 Avalonia 工程） |
| 业务功能 | 全删（账号、令牌、游戏库、挂卡、ASF、工具箱、更新器） |
| IPC | `src/BD.WTTS.Client.IPC` 与 `ref/dotnetCampus.Ipc` 全删，改为引擎内的同名本地实现 |
| 子模块 | 17 → 3（`ref/Common`、`ref/WTTS.MicroServices.ClientSDK`、`ref/DirectoryPackages`） |
| 其它 | AppCenter 遥测、迅游 SDK、MSIX/发布工具链、多语言与站点文件 |

`ref/WTTS.MicroServices.ClientSDK` 只用到 `AccelerateProjectDTO` / `AccelerateProjectGroupDTO` / `ApiRsp<T>`
三个类型。它自身还有两个嵌套子模块，**拉取子模块必须用 `--recurse-submodules`**，
否则 `BD.WTTS.Primitives` 会因缺少 `BD.WTTS.R` 命名空间编译失败。

## 2. 解耦 IPC

引擎原本是 GUI 的子进程，靠命名管道接受指令。裁剪后保留引擎业务代码不动，只在**同名接口**上提供本地实现。

| 原耦合点 | 改造 |
|---|---|
| `Program.cs` 的 `IPCSubProcessService.MainAsync(pipeName, pid, ...)` | 删除。改为自写宿主：`Ioc.ConfigureServices(...)` → `Ioc.Get<IReverseProxyService>()` → `StartProxyAsync(...)` |
| `YarpReverseProxyServiceImpl` / `CertificateManagerImpl` 构造函数里的 `IPCSubProcessService ipc` + `GetIPCService<T>()` | 改为直接注入 `IPCPlatformService` / `IPCToastService` |
| `IPCPlatformServiceImpl`（新增） | Win 用 `X509Store` 按指纹查/增/删证书；macOS 走 `security add-trusted-cert`；Linux 拷入 `/usr/local/share/ca-certificates` |
| `IPCToastServiceImpl`（新增） | 把原本弹给用户的通知写进日志与 `Console` |
| `LazyReverseProxyServiceImpl` / `LazyCertificateManager`、`[IpcPublic]` 标记 | 删除 |
| Yarp 内部 DI 容器靠 `ipcAddSingletonServices` 注入 IPC 代理 | 改为 `services.AddSingleton(platformService); services.AddSingleton(toast);` |

保留自上游的一条注释：
*不可把 `IPCSubProcessService ipc` 放进 YarpHost 的 DI 容器，因为停止加速时释放它会导致进程退出。*

## 3. 独立宿主

`src/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy/Program.cs` 的执行顺序：

1. 关机检测 `Windows.Win32.OSShuttingDownHelper.IsSystemShuttingDown()`；
2. 解析命令行；
3. `IOPath.InitFileSystem(...)` —— 引擎里所有 `IOPath.AppDataDirectory` 都会在未初始化时抛异常；
4. `InitNLog()` —— 控制台 + 文件两个 target；
5. `Ioc.ConfigureServices(...)` —— 引擎内部有 `Ioc.Get<T>()` 的硬依赖；
6. 加载加速规则；
7. 组装 `ReverseProxySettings` → `Serializable.SMP2(...)` → `StartProxyAsync(byte[])`；
8. 按模式施加系统副作用；
9. 等待 `Ctrl+C` / 进程退出信号；
10. 先撤销副作用，再 `StopProxyAsync()`，最后 `await Ioc.DisposeAsync()`。

### 实现约束

- `ReverseProxySettings` 是引擎内定义的 `readonly partial record struct`（22 个字段），是引擎唯一入口。
- `ProxyIp` 上游默认 `IPAddress.Any`；这里默认 `127.0.0.1`。
- 退出信号用 `Console.CancelKeyPress` + `AppDomain.ProcessExit`。`CancellationTokenSource` **不能**用 `using`——
  `ProcessExit` 处理器可能在它释放之后才被回调，会抛 `ObjectDisposedException`。
- **端口语义**：引擎只在 `System` / `PAC` / `VPN` 三种模式下监听正向代理端口（`ProxyPort`）。
  因此 **proxyonly 模式在引擎侧按 System 处理**（否则 `--port` 会被静默忽略，客户端连不上），
  而系统副作用仍由宿主按模式决定，proxyonly 一个都不施加。
  `hosts` / `dns` 走引擎的 `else` 分支，固定监听 `443` / `80` 做反向代理，`--port` 对这两种模式无效。
- **系统副作用**原在已删除的 `ProxyService.Operate.cs` 里，现直接实现在 `Program.cs`：
  - **system**：直接写 HKCU 的 `Internet Settings`（`ProxyEnable` / `ProxyServer` / `ProxyOverride`），
    再用 `wininet.dll` 的 `InternetSetOption`（`SETTINGS_CHANGED` / `REFRESH`）通知系统刷新。
    写入前记录原值，退出时**还原原值**而不是清空；
  - **pac**：同一注册表位置写 `AutoConfigURL=http://<ip>:<port>/proxy.pac`。PAC 脚本由引擎自带中间件在同一端口返回；
  - **hosts**：把规则里的监听域名（`ListeningDomainNamesArray`，支持 `ip domain` 形式）指向 `127.0.0.1`，
    写在 `# --- Steam++Accelerator BEGIN/END ---` 标记之间，停止时只删这段。
    文件按字节保真读写（Latin-1 映射），不剥 BOM、不改写用户已有的非 ASCII 注释、不吞末尾空行；
  - **dns**：WinDivert 由引擎自己 P/Invoke，不再需要插件里的 `WinDivertInitHelper`（那依赖插件工程的嵌入资源）。
- **根证书**：优先写 `LocalMachine\Root`（需管理员），失败回退 `CurrentUser\Root`；
  检测安装状态时两个存储都算已安装。`cer` 或 `pfx` 任一缺失即重新生成——
  只有 `cer` 存在而 `pfx` 缺失会导致永远无法启动。

## 4. 加速规则

规则集落成**一个可手工编辑的 JSON 文件**（`<数据目录>/accelerate-rules.json`，`--rules` 可指定其它位置），
程序启动时只读文件、不联网。结构见 `src/.../RuleConfig.cs`。

| 项 | 说明 |
|---|---|
| 序列化 | camelCase、缩进、UTF-8 无 BOM，文件头带 `//` 编辑说明，**每条规则后附一行中文说明** |
| 反序列化 | `ReadCommentHandling = Skip` + `AllowTrailingCommas`，允许用户写 `//` 注释与尾随逗号 |
| 默认启用 | Steam + GitHub 共 17 条（规则名以 `Steam` / `Github` 开头，`RuleConfig.IsEnabledByDefault`） |
| `--refresh-rules` | 重新拉取官方规则，**已改动的 `enabled` 按名字保留** |

两个必须处理的边界：

- 配置**解析失败时不能静默覆盖用户的文件**，改为报错退出并保留原文件，只在文件不存在或
  显式 `--refresh-rules` 时才写。
- 标准 `JsonSerializer` **默认不支持注释**。写文件时手工拼 JSON 以插入 `//` 注释，
  读文件时必须开 `ReadCommentHandling = Skip`，否则用户手写的注释会让整个配置解析失败、
  改动被重置回默认。

官方接口 `POST https://api.steampp.net/accelerator/projectgroups` 匿名可用，返回
`{"🦓": [...], "🦄": 200, "🐴": null}` 包装的 58 个顶层项目（含子项 62 个节点）。

规则集里 `Google(Recaptcha) 验证码` 匹配 `google.com`、转发到 `www.recaptcha.net`，
命中后 Google 首页返回 404——这是**唯一匹配 `google.com` 的规则**，
即 `google.com` 本身无法加速，只有 API / 字体类域名能加速。详见 README。

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

## 6. 验证记录

Windows 11 x64 + .NET SDK 11.0.100-rc.3：

| 项目 | 结果 |
|---|---|
| `dotnet build` / `dotnet publish -r win-x64 --self-contained true` | 0 错误，产出 `Steam++.Accelerator.exe` + `WinDivert.dll` / `WinDivert64.sys` |
| `--mode proxyonly --port 26590` | 监听 `http://127.0.0.1:26590`；系统代理未被改动；经该端口访问命中域名 200 |
| `--mode system --port 26592` | 写入 `ProxyEnable=1` / `ProxyServer` / `ProxyOverride`；经该端口访问命中域名 200；`Ctrl+C` 退出后注册表逐项还原为原值 |
| `--mode pac --port 26591` | 写入 `AutoConfigURL`；`/proxy.pac` 返回 PAC 脚本；`Ctrl+C` 退出后该项被删除 |
| `--mode hosts --port 26554` | hosts 标记块内写入 15 个域名，保留 BOM 与 CRLF；`Ctrl+C` 退出后 hosts 文件与运行前**字节级一致** |
| `--mode dns`（非管理员） | 按预期报 `Win32Exception(5) 拒绝访问` 并中止启动 |
| `--mode dns`（管理员） | 成功加载 WinDivert；`github.com` / `api.github.com` / `store.steampowered.com` / `steamcommunity.com` 的 DNS 应答被改写为 `127.0.0.1` / `::1`；经本机 443 反代访问上述站点均返回 200 |
| 加速命中 | 本机 DNS 对 `steamcommunity.com` 返回被污染的 `162.125.1.8`，直连失败（`code=000`）；经代理命中加速节点 `23.200.230.67` 后返回 200 |
| 规则配置 | 改 `enabled` 后重启即生效且不联网；`--refresh-rules` 保留用户改动；配置损坏时报错退出且不覆盖原文件 |

### 修复过的问题

| 问题 | 现象 | 修复 |
|---|---|---|
| 系统代理 / PAC 写入静默失败 | 写 `.reg` 后调 `regedit /s`，但 regedit 路径取 `SpecialFolder.System`（`C:\WINDOWS\system32`）下并不存在的文件，异常被吞掉 | 改用 `Microsoft.Win32.Registry` 直接写 HKCU |
| 退出时覆盖用户原有代理配置 | 撤销逻辑把 `ProxyServer` / `ProxyOverride` 一律清空 | 写入前记录原值，退出时逐项还原 |
| 首次运行可能永久无法启动 | `cer` 存在但 `pfx` 缺失时不重新生成根证书，直接返回 `GetCertificatePackableFail` | 任一缺失即重新生成 |
| 非管理员无法信任根证书 | 只写 `LocalMachine\Root` | 失败回退 `CurrentUser\Root`，检测时两个存储都算已安装 |
| `--port` 在 proxyonly 模式被忽略 | 引擎把 proxyonly 归入反向代理分支，固定监听 443/80 | 引擎侧把 proxyonly 按 System 处理 |
| hosts 写入破坏原文件 | `ReadAllTextAsync` 剥掉 BOM、把非 ASCII 注释写成乱码、吞掉末尾空行 | 改为 Latin-1 字节保真读写，只替换标记块 |
| hosts 域名取值错误 | `ListenDomainNames ?? MatchDomainNames` 中前者是非空字符串，`??` 永不生效 | 改用 `ListeningDomainNamesArray`，支持 `ip domain` 形式 |
| 配置注释导致改动丢失 | 写入的 `//` 注释使 `JsonSerializer` 解析失败，程序随后把配置重置回默认 | 开 `ReadCommentHandling = Skip`；配置损坏时报错退出不覆盖 |

## 7. 已知限制

- 强杀进程（任务管理器结束）不会撤销系统代理设置；
- 无 GUI，所有开关都在命令行或规则配置文件；
- `ref/` 仍以子模块方式引入上游代码，仓库不是完全自包含的；
- 引擎自带的 SSH / Git 反向代理监听器沿用上游的「端口 22 / 9418 占用则顺延」策略；
- `dns` 模式只在 Windows 上可用且必须管理员运行；
- 无管理员权限时根证书落在 `CurrentUser\Root`，只对该用户生效。
