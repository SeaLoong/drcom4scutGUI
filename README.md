# drcom4scutGUI

[![构建与测试](https://github.com/SeaLoong/drcom4scutGUI/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/SeaLoong/drcom4scutGUI/actions/workflows/ci.yml)
[![下载预览版](docs/badges/preview.svg)](https://github.com/SeaLoong/drcom4scutGUI/releases)
[![支持的桌面系统](docs/badges/platforms.svg)](#安装)
[![许可证](docs/badges/license.svg)](LICENSE)

**打开程序，填好账号，连接华南理工大学校园网。**

适用于 Windows、macOS 和 Linux。提供有线登录、断线重连、多账号、记住密码和开机自动连接，也可以代为提交受支持的校园网页登录。连接进度和排障信息都能在窗口里查看。

[软件首页与帮助中心](https://sealoong.github.io/drcom4scut/) · [下载客户端](https://github.com/SeaLoong/drcom4scutGUI/releases) · [完整使用指南](docs/usage.md) · [账号与密码](docs/security.md)

## 安装

安装需要 **界面程序 + 认证核心** 两个包。界面负责操作，核心负责联网；运行组件已随包提供。

### 1. 下载适合电脑的两个包

打开 [GUI 下载页](https://github.com/SeaLoong/drcom4scutGUI/releases)和[核心下载页](https://github.com/SeaLoong/drcom4scut/releases)，选择最新 **Pre-release（预览版）**，在 **Assets（文件列表）**里按下表选包。本文对应 GUI **3.1.1** 与核心 **0.5.1**；压缩包文件名前面还会有项目名和版本号。

| 你的电脑 | 界面包的文件名结尾 | 核心包的文件名结尾 |
| --- | --- | --- |
| Windows，Intel / AMD | `win-x64.zip` | `x86_64-pc-windows-msvc.zip` |
| Windows，ARM64 | `win-arm64.zip` | `aarch64-pc-windows-msvc.zip` |
| Mac，Apple 芯片 | `osx-arm64.tar.gz` | `aarch64-apple-darwin.tar.gz` |
| Mac，Intel | `osx-x64.tar.gz` | `x86_64-apple-darwin.tar.gz` |
| Linux，Intel / AMD | `linux-x64.tar.gz` | `x86_64-unknown-linux-gnu.tar.gz` |
| Linux，ARM64 | `linux-arm64.tar.gz` | `aarch64-unknown-linux-gnu.tar.gz` |

Windows 在“设置 → 系统 → 关于”查看系统类型；Mac 在“苹果菜单 → 关于本机”查看芯片。Mac 需要 **macOS 15 或更新版本**，Linux 需要图形桌面。`Source code` 是开发用的源码，安装请选上表中的程序包。

### 2. 解压并放在一起

完整解压界面包，再把核心程序复制进去：Windows 的 `drcom4scut.exe` 与 `drcom4scutGUI.exe` 放在一起；Linux 的 `drcom4scut` 与 `drcom4scutGUI` 放在一起。Mac 把 `drcom4scut` 放在 `drcom4scutGUI.app` **旁边**。

有线登录：Windows 先安装 [Npcap](https://npcap.com/#download)；Mac、Linux 按[系统安装指南](docs/usage.md#安装和系统要求)给核心一次网络访问授权。随后像其他桌面软件一样打开 GUI。

## 第一次登录

1. **接好网络。** 有线插好网线；无线先在系统里连上校园 Wi-Fi。
2. **选择方式，填写账号密码。** 有线选“有线网络”，网卡先用“自动选择”；无线选“校园网页认证”，填写平时使用的学校登录页地址。
3. **点击“连接”。** 按需勾选“记住密码”；成功后可以最小化窗口。断线时程序会尝试恢复，点击“断开”结束本次连接。

无线仍然是网页账号密码登录：客户端代为完成受支持的 DrCOM/ePortal 网页步骤。目前已通过模拟服务测试，实际校园适用情况请参照[无线说明](docs/usage.md#无线-portal)；验证码、扫码等页面可直接用浏览器登录。

## 常用帮助

| 想做什么 | 查看这里 |
| --- | --- |
| 首次运行、Mac 打开提示、Linux 授权 | [各系统安装步骤](docs/usage.md#安装和系统要求) |
| 多个账号、自动连接、托盘与退出 | [日常操作](docs/usage.md#多账号) |
| 连接失败、找不到核心、密码无法保存 | [常见问题](docs/usage.md#排查连接问题) |
| 了解密码保存、导出日志 | [账号、密码与诊断](docs/security.md) |
| 在 OpenWrt 路由器上使用 | [路由器指南](https://github.com/SeaLoong/drcom4scut/blob/master/docs/openwrt.md) |

遇到问题时先点“环境自检”，再按窗口提示处理。需要求助可“导出诊断”，附上系统、版本和操作经过。

[版本记录](docs/changes.md) · [开发与发布](docs/development.md) · [许可证](LICENSE) · [第三方组件](THIRD-PARTY-NOTICES.md)
