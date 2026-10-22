# drcom4scutGUI 3.0.0

华南理工大学 DrCOM 核心的 Windows 图形界面。支持有线 EAP+UDP、仅 EAP、无线 Portal 三种认证模式，以及多账号、网卡与 IPv4 选择、托盘、自动认证、按用户选择的 Windows 登录启动、自检和诊断导出。核心程序见 [SeaLoong/drcom4scut](https://github.com/SeaLoong/drcom4scut)。

## 安装与使用

1. 下载可信 Release 中的 `win-x64` 自包含 GUI 包；该包不需要另外安装 .NET Runtime。
2. 将 **drcom4scut 0.4.0 或更新版本**的 `drcom4scut.exe` 放在 GUI 同目录，有线认证还需安装 [Npcap](https://npcap.com/#download)，无线 Portal 不需要抓包驱动。GUI 包不会从未验证的网址自动下载或执行核心程序。
3. 运行 GUI，选择认证模式，填写账号、密码及网卡。本机源 IP 不指定时留空。有线默认需要 802.1X 和 UDP 心跳均成功后才显示认证成功；“有线仅 EAP”只适用于明确不需要 UDP 的网络。
4. 无线模式填写明确可信的门户地址，可选填同主机 API、AC IPv4 和提交给门户的载荷 IPv4；载荷地址与本机网络源 IP 分开。GUI 不会猜测主机或自动回退 HTTP。仅在门户确认在线后显示成功。
5. “Windows 登录时启动”只修改当前用户的启动项，无需管理员计划任务；“打开 GUI 后自动认证”单独控制是否立即认证。
6. 若认证异常，先运行“环境自检”，查看日志；“导出诊断”隐藏已知账号密码。发送日志前仍应检查网卡/IP、主机名及服务器返回的信息。

HTTP 门户会在网络中明文发送账号密码，默认禁止；只有明确接受这一风险并勾选允许 HTTP 才能使用。HTTPS 门户/API 不会自动降级为 HTTP，不允许跳到其他主机。地址不得包含账号密码、令牌、会话字段；`wlanacip`、`wlanuserip` 等发现信息可以保留。

新 GUI 不会使用命令行明文密码兼容旧核心；检测不到安全输入、私有控制管道和显式 UDP 模式控制能力会停止并提示升级。认证失败后的重连和定时恢复由核心管理，GUI 不再中断这些重试。

## 密码与配置

账号配置保存在 `%LOCALAPPDATA%\drcom4scutGUI\config.json`，密码由 Windows DPAPI `CurrentUser` 加密，只有当前 Windows 用户能够解密。JSON 中不保存明文密码。核心的普通网络配置位于同目录 `core.yml`，GUI 不会把密码写入该文件。

旧版的同目录 `drcom4scutGUI.json` 或工作目录 `gui.json` 会在读取成功后迁移；所有密码完成加密并原子写入后才删除旧文件，不生成含明文的 `.bak`。迁移失败时保留原文件并提示，不回退明文保存。DPAPI 配置不能直接复制给另一个 Windows 用户使用；需要重新输入密码。

密码通过 GUI 所创建子进程的专用标准输入管道发送，既不出现在命令行参数，也不写入环境变量。停止时通过当前用户专属随机 Named Pipe 请求核心注销，最多等待 2 秒，超时才终止所拥有的进程；GUI 崩溃时控制管道 EOF 也会通知核心退出。核心需要在自身内存中使用密码进行认证，因此此设计不保证抵抗当前用户或管理员对进程内存的读取，也不改变校园认证协议本身的加密强度。详见[安全说明](docs/security.md)。

## 开发、构建与测试

项目使用稳定版 **.NET 10 LTS / C# 14 / WPF**，已不再依赖 .NET Framework 4.7.2。安装 .NET 10 SDK 后：

```sh
dotnet restore drcom4scutGUI.sln --locked-mode
dotnet test --project drcom4scutGUI.Tests/drcom4scutGUI.Tests.csproj --no-restore --configuration Release --report-trx --results-directory artifacts/test-results
dotnet build drcom4scutGUI/drcom4scutGUI.csproj --no-restore --configuration Release
dotnet publish drcom4scutGUI/drcom4scutGUI.csproj --no-restore --configuration Release --output dist/gui
```

`drcom4scutGUI.Core` 将配置存储、凭据保护接口、进程生命周期与日志缓冲从 WPF 分离。测试使用真实辅助子进程验证 stdin、参数边界、双流读取与取消；配置测试使用注入的保护器。Windows 上另外执行真实 DPAPI 测试；Linux 跳过该项，不能据此声称 Windows UI 或驱动已经验证。Linux SDK 可以交叉构建 WPF，但不能运行 GUI。

依赖版本及 `packages.lock.json` 一同维护；更新依赖时执行 `dotnet restore --use-lock-file`，CI 使用锁定模式恢复。普通 CI 覆盖 Linux/Windows 共享逻辑测试、Linux 交叉构建、Windows 原生构建和自包含发布。手动 Release 工作流默认创建草稿，发布前核对版本、测试结果和包校验和。

## 版本变化

3.0.0 增加无线 Portal 图形配置和明确的认证模式，升级 .NET 10，移除密码命令行参数和明文账号持久化，改为 DPAPI 与 stdin；使用可取消的异步进程管理，移除 `Thread.Abort` 和全局按名字杀进程。只管理 GUI 启动的核心，不影响用户独立运行的其他核心进程。

## 许可证

沿用仓库 [LICENSE](LICENSE)。行为参考及来源说明见[变更与来源](docs/changes.md)。
