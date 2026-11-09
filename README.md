# Muse 登录器 for Windows

安卓版 [muse-launcher](https://github.com/seekstars306/muse-launcher) 的 Windows 版：一个
**多账户 muse.ai 启动器**。基于 WPF + Microsoft Edge WebView2（Chromium 内核），
直接加载网页版 muse.ai，每个账号一个完全隔离的浏览器环境。

## 原理

与安卓版用 WebView Profile 隔离的做法一一对应：

| | 安卓版 | Windows 版 |
|---|---|---|
| 账号隔离 | androidx WebView Profile（`acct_N`） | 每账号独立 WebView2 UserDataFolder + 独立浏览器进程 |
| 常驻上限 | LRU 保留最近 4 个 WebView | LRU 保留最近 4 个 WebView2（驱逐仅丢页面，磁盘登录态保留） |
| 代理 | ProxyController 切号时全局切换 | 每账号环境创建参数 `--proxy-server`，真正按账号生效 |
| 账号数据 | SharedPreferences `list/list_prev` 自愈 | `%APPDATA%\MuseLauncher\accounts.json` 同款自愈 |
| 登录探测 | 全量 CookieManager 探测 `hatch_sess` | 活动 webview 实时探测；未活动账号尽力读磁盘 Cookies 库 |

## 安装运行

1. 解压 `MuseLauncher-win-x64-v1.0.0.zip`（自包含，无需安装 .NET）；
2. 双击 `MuseLauncher.exe`；
3. 若提示缺少 WebView2 运行时（Windows 10/11 一般自带），点「去安装」下载
   [Evergreen 运行时](https://go.microsoft.com/fwlink/p/?LinkId=2124703)，装完重开。

**系统要求**：Windows 10 1803+ / Windows 11，x64。

## 使用

- **添加账号**：左侧窄条 `＋` 或展开列表底部的「添加 Muse 账号」；新账号**自动继承当前账号的代理**（国内直连 muse.ai 不通）。
- **登录**：第一次进入某个账号会打开 muse.ai 首页，登录一次后该账号的登录态持久保留（数据在各自目录，互不串）。
- **切换**：左侧头像列或抽屉列表点击即切换；绿点 = 已登录。
- **重命名 / 删除 / 配置**：账号行右侧三个小按钮；删除会连磁盘环境一起清除。
- **代理配置**：账号配置页支持 http/https/socks5 地址、「测活」（gstatic generate_204）、
  「探测」（自动探测本机 Clash 7890/7891/7892 端口）。修改代理后重新进入该账号生效。
- **搜索**：抽屉顶部搜索框按 名称+标签 过滤。
- **全屏**：`F11` 进入/退出，`Esc` 也可退出；状态会记住。

## 调试参数（仅 Debug 构建）

镜像安卓 am start extras，改为命令行参数：

```bash
dotnet run --project src/MuseLauncher -- --start_account=2 --show_drawer
```

支持：`--start_account=N` `--add_account` `--del_account=N` `--dump_auth`
`--show_drawer` `--open_config=N` `--search_query=xx` `--fullscreen=true|false`
`--proxy_preset=xxx [--proxy_autotest] [--proxy_save]`。第二次启动的参数会转发给已运行的实例。
`--dump_auth` 会把各账号 cookie 名与 localStorage 键输出到日志文件。

## 数据位置

| 内容 | 路径 |
|---|---|
| 账号列表 | `%APPDATA%\MuseLauncher\accounts.json`（损坏自动从 `list_prev` 自愈，原始值留底 `list_corrupt`） |
| 每账号登录数据 | `%LOCALAPPDATA%\MuseLauncher\Profiles\acct_N\` |
| 运行日志 | `%LOCALAPPDATA%\MuseLauncher\log.txt`（MusePerf / MuseAuth / MuseWebview，与安卓 logcat tag 对应） |

## 从源码构建

```bash
# 任意平台（Linux/macOS 需 .NET 8 SDK，靠 EnableWindowsTargeting 交叉构建）
dotnet test tests/MuseLauncher.Core.Tests
dotnet publish src/MuseLauncher -c Release -r win-x64 --self-contained true -o publish/win-x64
```

Windows 上也可以直接跑 `build.ps1`（测试 + 发布 + 打 zip）。

## 与安卓版的已知差异

1. **登录绿点**：未活动的账号（被 LRU 驱逐后）用读磁盘 Cookies 库的方式尽力探测，失败则保留上次状态；安卓可全量实时探测。
2. **下载**：使用 WebView2 自带下载 UI（安卓走 DownloadManager）；UA 为桌面 Edge，无需安卓的去 `; wv` 处理。
3. **内存**：LRU 常驻 4 个账号 = 4 个浏览器进程，空闲内存高于安卓。
4. 鉴权代理：`--proxy-server` 不支持 URL 内嵌账密；测活支持账密（407 应答），但浏览代理请在 Clash 等本地工具侧处理。

---

## 🔗 友情链接

- [LINUX DO](https://linux.do/) —— 真诚分享、友好讨论的技术社区，本项目的交流与反馈也发布于此

---

## 🔗 相关链接

- [Muse Launcher 安卓版](https://github.com/seekstars306/muse-launcher) —— 对应的 Android WebView Profile 多账号启动器

