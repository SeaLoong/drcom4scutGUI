# 平台支持与发布体积

[返回首页](../README.md) · [开发、测试与发布](development.md)

## 当前发布平台

桌面一体包包含 GUI、Rust 认证核心和 .NET 运行组件。当前只发布以下六种组合；CI 在对应系统与处理器上执行构建、程序测试、Headless UI 测试和打包：

| 系统 | 处理器 | GUI RID |
| --- | --- | --- |
| Windows | Intel / AMD 64 位 | `win-x64` |
| Windows | ARM64 | `win-arm64` |
| Linux，glibc 与 X11 桌面 | x64 | `linux-x64` |
| Linux，glibc 与 X11 桌面 | ARM64 | `linux-arm64` |
| macOS 15 及以上 | Intel 64 位 | `osx-x64` |
| macOS 15 及以上 | Apple 芯片 | `osx-arm64` |

具体发布目标见[平台清单](https://github.com/SeaLoong/drcom4scutGUI/blob/master/.github/scripts/platforms.json)。原生运行检查不等于校园网、托盘宿主和系统授权交互均已实网验证。

## 为什么不能只看 CPU 或 libc

GUI 需要几层同时兼容：.NET 运行时负责执行 C#，Avalonia 负责窗口，Skia/HarfBuzz 等原生库负责绘图和文字，系统还需提供匹配的 libc、图形接口与密码库。Rust 核心是另一个进程，使用自己的平台目标；核心能运行，不代表同一设备也能运行 GUI。

Linux GUI 当前动态链接系统 glibc。发布包中的 .NET 运行时会增加体积，但没有把整个 glibc 复制进包；改成 musl 也不会自动去掉 .NET 或图形库。

| 其他平台 | 当前结论 |
| --- | --- |
| Linux ARM32 | .NET 10 支持，Avalonia 也列出 Raspberry Pi ARM32 嵌入式支持；本项目尚未发布或完成该目标的原生验证。 |
| Alpine / musl | .NET 10 支持部分 musl 目标，Avalonia 将此类发行版列为 Tier 3；本项目尚未发布 musl GUI。 |
| Windows x86 | .NET 10 支持，仍需本项目补齐 32 位打包、更新校验和原生验证；当前未发布。 |
| MIPS / OpenWrt | .NET 10 的官方支持架构不含 MIPS。应采用 Rust 核心与浏览器中的 LuCI 管理页，核对设备是否有匹配的核心包；不能使用桌面 GUI 包代替。 |

当前锁定的 `SkiaSharp.NativeAssets.Linux 3.119.4` 和 `HarfBuzzSharp.NativeAssets.Linux 8.3.1.3` 已包含 `linux-arm`、`linux-musl-x64`、`linux-musl-arm64`、`linux-musl-arm` 原生文件。因此不能仅以“没有原生库”否定 ARM32 或 musl，但文件存在也不等于整套程序已验证。扩展时还需要调整 RID、核心映射、32 位二进制检查、更新器和 CI。

这里的未发布状态仅指桌面 GUI。路由器构建独立提供 Rust 核心、procd 脚本和可选 LuCI 资源，不为 MIPS 或其他路由器架构引入 .NET；GUI 一体包仍为六个平台。

OpenWrt 的设备与安装要求见[核心路由器指南](https://github.com/SeaLoong/drcom4scut/blob/master/docs/openwrt.md)。

## 发布体积的来源

以下是 **2026-10-28** 的本地测量，源码为 `d5e35a0734a8a19c713ecde7dabdd5bcd65e98f4`，使用 .NET SDK 10.0.402、运行时 10.0.13 和锁定的 Avalonia 12.1.4 依赖。两种发布均为 `Release / linux-x64`，tar.gz 使用 gzip 级别 9：

| GUI 发布方式 | 文件数 | 未压缩 | tar.gz |
| --- | ---: | ---: | ---: |
| 自包含，含 .NET 运行时 | 224 | 103.08 MiB | 43.27 MiB |
| 依赖系统安装的 .NET | 37 | 24.34 MiB | 9.60 MiB |

测量仅包含 `dotnet publish` 输出，**不含 Rust 核心、文档、许可证和桌面一体包元数据**，不是完整 Release 附件大小。版本、平台和依赖变化后应重新测量。

两者相差 187 个运行时文件、78.72 MiB 未压缩数据。自包含输出中较大的文件包括 `System.Private.CoreLib.dll`（14.86 MiB）、`libSkiaSharp.so`（10.65 MiB）、`System.Private.Xml.dll`（7.54 MiB）和 `libcoreclr.so`（6.78 MiB）。当前默认自包含包让用户无需预先安装 .NET；依赖系统 .NET 的方式将运行时安装与维护交给用户，多款应用可共享它。

## 开发者手动发布较小的 GUI

以下命令已在 Linux x64 验证可生成发布输出，在仓库根目录执行。运行前需在目标机器安装 **.NET 10 运行时**，并准备图形桌面依赖；完整 SDK 也包含运行时。

```sh
dotnet restore drcom4scutGUI/drcom4scutGUI.csproj --locked-mode
dotnet publish drcom4scutGUI/drcom4scutGUI.csproj \
  --configuration Release --runtime linux-x64 --self-contained false \
  --no-restore --output artifacts/publish-framework-dependent/linux-x64 -warnaserror
```

这是开发者手动使用的输出，不是当前官方发布包格式。它不包含认证核心；需要另行配好匹配的核心。现有打包器和程序内更新验证要求自包含组件，不能将此输出改名替换官方附件，也不能把它当成程序内更新包；请手动维护此输出，当前没有依赖框架版的独立更新渠道。发布命令成功仅验证构建，不代表已完成该输出的原生窗口、密码库或校园网测试。

Native AOT 和裁剪也可降低部分发布体积，但需检查反射、JSON 序列化、系统适配与更新程序，不能直接打开开关后视为兼容。

## 官方资料

- [.NET 10 系统、架构与 libc 要求](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
- [Avalonia 平台支持与支持等级](https://docs.avaloniaui.net/docs/supported-platforms)
- [.NET 自包含与依赖框架发布](https://learn.microsoft.com/en-us/dotnet/core/deploying/deploy-with-cli)
- [Avalonia Native AOT 与限制](https://docs.avaloniaui.net/docs/deployment/native-aot)
