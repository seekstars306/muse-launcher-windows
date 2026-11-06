namespace MuseLauncher;

/// <summary>界面文案（镜像安卓 strings.xml）。</summary>
public static class Strings
{
    public const string AppName = "Muse 登录器";
    public const string DrawerTitle = "Muse 账号";
    public const string AccountsCountFmt = "{0} 个账号 · 相互隔离";
    public const string AddAccount = "添加 Muse 账号";
    public const string AddAccountSub = "新开一个独立无痕的 muse.ai 页面";
    public const string DrawerFootnote =
        "每个账号使用完全隔离的存储环境，登录后状态持久保留，账号之间互不串数据。";
    public const string StatusLogged = "已登录 · 独立环境";
    public const string StatusNotLogged = "未登录 · 无痕环境";
    public const string EmptyTitle = "还没有 Muse 账号";
    public const string EmptySub = "每个账号都是一个完全隔离的无痕环境";
    public const string EmptyAdd = "添加第一个 Muse 账号";
    public const string SplashSub = "正在加载独立环境 · 数据互不相通";
    public const string RenameHint = "账号名称";
    public const string ToastCreatedFmt = "已创建独立无痕环境：{0}";
    public const string ToastRenamedFmt = "已重命名为「{0}」";
    public const string ToastDeleted = "已删除账号环境，并清除其登录数据";
    public const string ToastWebviewRecovered = "页面出现异常，已自动恢复当前账号环境";
    public const string ToastStoreRecovered = "账号数据出现异常，已自动从备份恢复";
    public const string ToastStoreCorrupt = "账号数据损坏且无法自动恢复，原始内容已留底";

    // 加载失败遮罩
    public const string ErrorTitle = "页面加载失败";
    public const string ErrorHint = "请检查网络连接，或到账号配置里确认该账号的代理可用";
    public const string ErrorRetry = "重试";
    public const string ErrorGeneric = "网络连接异常";

    public const string ConfirmDeleteFmt = "删除 {0} 的隔离环境？\n该账号的登录数据将被清除。";
    public const string BtnDelete = "删除";
    public const string BtnCancel = "取消";
    public const string CdExpand = "展开账号列表";
    public const string CdCollapse = "收起列表";
    public const string CdAdd = "新建账号环境";
    public const string CdFullscreen = "全屏显示当前账号";
    public const string CdRename = "重命名";
    public const string CdDelete = "删除账号";
    public const string CdConfig = "账号配置";
    public const string SearchHint = "搜索名称或标签";
    public const string SearchEmpty = "没有匹配的账号";

    // 账号配置页
    public const string ConfigTitle = "账号配置";
    public const string ConfigAvatar = "头像";
    public const string ConfigAvatarHint = "1-2 个字符或 Emoji";
    public const string ConfigName = "名称";
    public const string ConfigProxy = "代理";
    public const string ConfigProxyPh = "如 127.0.0.1:7890 或 socks5://127.0.0.1:1080";
    public const string ConfigProxyHint = "留空则跟随系统网络，支持 http / https / socks5。修改代理后重新进入该账号生效。";
    public const string ConfigProxyDetect = "探测";
    public const string ConfigProxyDetecting = "正在探测本机 Clash 端口…";
    public const string ConfigProxySaved = "已保存到账号";
    public const string ConfigProxyDetectFound = "已检测到本机 Clash 端口 {0}，已自动填入（保存后生效）";
    public const string ConfigProxyDetectNone = "未检测到 Clash 默认端口（7890/7891/7892），Clash 未启动或使用了非默认端口";
    public const string ConfigProxyTest = "测活";
    public const string ConfigProxyTesting = "测活中…";
    public const string ConfigProxyOk = "连接成功";
    public const string ConfigProxyFail = "连接失败：";
    public const string ConfigProxyTimeout = "连接超时（10 秒）";
    public const string ConfigProxyEmpty = "请先填写代理地址";
    public const string ConfigProxySocksReachable = "端口可达（未验证代理协议）";
    public const string ConfigProxySocksUnreachable = "无法连上 SOCKS 端口";
    public const string ConfigTags = "标签";
    public const string ConfigTagsHint = "用逗号分隔，如：电商, 主力, 测试";
    public const string ConfigTagsNote = "标签用于侧栏搜索过滤，每个最多 8 个字符。";
    public const string ConfigSave = "保存";

    // WebView2 运行时
    public const string RuntimeNeedTitle = "需要安装 WebView2 运行时";
    public const string RuntimeNeedMsg =
        "本应用使用 Microsoft Edge WebView2 加载 muse.ai。\n" +
        "当前系统未检测到 WebView2 运行时（Windows 10/11 一般自带，旧系统需单独安装）。\n\n" +
        "点击「去安装」下载 Evergreen 运行时，安装完成后重新打开本应用。";
    public const string RuntimeInstallBtn = "去安装";
    public const string BtnExit = "退出";
}
