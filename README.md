# agbrowser

**Agent-first Chromium browser runtime.** 用系统已装的 Chrome / Edge 内核，套一层为 AI Agent 设计的运行时：网络层广告拦截、多渠道多账号标签同步、原生左侧纵向标签栏，以及一个让 Codex / Claude Code / WorkBuddy / Trae 等客户端直接接管浏览器的 MCP 服务。

零运行时依赖（只用 Node 内置模块）· 启动 < 300ms · 不下载浏览器 · 不解密任何凭据。

---

## 为什么不是又一个 Chromium 分支

编译一个 Chromium 分支需要 Rust 工具链 + 约 100GB 磁盘 + 数小时编译，对绝大多数人是不可行的。agbrowser 走另一条路：

**直接使用你机器上已经装好的 Chrome / Edge**，把内核该有的能力全部接管过来。

| 需求 | agbrowser 的做法 |
| --- | --- |
| Chrome 最新内核 | 直接用本机 Chrome 可执行文件，永远是最新的那个 |
| Edge 的收藏夹/密码/同步 | 直接用 Edge 的用户数据目录，Edge 原生同步继续跑 |
| 密码管理器 | 原生接管，agbrowser 不解密、不导出、不搬运任何凭据 |
| 默认代替 Edge + Chrome | 注册 ProgID + 引导用户在系统设置里确认一次 |
| 纵向标签栏 | 写 profile 偏好，Chrome/Edge 原生纵向标签即刻生效 |

代价是：agbrowser 不是一个新的浏览器二进制，而是一层**运行时 + 集成层**。这换来的是安装体积接近零、启动毫秒级、以及内核永远是最新的。

---

## 快速开始

```bash
# 需要 Node.js >= 24（22 的内置 WebSocket 有 4MB 消息上限，会导致快照/截图卡死）
git clone https://github.com/neko233-com/agbrowser.git
cd agbrowser
npm install

npm run doctor          # 环境体检
npm run install:mcp     # 一键装进所有检测到的 AI 客户端
```

`agb install` 会自动写入这些客户端的原生配置格式：

`codex` · `claude` · `claudeDesktop` · `workbuddy` · `trae` · `cursor` · `windsurf` · `gemini` · `vscode` · `opencode` · `zed` · `continue`

写入是原子的，原文件带时间戳备份，无法解析的配置绝不会被覆盖。

---

## 核心能力

### 1. Agent 优先的 MCP 服务

19 个 `browser_*` 工具，遵循其它 agent browser 的命名约定，现有 skill 与 prompt 可直接复用。

关键设计是**紧凑快照**：不用返回原始 HTML，而是返回 a11y 树里的 `@e1`、`@e2` 引用。

```
browser_open https://example.com
→ @e1 link "Documentation"
  @e2 textbox "Search"
  @e3 button "Submit"
```

一次典型的页面读取从数万 token 降到几百。Agent 后续用 `@e3` 直接操作，页面变化后重新快照即可。

可用工具：`browser_open` `browser_snapshot` `browser_read` `browser_extract` `browser_click` `browser_fill` `browser_type` `browser_press` `browser_scroll` `browser_hover` `browser_wait` `browser_screenshot` `browser_back` `browser_forward` `browser_reload` `browser_tabs` `browser_accounts` `browser_switch_account` `browser_sync_add` `browser_sync_list` `browser_sync_run` `browser_status` `browser_adblock_stats` `browser_speed_test`

### 2. 网络层广告拦截

不是靠扩展，是**在请求发出前拦掉**：

- **第一层（原生）**：纯域名规则走 Chromium 的 `Network.setBlockedURLs`，由网络栈原生匹配，被拦的请求根本不会进 JS，零开销。
- **第二层（深度）**：只有正则规则、路径规则、资源类型过滤、例外规则才启用 CDP `Fetch` 拦截。

规则引擎兼容 EasyList 语法，支持 `||domain^`、`@@` 例外、`$script,third-party` 选项、正则、以及 `##` 元素隐藏规则。

启动时还会通过 `--host-resolver-rules` 把已知广告域名解析到黑洞，广告域名连 DNS 都不发。

### 3. 多渠道多账号标签同步

- **渠道（channel）**：Chrome / Edge / Edge Beta / Brave / Chromium，按本机安装情况自动发现。
- **账号（account）**：每个渠道下的 profile 目录（`Default`、`Profile 1`…），各自独立的 Cookie、登录态与标签。

同步规则把一个账号的标签实时镜像到另一个账号（Chrome ↔ Edge 跨渠道也可以），冲突用 **LWW（last-writer-wins）** 按版本号合并。

```bash
agb sync add chrome/Default edge/Default   # 建立镜像规则
agb sync run chrome/Default edge/Default   # 立即推送当前所有标签
```

### 4. 让网站对 Agent 与对人表现一致

很多网站会拒绝自动化客户端。agbrowser 的做法不是伪装身份，而是**消除自动化本身带来的可观测差异**，让页面在「人操作」和「Agent 代操作」两种情况下表现完全相同：

- `navigator.webdriver` 恒为 `false`（实例级遮蔽，覆盖 headless 默认值）
- 清除 `cdc_*`、`__webdriver_*`、`__selenium_*`、`_phantom` 等驱动痕迹
- 补齐 `window.chrome`、`navigator.plugins`、`navigator.languages`
- 修正 Permissions API 默认值与 `Notification.permission`
- 统一 WebGL vendor / renderer 字符串
- 屏蔽 `Runtime.enable` 带来的 console 标记

对应站点上，你用自己的账号、自己的登录态操作，Agent 只是替你点击——权限和身份始终是本人。

### 5. 内置插件

三个 Manifest V3 插件，随浏览器一起加载：

| 插件 | 作用 |
| --- | --- |
| **agb Shield** | 元素级广告/追踪隔离、广告 iframe 清理、指纹降噪，补充内核级拦截 |
| **agb Speed** | 网络测速（延迟/抖动/上行/下行）+ 页面性能（FCP/LCP/CLS/TTFB/资源体积） |
| **agb Search** | 多引擎聚合搜索，`g:` `b:` `ddg:` `bd:` `zh:` `gh:` `bili:` 前缀直达，历史/书签聚合搜索 |

### 6. 默认左侧纵向标签栏

Chrome 145+ 与 Edge 均原生支持纵向标签。agbrowser 会在启动前写入 profile 偏好：

```json
{ "vertical_tabs": { "enabled": true, "collapsed_state": false, "uncollapsed_width": 240 } }
```

写入后浏览器启动即为左侧纵向列表，无需手动进设置切换。Edge 的纵向标签已 GA，Chrome 通过 `VerticalTabs` / `TabStripUnification` 特性开启。

---

## 命令行

```
agb install [client...]     把 MCP 装进 AI 客户端
agb uninstall [client...]   移除
agb clients                 列出检测到的客户端
agb doctor                  环境体检
agb accounts                列出渠道与账号
agb launch [url]            启动浏览器会话
agb sync list|add|run       标签同步规则管理
agb mcp                     以 stdio 运行 MCP 服务
agb console                 本地控制面板
agb default status|register|set|restore
```

---

## 设为默认浏览器

```bash
agb default set
```

这会注册 `AgbrowserHTML` ProgID（应用列表、「打开方式」菜单立即可见）。

**关于自动写入默认项**：Windows 从 2024 年 2 月的累积更新起引入了 `UCPD.sys`（User Choice Protection Driver），它会拦截所有程序对 `UserChoice` 键的写入——即使该键当前用户拥有完全控制权限。agbrowser 不会去禁用系统驱动（那是修改系统安全配置，且需要重启），而是检测它的状态并给出准确指引：

> 设置 → 应用 → 默认网页浏览器 → 选择 agbrowser

选择一次即可，Chrome 与 Edge 的链接都会被 agbrowser 接管。`agb default restore` 可还原。

---

## 项目结构

```
packages/
  core/       零依赖 CDP 客户端、启动器、账号/渠道、广告拦截、标签同步、纵向标签、反检测
  mcp/        MCP 协议服务器、19 个工具、一键安装器
  cli/        agb 命令行
  console/    本地控制面板（HTTP + 单页 UI）
extensions/
  agb-shield/ 广告与追踪隔离
  agb-speed/  测速与性能分析
  agb-search/ 聚合搜索
```

## 技术要求

- **Node.js >= 24**（硬性要求）。Node 22 的内置 WebSocket 在约 4MB 处卡死，会导致大页面快照与截图失败。
- Windows / macOS / Linux 上已安装的 Chrome 或 Edge。

## 开发

```bash
npm test           # 单元 + 集成测试
npm run test:e2e   # 真实浏览器端到端测试
npm run icons      # 重新生成插件图标
```

## License

MIT
