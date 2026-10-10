# vpngate_proxy for Windows

[下载最新版 ZIP](https://github.com/snakexgc/vpngate_proxy/releases/latest) · [GitHub Actions](https://github.com/snakexgc/vpngate_proxy/actions)

原生 Windows GUI，使用 VPNGate 公共 OpenVPN 节点，并通过指定的上游代理传输 OpenVPN 数据。支持本地 HTTP / SOCKS5 混合代理，以及 Windows TUN 全局公网流量转发。

## 使用

解压 `dist/vpngate_proxy-win-x64.zip`，打开文件夹中的 `vpngate_proxy.exe`。便携包包含运行时，无需安装 .NET、OpenVPN 或更改 v2rayN 配置。

1. VPN 上游代理默认是 `http://127.0.0.1:10808`。支持 HTTP、HTTPS、SOCKS5、SOCKS4、SOCKS4a；用户名和密码可选。SOCKS5h 和 socks 地址会归一化为 SOCKS5。
2. 本地监听默认是 `127.0.0.1:10800`，同一端口提供 HTTP 和 SOCKS5。
3. 点击「获取 / 刷新节点」，从 VPNGate 官方 HTTPS 接口获取所有公开节点。上游代理同样用于获取列表。
4. 选择节点，点击「连接选中节点」。连接成功后，可让浏览器或其他程序使用 `http://127.0.0.1:10800` 或 `socks5://127.0.0.1:10800`。
5. 双击另一个节点，或选择后点击「切换选中节点」，即可切换。切换会关闭旧连接，已有传输需要重新建立。
6. 点击独立的「开启 TUN」按钮即可创建 `VPNGate-TUN` 网卡，再次点击「关闭 TUN」即可移除。不需要先连接 OpenVPN；未连接时显示「等待 OpenVPN」，连接节点后自动开始转发。OpenVPN 正在连接、已连接或已断开时，TUN 按钮都可操作。开启 / 关闭 TUN 不重建 OpenVPN、不关闭本地代理；断开 / 切换节点也不会关闭 TUN。TUN 开启且没有节点连接时，公网流量等待节点，不自动绕过 VPN。
7. 「测全部」会对当前获取的全部节点逐一建立独立 OpenVPN 会话；「测选中」支持 Ctrl / Shift 多选。测试不替换当前使用的连接。可随时取消，排队工作会停止，测试会话自动清理。
8. 使用「网络延时 ↑ / ↓」「国家 A → Z」，或点击表头排序。失败及未测试节点排在延时结果之后。国家排序使用 ISO 国家代码。国家筛选和搜索可组合使用。
9. 「节点类型」位于 IP 与网络延时之间，显示「机房（官方中继）」「机房」「住宅（推测）」或「未知」。优先使用源中的 public-vpn-* 名称和已知官方 219.100.37.0/24 网段识别官方中继，不对这批节点额外查询。其他节点通过 IP-API 查询，信息按 IP 保存到本地，有效期为查询成功后的 7 天；期限内刷新节点、切换节点、重新启动程序都会直接复用缓存。只查询没有信息的新 IP 或已过期 IP；失败或空结果冷却 5 分钟后才允许重试。每批最多 100 个不同 IP，同一 IP 去重；遵守并持久保存接口的限流等待时间。悬停类型可查看识别依据、运营商、组织、ASN、来源和时间；支持按类型排序、搜索，并包含在 CSV 导出中。类型查询在后台运行，不阻止连接或测速。

[VPNGate 原始 CSV](https://www.vpngate.net/api/iphone/) 没有独立的住宅 / 机房字段；LogType 是日志策略。官方中继通过源中的名称 / IP 规则识别，不能据此认定其余节点全是住宅。其他节点使用 [IP-API 的 hosting 标记](https://ip-api.com/docs/api:batch)：true 表示托管 / 数据中心，false 只表示未标记为机房，因此住宅显示为推断，不能证明 IP 一定属于家庭宽带。缺失标记、查询失败或限流时显示未知。当前免费查询接口用于个人非商业用途，无需 API Key；只查询列表中的公共节点 IP，不提交代理密码或 OpenVPN 配置。

HTTP / HTTPS / SOCKS4 上游只支持 OpenVPN **TCP** 传输。UDP 节点仍显示在列表中，但测试时会明确说明不兼容；SOCKS5 上游需支持 UDP ASSOCIATE 才能连接 UDP 节点。不会把 UDP 节点伪装成 TCP。

GUI 的「网络延时」是 OpenVPN 连接完成后，经隧道访问 HTTPS 测试地址的请求往返时间。先预热 DNS / CONNECT / TLS，再取三次请求的中位数。默认测试地址是 `https://www.gstatic.com/generate_204`，可修改。它与 VPNGate 公布的 Ping 分列展示；不使用节点端口连接耗时冒充网络延时。并发默认 4，可配置 1–12。

## 与现有 v2rayN 共存

不会停止 v2rayN / Xray，不读取或修改其配置，不更改 Windows 系统代理。TUN 使用独立网卡；识别 v2rayN、Xray、sing-box、mihomo 等上游客户端进程的公网连接，并从物理网卡转发以避免套环。停用 / 退出时通过独立隐藏控制台向本软件内核发送正常退出信号，恢复本软件创建的 TUN 路由；进程组还负责异常退出时清理本软件子进程。

OpenVPN 代理和 TUN 使用两个独立内核。TUN 通过配置的本地 SOCKS5 入口接入 OpenVPN，所以可以随时单独开关。普通用户 GUI 首次开启 TUN 时只提升专用后台进程，GUI 和已连接的 OpenVPN 保持运行；本次软件运行期间反复开关复用已授权进程，无需反复重启 GUI。退出软件会同时清理两个会话。

TUN 模式另提供本机 TCP / UDP DNS 入口，经 OpenVPN 内的加密 DNS 上游解析。默认监听 `127.0.0.1:53`；存在已运行的 Wintun 网卡时，使用该网卡已有的本机 IPv4 地址，以避开其对回环 DNS 的阻断。只绑定服务，不修改该网卡或其客户端配置。为确保 Windows 系统 DNS 不被其他 TUN 分流，连接期间添加本软件独立标识的临时 NRPT 规则；断开后只删除这条规则。独立守护进程也会在程序意外退出时恢复；若电脑意外重启，软件下次启动会检查自己的恢复记录，必要时请求管理员权限清理。没有修改其他网卡的 DNS、其他 NRPT 规则或 v2rayN 配置，也不叠加全局 WFP DNS 阻断规则。选定地址上的端口 53 必须可用。

如有多个有效的物理网卡，当前自动优先选择带 IPv4 网关的以太网，其次选择 Wi-Fi。节点主要提供 IPv4 出口；TUN 捕获 IPv6 后交给同一 VPN 端点，节点不支持时返回失败，不自动绕过 VPN。公共节点的可用性随时间变化。

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
```

TUN 实测运行同一个 `VPNGate.SelfTest.exe --tun --output artifacts\tun-tests.json` 即可；普通用户运行时会请求后台进程的管理员授权。检查覆盖未连接节点时开关 TUN、已连接时切换且保持同一 OpenVPN 进程、断开节点保持 TUN、系统 DNS 及出口 IP、网卡和 DNS 清理、v2rayN / Xray 与 10808 服务。GUI 验证可运行 `vpngate_proxy.exe --ui-smoke artifacts\tun-ui --tun-ui`，同样无需提升整个 GUI。

`test-ui-logs.ps1` 是无需管理员和网络的 GUI 日志压力回归检查，已加入自动发版流程；`--tun-only-ui` 则在没有 OpenVPN 会话的情况下先点击 TUN 按钮，连续开关三次，验证窗口响应、后台复用以及网卡和临时 DNS 规则恢复。

## 实现

- `src/VPNGate.Desktop`：WPF 原生中文 GUI、设置、筛选、排序、切换、并发测速、CSV 和日志。
- `src/VPNGate.Core`：VPNGate CSV 获取及解析、OpenVPN profile 转换、sing-box 配置、会话生命周期、DPAPI、HTTPS 测速。
- `tests/VPNGate.SelfTest`：无需外部测试框架的自动检查及真实网络 / TUN 验证。
- `runtime`：独立内核和 Wintun，未引用 v2rayN 的可执行文件或配置。

内核为 [sing-box 1.14.2](https://github.com/SagerNet/sing-box/releases/tag/v1.14.2)，其 [OpenVPN Client](https://sing-box.sagernet.org/configuration/endpoint/openvpn-client/) 使用内部网络栈；[TUN](https://sing-box.sagernet.org/configuration/inbound/tun/) 通过 [Wintun](https://www.wintun.net/) 提供 Windows 虚拟网卡。节点来源为 [VPNGate 官方接口](https://www.vpngate.net/api/iphone/)。授权及来源见 `THIRD-PARTY-NOTICES.md`。
