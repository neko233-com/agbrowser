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

### Chrome 136+ 的调试端口限制，以及 agbrowser 的应对

Chrome 136 起，出于安全考虑，浏览器**拒绝在默认用户数据目录下开启远程调试端口**——否则任何本地进程都能通过调试端口读取浏览器里的全部 Cookie 与令牌。

agbrowser 的应对是**接管而非绕过**：它为每个渠道建一个受管根目录，其中的 profile 项是指向真实 profile 的**目录联接（junction）**。

```
%LOCALAPPDATA%\agbrowser\channels\chrome\
  Local State  ->  %LOCALAPPDATA%\Google\Chrome\User Data\Local State
  Default\     ->  %LOCALAPPDATA%\Google\Chrome\User Data\Default
```

浏览器读写的仍是它自己原来那批文件，所以 Cookie、登录态、密码管理器、收藏夹、厂商同步全部照常工作。**没有任何数据被复制、导出或解密**，真实 profile 也不会被修改。

实测（Edge 默认 profile）：`Bookmarks` 1.4 MB、`Login Data` 983 KB、`Preferences` 390 KB 全部原样可见。

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

Windows 用户也可以直接下载安装包：

```
https://github.com/neko233-com/agbrowser/releases/latest/download/agbrowser-0.1.0-x86.msi
```

安装包只有 **1.25 MB**——因为它只装原生启动器与图标，浏览器内核用的就是你机器上已有的 Chrome / Edge，Agent 运行时是纯 JavaScript，按需从仓库解析。

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

### 6. Web 游戏开发：一等公民支持

Canvas 游戏在无障碍树里是**完全隐形**的——没有按钮、没有文本、没有角色。Agent 能接触的只有像素、帧循环，以及游戏自己挂在全局对象上的状态。agbrowser 为此提供了一整套原语：

| MCP 工具 | 作用 |
| --- | --- |
| `browser_game_detect` | 识别游戏类型：Canvas 2D / WebGL / WebGPU，以及引擎（Phaser、Three.js、PixiJS、p5、Kaboom、Crafty），并报告画布尺寸、渲染上下文、GPU 渲染器字符串 |
| `browser_game_state` | 读出游戏实时状态：帧率、实体集合、玩家位置等；也可传入任意表达式读取游戏内部数据 |
| `browser_game_control` | 控制帧循环：`pause` 暂停、`step` 逐帧推进、`resume` 恢复、`status` 查状态、`reset` 重置统计 |
| `browser_game_input` | 向游戏注入指针与键盘输入。指针事件带真实坐标，键盘可重复（移动类操作） |
| `browser_game_probe` | 采样画布区域亮度，判断游戏是否真的在渲染（崩溃后是否黑屏） |
| `browser_perf_profile` | 页面性能剖析：TTFB、FCP、资源体积分类、最重的请求、JS 堆内存 |

**帧步进**是调试游戏的关键能力。暂停时游戏会收到**冻结的时间戳**，所以世界不会推进，但它的 rAF 链保持完整——恢复后从原处继续。这让「逐帧定位卡死的那一帧」成为可能。

实测（真实 canvas 游戏，21 项检查全通过）：暂停后帧数精确不变；`step(3)` 恰好推进 3 帧；恢复后继续运行；指针坐标精确到像素；键盘事件正确抵达游戏。

### 7. 默认左侧纵向标签栏

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

## 安装包

Windows 安装包由 WiX 7 构建，x86 架构（可安装到 32 位与 64 位 Windows）：

```bash
node packaging/build-msi.mjs   # -> build/agbrowser-<version>-x86.msi
node packaging/validate-msi.mjs
```

MSI 内含：原生启动器（Go，2.9 MB，零运行时依赖）、应用图标、开始菜单快捷方式、ProgID 与 http/https 关联注册。

**本仓库不使用 GitHub Actions**：构建与发布全部手动完成，避免任何 runner 执行来自 PR 的代码。发布流程见 `.github/workflows/README.md`。

## 品牌图标

图标是全新设计的，不沿用任何现有浏览器：

<p align="center">
  <img src="packages/launcher/assets/icon-256.png" width="128" alt="agbrowser">
</p>

标识是一个**开口轨道 + 轨道上的 agent 节点 + 中心核**——象征由 Agent 驱动的浏览器，与 Chrome 的三色圆环完全不同。图形由 Go 程序按解析式绘制，4 倍超采样抗锯齿，从 16px favicon 到 512px 应用图标都保持清晰。三个内置扩展各有自己的配色。

重新生成：

```bash
cd packages/launcher && go run ./icon .
```

---

## 设为默认浏览器

```bash
agb default set
```

这会注册 `AgbrowserHTML` ProgID，应用列表与「打开方式」菜单立即可见。

**关于「每次都弹确认框」**：agbrowser **只注册 ProgID 和 Capabilities，绝不写 `HKCU\Software\Classes\http\shell\open\command`** 这类传统关联。原因很实际——写传统关联会让 http/https 协议同时存在多个候选处理器，而 UserChoice 仍指向别的浏览器，此时 Windows 每点一次链接就问一次「用哪个打开」。agbrowser 注册时会主动清理这类遗留项，安装包也会在卸载时移除它们。

结果是：**选择一次，之后永远不再询问。**

Windows 2024-02 起的 `UCPD.sys`（User Choice Protection Driver）会拦截所有程序对 UserChoice 键的写入，因此程序无法替你完成最后那一步选择——**这一步只需要你做一次**：

> 设置 → 应用 → 默认网页浏览器 → 选择 agbrowser

选定之后 Windows 会自己记住，Chrome 与 Edge 的链接都由 agbrowser 直接打开，全程无弹窗。agbrowser 不会去禁用系统驱动（那是修改系统安全配置且需重启）。

`agb default restore` 可还原原来的默认浏览器。

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
npm test           # 单元 + 集成 + 端到端 + 验收
npm run test:unit  # 纯单元测试
npm run test:e2e   # 真实浏览器端到端测试
npm run test:ext   # 内置插件加载测试
npm run icons      # 重新生成插件图标
```

## License

MIT
