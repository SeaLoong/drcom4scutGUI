# drcom4scutGUI

华南理工大学校园网认证客户端的图形界面，可在 Windows、macOS 和 Linux 桌面上使用。配合 [drcom4scut 核心](https://github.com/SeaLoong/drcom4scut)，支持有线和无线网络认证。

## 功能

- 有线 EAP + UDP、有线仅 EAP、无线 Portal 三种模式。
- 保存和切换多个账号，选择网卡或指定本机 IPv4。
- 托盘菜单、手动停止和断线重试；Windows、macOS 认证成功后可收起，Linux 保留普通窗口。
- 可分别设置登录系统时启动、打开 GUI 后自动认证。
- 环境自检、运行日志和诊断导出。
- 可按账号选择是否记住密码；保存时使用当前系统用户的密码保护。

## 安装

从 [GUI Releases](https://github.com/SeaLoong/drcom4scutGUI/releases) 下载对应系统和 CPU 的包并解压。包自带运行时，不必另装 .NET；需要试用测试版时，选择标有 **Pre-release** 的开发预览包。

| 系统 | 选择的包 | 打开的程序 |
| --- | --- | --- |
| Windows x64 / ARM64 | `win-x64.zip` / `win-arm64.zip` | `drcom4scutGUI.exe` |
| macOS Intel / Apple Silicon | `osx-x64.tar.gz` / `osx-arm64.tar.gz` | `drcom4scutGUI.app`；需要 macOS 15 或更新的受支持版本 |
| Linux x64 / ARM64 | `linux-x64.tar.gz` / `linux-arm64.tar.gz` | `drcom4scutGUI`；需要图形桌面，见 [Linux 安装](docs/usage.md#linux) |

再从[核心 Releases](https://github.com/SeaLoong/drcom4scut/releases)下载对应系统和 CPU 的 **0.5.0 或更新版本**核心。Windows 将 `drcom4scut.exe` 放在 GUI 程序同目录；Linux 将 `drcom4scut` 放在 GUI 程序同目录；macOS 将 `drcom4scut` 放在 `.app` **同级目录**，不要修改应用包内部。GUI 包不包含核心。

有线模式需要系统允许核心抓包：Windows 安装 [Npcap](https://npcap.com/#download)，macOS 配置 [BPF 权限](https://github.com/SeaLoong/drcom4scut/blob/master/docs/macos.md#有线抓包权限)，Linux 由管理员授予核心所需权限。GUI 本身使用普通用户运行。无线 Portal 不需要抓包驱动或这些权限。

请使用项目发布的文件；源码包不能直接当作可执行程序运行。需要从源码构建时，见[开发说明](docs/development.md)。

## 开始认证

**有线网络：** 选择“有线 EAP + UDP”，填写账号和密码，选择正在连接校园网的以太网卡，然后点击“认证”。本机源 IP 通常留空。只有网络明确不需要 UDP 时，才选择“有线仅 EAP”。

**无线网络：** 先连接校园无线网络，再选择“无线 Portal”，填写账号、密码和明确可信的门户地址，点击“认证”。API、AC IPv4 和载荷 IPv4 通常可先留空；需要指定时见[无线设置](docs/usage.md#无线-portal)。

门户默认使用 HTTPS。只有确认校园门户必须使用 HTTP，并接受账号密码在网络中明文传输的风险时，才勾选允许 HTTP；程序不会自动回退到 HTTP。

按需要勾选“记住密码”。Windows 使用当前用户的密码保护，macOS 使用登录钥匙串，Linux 需要可用且已解锁的桌面密码库。无法保存时可以取消勾选，输入密码进行本次认证，详见[账号与隐私](docs/security.md)。

Windows、macOS 认证成功后会在托盘可用时收起窗口，可通过托盘菜单“打开”恢复。Linux 保留普通窗口，避免桌面没有托盘入口时无法找回界面。点击“停止”会请求注销并停止本次认证；关闭窗口或从托盘选择“退出”会退出 GUI。

## 常见问题

**提示找不到核心，或核心能力不兼容？**

确认使用了对应系统和 CPU 的核心，文件名和位置符合上述安装说明，且来自兼容的核心发行包。

**有线认证失败怎么办？**

先点击“环境自检”，检查网卡、地址和该系统的抓包权限；若列出未满足的条件，按提示修复，再确认选择的是校园网以太网卡。查看下方日志；需要帮助时可[导出诊断](docs/security.md#导出和分享诊断)。

**macOS 提示无法验证开发者，或 Linux 无法保存密码？**

分别见 [macOS 首次打开](docs/usage.md#macos)和 [Linux 桌面与密码库](docs/usage.md#linux)。保存密码不可用时，仍可取消“记住密码”后认证。

**只显示“802.1X 已通过”，还没有认证成功？**

“有线 EAP + UDP”还需要完成 UDP 会话与心跳。不要仅为消除这条状态而切换到“仅 EAP”；应先检查日志和网络要求。

**最小化以后还会联网吗？**

会。最小化或收起到托盘不会停止认证。要断开本次认证，请点击“停止”或退出 GUI。

**两个自动选项有什么区别？**

“登录系统时启动”负责打开 GUI；“打开 GUI 后自动认证”负责使用已选账号开始认证。想在登录后直接认证，需要同时启用两项，并保存该账号的密码。

更多内容：[详细使用](docs/usage.md) · [账号与隐私](docs/security.md) · [开发与测试](docs/development.md) · [版本与参考资料](docs/changes.md)。

## 许可证

见 [LICENSE](LICENSE)及[第三方组件声明](THIRD-PARTY-NOTICES.md)。
