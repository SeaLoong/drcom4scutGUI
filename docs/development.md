# 开发、测试与发布

[返回首页](../README.md)

## 环境和项目

使用 `global.json` 选择的稳定版 .NET 10 SDK。项目采用 C# 14，GUI 为 WPF/Windows Forms 托盘应用，发布目标为自包含的 `win-x64`。

在已有 checkout 的仓库根目录执行下面的命令。Windows 可以构建并运行 GUI；Linux 可以交叉构建 WPF 和运行共享逻辑测试，不能据此确认窗口、托盘、Npcap 或校园网络的实际行为。

| 目录 | 职责 |
| --- | --- |
| `drcom4scutGUI` | 窗口、托盘、当前用户登录启动设置 |
| `drcom4scutGUI.Core` | 配置、密码保护接口、核心进程与日志处理 |
| `drcom4scutGUI.Tests` | 配置、安全参数、异步生命周期及状态测试 |
| `drcom4scutGUI.Tests/FakeCore` | 验证真实子进程交互的独立辅助程序 |

## 锁定恢复、构建和测试

```sh
dotnet restore drcom4scutGUI.sln --locked-mode
dotnet build drcom4scutGUI/drcom4scutGUI.csproj --configuration Release --no-restore -warnaserror
dotnet test --project drcom4scutGUI.Tests/drcom4scutGUI.Tests.csproj --configuration Release --no-restore --report-trx --results-directory artifacts/test-results
python .github/scripts/check_test_results.py artifacts/test-results
python -B -m unittest discover -s .github/scripts -p 'test_*.py'
```

测试使用 Microsoft.Testing.Platform；不要将命令替换为 VSTest 的 `--logger` 用法。应检查本次生成的 TRX，确认至少一项测试实际执行且没有失败。发布工具检查不能用格式化或编译成功代替。

共享测试覆盖凭据序列化、旧文件处理、门户 URL 和参数校验、真实子进程 stdin/双输出流、取消、控制管道和日志处理。Windows 执行实际 DPAPI 测试，Unix 执行本地文件权限测试；对应另一平台的测试会明确跳过。普通自动化不连接真实校园账号。

依赖版本保存在各项目的 `packages.lock.json`。有意更新包版本后，运行：

```sh
dotnet restore drcom4scutGUI.sln --use-lock-file --force-evaluate
```

审查包引用和锁文件的变化，再重新执行锁定恢复与完整验证。不要关闭签名、校验和或 TLS 验证来绕过恢复错误。

## 与核心的接口

GUI 只启动安装目录中的 `drcom4scut.exe`，要求核心 0.4.1 或更新版本。启动前检查核心版本和所需的命令行能力；不支持时报告错误，不回退到密码参数。

- 密码通过 UTF-8、无 BOM 的重定向 stdin 发送一行，然后关闭输入。换行或 NUL 字符不属于这一接口可接受的密码。
- 其他参数使用独立的 `ArgumentList` 条目。模式始终显式指定；有线模式显式选择 `--with-udp` 或 `--no-udp`。
- 无线模式未允许 HTTP 时显式发送 `--require-https-portal`；允许时才发送 `--allow-insecure-portal`。URL 校验不接受凭据，公开查询字段以 [PortalOptions.cs](../drcom4scutGUI.Core/PortalOptions.cs) 为准。
- 生命周期使用随机的 `\\.\pipe\drcom4scut-{guid}`，服务器限制为当前用户。核心读取控制消息；固定 UTF-8 `stop\n` 请求注销退出。GUI 请求停止后给核心有界退出时间，超时才终止所属进程。
- 控制管道由父进程持有；断开或 EOF 使兼容核心进入停止流程。不要用无条件 kill 代替正常注销，也不要按名称终止其他核心实例。
- stdout/stderr 必须并行读取。测试辅助核心同样明确使用 UTF-8，避免 Windows 控制台代码页改变 Unicode 密码的含义。
- 每次认证创建独立的临时状态目录，通过 `--status-file` 交给本次核心。Windows 目录 ACL 只允许当前用户访问，Unix 目录权限为 `0700`；会话结束后清理。状态文件不用于存放账号密码。
- 连接状态从该文件的 JSON 读取，经 `AuthenticationStatus` 验证 schema、所属子进程 PID、提供者及认证字段的一致性后，才通过类型化回调更新界面。无效、超大、残缺或其他进程的状态不会成为认证成功；stdout/stderr 仅作为诊断文本。

认证成功状态按提供者区分：完整有线模式等待 UDP 心跳完成，仅 EAP 等待 802.1X 成功，无线模式等待门户确认在线。在线状态丢失后界面展示当前恢复阶段，保留核心自身的重连流程。不要把日志中的成功短语或进程仍在运行当作已经联网。

## 本地发布包

```sh
dotnet publish drcom4scutGUI/drcom4scutGUI.csproj --configuration Release --runtime win-x64 --self-contained true --no-restore --output artifacts/publish/win-x64
python .github/scripts/package_release.py --publish-dir artifacts/publish/win-x64
```

产物在 `dist`，包含 GUI、运行时、README、`docs` 文档、许可证、来源元数据及对应 SHA-256 校验文件；不包含 Rust 核心或用户配置。打包器会检查目标平台、源版本和必需的自包含运行时文件，并拒绝账号文件或 `core.yml` 等配置文件。

版本由 `Directory.Build.props` 统一声明。元数据记录源码提交和工作区是否有未提交修改；本地脏工作区的包不应当作干净提交的正式发布。

## CI 和手动 Release

[普通 CI](../.github/workflows/ci.yml)通过[共用验证流程](../.github/workflows/validate-build.yml)执行 Linux/Windows 共享测试、GUI 构建，并在 Windows 发布自包含产物。注意 CI 结论对应的具体提交，不能将先前通过的运行算作后续改动的验证。

手动发布入口为 GitHub **Actions → Manual GUI release → Run workflow**。标签必须与 `Directory.Build.props` 的版本一致；默认创建草稿。流程重新验证源码、测试和包校验和，并要求包来自本次验证的干净提交；已有标签指向其他提交时会拒绝发布。

原生 Windows CI 可以验证编译、共享逻辑和系统密码保护，但真实 UI 交互、驱动捕获及校园认证仍需要目标环境验证。不要把 Linux 交叉构建、模拟服务端或一个 PID 当作这些检查的替代。
