# 安装与日常使用

[返回首页](../README.md)

按自己的电脑完成安装后，就可以在窗口中连接校园网。本页也介绍多账号、自动连接和常见问题。

## 安装和系统要求

先下载两个包：**GUI 提供操作窗口，核心执行校园认证**。GUI 自带运行组件，无需另外安装 .NET。

从 [GUI 发布页](https://github.com/SeaLoong/drcom4scutGUI/releases)选择最新的 **Pre-release（预览版）**，再到[核心发布页](https://github.com/SeaLoong/drcom4scut/releases)选择配套核心。本文对应 GUI **3.1.1** 和核心 **0.5.1**。在 **Assets（文件列表）**下载下表中的程序包；`Source code` 用于开发。

### 下载哪一个核心包

Windows 在“设置 → 系统 → 关于”查看系统类型；Mac 在“苹果菜单 → 关于本机”查看芯片。Linux 终端运行 `uname -m`：`x86_64` 选 x64，`aarch64` 选 ARM64。

| 电脑 | GUI 包的文件名结尾 | 核心包的文件名结尾 |
| --- | --- | --- |
| Windows，Intel / AMD | `win-x64.zip` | `x86_64-pc-windows-msvc.zip` |
| Windows，ARM64 | `win-arm64.zip` | `aarch64-pc-windows-msvc.zip` |
| Mac，Intel | `osx-x64.tar.gz` | `x86_64-apple-darwin.tar.gz` |
| Mac，Apple 芯片 | `osx-arm64.tar.gz` | `aarch64-apple-darwin.tar.gz` |
| Linux，Intel / AMD | `linux-x64.tar.gz` | `x86_64-unknown-linux-gnu.tar.gz` |
| Linux，ARM64 | `linux-arm64.tar.gz` | `aarch64-unknown-linux-gnu.tar.gz` |

完整解压 GUI 包，保留全部文件，再放入核心程序。需要检查下载完整性时，可将压缩包的 SHA-256 与发布页 `SHA256SUMS` 中对应的值比较：Windows 用 `Get-FileHash 文件名 -Algorithm SHA256`，Mac 用 `shasum -a 256 文件名`，Linux 用 `sha256sum 文件名`。

### Windows

推荐使用受支持的 Windows 11。文件应像这样放在一起：

```text
校园网客户端/
├── drcom4scutGUI.exe
├── drcom4scut.exe
└── 界面包中的其他文件……
```

有线连接先安装与你系统对应的 [Npcap](https://npcap.com/#download)，它提供校园认证需要的网络报文访问能力。然后双击 `drcom4scutGUI.exe`，按普通用户运行。校园网页认证可以直接使用。

### macOS

GUI 需要 **macOS 15 或更新且受 .NET 10 支持的版本**；完整列表见[系统支持说明](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)。Apple 芯片选 ARM64 包，可直接运行。

把核心放在 `.app` 旁边，移动安装位置时将两者一起移动：

```text
校园网客户端/
├── drcom4scutGUI.app
└── drcom4scut
```

双击 `.app` 打开。当前程序尚未取得 Apple Developer ID 签名和公证；首次打开提示来源未验证时，核对下载来源后，按 Apple 的[单个应用打开方法](https://support.apple.com/guide/mac-help/open-a-mac-app-from-an-unknown-developer-mh40616/mac)，在“系统设置 → 隐私与安全性”允许这份应用。

核心是独立程序，也需要系统允许运行。在它所在文件夹的终端执行 `./drcom4scut --version`，按系统提示允许后，再打开 GUI。

有线连接还需要一次 **BPF 授权**，即允许核心访问认证使用的网络报文设备。按[核心 Mac 安装指南](https://github.com/SeaLoong/drcom4scut/blob/master/docs/macos.md)完成后，以普通用户打开 GUI。校园网页认证可以直接使用。

### Linux

GUI 适用于带图形桌面的常见发行版，例如 Ubuntu 22.04 / 24.04；使用 X11，Wayland 桌面需要 XWayland。路由器安装请看 [OpenWrt 指南](https://github.com/SeaLoong/drcom4scut/blob/master/docs/openwrt.md)。

将核心 `drcom4scut` 放进 GUI 文件夹，保留解压出的其他文件，双击 `drcom4scutGUI` 或在该目录执行：

```sh
./drcom4scutGUI
```

**缺少组件或中文字体：** Ubuntu 24.04 可以安装以下系统组件，其他发行版使用对应包名：

```sh
sudo apt install libx11-6 libice6 libsm6 libxcb1 libxrandr2 libxi6 \
  libxcursor1 libxrender1 libgl1 libfontconfig1 libgtk-3-0t64 \
  libicu74 libssl3t64 fonts-noto-cjk
```

完整依赖见 [Avalonia 的 Linux 说明](https://docs.avaloniaui.net/docs/supported-platforms#desktop-linux)与 [.NET 系统列表](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)。桌面包使用 glibc。

**记住密码：** Ubuntu 安装 `libsecret-tools`，并解锁桌面密码库，例如 GNOME Keyring。它们一起提供安全保存密码的功能；也可以取消“记住密码”，每次输入。

**有线连接：** 在解压后的文件夹，给核心授予网络报文权限，然后检查：

```sh
sudo setcap cap_net_raw=ep ./drcom4scut
getcap ./drcom4scut
./drcom4scut doctor
```

`cap_net_raw` 是这项权限的系统名称，授权对象是核心 `drcom4scut`。缺少 `setcap` 时，Ubuntu 安装 `libcap2-bin`。之后以普通用户打开 GUI，点击“环境自检”。每次替换核心后重新授权；受管理的电脑由管理员协助完成。校园网页认证可以直接使用。

## 有线认证

1. 插好网线，确认系统的有线连接已启用。
2. 模式选择“有线网络”，填写校园账号和密码，包括学校要求的账号后缀。
3. 网卡先用“自动选择”；需要指定时，选插着校园网线的以太网卡。“本机 IP（可选）”通常留空。
4. 按需勾选“记住密码”，点击“连接”。

窗口会显示身份认证、在线确认和连接结果。等待完整连接成功后即可上网；断线时程序会继续尝试恢复。

“有线网络（仅 802.1X）”用于学校只要求身份认证的特殊网络。华工常规有线连接使用默认的“有线网络”。

## 无线 Portal

### 这与浏览器登录有什么关系

无线使用顺序仍然是：**连接校园 Wi-Fi → 打开学校网页 → 输入账号密码登录。** 客户端的“校园网页认证”代为提交受支持的 DrCOM/ePortal 网页请求，并查询当前设备的在线状态。Wi-Fi 连接由系统完成。

这项功能参考了公开校园客户端，已通过模拟服务测试，实际校园使用情况仍待验证。需要验证码、短信、扫码或统一身份认证跳转的页面，继续使用浏览器即可。实现来源见[参考记录](https://github.com/SeaLoong/drcom4scut/blob/master/docs/references-clients.md)。

### 使用校园网页认证

1. 在系统中连接校园 Wi-Fi，确认平时的校园登录页能正常打开。
2. 在客户端选择“校园网页认证”，填写账号和密码。
3. 将学校**尚未提交账号密码的登录页地址**填入“登录页地址”。“本机 IP（可选）”和“更多网页设置”通常留空。
4. 点击“连接”，等待学校确认当前设备在线。

优先使用学校提供的 `https://` 地址，账号密码会加密传输。学校只提供 `http://` 时，可核对入口后勾选“登录页使用 HTTP（明文传输）”；这会将账号密码明文发送到学校服务器。[了解传输与密码保存](security.md#无线门户与密码传输)。

点击“断开”会请求注销并停止重连。如需同时关闭学校的设备自动登录，请在校园网页中管理绑定和自动登录选项。

### 浏览器能用，程序不能用怎么办

先用浏览器完成登录，再检查客户端日志中的错误信息。常见原因是学校页面流程不同、入口地址变化，或账号后缀需要调整。

求助时附上客户端版本、校区、错误信息和学校公开登录页地址。诊断文本的分享方法见[导出和分享诊断](security.md#导出和分享诊断)。

### 高级：无线地址字段

这些字段位于“更多网页设置”，通常保持空白，由核心自动查询。学校或排障人员提供了明确数值后，再按下表填写。

| 字段 | 用途 |
| --- | --- |
| 登录页地址 | 平时在浏览器打开的学校登录入口 |
| API 地址 | 学校网页提交请求的后台地址，与入口保持同一主机 |
| AC IPv4 | 校园接入设备的地址 |
| 载荷 IPv4 | 学校登录系统识别的本机地址 |
| 本机 IP（可选） | 电脑实际发出请求时使用的本地地址 |

经过路由器时，学校看到的设备地址可能与电脑地址不同。前者用于登录数据，后者用于选择网络出口。

## 多账号

| 想做什么 | 操作 |
| --- | --- |
| 添加账号 | 输入新账号和密码，点击“连接” |
| 切换账号 | 先“断开”，从账号下拉框选择另一个账号，再“连接” |
| 修改密码 | 选中账号，填写新密码，按需勾选“记住密码”，再“连接” |
| 删除本机记录 | 先“断开”，选中账号，点击“删除”并确认 |

每个账号分别设置“记住密码”。取消勾选仍会保存账号和网络设置，下次填写密码即可使用。本机记录与学校账号独立；学校密码在学校账号管理入口修改。

## 托盘、停止和退出

Windows 的右下角托盘、Mac 的顶部菜单栏都有程序图标。连接成功后，图标可用时窗口会收起；从图标菜单选择“打开”即可找回。Linux 保留普通窗口。

| 操作 | 结果 |
| --- | --- |
| 最小化或收起窗口 | 继续保持连接 |
| 点击“断开” | 注销本次连接并停止重连 |
| 关闭窗口或选择菜单“退出” | 停止连接并退出程序 |

连接中断时可打开窗口查看恢复进度，程序会继续尝试连接。

## 自动启动和自动认证

| 选项 | 作用 |
| --- | --- |
| 登录系统时启动 | 下次登录电脑时打开客户端 |
| 打开后自动连接 | 打开客户端后使用已选账号连接 |

想要登录电脑后自动上网，勾选这两项，并记住所选账号的密码。设置针对当前系统用户生效。移动程序文件夹后，重新设置启动选项即可更新位置。

## 排查连接问题

先点击“环境自检”，再按报告检查。自检负责确认电脑的运行条件；连接时窗口会继续显示学校的认证结果。

| 遇到的情况 | 处理方法 |
| --- | --- |
| 找不到核心或版本不兼容 | 核对[下载对应表](#下载哪一个核心包)，将核心放在 GUI 旁边，使用对应系统与处理器的版本 |
| Mac 核心无法启动 | 在终端运行 `./drcom4scut --version`，完成系统允许后再试 |
| 有线环境自检未通过 | Windows 检查 Npcap；Mac、Linux 按安装步骤完成核心授权，再核对网卡 |
| 填好账号仍连接失败 | 核对账号后缀、密码、网线与所选网卡，查看窗口下方的学校返回信息 |
| 浏览器能登录，校园网页认证失败 | 使用浏览器上网，并按[无线说明](#无线-portal)核对入口与页面类型 |
| 密码无法保存 | 解锁系统密码库，或取消“记住密码”后输入密码连接 |
| Linux 中文显示方框 | 安装中文字体，如 Ubuntu 的 `fonts-noto-cjk` |
| 窗口找不到了 | 从 Windows 托盘或 Mac 菜单栏图标选择“打开” |

需要进一步帮助时，“导出诊断”并附上系统、版本、校区、操作经过，以及问题发生前是否切换 Wi-Fi、VPN、网线或从睡眠恢复。分享前按[诊断说明](security.md#导出和分享诊断)检查私人信息。

## 高级：核心配置

额外的核心选项放在 GUI [配置目录](security.md#保存了哪些内容)中的 `core.yml`，修改前先断开连接。字段参考[核心配置说明](https://github.com/SeaLoong/drcom4scut/blob/master/docs/configuration.md)；账号密码由窗口提供。

`profile` 是有线报文兼容选项，默认 `legacy`，正常使用保持默认即可。它与 Windows、Mac 或 Linux 无关。窗口选择的连接方式、HTTP 许可和地址会优先传给核心。
