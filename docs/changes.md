# 版本与参考资料

[返回首页](../README.md)

本页用于查版本差异和设计来源。新用户直接看[安装与日常使用](usage.md)，不必先阅读历史记录。下列版本号表示相应源码与功能，不表示每个版本都已发布为正式版；实际可下载文件以[发布页](https://github.com/SeaLoong/drcom4scutGUI/releases)为准。

当前跨平台版本为 GUI 3.1.0，要求核心 0.5.0 或更新的兼容版本，通过 **Pre-release（预发布）**提供。旧的 **Latest** 正式版与这些预发布不是同一个版本。

## 版本记录

### 3.1.0

- 可在 Windows、macOS、Linux 的 x64 / ARM64 电脑上使用，运行所需组件随包提供；Mac 使用 `.app` 打开。界面使用 Avalonia 实现。
- 记住密码时由当前系统用户的密码保护保管：Windows 使用系统保护，Mac 使用登录钥匙串，Linux 使用桌面密码库。每个账号也可选择不保存密码。
- Mac、Linux 也能向配套核心请求停止、注销，并在 GUI 退出后清理自己启动的核心；要求核心 0.5.0 或更新版本。
- 三个平台都可在登录电脑后打开程序；Linux 保留普通窗口，避免桌面没有托盘时找不到入口。
- 发布前自动在六个平台上检查逻辑、界面交互和实际窗口启动；核对下载包和校验值。Mac 包在解压后再次严格验签。

### 3.0.1

- 根据本次认证核心报告的连接状态显示进度，不再只凭日志中的“成功”文字判断；要求核心 0.4.1 或更新版本。
- 环境自检保留未满足条件时的完整报告，并以警告提示，便于继续排查和导出诊断。
- 发布包附带使用文档，打包时拒绝混入账号及核心配置文件。

### 3.0.0

- 提供完整有线、有线仅身份认证，以及尝试自动提交校园无线登录网页的界面和连接状态。
- 保存的密码由当前 Windows 用户的系统保护保管；密码通过程序之间的私有输入传给核心，不放入启动参数。窗口退出后，核心也会停止并请求注销。
- Windows x64 包自带 .NET 10 运行组件，不必单独安装；增加登录逻辑、程序间通信和系统功能的自动测试。
- 提供当前用户登录启动、自检、诊断导出及持续重连状态展示。

### 2.0.0 及早期实现

已有多账号、指定网卡/IP、托盘和自动认证界面。历史源码与版本以[提交记录](https://github.com/SeaLoong/drcom4scutGUI/commits/master/)和[发行记录](https://github.com/SeaLoong/drcom4scutGUI/releases)为准。

## 参考来源

界面与账号使用流程来自 [SeaLoong/drcom4scutGUI](https://github.com/SeaLoong/drcom4scutGUI) 的原始实现。

[LuoMuQAQ/drcom4scut](https://github.com/LuoMuQAQ/drcom4scut) 提供了 Windows 图形客户端、托盘、自启动、密码保护及父进程监督等设计参考。本仓库根据这些使用需求独立实现对应行为，没有捆绑其二进制、图标或安装器。

无线仍是校园网页账号密码登录；这里的自动化只针对特定网页请求，尚未经过本项目实际校园网验证，不代表兼容所有校区。它不负责连接 Wi-Fi，也不代替验证码、短信或扫码。具体适用范围见[无线使用说明](usage.md#无线-portal)。

有线和无线认证协议由独立的 [drcom4scut 核心](https://github.com/SeaLoong/drcom4scut)实现。相关客户端与 fork 的调查范围、具体提交及采纳记录见核心仓库的[参考客户端](https://github.com/SeaLoong/drcom4scut/blob/master/docs/references-clients.md)和[fork 记录](https://github.com/SeaLoong/drcom4scut/blob/master/docs/references-forks.md)。这些资料说明设计来源，不代表所有设备或校园网络都已通过实机验证。

## 许可证

本仓库许可证见 [LICENSE](../LICENSE)。参考项目各自的版权和许可证保持独立；公开可读不代表可以省略署名或直接重新许可其代码、资源。
