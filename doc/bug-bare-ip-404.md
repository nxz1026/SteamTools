# 裸 IP 经代理返回 404（自建服务器无法访问）

> 状态：已定位根因并修复源码，待有 .NET SDK 的环境重新发布验证。

## 一、现象

开启 `--mode system` 代理后，访问**裸 IP 的 HTTPS 站点**（如用户自建服务器 `https://140.83.62.161/`）
无法访问（代理返回 **404**）；而普通域名站点（`example.com`）与加速站点（`github.com`）正常返回 200。
关闭代理后直接访问该 IP 正常（TCP 443 端口开放、可达）。

实测证据（发布版 `Steam++.Accelerator.exe --mode system`，curl 走 `127.0.0.1:26501`）：

| 目标 | 经代理结果 |
| --- | --- |
| `https://140.83.62.161/`（裸 IP，自建服务器） | **404（代理丢弃）** |
| `https://example.com/`（普通未匹配域名） | 200（默认直连转发） |
| `https://github.com/`（加速域名） | 200（加速转发） |

## 二、根因

代理在 system 模式下，对**全部 HTTPS 流量做本地 MITM**（TLS 终止 + 自签证书，见
`ListenOptionsExtensions.UseTls` 与 `TlsInvadeMiddleware`），解密后由
`HttpReverseProxyMiddleware` 路由：

- 命中加速规则（Steam/GitHub 等）→ 加速转发；
- 未命中但为**域名** → 赋 `defaultDomainConfig`，走默认直连转发（200）；
- 未命中且为**裸 IP** → 因 `TryGetDomainConfig` 中 `IsDomain(host)` 用
  `!IPAddress.TryParse(host, out _)` 把裸 IP 排除在外，返回 `false`，在
  `TwoLevelAgentEnable=false`（运行时默认值）时走 `await next(context)` → **404**。

即：`HttpReverseProxyMiddleware.TryGetDomainConfig` 的 `IsDomain` 判定把裸 IP 排除在
"默认直连转发"之外，导致裸 IP 被丢弃。这不是服务器的问题，而是代理"只加速、不全局转发"
的设计 + 裸 IP 被漏判。

> 注：这与 hosts/DNS 拦截模式无关。裸 IP 不经过 DNS，本就不受 DNS 污染绕行影响；
> 问题出在 HTTP 代理（26501）的 MITM 反向代理中间件。

## 三、修复（3 处）

1. `Services.Implementation/HttpServer/Middleware/HttpReverseProxyMiddleware.cs`
   - `TryGetDomainConfig`：未配置域名**含裸 IP** 也返回 `defaultDomainConfig`，纳入默认直连转发。
   - `InvokeAsync` 的 `defaultDomainConfig` 分支：裸 IP 跳过"DNS 污染检查"（IP 无需 DNS 解析），直接进入转发。

2. `Services.Implementation/Http/ReverseProxyHttpClientHandler.cs`
   - `ConnectAsync`：对**裸 IP 目标**放宽客户端证书校验（自建服务器常用自签 / IP-SAN 证书，
     严格校验会二次失败），域名目标仍走原有 `ValidateServerCertificate`。

## 四、验证

在有 .NET SDK 的机器重新发布后：

```powershell
# 发布（框架依赖，SelfContained=false）
dotnet publish src/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy/ `
  -c Release -r win-x64 -o C:\SteamToolsAccelerator

# 启动
Start-Process C:\SteamToolsAccelerator\Steam++.Accelerator.exe -ArgumentList '--mode system'

# 经代理访问裸 IP，应返回 200（修复前为 404）
curl -k -x http://127.0.0.1:26501 https://140.83.62.161/
```

预期结果：`code=200`。退出代理会自动还原系统代理（`ProxyEnable=0`、`ProxyServer`/`ProxyOverride` 删除）。

## 五、临时绕过（不想改代码时）

若暂时不想重新发布，可在 Windows「Internet 选项 → 连接 → LAN 设置」的代理"例外"里加入
`140.83.62.161`，让系统代理直接绕过该 IP（直连，不经过代理）。
注意：程序启动时会覆盖 `ProxyOverride` 为硬编码值，因此该例外需在每次代理启动后手动补加，
不如代码修复一劳永逸。
