# drcom4scutGUI

[![构建与测试](https://github.com/SeaLoong/drcom4scutGUI/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/SeaLoong/drcom4scutGUI/actions/workflows/ci.yml)
[![下载预览版](docs/badges/preview.svg)](https://github.com/SeaLoong/drcom4scutGUI/releases/tag/preview-11-1-0416bf21)
[![支持的桌面系统](docs/badges/platforms.svg)](#安装)
[![许可证](docs/badges/license.svg)](LICENSE)

华南理工大学校园网的桌面登录工具。插好网线后，填写账号和密码即可发起认证；还可以记住多个账号、断线重试、随系统启动，并查看连接情况。

不熟悉命令行也可以使用。这个仓库提供操作窗口，实际登录由配套的 [drcom4scut 核心程序](https://github.com/SeaLoong/drcom4scut)完成，因此安装时需要下载两个包。

无线网络仍是你熟悉的“连上校园 Wi-Fi，再打开网页输入账号密码”。本工具的无线选项尝试代替手工提交受支持的校园登录网页；**它不会替你连接 Wi-Fi，也不是适用于所有登录页的通用工具**。这部分尚未经过实际校园网验证，详见[无线使用说明](docs/usage.md#无线-portal)。使用浏览器手动登录不需要安装本工具。

## 能做什么

- 登录有线校园网，断线后尝试重新认证。
- 保存和切换多个账号，也可选择每次重新输入密码。
- 查看当前连接情况、检查运行条件、导出排障信息。
- 设置登录电脑后打开程序、打开程序后自动认证。
- 在 Windows、macOS 上通过托盘或菜单栏找回窗口；Linux 保留普通窗口。
- 尝试自动提交受支持的校园无线登录网页，并查询当前设备是否在线。

## 安装

本文对应 **GUI 3.1.0 + 核心 0.5.0 或更新的兼容版本**。目前跨平台版本在发布页标为 **Pre-release（预发布）**；请展开这类版本的 **Assets（文件列表）**下载。标为 **Latest** 的旧正式版可能只有 Windows 包；也不是每个旧预发布都包含所有系统，请按版本和文件名选择。

### 1. 下载界面程序

打开[当前跨平台预览下载页](https://github.com/SeaLoong/drcom4scutGUI/releases/tag/preview-11-1-0416bf21)，或在[全部版本](https://github.com/SeaLoong/drcom4scutGUI/releases)中选择 **3.1.0 或更新版本、且包含所需系统包**的预发布。按下表找到文件名结尾对应的包，并解压整个文件夹。程序已带运行所需组件，不必另外安装 .NET。

| 你的电脑 | 选择的包 | 打开的程序 |
| --- | --- | --- |
| 普通 Intel / AMD Windows 电脑 | `win-x64.zip` | `drcom4scutGUI.exe` |
| Windows ARM 电脑 | `win-arm64.zip` | `drcom4scutGUI.exe` |
| Intel Mac | `osx-x64.tar.gz` | `drcom4scutGUI.app` |
| Apple 芯片 Mac，如 M1 / M2 / M3 / M4 | `osx-arm64.tar.gz` | `drcom4scutGUI.app` |
| 普通 Intel / AMD Linux 电脑 | `linux-x64.tar.gz` | `drcom4scutGUI` |
| ARM64 Linux 电脑 | `linux-arm64.tar.gz` | `drcom4scutGUI` |

Windows 可在“设置 → 系统 → 关于”查看系统类型；Mac 在“苹果菜单 → 关于本机”查看“芯片”或“处理器”。Mac 需要 **macOS 15 或更新的受支持版本**，Linux 需要图形桌面。首次打开被拦截或缺少运行组件时，见[各系统安装说明](docs/usage.md#安装和系统要求)。不要下载文件列表中的 `Source code`，它是供开发者使用的源码。

### 2. 放好配套核心

打开[核心下载页](https://github.com/SeaLoong/drcom4scut/releases)，选择相同系统、相同处理器的 **0.5.0 或更新的兼容版本**并解压。按以下方式放置程序：

- **Windows：** 将 `drcom4scut.exe` 放进 `drcom4scutGUI.exe` 所在的文件夹。
- **macOS：** 将 `drcom4scut` 放在 `drcom4scutGUI.app` 旁边，**不要放进 `.app` 里面**。
- **Linux：** 将 `drcom4scut` 放进 `drcom4scutGUI` 所在的文件夹。

只复制核心程序即可；GUI 文件夹中原有的其他文件也要保留。不会选核心文件时，按[下载对应表](docs/usage.md#下载哪一个核心包)操作。

### 3. 准备校园网络

**插网线认证：** Windows 需要安装 [Npcap](https://npcap.com/#download)，它让核心能够与校园网交换登录报文；Mac 和 Linux 也需要一次相应授权，见[系统安装说明](docs/usage.md#安装和系统要求)。GUI 本身按普通程序打开，不要以管理员或 `sudo` 身份运行它。

**无线网页登录：** 先在系统里连接校园 Wi-Fi。无线选项不需要 Npcap 或上述有线授权。

## 第一次登录

| 你要怎么上网 | 在“模式”中选择 | 接下来做什么 |
| --- | --- | --- |
| 插网线 | **有线 EAP + UDP** | 填账号、密码，选择连着校园网线的网卡，点击“认证” |
| 连校园 Wi-Fi 后网页登录 | **无线 Portal** | 填账号、密码和学校实际登录页地址，点击“认证”；[先查看适用范围](docs/usage.md#无线-portal) |

“本机源 IP”以及无线的其他地址项通常先留空。只有确认校园网不需要完整的有线认证时，才选择“有线仅 EAP”，不要把它当作认证失败时的通用修复方法。

无线地址应来自**尚未填写账号密码的学校登录页面**，不要复制提交后的登录请求。若页面需要验证码、扫码或短信，请继续使用学校网页。学校只提供 `http://` 时，先看[密码传输说明](docs/security.md#无线门户与密码传输)，再决定是否允许明文传输。

想让程序下次自动填密码，可以勾选“记住密码”；保存的密码由当前系统用户的密码保护功能保管。保存失败时，可取消勾选并重新输入密码进行本次登录。

认证期间按钮会变为“停止”。点击它可请求注销并停止本次重连；关闭窗口或在托盘菜单选择“退出”，也会停止认证并关闭程序。仅最小化窗口不会停止联网。

## 遇到问题

| 遇到的情况 | 下一步 |
| --- | --- |
| 提示找不到核心或版本不兼容 | 核对两个包的版本、系统和处理器，以及核心是否放在上述位置 |
| 有线认证失败 | 点击“环境自检”，按提示检查网线、网卡和运行权限，再看下方日志 |
| 无线在浏览器能登录，程序不能 | 先继续使用浏览器；程序只支持特定网页登录流程，见[无线排障](docs/usage.md#无线-portal) |
| 窗口不见了 | Windows 找右下角托盘图标，Mac 找顶部菜单栏图标，再选择“打开” |
| 密码无法保存 | 先取消“记住密码”；Linux 另需可用的桌面密码库，见[密码说明](docs/security.md) |
| 需要请别人帮忙排查 | 点击“导出诊断”，检查并删去私人信息后再分享，见[分享诊断](docs/security.md#导出和分享诊断) |

自检通过表示电脑的运行条件满足，不等于学校服务器已接受账号。更多操作见[详细使用](docs/usage.md)，密码保存与隐私见[账号、密码与诊断](docs/security.md)。

## 其他资料

[开发、测试与发布](docs/development.md) · [版本与参考资料](docs/changes.md) · [许可证](LICENSE) · [第三方组件声明](THIRD-PARTY-NOTICES.md)
