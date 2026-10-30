# Steam++ Accelerator · 仅代理版

> [Watt Toolkit（原 Steam++ / SteamTools）](https://github.com/BeyondDimension/SteamTools) 的**裁剪分支（fork）**：
> 只保留「网络加速 / 反向代理」能力，删除全部界面与 Steam 账号、令牌、游戏库等业务功能。
> 上游以 **GPL-3.0** 授权，本仓沿用同一许可，详见 [LICENSE](LICENSE)。

| 项 | 值 |
|---|---|
| 上游仓库 | https://github.com/BeyondDimension/SteamTools |
| 本仓（fork） | https://github.com/nxz1026/SteamTools |
| 上游基线 commit | `2195137ed9dd3db8cf0f5a056b7a0c33317fe2ed`（`develop`） |
| 许可 | GPL-3.0 |

## 这是什么

一个**没有界面、可以独立运行**的 Windows 控制台程序 `Steam++.Accelerator.exe`：

- 内置 YARP 反向代理与自签 CA（MITM 解密），按加速规则把命中域名的流量转发到国内加速节点；
- 可选地把系统流量引流过来：Windows 系统代理 / PAC / hosts / WinDivert 驱动级 DNS 拦截；
- 加速规则由一个 JSON 配置文件控制，随时可改。

与上游最大的结构差异：它**不再作为 GUI 主程序的 IPC 子进程**运行——没有命名管道、没有父进程签名校验、
不需要 `modules/Accelerator/` 目录布局，可以直接运行、写进计划任务或做成服务。

裁剪掉的：全部 Avalonia 界面工程、`src/BD.WTTS.Client`（约 3.8 万行基础库）与全部业务插件、
`src/BD.WTTS.Client.IPC` 与 `ref/dotnetCampus.Ipc`（改为引擎内的同名本地实现）、
17 个子模块中的 14 个、AppCenter 遥测、迅游 SDK、MSIX/发布工具链与多语言站点文件。
详见 [doc/proxy-only.md](doc/proxy-only.md)。

## 构建

需要 **.NET 11 预览 SDK**（见 `global.json`）。

```bash
git clone --recurse-submodules https://github.com/nxz1026/SteamTools.git
cd SteamTools

# Windows x64、自包含（目标机无需安装 .NET 运行时）
dotnet publish src/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy.csproj \
  -c Release -f net11.0 -r win-x64 --self-contained true -o dist/win-x64
```

框架依赖的构建把 `--self-contained true` 换成 `false` 即可。

> 子模块必须是完整的：`ref/WTTS.MicroServices.ClientSDK` 自身还有两个嵌套子模块，
> 所以要用 `--recurse-submodules`（或事后执行 `git submodule update --init --recursive`）。

## 使用

```
Steam++.Accelerator.exe [选项]

  --mode <system|hosts|pac|dns|proxyonly>  运行模式，默认 system
  --port <端口>                            代理监听端口，默认 26501（hosts / dns 模式固定 443）
  --ip <地址>                              监听地址，默认 127.0.0.1
  --dns <地址>                             上游 DNS，默认 223.5.5.5
  --no-doh                                 关闭 DoH
  --all                                    忽略配置文件里的开关，启用全部规则
  --exclude <项目名>[,<项目名>...]        按项目名排除规则，可重复指定
  --rules <路径>                          指定规则配置文件，默认 <数据目录>\accelerate-rules.json
  --refresh-rules                         从官方接口重新拉取规则并更新配置文件（保留已改动的开关）
  --help                                   显示帮助
```

| 模式 | 作用 | 权限 |
|---|---|---|
| `system`（默认） | 设置 Windows 系统代理（`ProxyEnable` / `proxyServer` / `ProxyOverride`） | 普通用户 |
| `pac` | 写系统 `AutoConfigURL`，由引擎自带 PAC 脚本分流 | 普通用户 |
| `hosts` | 把规则里的监听域名指向本机并做反向代理（监听 443） | 普通用户 |
| `dns` | WinDivert 驱动级 DNS 拦截 | **管理员** |
| `proxyonly` | 只监听代理端口，不改动任何系统设置 | 普通用户 |

`Ctrl+C` 停止：程序会先撤销自己写入的系统设置，再关闭代理引擎。

几点注意：

- `--ip` 默认只监听 `127.0.0.1`。改成 `0.0.0.0` 会把这个 MITM 代理暴露给同网段的所有设备，请谨慎。
- `hosts` / `dns` 模式由引擎在 `443` / `80` 上做反向代理，`--port` 对这两种模式无效。
- 首次运行会生成一张自签根证书并写入受信任的根证书存储：优先写 `LocalMachine\Root`（需管理员），
  无管理员权限时回退到 `CurrentUser\Root`（仅对本用户生效）。
- `dns` 模式依赖 `WinDivert.dll` / `WinDivert64.sys`，发布目录中已随程序附带；加载驱动需要管理员权限。
- 强杀进程（而非 `Ctrl+C`）不会撤销系统代理设置，需要手动关闭。

## 加速规则

规则（域名 → 加速节点）由**一个 JSON 配置文件**控制，随时可以改，改完重启程序即生效：

| 项 | 路径 |
|---|---|
| 规则配置 | `%LOCALAPPDATA%\Steam++Accelerator\accelerate-rules.json` |

程序启动时**只读这个文件，不联网**。首次运行（或用 `--refresh-rules`）时从官方接口
`POST https://api.steampp.net/accelerator/projectgroups`（匿名可用）拉一次规则并生成该文件，
之后完全以文件为准。`--refresh-rules` 会重新拉取并**保留你改过的 `enabled` 状态**。

每条规则带一个 `enabled` 开关，规则下面附有一行中文注释说明它的作用：

```json
{
  "name": "Steam 商店",
  "enabled": true,
  "matchDomainNames": "store.steampowered.com;api.steampowered.com;...",
  "forwardDomainNames": "steamstore.rmbgame.net",
  "port": 443,
  "proxyType": 0
},
// 本地代理：store.steampowered.com 等 5 个域名 直连 steamstore.rmbgame.net 取数据，保留原始 Host
```

**默认启用 Steam + GitHub**（共 17 条）。想启用别的规则（Twitch、Docker Hub、Google Fonts 等），
把对应条目的 `enabled` 改成 `true` 即可。文件支持 `//` 注释；
解析失败时程序会报错退出并保留原文件，不会静默覆盖你的改动。

### Google

`google.com` / `www.google.com` **本身无法加速**——规则集里唯一匹配它的是 `Google(Recaptcha) 验证码`，
转发到 `www.recaptcha.net`，用途是让页面里的 reCAPTCHA 组件能加载，启用它只会让 Google 首页变成 404。

能加速的是 Google 的 API / 字体类域名，实测均 200：

| 域名 | 转发到（国内镜像） |
|---|---|
| `ajax.googleapis.com` | `ajax.proxy.ustclug.org` |
| `fonts.googleapis.com` | `fonts.font.im` |
| `fonts.gstatic.com` | `gstatic.loli.net` |
| `themes.googleusercontent.com` | `themes.loli.net` |

> 规则的匹配字段是 `matchDomainNames`，按 `^` 锚定的正则匹配，因此 `raw.githubusercontent.com`
> 这类域名不在规则的匹配范围内，走直连，不受影响。

## 数据目录与日志

| 平台 | 路径 |
|---|---|
| Windows | `%LOCALAPPDATA%\Steam++Accelerator` |
| 其它 | `~/.config/Steam++Accelerator` |

| 文件 | 说明 |
|---|---|
| `accelerate-rules.json` | **规则配置**，改这个文件控制启用哪些规则 |
| `accelerate-projects.json` | 官方规则原始 JSON 缓存（`--refresh-rules` 时的兜底） |
| `Cache/Logs/accelerator-<日期>.log` | 运行日志（10 MB 轮转，保留 14 份） |

## 目录结构

```
src/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy/   代理引擎 + 独立宿主（Program.cs）
src/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy/RuleConfig.cs  规则配置文件读写
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
