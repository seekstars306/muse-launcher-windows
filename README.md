# Muse 登录器 for Windows

安卓版 [muse-launcher](https://github.com/seekstars306/muse-launcher) 的 Windows 版，基于 WPF + Microsoft Edge WebView2，直接加载 muse.ai 网页版。每个账号使用独立的用户数据目录，Cookie 和网页存储不共用。

> **v1.0.4**：[下载正式版](https://github.com/seekstars306/muse-launcher-windows/releases/tag/v1.0.4)。已通过 245 项核心与本机模拟代理回归测试，并由维护者在 Windows 上完成 WebView2 冒烟验收。旧 v1.0.3 中“`--proxy-auth-scheme=basic` 可以提供 SOCKS5 账密认证”的说法有误；v1.0.4 改用真实的本机协议桥接，不依赖该参数。

## 下载与运行

- [运行包：MuseLauncher-win-x64-v1.0.4.zip](https://github.com/seekstars306/muse-launcher-windows/releases/download/v1.0.4/MuseLauncher-win-x64-v1.0.4.zip)
- [Windows 冒烟测试包：MuseLauncher-WindowsSmoke-v1.0.4.zip](https://github.com/seekstars306/muse-launcher-windows/releases/download/v1.0.4/MuseLauncher-WindowsSmoke-v1.0.4.zip)
- 升级前退出旧版，并备份 `%APPDATA%\MuseLauncher` 和 `%LOCALAPPDATA%\MuseLauncher`。
- 解压 Windows x64 自包含包，运行 `MuseLauncher.exe`；不需要单独安装 .NET，仍需要 Microsoft [WebView2 Evergreen Runtime](https://go.microsoft.com/fwlink/p/?LinkId=2124703)。
- 添加账号后在 muse.ai 页面登录。服务端可能使会话失效，本程序不保证账号永不掉登录。
- 左侧头像条和展开账号列表可切换账号；展开列表与网页左右并列，不覆盖 WebView2 原生窗口。
- 设置可修改名称、头像、标签及代理；头像支持 PNG/JPG/BMP/GIF 图片，保存采用草稿，失败时不污染正在使用的配置。
- 搜索按名称和标签过滤。`F11` 切换全屏；`Esc` 收起列表或退出全屏；展开列表可完全隐藏，鼠标移入窗口左边缘或按 `Ctrl+Shift+B` 恢复。
- 默认最多保留 4 个浏览器会话。淘汰会真实销毁控件和代理隧道，但保留账号目录，切回时重新加载。
- 删除账号会先等待其浏览器释放、保存列表，再删除对应目录；目录被占用时明确提示并保留，不会悄悄拿给新账号。

## 每账号不同代理

每个有代理的账号拥有单独的本机监听端口和单独的上游配置：

```text
账号 A 的 WebView2 → 127.0.0.1:随机端口 A → 上游代理 A
账号 B 的 WebView2 → 127.0.0.1:随机端口 B → 上游代理 B
```

本机桥接只接受带随机本机会话认证的请求，只连接配置的上游代理，不在上游失败后改为直连。服务商账密只由桥接向上游提交，不会作为浏览器启动参数，也不会用于普通网站认证。

### 输入格式

| 协议 | 示例 | 认证 |
|---|---|---|
| HTTP | `http://user:password@gateway.example:8000` | Basic 上游代理认证；无账密也可 |
| HTTPS 代理 | `https://user:password@gateway.example:443` | 先验证上游 TLS 证书，再认证/CONNECT |
| SOCKS5 | `socks5://user:password@gateway.example:1080` | 桥接实现 SOCKS5/RFC 1929 username/password；无认证也可 |
| 本机 HTTP | `127.0.0.1:7897` | 默认补 HTTP 协议 |

- 密码中的 `@`、`/`、`#` 等 URI 特殊字符需要百分号编码，例如 `p@ss:word` 写为 `p%40ss%3Aword`。
- 只接受代理端点，不接受路径、query、fragment、控制字符、不合法端口；不支持 SOCKS4、Digest/NTLM 上游代理认证。
- 同网关换密码、国家或 session 用户名也会更新环境，不能继续使用旧认证缓存和隧道。
- 测活与浏览器使用同一个桥接实现，验证代理协议、认证和目标 HTTPS 请求，不把“TCP 端口开放”误报为认证通过。
- 本机端口探测只表示找到监听端口；支持探测 7890、7897、7891、7892，之后仍需测活确认。

### 保存与重建

WebView2 的代理参数在浏览器环境启动时指定。配置变化后，本程序先销毁旧环境并等待旧浏览器进程退出，再在原账号目录创建新环境。等待超时会显示错误并提供重试，不强行杀进程、不清除登录数据。

账号代理留空表示跟随系统网络；已有 Clash 系统代理/TUN 也可能影响流量路由。为不同账号分配不同出口时，应为每个账号明确填写各自上游，并核对供应商的实际出口行为。

### 代理绕过控制的边界

配置页的“取消浏览器本机地址的代理绕过”保留旧 `ForceProxy` 字段，作用是取消 Chromium 的 loopback bypass。启用该选项却没有有效代理会明确报错，不默认转为直连。

**它不是 VPN、系统防火墙或全流量 kill switch**，不保证 WebRTC/UDP、其他软件及系统服务走该代理。HTTP Basic 和 SOCKS5 的账密本身不加密；远程不可信网络优先选择支持 TLS 的 HTTPS 代理。

## 数据和恢复

| 数据 | 位置 |
|---|---|
| 账号配置 | `%APPDATA%\MuseLauncher\accounts.json` |
| 自定义头像 | 与账号配置同目录的 `Avatars\` |
| 文件级滚动备份 | 同目录 `accounts.json.bak` |
| 损坏文件留底 | 同目录独立的带时间/随机标识文件 |
| 每账号 Cookie、网页存储 | `%LOCALAPPDATA%\MuseLauncher\Profiles\acct_N\` |
| 运行日志 | `%LOCALAPPDATA%\MuseLauncher\log.txt` |

兼容旧账号 JSON 字段及 `acct_N` 目录。加载时会校验正数、唯一 ID 并修复陈旧计数，新分配 ID 避开已存在的账号目录。完整文件损坏且没有可恢复备份时停止启动并保留原文件，不当作首次运行。

文件写入成功后才发布内存状态；磁盘满、权限或文件锁错误会报告，不能显示虚假的保存成功。备份仍不等于保险，请在升级前退出程序并备份上述用户目录。账号文件中的代理账密仍是本地明文，注意文件权限，不要上传完整配置或用户目录。

登录点只认 muse.ai 的 `hatch_sess` 名称，活动环境由 CookieManager 探测；未活动账号用不池化的独立 SQLite 快照，按域、路径和有效期筛选。无数据、未知会话或读取失败保持未知，不读取 cookie 值。

## 测试与构建

需要 .NET 8 SDK。Linux/macOS 可以交叉编译 WPF，但不能运行 Windows WebView2 控件。

```bash
dotnet restore MuseLauncher.sln
dotnet test tests/MuseLauncher.Core.Tests -c Release
dotnet build MuseLauncher.sln -c Debug
dotnet build MuseLauncher.sln -c Release
```

核心测试使用合成数据和 loopback 模拟服务，覆盖：

- HTTP CONNECT/407、HTTPS 代理 TLS、SOCKS5 真实认证成功/拒绝；
- 本机认证、Header 剥离、双向数据、WebSocket/分块响应、超时与停止清理；
- 两账号不同上游与凭证、上游失败不改直连、特殊字符账密；
- 真正的文件读写/备份/故障注入、ID 校验、账号草稿保存；
- 实际假资源 Dispose、容量预算、乱序完成、修改配置/删除/关闭、旧事件和展示代次；
- 登录数据库快照、精确域与有效期、参数安全编解码。

### Windows WebView2 冒烟测试

在 Windows 及 WebView2 Runtime 环境下运行：

```powershell
dotnet run --project tests/MuseLauncher.WindowsSmoke -c Release
```

入口只使用新临时目录、合成账号和本机模拟 HTTP 代理，不访问 muse.ai，也不使用真实代理密码。验证真实 WebView2 账号存储隔离、5 账号 LRU 控件释放、同网关换账密重建、创建失败与重试。仅验证界面布局、头像和配置保存时，可运行 `dotnet run --project tests/MuseLauncher.WindowsSmoke -c Release -- --ui-only`；该模式不创建 WebView。Windows 维护者运行记录见 [REPAIR-TEST-REPORT.md](REPAIR-TEST-REPORT.md)。

### 本地构建包

```bash
dotnet publish src/MuseLauncher -c Release -r win-x64 --self-contained true -o publish/release-win-x64
```

Windows `build.ps1` 执行测试后生成本地包，不负责 GitHub 提交或发布。应用不带在线更新器，退出后解压到新目录或覆盖程序文件更新；用户数据位于独立用户目录。

## Debug 参数

仅 Debug 构建启用 `--start_account=N`、`--add_account`、`--del_account=N`、`--show_drawer`、`--open_config=N`、`--search_query=文本`、`--fullscreen=true|false`、`--dump_auth`。

```bash
dotnet run --project src/MuseLauncher -- --start_account=2 --show_drawer
```

第二次启动通过当前 Windows 用户隔离、限长度的 JSON 命名管道转发参数。诊断只记录 cookie 名与存储键，不写 cookie 值。Release 不响应调试操作。不要从不可信脚本传入删除账号等参数。

## 隐私和限制

- 本程序是网页启动器，不是反检测、指纹隔离或服务条款规避工具。
- 它不保证供应商 IP 固定、账号永不掉登录、网页功能永久兼容或全流量防泄漏。
- 只有用户主动设置的代理和网页目标会参与请求；测活主动访问 gstatic 的 HTTPS 测试端点。
- 运行日志不包含代理密码或 cookie 值；仍应在分享日志前检查个人信息。
- 与 muse.ai 官方无关，请遵守目标服务条款及当地法律法规。

---

## 🔗 友情链接

- [LINUX DO](https://linux.do/) —— 真诚分享、友好讨论的技术社区。

## 🔗 相关链接

- [Muse Launcher 安卓版](https://github.com/seekstars306/muse-launcher) —— Android WebView Profile 多账号启动器。
