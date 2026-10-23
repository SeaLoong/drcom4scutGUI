# 第三方组件与许可声明

程序随包提供以下界面、绘图与字体组件。修改或再次分发时，请保留对应版权与许可声明。

各组件保持其原有版权和许可；本项目自身许可见 [LICENSE](LICENSE)。下表对应 GUI 3.1.1，具体依赖版本以发布提交中的 NuGet 锁文件为准。完整许可证见表中链接。

| 组件 | 版本 | 随包声明 |
| --- | --- | --- |
| Avalonia 桌面、平台后端、Fluent 主题及字体封装 | 12.1.4 | [MIT](docs/licenses/Avalonia-MIT.txt) |
| Inter 字体 | 随 Avalonia.Fonts.Inter 12.1.4；字体内版本 3.019 | [SIL Open Font License 1.1](docs/licenses/Inter-OFL-1.1.txt) |
| MicroCom.Runtime | 0.11.6 | [MIT](docs/licenses/MicroCom-MIT.txt) |
| Tmds.DBus.Protocol | 0.94.1 | [MIT](docs/licenses/Tmds.DBus-MIT.txt) |
| SkiaSharp / HarfBuzzSharp 及对应原生库 | 3.119.4 / 8.3.1.3 | [绑定许可](docs/licenses/SkiaSharp-HarfBuzzSharp-MIT.txt)、[完整原生组件声明](docs/licenses/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt) |
| ANGLE Windows 原生库 | 2.1.27548.20260419 | [许可](docs/licenses/ANGLE-LICENSE.txt) |

Inter 字体内的原始版权记录为：

> Copyright 2020 The Inter Project Authors (https://github.com/rsms/inter)

字体按 OFL 分发，名称和字体内容未在本项目中修改。字体封装的 MIT 许可不替代字体自身的 OFL。

.NET 运行时与 Microsoft 库的 `LICENSE.txt`、`ThirdPartyNotices.txt` 等声明随自包含发布目录一并保留。测试或构建专用包不属于桌面发行内容，其声明可从相应 NuGet 包和源码获取。

上述文本来自锁定 NuGet 包自带声明，以及对应的上游版本：

- [Avalonia 12.1.4 源码许可](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/licence.md)。
- [Inter 3.19 许可](https://github.com/rsms/inter/blob/v3.19/LICENSE.txt)，对应字体中的 3.019 版本记录。
- [MicroCom 源码许可](https://github.com/kekekeks/MicroCom/blob/76785efcafd91b5902fd19dd11145f6dd655b7b4/LICENSE)。
- [Tmds.DBus 源码许可](https://github.com/tmds/Tmds.DBus/blob/b4a7fed0b878f74cb54f7cca84d2889af4e596ba/COPYING)。

本地许可文本仅归一化换行和行末空白，保留版权、许可条件与免责声明。依赖更新后，应同步锁文件与这些分发声明。
