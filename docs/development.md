# 开发、测试与发布

[返回首页](../README.md)

本页供修改源码、运行测试和发布版本时查阅。客户端安装与日常操作见[使用指南](usage.md)。

## 环境和项目

使用 `global.json` 指定的稳定版 .NET 10 SDK、C# 14 和 Avalonia。NuGet 锁文件固定依赖版本，发布包自带运行时。六个运行目标为 `win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64`；这些系统与处理器组合也称为 RID。

| 目录 | 职责 |
| --- | --- |
| `drcom4scutGUI` | Avalonia 窗口、托盘和桌面入口 |
| `drcom4scutGUI.Core` | 配置、密码保护、核心进程和连接状态 |
| `drcom4scutGUI.Tests` | 共享逻辑、系统适配器和真实子进程测试 |
| `drcom4scutGUI.Tests/FakeCore` | 模拟核心输入、状态和停止通道的独立进程 |
| `drcom4scutGUI.UiTests` | Avalonia Headless 界面交互测试 |

macOS GUI 从 15 开始支持，具体系统范围遵循 [.NET 10 系统列表](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)。Linux 包使用 glibc、X11；Wayland 会话通过 XWayland 运行。

## 锁定恢复、构建和测试

在仓库根目录执行以下命令。测试输出目录使用本次运行的新目录，便于确认实际执行结果：

```sh
dotnet restore drcom4scutGUI.sln --locked-mode
dotnet build drcom4scutGUI/drcom4scutGUI.csproj --configuration Release --no-restore -warnaserror
dotnet test --project drcom4scutGUI.Tests/drcom4scutGUI.Tests.csproj --configuration Release --no-restore --report-trx --results-directory artifacts/test-results/core
dotnet test --project drcom4scutGUI.UiTests/drcom4scutGUI.UiTests.csproj --configuration Release --no-restore --report-trx --results-directory artifacts/test-results/ui
python .github/scripts/check_test_results.py artifacts/test-results/core
python .github/scripts/check_test_results.py artifacts/test-results/ui
python -B -m unittest discover -s .github/scripts -p 'test_*.py'
```

测试使用 Microsoft.Testing.Platform，入口为 `dotnet test --project`，结果通过 `--report-trx` 写出。检查脚本要求有实际执行的测试且结果通过。

| 检查 | 覆盖范围 |
| --- | --- |
| 共享逻辑 | 凭据序列化、旧文件读取、门户参数、日志隐藏、状态解析、子进程输入输出、取消和停止 |
| Headless UI | 控件、选项和窗口交互逻辑 |
| Windows 原生 | DPAPI 与 ACL 权限 |
| Linux / macOS 原生 | 文件和 socket 权限、系统密码库 |
| 发布包窗口检查 | 解压后的程序实际显示窗口并正常退出 |

macOS 的[密码库测试](https://github.com/SeaLoong/drcom4scutGUI/blob/master/.github/scripts/test_native_keychain.py)创建临时 Keychain，结束后恢复 runner 原有设置；Linux 的[密码库测试](https://github.com/SeaLoong/drcom4scutGUI/blob/master/.github/scripts/test_native_secrets.sh)在隔离的 D-Bus 和用户目录启动 Secret Service。本地缺少相应会话时，测试结果会注明跳过。

窗口检查通过 `--smoke-test` 启动真实窗口，检查可见尺寸后退出，使用独立测试流程。Linux CI 使用 Xvfb，Windows、macOS 在对应系统执行。校园认证、实际托盘宿主和授权交互另在目标设备验证。

更新依赖时，可执行 `dotnet restore drcom4scutGUI.sln --use-lock-file --force-evaluate`，提交更新后的锁文件，再执行上述锁定恢复、构建和测试。

## 系统适配与秘密存储

配置位置见[账号与密码](security.md#保存了哪些内容)。Windows 使用 DPAPI CurrentUser；macOS 使用登录 Keychain；Linux 通过 `/usr/bin/secret-tool` 使用 Secret Service。Unix 平台把随机 AES-GCM 密钥交给系统密码库，JSON 保存密码密文。

“记住密码”对每个账号独立生效。关闭时保留账号及网络设置，本次密码仅留在运行内存。密码库锁定、不可用或密文损坏时给出明确提示，让用户选择解锁或本次输入。

日志在进入缓冲区前隐藏本次会话的密码和账号；诊断导出沿用已处理的内容。超长核心日志整行省略，自检输出超过上限时结束子进程。界面合并高频日志刷新，保持响应；相关测试覆盖账号切换、超长输出与子进程退出。

登录启动仅针对当前用户：Windows 使用 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`；macOS 使用 `~/Library/LaunchAgents/net.sealoong.drcom4scutGUI.plist`；Linux 使用 XDG 配置目录下的 `autostart/drcom4scutGUI.desktop`。macOS 的 `RunAtLoad` 在下次登录时打开程序。

Windows、macOS 在托盘可用时收起窗口，并提供“打开”入口。Linux 保持窗口可见，适应不同桌面的托盘支持情况。

## 与核心的接口

GUI 收集输入和展示状态，核心执行登录。最低兼容核心版本为 0.5.0，本轮推荐 0.5.1。

| 接口 | 约定 |
| --- | --- |
| 程序位置 | 安装目录的 `drcom4scut.exe` 或 `drcom4scut`；macOS 位于 `.app` 旁边 |
| 密码 | 私有重定向 stdin，UTF-8 无 BOM，一行后关闭；输入校验排除换行与 NUL |
| 其他参数 | 使用独立 `ArgumentList` 条目，显式选择 provider 和有线 UDP 开关 |
| 网页 HTTPS | 默认 `--require-https-portal`；用户选择 HTTP 后使用 `--allow-insecure-portal` |
| Windows 停止通道 | 随机 CurrentUserOnly 命名管道 |
| Unix 停止通道 | 私有目录 `0700`、socket `0600`；核对所有者与对端用户，绝对路径最多 103 字节 |
| 停止请求 | UTF-8 `stop\n`；控制通道关闭产生 EOF 时同样请求注销 |
| 进程输出 | 同时读取 stdout 与 stderr，统一 UTF-8 |
| 连接状态 | 每次会话的私有状态文件；检查 JSON schema、PID、provider 和认证字段 |

GUI 先给核心有界正常退出时间，超时再结束自己启动的进程。凭据仅通过私有输入传递，核心选择限定在安装位置。

完整有线连接等待身份认证和 UDP 在线确认；仅 802.1X 模式等待身份认证；校园网页认证等待门户确认在线。状态文件决定界面连接状态，日志负责辅助诊断。

网页登录由系统先连接 Wi-Fi，再由核心提交受支持的 DrCOM/ePortal 请求。协议来源和测试范围见[核心无线说明](https://github.com/SeaLoong/drcom4scut/blob/master/docs/portal.md)。学校的自动登录与设备绑定由官方网页管理。

## 本地发布包

在原生 Linux x64 上，可以这样构建安装包；其他系统换成对应 RID：

```sh
dotnet restore drcom4scutGUI/drcom4scutGUI.csproj --locked-mode
dotnet publish drcom4scutGUI/drcom4scutGUI.csproj --configuration Release --runtime linux-x64 --self-contained true --no-restore --output artifacts/publish/linux-x64
python .github/scripts/package_release.py --runtime linux-x64 --publish-dir artifacts/publish/linux-x64
```

归档包含版本/RID 外层目录、自包含程序、README、`docs`、许可证、第三方声明和 `BUILD-INFO.json`。核心由用户单独下载。Windows 使用 ZIP，Linux/macOS 使用 tar.gz。

macOS 打包在 macOS 执行：原生启动程序位于 `.app/Contents/MacOS`，完整 .NET 载荷位于 `.app/Contents/Resources/publish`。`Info.plist` 记录版本和系统要求；ad-hoc 签名在生成归档及解压后检查完整性。Developer ID 签名和 Apple 公证是独立的发布步骤，目前尚未配置。

打包脚本根据平台清单核对 PE、ELF、Mach-O 格式、处理器、运行时和必要文件。`Directory.Build.props` 统一程序版本；来源信息记录完整提交 SHA 与工作区状态。正式流水线要求干净工作区和精确匹配的源版本，并检查包内没有账号配置或符号链接。

## CI、自动预发布和手动 Release

[CI](https://github.com/SeaLoong/drcom4scutGUI/blob/master/.github/workflows/ci.yml)调用[共用验证流程](https://github.com/SeaLoong/drcom4scutGUI/blob/master/.github/workflows/validate-build.yml)，在六种系统与处理器的原生 runner 上执行测试、窗口检查和打包。汇总阶段检查六个包、源版本、完整 SHA、工作区状态与校验值，再生成 `SHA256SUMS`。

向 `master` 推送并通过全部验证后，流程使用本次产物发布预览版。PR、其他分支、fork 和手动普通 CI 执行验证。预览先上传到草稿，核对远端文件名、大小和 SHA-256 后公开为 **Pre-release**。

预览标签为 `preview-运行号-尝试号-提交前8位`，固定到完整源码 SHA；程序版本来自 manifest。流程保留最近 10 个自动预览，清理预览 Release 时保留标签。正式版 **Latest** 保持独立。

正式发布通过 **Actions → Manual GUI release → Run workflow** 手动触发，标签使用 `Directory.Build.props` 中的版本，例如 `v3.1.1`。流程重新验证源码与全部产物，先上传到私有草稿，再核对远端文件校验值、大小和标签提交。`draft` 默认为 `true`，便于维护者检查；选择 `false` 时，校验通过后公开为正式版 **Latest**。发布正文提供中文下载说明、平台选择和本版变更。

官网由核心仓库统一构建，直接导入这里的 README 和 `docs`。修改帮助内容后，在核心站点的来源清单中更新 GUI 提交 SHA，即可发布同一份文档。

预览与正式发布流程参考 [Sensa 的 CI](https://github.com/SeaLoong/Sensa/blob/9dc7de27645fb484fa52711656bf31b05ff3f84a/.github/workflows/ci.yml)与[发布实现](https://github.com/SeaLoong/Sensa/blob/9dc7de27645fb484fa52711656bf31b05ff3f84a/.github/workflows/release.yml)。
