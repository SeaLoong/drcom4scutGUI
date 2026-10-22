# drcom4scutGUI

华南理工大学校园网认证客户端的 Windows 图形界面。配合 [drcom4scut 核心](https://github.com/SeaLoong/drcom4scut)使用，支持有线和无线网络认证。

## 功能

- 有线 EAP + UDP、有线仅 EAP、无线 Portal 三种模式。
- 保存和切换多个账号，选择网卡或指定本机 IPv4。
- 认证成功后收起到托盘；可手动停止，连接异常时由核心重试。
- 可分别设置 Windows 登录时启动、打开 GUI 后自动认证。
- 环境自检、运行日志和诊断导出。
- 可保存密码，由当前 Windows 用户保护。

## 安装

1. 从 [GUI Releases](https://github.com/SeaLoong/drcom4scutGUI/releases) 下载 `win-x64` 压缩包并解压。该包自带 .NET 运行时；需要试用测试版时，选择标有 **Pre-release** 的开发预览包。
2. 从[核心 Releases](https://github.com/SeaLoong/drcom4scut/releases)下载兼容的 Windows 核心，将 **0.4.1 或更新版本**的 `drcom4scut.exe` 放在 `drcom4scutGUI.exe` 同目录。GUI 包不包含核心。
3. 使用有线模式时安装 [Npcap](https://npcap.com/#download)。无线 Portal 模式不需要抓包驱动。
4. 运行 `drcom4scutGUI.exe`。

请使用项目发布的文件；源码包不能直接当作可执行程序运行。需要从源码构建时，见[开发说明](docs/development.md)。

## 开始认证

**有线网络：** 选择“有线 EAP + UDP”，填写账号和密码，选择正在连接校园网的以太网卡，然后点击“认证”。本机源 IP 通常留空。只有网络明确不需要 UDP 时，才选择“有线仅 EAP”。

**无线网络：** 先连接校园无线网络，再选择“无线 Portal”，填写账号、密码和明确可信的门户地址，点击“认证”。API、AC IPv4 和载荷 IPv4 通常可先留空；需要指定时见[无线设置](docs/usage.md#无线-portal)。

门户默认使用 HTTPS。只有确认校园门户必须使用 HTTP，并接受账号密码在网络中明文传输的风险时，才勾选允许 HTTP；程序不会自动回退到 HTTP。

认证成功后窗口会收起到托盘。点击托盘图标可重新打开；点击“停止”会请求注销并停止本次认证。关闭窗口或从托盘选择“退出”会退出 GUI。

## 常见问题

**提示找不到核心，或核心能力不兼容？**

确认文件名是 `drcom4scut.exe`，与 GUI 放在同一目录，且来自兼容的核心发行包。

**有线认证失败怎么办？**

先点击“环境自检”，检查 Npcap、网卡和权限；若列出未满足的条件，按提示修复，再确认选择的是校园网以太网卡。查看下方日志；需要帮助时可[导出诊断](docs/security.md#导出和分享诊断)。

**只显示“802.1X 已通过”，还没有认证成功？**

“有线 EAP + UDP”还需要完成 UDP 会话与心跳。不要仅为消除这条状态而切换到“仅 EAP”；应先检查日志和网络要求。

**最小化以后还会联网吗？**

会。收起到托盘不会停止认证。要断开本次认证，请点击“停止”或退出 GUI。

**两个自动选项有什么区别？**

“Windows 登录时启动”负责打开 GUI；“打开 GUI 后自动认证”负责使用已选账号开始认证。想在登录后直接认证，需要同时启用两项。

更多内容：[详细使用](docs/usage.md) · [账号与隐私](docs/security.md) · [开发与测试](docs/development.md) · [版本与参考资料](docs/changes.md)。

## 许可证

见 [LICENSE](LICENSE)。
