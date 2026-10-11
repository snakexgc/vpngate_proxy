# vpngate_proxy for Windows

[下载最新版 ZIP](https://github.com/snakexgc/vpngate_proxy/releases/latest) · [GitHub Actions](https://github.com/snakexgc/vpngate_proxy/actions)

原生 Windows GUI，使用 VPNGate 公共 OpenVPN 节点，并通过指定的上游代理传输 OpenVPN 数据。本地 HTTP / SOCKS5 混合代理和 Windows TUN 支持全局、规则、直连三种流量模式。

## 使用

解压 `dist/vpngate_proxy-win-x64.zip`，打开文件夹中的 `vpngate_proxy.exe`。便携包包含运行时，无需安装 .NET、OpenVPN 或更改 v2rayN 配置。

1. VPN 上游代理默认是 `http://127.0.0.1:10808`。支持 HTTP、HTTPS、SOCKS5、SOCKS4、SOCKS4a；用户名和密码可选。SOCKS5h 和 socks 地址会归一化为 SOCKS5。
2. 本地监听默认是 `127.0.0.1:10800`，同一端口提供 HTTP 和 SOCKS5。
3. 软件维护可用节点池：首次打开且池为空时立即从 VPNGate 官方 HTTPS 接口获取节点，此后每小时自动获取一次；「立即刷新节点池」可手动触发。每轮合并新节点和旧池，再后台测试全部候选，只保留 OpenVPN 握手及 HTTPS 验证均成功的节点。
4. 选择池内节点，点击「连接选中节点」。本地 `http://127.0.0.1:10800` / `socks5://127.0.0.1:10800` 入口随主核心启动；未连接节点时支持直连和国内规则分流，连接节点后可转发其余流量。
5. 双击另一个节点，或选择后点击「切换选中节点」，即可切换。切换会关闭旧连接，已有传输需要重新建立。
6. 「开启 TUN」将 TUN 入口加入同一个 sing-box 主核心，与 OpenVPN 端点、上游代理和本地入口共同运行；「关闭 TUN」从统一配置中移除网卡。本地入口无需节点即可启动；TUN 未连接节点时，直连模式可联网，规则模式允许国内和局域网直连，其余等待节点。首次启用 TUN 只提升后台监督进程，GUI 保持普通用户权限。节点切换、断开节点和 TUN 开关会重载主核心配置；已选择的节点在 TUN 开关后重新握手，已有连接需重新建立。断开节点保留本地入口及已开启的 TUN。
7. 「复测节点池」重新验证整个池；「测选中」支持 Ctrl / Shift 多选，失败节点从池中删除。并发自动按 `min(20, ceil(候选节点数 / 30))` 计算：1–30 个为 1 路，31–60 个为 2 路，以此类推，最多 20 路。后台测试不会替换主连接；当前连接的节点优先复用其 VPN 测速入口，避免重复会话被节点拒绝。可随时取消，正在运行的测试会话自动清理，并保留上一轮完整可用池。
8. 使用「网络延时 ↑ / ↓」「国家 A → Z」，或点击表头排序。失败及未测试节点排在延时结果之后。国家排序使用 ISO 国家代码。国家筛选和搜索可组合使用。
9. 「节点类型」位于 IP 与网络延时之间，显示「机房（官方中继）」「机房」「住宅（推测）」或「未知」。优先使用源中的 public-vpn-* 名称和已知官方 219.100.37.0/24 网段识别官方中继，不对这批节点额外查询。其他节点通过 IP-API 查询，信息按 IP 保存到本地，有效期为查询成功后的 7 天；期限内刷新节点、切换节点、重新启动程序都会直接复用缓存。只查询没有信息的新 IP 或已过期 IP；失败或空结果冷却 5 分钟后才允许重试。每批最多 100 个不同 IP，同一 IP 去重；遵守并持久保存接口的限流等待时间。悬停类型可查看识别依据、运营商、组织、ASN、来源和时间；支持按类型排序、搜索，并包含在 CSV 导出中。类型查询在后台运行，不阻止连接或测速。

[VPNGate 原始 CSV](https://www.vpngate.net/api/iphone/) 没有独立的住宅 / 机房字段；LogType 是日志策略。官方中继通过源中的名称 / IP 规则识别，不能据此认定其余节点全是住宅。其他节点使用 [IP-API 的 hosting 标记](https://ip-api.com/docs/api:batch)：true 表示托管 / 数据中心，false 只表示未标记为机房，因此住宅显示为推断，不能证明 IP 一定属于家庭宽带。缺失标记、查询失败或限流时显示未知。当前免费查询接口用于个人非商业用途，无需 API Key；只查询列表中的公共节点 IP，不提交代理密码或 OpenVPN 配置。

HTTP / HTTPS / SOCKS4 上游只支持 OpenVPN **TCP** 传输。UDP 节点仍显示在列表中，但测试时会明确说明不兼容；SOCKS5 上游需支持 UDP ASSOCIATE 才能连接 UDP 节点。不会把 UDP 节点伪装成 TCP。

GUI 的「节点网络延时」始终是 OpenVPN 连接完成后，经隧道访问 HTTPS 测试地址的请求往返时间。使用独立的回环测速入口强制走节点，即使当前选择直连模式或测试地址匹配国内规则，也不会将直连延时或本机出口 IP 当作节点结果。先预热 DNS / CONNECT / TLS，再取三次请求的中位数。默认测试地址是 `https://www.gstatic.com/generate_204`，可修改。它与 VPNGate 公布的 Ping 分列展示；不使用节点端口连接耗时冒充网络延时。验证并发按节点池大小自动计算，最多 20 路。

## 流量模式与规则更新

GUI 的「流量模式」可在未连接时选择、或在连接后切换；模式切换不会重建主核心。分流规则通过 sing-box 的 `clash_mode` 和二进制 `rule_set` 生效，本地代理和 TUN 使用相同模式。模式与新规则作用于新建连接，已有长连接保持原来的出口。

| 模式 | 公网流量 | 没有 OpenVPN 节点时的 TUN |
| --- | --- | --- |
| 全局 | 经 OpenVPN 节点，再由上游代理承载传输 | 等待节点，不自动改为直连 |
| 规则（默认） | 中国域名、中国 IP 直连，其他经 OpenVPN | 国内流量可用，其他等待节点 |
| 直连 | 经物理网卡直连 | 无需节点即可联网 |

局域网始终直连；现有 v2rayN / Xray 等上游客户端的传输连接从物理网卡发出，避免环路。国内判定采用 SagerNet 的 [geosite-cn](https://github.com/SagerNet/sing-geosite/tree/rule-set) 和 [geoip-cn](https://github.com/SagerNet/sing-geoip/tree/rule-set)。TUN 会嗅探 HTTP / TLS / QUIC 域名，并结合目标 IP 匹配；DNS 也按模式分流，直连和国内域名使用绑定物理网卡的加密 DNS，其他使用节点内的加密 DNS。

便携包内置经 SHA256 校验的初始规则，首次运行不依赖下载规则。规则缓存位于 `%LOCALAPPDATA%\VPNGateXray\routing-rules`。在「分流规则」中可查看检查 / 更新时间、启用或停用自动更新、调整 1–168 小时的更新间隔（默认 24 小时），或点击「立即更新规则」。运行时每小时检查是否到期，也会在启动和修改更新策略时检查。查询通过已配置的上游代理完成，使用 ETag 条件请求复用未变的数据；自动检查失败至少等待 15 分钟再重试。

两个规则文件均下载、经过实际 sing-box 解析校验后才替换缓存。下载、格式校验或安装失败会保留已有规则并在 GUI 显示失败状态。文件被替换后，正在运行的内核自动重新载入，无需断开节点、重启网卡或再次授权。手动更新不受自动更新开关和更新间隔限制。本地 HTTP / SOCKS5 入口随主核心启动；TUN 与 OpenVPN 使用同一配置，可以在未连接节点时进行规则分流或直连。

## 与现有 v2rayN 共存

不会停止 v2rayN / Xray，不读取或修改其配置，不更改 Windows 系统代理。TUN 使用独立网卡；识别 v2rayN、Xray、sing-box、mihomo 等上游客户端进程的公网连接，并从物理网卡转发以避免套环。停用 / 退出时通过独立隐藏控制台向本软件内核发送正常退出信号，恢复本软件创建的 TUN 路由；进程组还负责异常退出时清理本软件子进程。

主连接只有一个 sing-box 进程，配置中直接包含 mixed / TUN 入站、OpenVPN 端点和上游 HTTP / SOCKS 出站，不再经过第二个内核或本地 SOCKS 中转。后台验证按并发数创建临时测试核心，验证结束后立即释放；这些测试会话不启用 TUN、不改变系统路由。本次运行已授权的主核心监督进程会被后续重载复用，退出时统一清理主核心与测试会话。

TUN 模式另提供本机 TCP / UDP DNS 入口，按所选模式经直连或 OpenVPN 内的加密 DNS 上游解析。默认监听 `127.0.0.1:53`；存在已运行的 Wintun 网卡时，使用该网卡已有的本机 IPv4 地址，以避开其对回环 DNS 的阻断。只绑定服务，不修改该网卡或其客户端配置。为确保 Windows 系统 DNS 不被其他 TUN 分流，TUN 开启期间添加本软件独立标识的临时 NRPT 规则；关闭 TUN 后只删除这条规则。独立守护进程也会在程序意外退出时恢复；若电脑意外重启，软件下次启动会检查自己的恢复记录，必要时请求管理员权限清理。没有修改其他网卡的 DNS、其他 NRPT 规则或 v2rayN 配置，也不叠加全局 WFP DNS 阻断规则。选定地址上的端口 53 必须可用。

如有多个有效的物理网卡，当前自动优先选择带 IPv4 网关的以太网，其次选择 Wi-Fi。节点主要提供 IPv4 出口；需要经节点转发的 IPv6 流量同样交给 VPN 端点，节点不支持时返回失败。明确选择直连或匹配直连规则的流量从物理网卡发出。公共节点的可用性随时间变化。

## 数据与诊断

- 设置、节点缓存、历史测速：`%LOCALAPPDATA%\VPNGateXray`。
- 密码通过当前 Windows 用户的 DPAPI 加密，日志自动隐藏代理密码。
- 运行目录和配置仅授予当前用户、管理员及 SYSTEM 访问权限。
- 临时内核配置在断开后删除。节点配置仅解析内嵌证书与必要传输参数，不运行其脚本、插件或外部文件。
- GUI 支持导出当前筛选结果为 CSV，节点失败原因可在行提示和日志查看。
- 内核日志在后台读取和批量写盘，界面每 250 毫秒合并刷新最近 300 行。高流量时限制日志缓冲，避免未连接节点时的连接失败日志堵塞 TUN 开关和窗口消息；写盘缓冲溢出会记录省略行数。
- 绑定 `0.0.0.0` 会将无认证本地代理开放给其他设备；默认仅监听回环地址。

## 从源码构建

要求 Windows 10 / 11 x64 和 .NET 10 SDK。使用现有代理下载经 SHA256 校验的固定内核和签名驱动：

```powershell
.\scripts\build.ps1
```

输出 `dist\vpngate_proxy\vpngate_proxy.exe` 及 `dist\vpngate_proxy-win-x64.zip`。如果依赖已就绪，可用 `-SkipRuntime`。

GitHub Actions 使用 Windows 托管 runner 和 .NET 10，在每次分支推送后自动下载并验证运行时、编译、执行离线检查、打包并发版。Release 标签为 `v版本号-build.构建编号`，仅上传 `vpngate_proxy-win-x64.zip`。默认分支生成正式版，其他分支生成预发行版；也可在 Actions 中手动运行。工作流不依赖本机的 10808 代理，不需要配置个人访问令牌，使用仓库自带的 GITHUB_TOKEN。

在无本地代理的构建环境可运行 `.\scripts\build.ps1 -ProxyUrl ''`。项目重命名后，用户数据目录仍沿用 `%LOCALAPPDATA%\VPNGateXray`，以保留已有设置、IP 信息缓存和 DNS 恢复记录。

```powershell
dotnet build vpngate_proxy.sln -c Release
.\tests\VPNGate.SelfTest\bin\Release\net10.0-windows\VPNGate.SelfTest.exe --output artifacts\unit-tests.json
.\tests\VPNGate.SelfTest\bin\Release\net10.0-windows\VPNGate.SelfTest.exe --live --count 4 --output artifacts\live-tests.json
.\tests\VPNGate.SelfTest\bin\Release\net10.0-windows\VPNGate.SelfTest.exe --live --all --output artifacts\all-nodes.json
.\src\VPNGate.Desktop\bin\Release\net10.0-windows\vpngate_proxy.exe --ui-smoke artifacts\ui
.\scripts\test-ui-logs.ps1
.\src\VPNGate.Desktop\bin\Release\net10.0-windows\vpngate_proxy.exe --ui-smoke artifacts\tun-startup --tun-only-ui
.\tests\VPNGate.SelfTest\bin\Release\net10.0-windows\VPNGate.SelfTest.exe --routing --output artifacts\routing-tests.json
.\src\VPNGate.Desktop\bin\Release\net10.0-windows\vpngate_proxy.exe --ui-smoke artifacts\routing-ui --routing-ui
.\src\VPNGate.Desktop\bin\Release\net10.0-windows\vpngate_proxy.exe --ui-smoke artifacts\routing-connected --routing-connected-ui
```

`VPNGate.SelfTest.exe --pool` 验证节点池合并、失效剔除、小时刷新时机、取消保留、去重和实际 20 路并发上限；`--unified` 验证统一主核心与本地入口，无需管理员或外部网络；`--tun` 验证同一核心中的网卡开关及恢复，需要后台管理员授权。GUI 的完整实测使用 `vpngate_proxy.exe --ui-smoke artifacts\pool-live --pool-ui`：获取并验证真实节点池、连接池内节点、启用合并 TUN、核对 TUN 与节点出口一致、关闭 TUN 后重连以及断开节点后保留本地入口。

`test-ui-logs.ps1` 是无需管理员和网络的 GUI 日志压力回归检查，已加入自动发版流程；`--tun-only-ui` 则在没有 OpenVPN 会话的情况下先点击 TUN 按钮，连续开关三次，验证窗口响应、后台复用以及网卡和临时 DNS 规则恢复。

## 实现

- `src/VPNGate.Desktop`：WPF 原生中文 GUI、设置、筛选、排序、切换、并发测速、CSV 和日志。
- `src/VPNGate.Core`：VPNGate CSV 获取及解析、OpenVPN profile 转换、sing-box 配置、会话生命周期、DPAPI、HTTPS 测速。
- `tests/VPNGate.SelfTest`：无需外部测试框架的自动检查及真实网络 / TUN 验证。
- `runtime`：独立内核和 Wintun，未引用 v2rayN 的可执行文件或配置。

内核为 [sing-box 1.14.2](https://github.com/SagerNet/sing-box/releases/tag/v1.14.2)，其 [OpenVPN Client](https://sing-box.sagernet.org/configuration/endpoint/openvpn-client/) 使用内部网络栈；[TUN](https://sing-box.sagernet.org/configuration/inbound/tun/) 通过 [Wintun](https://www.wintun.net/) 提供 Windows 虚拟网卡。节点来源为 [VPNGate 官方接口](https://www.vpngate.net/api/iphone/)。授权及来源见 `THIRD-PARTY-NOTICES.md`。
