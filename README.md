# drcom4scutGUI

[![构建与测试](https://github.com/SeaLoong/drcom4scutGUI/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/SeaLoong/drcom4scutGUI/actions/workflows/ci.yml)
[![下载桌面包](docs/badges/preview.svg)](https://github.com/SeaLoong/drcom4scut/releases/latest)
[![支持的桌面系统](docs/badges/platforms.svg)](#安装)
[![许可证](docs/badges/license.svg)](LICENSE)

**打开程序，填好账号，连接华南理工大学校园网。**

适用于 Windows、macOS 和 Linux。提供有线登录、断线重连、多账号、记住密码和开机自动连接，也可以代为提交受支持的校园网页登录。连接进度和排障信息都能在窗口里查看。

[软件首页与帮助中心](https://sealoong.github.io/drcom4scut/) · [下载客户端](https://github.com/SeaLoong/drcom4scut/releases/latest) · [完整使用指南](docs/usage.md) · [账号与密码](docs/security.md)

## 安装

选择你的系统，按对应指南完成下载、解压和首次连接：

| 你的电脑 | 开始安装 |
| --- | --- |
| Windows，Intel / AMD 或 ARM64 | [Windows 安装与连接](docs/windows.md) |
| Mac，Apple 芯片或 Intel，macOS 15 及以上 | [Mac 安装与连接](docs/macos.md) |
| Linux 图形桌面，x64 或 ARM64 | [Linux 桌面安装与连接](docs/linux.md) |

下载一个文件名含 **`desktop`** 的桌面包，完整解压后打开程序即可。包内已配好图形界面、认证核心和运行组件，也能直接使用命令行。下载页提供最新版本，包括标为 **Pre-release** 的预览版。

直接查文件名可看[下载对应表](docs/usage.md#下载哪一个核心包)。OpenWrt 路由器使用[路由器指南](https://github.com/SeaLoong/drcom4scut/blob/master/docs/openwrt.md)。

## 第一次登录

1. **接好网络。** 有线插好网线；无线先在系统里连上校园 Wi-Fi。
2. **选择方式，填写账号密码。** 有线选“有线网络”，网卡先用“自动选择”；无线选“校园网页认证”，填写平时使用的学校登录页地址。
3. **点击“连接”。** 按需勾选“记住密码”；成功后可以最小化窗口。断线时程序会尝试恢复，点击“断开”结束本次连接。

无线仍然是网页账号密码登录：客户端代为完成受支持的 DrCOM/ePortal 网页步骤。目前已通过模拟服务测试，实际校园适用情况请参照[无线说明](docs/usage.md#无线-portal)；验证码、扫码等页面可直接用浏览器登录。

## 程序界面

![Linux 上实际运行的有线连接窗口](docs/images/gui-linux-main.png)

Linux 实际运行截图，使用示例账号，处于连接前状态。查看各系统界面：[Windows](docs/windows.md#4-连接校园网) · [Mac](docs/macos.md#5-点击连接) · [Linux](docs/linux.md#4-打开窗口并连接)。

## 常用帮助

| 想做什么 | 查看这里 |
| --- | --- |
| 首次运行、Mac 打开提示、Linux 授权 | [各系统安装步骤](docs/usage.md#安装和系统要求) |
| 多个账号、自动连接、托盘与退出 | [日常操作](docs/usage.md#多账号) |
| 检查新版本、更新界面或核心 | [更新程序](docs/update.md) |
| 连接失败、找不到核心、密码无法保存 | [常见问题](docs/usage.md#排查连接问题) |
| 了解密码保存、导出日志 | [账号、密码与诊断](docs/security.md) |
| 在 OpenWrt 路由器上使用 | [路由器指南](https://github.com/SeaLoong/drcom4scut/blob/master/docs/openwrt.md) |

遇到问题时先点“环境自检”，再按窗口提示处理。需要求助可“导出诊断”，附上系统、版本和操作经过。

[版本记录](docs/changes.md) · [开发与发布](docs/development.md) · [许可证](LICENSE) · [第三方组件](THIRD-PARTY-NOTICES.md)
