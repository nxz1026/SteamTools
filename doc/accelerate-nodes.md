# 加速节点与上游：流量到底去了哪

回答一个常见的疑问：**这个代理没有配置「服务器地址」，那它把流量发给谁？**

结论：**没有一台统一的服务器。** 转发目标是**每条规则自带的** `forwardDomainNames`，
跟着规则集一起下发；规则集本身只在首次运行 / `--refresh-rules` 时从 `api.steampp.net` 拉一次，
拉完落盘，之后启动完全不联网。

本文数据采集于 **2026-10-30**，样本为当时的规则集（58 个顶层项目）。

---

## 1. 规则集的来源与生命周期

| 阶段 | 是否联网 | 说明 |
|---|---|---|
| 首次运行 | 是，一次 | `POST https://api.steampp.net/accelerator/projectgroups`，匿名可用 |
| 落盘 | — | `<数据目录>\accelerate-rules.json`，附中文注释，可手工编辑 |
| 日常启动 | **否** | 只读本地文件 |
| `--refresh-rules` | 是 | 重新拉取，按规则名保留用户改过的 `enabled` |

`api.steampp.net` 是**唯一**一个「由项目方提供的服务器」，而且只在上面两个时刻被访问，
与加速过程无关。规则文件头两行记录了来源：

```json
"generatedAt": "2026-10-30T09:46:53",
"source": "https://api.steampp.net/accelerator/projectgroups"
```

## 2. 每条规则携带的全部「服务器信息」

| 字段 | 含义 |
|---|---|
| `forwardDomainNames` | 转发目标——这才是「节点」 |
| `port` | 目标端口，绝大多数为 443 |
| `proxyType` | `0` 本地代理 / `1` 重定向 / `2` 直连成功 / `4` 反向代理 |
| `ignoreSslCertVerification` | 多数为 `true`，即本地代理不校验上游证书 |
| `fakeServerName` | 改写后的 SNI |
| `fakeUserAgent` | 改写后的 UA（个别规则伪装成 Googlebot） |
| `matchDomainNames` | 命中域名 |
| `listenDomainNames` | hosts / dns 模式下写入的域名 |

58 个项目的 `proxyType` 分布：`0`=46、`1`=11、`4`=1。**没有 `3`（服务端代理）**，
所以 `ServerSideProxyToken` 恒为 `null` 也不影响功能——这条链路根本不存在。

## 3. 17 条默认启用规则的三类节点

默认启用 Steam + GitHub 共 17 条，按转发目标性质分三类：

### 3.1 CNAME 别名（12 条）——不是第三方服务器

Steam 图片/静态/更新/社区/商店、Github Api/Assets/Education 这些规则转发到 `*.rmbgame.net`，
而 `rmbgame.net` 只是一个**指向上游官方的别名**，没有中转服务器参与传输。解析链实测：

```
steamstore.rmbgame.net  → steamstore-a.akamaihd.net → a1737.b.akamai.net → 23.219.78.45 / .48
githubapi.rmbgame.net   → api.github.com           → 20.205.243.168
githubdocs.rmbgame.net  → docs.github.com          → 185.199.108-111.154
```

两条证据说明「只是别名、不是缓存」：

- 用 `SNI=store.steampowered.com` 连 `steamstore.rmbgame.net`，返回的证书是
  **`CN=a248.e.akamai.net`（DigiCert 签发）**，即 Akamai 的真证书，不是中间人重签。
- 终点是 Akamai 边缘节点（`23.219.78.x`），拿到的就是官方内容。

**这类规则绕过的是 DNS 污染，不是网络封锁。** 域名解析被污染时，把域名换成未被污染的
同义域名，让解析回到真实 IP。

### 3.2 裸 IP 直连（5 条）——完全绕开 DNS

Github Dev / App / .io / UserContent / Push 五条直接写 IP：

| 规则 | 转发目标 |
|---|---|
| Github Dev | `20.43.185.14` |
| Github App | `140.82.112.29` |
| Github .io | `185.199.110.153` |
| Github UserContent | `23.235.37.133` |
| Github Push | `20.207.73.82` |

字面 IP，`forwardDomainNames` 直接是 IP 时连 DNS 查询都不发生。

### 3.3 第三方反代（启用 1 条，**当前不可用**）

`Steam 白山云CDN 修复` → `cdn.queniuqe.com`。这是唯一一条真正依赖第三方服务器的启用规则，
而 **`cdn.queniuqe.com` 当前 DNS 解析失败**，等于该规则空转。

> 处理建议：把这条的 `enabled` 改成 `false`，或等上游规则集修复。
> 不影响其它 16 条。

## 4. 连通性实测

11 个目标 TCP 443 全部可连，唯一失败的是上面那条 `cdn.queniuqe.com`。

| 节点 | RTT（3 次取样均值） |
|---|---|
| `githubapi.rmbgame.net` | 87.85 ms |
| `20.43.185.14`（Github Dev） | 115.70 ms |
| `steamimage.rmbgame.net` | 151.03 ms |
| `steamstore.rmbgame.net` | 252.25 ms |
| `githubdocs.rmbgame.net` | 385.38 ms |

`steamstore` 慢的原因是 IPv4/IPv6 差异：**IPv6 60.39 ms / IPv4 174.83 ms**，
双栈环境下解析结果会明显影响体验。

## 5. Google 能不能加速

**不能。** `google.com` 本身没有任何可用规则。

规则集里与 Google 相关的 4 条，**全部是 `enabled: false`**：

| 规则 | 匹配 | 转发到 | 作用 |
|---|---|---|---|
| `Google(Recaptcha) 验证码` | `google.com` | `www.recaptcha.net` | 让页面里的 reCAPTCHA 组件能加载 |
| `ajax.googleapis.com` | 同名 | `ajax.proxy.ustclug.org` | JS 库镜像 |
| `fonts.googleapis.com` | 同名 | `fonts.font.im` | 字体镜像 |
| `themes.googleusercontent.com` | 同名 | `themes.loli.net` | 主题资源镜像 |

注意第一条：它的转发目标是 `www.recaptcha.net`，而 `recaptcha.net` 是 Google 给验证码场景
准备的**独立域名**，只有验证码接口，没有搜索。所以即便手动启用这条，`www.google.com`
也只会被重定向到 recaptcha 站点，得不到搜索页面——**启用它不能拿来翻墙**，
反而可能让依赖 reCAPTCHA 的页面出问题。

能加速的是后三条的 API / 字体 / 静态资源类域名。要启用就把 `enabled` 改成 `true`，
同时确认 `Google(Recaptcha) 验证码` 保持 `false`。

## 6. 一个必须知道的副作用：全流量 MITM

`system` / `pac` 模式下，**所有 HTTPS 流量都会进入本地 MITM 解密**，不只是命中规则的域名。
未命中规则的请求会原样直连回源，但**解密这一步已经发生**。日志里的证据：

```
WARN|HttpReverseProxyMiddleware|域名 raw.githubusercontent.com 可能已经被 DNS 污染…
INFO|HttpForwarder|Proxying to https://raw.githubusercontent.com/
INFO|HttpForwarder|Proxying to https://imapi.douyin.com/v1/stranger/get_conversation_list
```

`raw.githubusercontent.com`、`imapi.douyin.com` 都不在任何规则里，照样被解密后转发。

叠加「自签根证书已装入 `LocalMachine\Root`」，等于这台机器上任意 HTTPS 流量对本程序
都是明文可见的。功能上没问题，但这是自建 MITM 的固有性质，需要知情。
