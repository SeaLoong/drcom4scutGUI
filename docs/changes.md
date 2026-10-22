# 版本与参考资料

[返回首页](../README.md)

## 版本记录

### 3.1.0

- 使用 Avalonia 桌面界面，提供 Windows、macOS、Linux 的 x64/ARM64 六种自包含包；macOS 以 `.app` 分发。
- 保留 Windows DPAPI 密码保护，macOS 使用登录钥匙串、Linux 使用 Secret Service 保存加密密钥；支持逐账号选择不记住密码，不回退为明文保存。
- macOS、Linux 通过私有 Unix-domain socket 控制核心停止，保持注销与父进程断开清理；要求核心 0.5.0 或更新版本。
- 提供三平台用户登录启动；Linux 保留普通窗口，避免托盘不可见时丢失操作入口。
- 补充原生六平台构建、无显示 UI 交互测试、真实窗口启动检查，以及多平台包与校验和的发布验证。

### 3.0.1

- 使用本次核心会话的结构化状态判断认证阶段和在线状态，不再根据日志文字判断成功；要求核心 0.4.1 或更新版本。
- 环境自检保留未满足条件时的完整报告，并以警告提示，便于继续排查和导出诊断。
- 发布包附带使用文档，打包时拒绝混入账号及核心配置文件。

### 3.0.0

- 支持有线 EAP + UDP、仅 EAP、无线 Portal 的图形配置与相应在线状态。
- 密码保存使用当前用户 DPAPI，密码传递使用专用 stdin；控制管道支持注销和父进程断开退出。
- 使用 .NET 10、自包含 Windows x64 包，以及共享逻辑、进程交互和原生平台条件测试。
- 提供当前用户登录启动、自检、诊断导出及持续重连状态展示。

### 2.0.0 及早期实现

已有多账号、指定网卡/IP、托盘和自动认证界面。历史源码与版本以[提交记录](https://github.com/SeaLoong/drcom4scutGUI/commits/master/)和[发行记录](https://github.com/SeaLoong/drcom4scutGUI/releases)为准。

## 参考来源

界面与账号使用流程来自 [SeaLoong/drcom4scutGUI](https://github.com/SeaLoong/drcom4scutGUI) 的原始实现。

[LuoMuQAQ/drcom4scut](https://github.com/LuoMuQAQ/drcom4scut) 提供了 Windows 图形客户端、托盘、自启动、密码保护及父进程监督等设计参考。本仓库根据这些使用需求独立实现对应行为，没有捆绑其二进制、图标或安装器。

有线和无线认证协议由独立的 [drcom4scut 核心](https://github.com/SeaLoong/drcom4scut)实现。相关客户端与 fork 的调查范围、具体提交及采纳记录见核心仓库的[参考客户端](https://github.com/SeaLoong/drcom4scut/blob/master/docs/references-clients.md)和[fork 记录](https://github.com/SeaLoong/drcom4scut/blob/master/docs/references-forks.md)。这些资料说明设计来源，不代表所有设备或校园网络都已通过实机验证。

## 许可证

本仓库许可证见 [LICENSE](../LICENSE)。参考项目各自的版权和许可证保持独立；公开可读不代表可以省略署名或直接重新许可其代码、资源。
