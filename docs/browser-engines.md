# 浏览器内核适配边界

Windows 当前使用 WinForms + WebView2 Evergreen Runtime：应用负责标签栏、导航、会话和受控的用户数据目录；WebView2 负责网页呈现、网络、站点存储和自身安全更新。MCP 的旧 Chromium 启动路径尚未连接该窗口。

将来 macOS 应通过独立的原生宿主接入系统 WebKit/WKWebView（Safari 同源引擎），使用独立的应用数据和同样的标签、导航、会话行为。WKWebView 不是 Safari 本身，不应承诺继承 Safari 的 Cookie、密码、扩展或 Apple ID 同步，也不应假定 WebView2 的 CDP、扩展和下载 API 在 macOS 可用。适配必须分别验证权限、下载、登录、弹窗、生命周期和安全更新。

当前没有 macOS 实现、构建产物或真实设备验收。跨平台协议在开始 macOS 实现时根据实际需求定义；不要把 Windows 专用 WebView2 类型写进共享协议。
