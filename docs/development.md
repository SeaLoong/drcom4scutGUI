# 开发、测试与发布

[返回首页](../README.md)

## 环境和项目

使用 `global.json` 选择的稳定版 .NET 10 SDK、C# 14，以及锁文件中的正式版 Avalonia 桌面依赖。发布目标为六个自包含 RID：`win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64`。

| 目录 | 职责 |
| --- | --- |
| `drcom4scutGUI` | Avalonia 窗口、托盘、当前用户登录启动与桌面入口 |
| `drcom4scutGUI.Core` | 配置、系统密码保护、核心进程和连接状态 |
| `drcom4scutGUI.Tests` | 配置、安全参数、系统适配器、真实子进程生命周期 |
| `drcom4scutGUI.Tests/FakeCore` | 验证 stdin、状态与停止通道的独立辅助进程 |
| `drcom4scutGUI.UiTests` | Avalonia Headless 下的窗口和交互检查 |

macOS GUI 的支持范围从 15 开始，并要求处于 [.NET 10 官方支持范围](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)。这由运行时与桌面依赖共同决定，不等于 Rust 核心的最低操作系统版本。Linux 使用 X11，Wayland 会话需要 XWayland；发行包面向 glibc 桌面，不是 OpenWrt/musl GUI。

## 锁定恢复、构建和测试

在仓库根目录执行：

```sh
dotnet restore drcom4scutGUI.sln --locked-mode
dotnet build drcom4scutGUI/drcom4scutGUI.csproj --configuration Release --no-restore -warnaserror
dotnet test --project drcom4scutGUI.Tests/drcom4scutGUI.Tests.csproj --configuration Release --no-restore --report-trx --results-directory artifacts/test-results/core
dotnet test --project drcom4scutGUI.UiTests/drcom4scutGUI.UiTests.csproj --configuration Release --no-restore --report-trx --results-directory artifacts/test-results/ui
python .github/scripts/check_test_results.py artifacts/test-results/core
python .github/scripts/check_test_results.py artifacts/test-results/ui
python -B -m unittest discover -s .github/scripts -p 'test_*.py'
```

测试使用 Microsoft.Testing.Platform；不要将命令替换为 VSTest 的 `--logger` 用法。结果目录应来自本次运行，并确认每组至少一项测试实际执行且没有失败。平台跳过、无显示测试、原生窗口检查和实网验证分别报告，不能用格式化、编译或零测试代替。

共享逻辑测试覆盖凭据序列化、旧文件处理、门户 URL 与参数、真实子进程 stdin/双输出流、取消、控制通道和结构化状态。Windows 执行 DPAPI 与 ACL 条件测试，Unix 执行文件和 socket 权限检查；Headless 测试检查 UI 结构与交互，不要求图形桌面。

原生密码库验证单独配置测试环境：macOS 的[测试入口](https://github.com/SeaLoong/drcom4scutGUI/blob/master/.github/scripts/test_native_keychain.py)创建临时 Keychain，测试后恢复 runner 的默认钥匙串与搜索列表；Linux 的[测试入口](https://github.com/SeaLoong/drcom4scutGUI/blob/master/.github/scripts/test_native_secrets.sh)在隔离 D-Bus/用户数据目录中启动 Secret Service。它们执行实际系统适配器，不访问开发者个人密码库。本地普通测试缺少相应系统会话时，按测试条件报告跳过，不把跳过当作密码库已验证。

原生窗口检查使用 `--smoke-test`。它实际显示窗口、检查可见尺寸后退出，不读取用户配置、不启动核心、不写密码库，也不进行认证。Linux CI 在 Xvfb 中运行；macOS、Windows 在对应原生 runner 上执行。该检查不验证真实托盘宿主、用户确认密码库提示、Npcap/BPF 权限或校园联网。

有意更新依赖时同步各项目的 `packages.lock.json`，再重新进行锁定恢复、构建与测试。更新命令可使用 `dotnet restore drcom4scutGUI.sln --use-lock-file --force-evaluate`，但不得在普通 CI 中用它悄悄改写依赖。不要关闭包签名、校验和或 TLS 校验来绕过恢复问题。

## 系统适配与秘密存储

配置路径见[账号与隐私](security.md#保存了哪些内容)。Windows 保留原有 CurrentUser DPAPI；macOS 使用登录 Keychain，Linux 使用 `/usr/bin/secret-tool` 访问当前桌面的 Secret Service。Unix 平台将随机 AES-GCM 密钥交给系统密码库保存，JSON 中只写密文，不创建配置旁的明文密钥文件。

每个账号的“记住密码”决定是否保存密码。不保存时仍可记录用户名、网络字段和自动选项，本次输入只留在运行内存；加载此账号不应访问密码库恢复不存在的密码。系统密码库缺失、锁定、拒绝访问或密文损坏时报告错误，不回退到明文。

登录启动只作用于当前用户：Windows 使用 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`；macOS 使用 `~/Library/LaunchAgents/net.sealoong.drcom4scutGUI.plist`；Linux 使用 XDG 配置目录下的 `autostart/drcom4scutGUI.desktop`。启用 macOS 启动项只登记下次登录的 `RunAtLoad`，不额外启动当前会话。

Linux 不自动隐藏主窗口，即使桌面 API 提供托盘对象，也不能据此假设存在可见托盘宿主。Windows、macOS 仅在托盘可用时收起；macOS 从菜单栏图标菜单恢复窗口。

## 与核心的接口

GUI 要求核心 0.5.0 或更新版本。Windows 查找 `drcom4scut.exe`，Unix 查找 `drcom4scut`；只从安装位置查找，不搜索 `PATH`。macOS `.app` 优先使用应用包同级的核心；普通安装应采用此布局，避免修改已签名的应用包。

- 密码通过 UTF-8、无 BOM 的私有重定向 stdin 发送一行，然后关闭输入；换行或 NUL 不属于此接口可接受的密码。密码不进入参数或环境变量。
- 其他参数使用独立 `ArgumentList` 条目。模式始终显式指定；有线模式明确传 `--with-udp` 或 `--no-udp`。
- 无线未允许 HTTP 时传 `--require-https-portal`；允许时才传 `--allow-insecure-portal`。URL 校验见 [PortalOptions.cs](https://github.com/SeaLoong/drcom4scutGUI/blob/master/drcom4scutGUI.Core/PortalOptions.cs)。
- Windows 使用随机的 CurrentUserOnly 命名管道。Unix 在私有临时目录中创建 Unix-domain socket，通过 `--control-socket` 交给核心；目录 `0700`、socket `0600`，核心核对所有者及对端用户，路径为绝对路径且不超过 103 字节。
- 固定 UTF-8 `stop\n` 请求注销退出；父进程关闭通道或崩溃产生的 EOF 同样进入停止流程。GUI 先给核心有界正常退出时间，超时才结束自己拥有的进程，不按进程名称杀死其他核心。
- stdout/stderr 必须同时读取；日志只作诊断。测试辅助核心也明确使用 UTF-8，避免 Windows 控制台代码页影响凭据。
- 每次认证使用独立私有状态目录与 `--status-file`，结束后清理。只在 JSON schema、所属 PID、提供者及认证字段一致时接受结构化状态；超大、残缺或其他进程的文件不会成为认证成功。

完整有线模式等待 UDP 心跳完成，仅 EAP 等待 802.1X，无线等待门户确认在线。连接丢失时展示恢复阶段，保留核心重连；不以日志短语或进程存在代替在线状态。

## 本地发布包

以下以原生 Linux x64 为例；在其他平台换成对应 RID 和输出目录。macOS `.app` 打包及签名检查应在 macOS 执行：

```sh
dotnet restore drcom4scutGUI/drcom4scutGUI.csproj --locked-mode
dotnet publish drcom4scutGUI/drcom4scutGUI.csproj --configuration Release --runtime linux-x64 --self-contained true --no-restore --output artifacts/publish/linux-x64
python .github/scripts/package_release.py --runtime linux-x64 --publish-dir artifacts/publish/linux-x64
```

每个归档含外层版本/RID 目录、README、`docs`、许可证、[第三方声明](../THIRD-PARTY-NOTICES.md)、`BUILD-INFO.json` 和自包含程序，不包含 Rust 核心或用户配置。Windows 使用 ZIP，Linux/macOS 使用 tar.gz；macOS 的 `.app/Contents/MacOS` 放置自包含程序及其原生依赖，`.app/Contents/Resources` 另保留文档、许可和来源信息。其 `Info.plist` 声明应用版本和系统要求，使用 ad-hoc 签名并检查完整性，不宣称 Developer ID 签名或 Apple 公证。

平台清单驱动 ABI、PE/ELF/Mach-O 架构、源版本、发布 runtime 和所需文件检查，拒绝错误架构、缺失运行时、账号配置或符号链接。版本由 `Directory.Build.props` 统一声明；元数据记录完整源码 SHA 和工作区状态。本地脏工作区的包不当作干净提交的正式发布。

## CI、自动预发布和手动 Release

[普通 CI](https://github.com/SeaLoong/drcom4scutGUI/blob/master/.github/workflows/ci.yml)调用[共用验证流程](https://github.com/SeaLoong/drcom4scutGUI/blob/master/.github/workflows/validate-build.yml)。六个 RID 分别在相应操作系统和 CPU 的原生 runner 上执行共享测试、Headless UI 测试、构建、窗口启动与打包。Linux 的 Xvfb 窗口检查仍是自动化显示环境，不证明所有桌面、Wayland/XWayland 或托盘组合已验证。

发布完整性检查要求平台清单中的六个包齐全，核对各包 SHA-256、runtime、源版本、完整源码提交和干净工作区后，统一生成 `SHA256SUMS`。不能把先前通过的运行当作后续提交的验证，也不能用某一平台包顶替另一平台产物。

向本仓库 `master` 推送提交且全部验证通过后，自动发布开发预览。PR、其他分支、fork 和手动运行普通 CI 只验证不发布。预览复用本次 CI 产物，先上传到草稿，核对远端文件名、大小和 SHA-256 后才公开为 **Pre-release**，不改变正式版 **Latest**。

预览标签为 `preview-运行号-尝试号-提交前8位`，固定到完整源码 SHA，不移动已有标签；程序版本仍由 manifest 声明。仅保留最近 10 个本流程公开预览，清理 Release 时保留 Git 标签；正式版、草稿和其他发布不被清理。

正式发布仍由 **Actions → Manual GUI release → Run workflow** 手动触发。标签与 `Directory.Build.props` 版本一致，例如 `v3.1.0`，默认创建草稿。流程重新验证源码及全部六个包，拒绝覆盖指向其他提交的已有标签。

自动预览与手动正式发布的安排参考 [Sensa 的 CI](https://github.com/SeaLoong/Sensa/blob/9dc7de27645fb484fa52711656bf31b05ff3f84a/.github/workflows/ci.yml)和[发布流程](https://github.com/SeaLoong/Sensa/blob/9dc7de27645fb484fa52711656bf31b05ff3f84a/.github/workflows/release.yml)。实际校园认证、抓包权限、密码库交互和长期在线仍需在目标环境验证。
