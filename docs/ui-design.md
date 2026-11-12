# 校园连接界面设计

## 方向与保留项

这是日常使用的原生桌面客户端。保留 Avalonia Fluent 控件、校徽校名、账号表单顺序、网络模式和“更多”中的设置、诊断、更新入口。设计变化度 5、动效强度 3、信息密度 4。网页营销技能中的大幅首屏、滚动叙事、卡片网格、SEO 和 Web Vitals 不适用于这里。

原界面的浅蓝块、过大的状态标题和插头装饰共同抢占视觉注意力。新版移除插头，以一幅浅幅校园生活插画提供场景；状态文字与真正的连接状态点负责说明当前状态。表单以留白组织，功能按钮保留清楚的边界。

蓝色沿用校园品牌，表面使用略带灰绿的中性色。输入和按钮统一 6px 圆角，插画 6px、校名图底板 4px。校名原图保持原样。明暗主题跟随系统，整窗统一切换；插画和原始校名图保持自身颜色。

## 性能与可访问性

- 校园插画嵌入本地程序，1200×400 JPEG，约 186 KiB。每个窗口只解码一次，关闭时释放；无网络请求、视频、模糊或视差。
- 窗口客户区低于 620px 时收起插画，为账号表单让出空间。网页模式需要额外地址栏，低于 820px 时收起插画。440×520 的有线窗口及默认大小的网页窗口可直接看到连接按钮；额外字段保留可滚动布局。
- 不使用无限进度条、呼吸效果或定时装饰。连接状态由已有核心事件更新，保持文字反馈。
- 操作反馈仅使用 100ms 透明度变化。Windows 读取 [SPI_GETCLIENTAREAANIMATION](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow)；关闭系统动画时取消自定义过渡。其他平台没有相应适配时采用即时反馈。窗口激活时重新读取，无轮询。
- 窗口隐藏、最小化或离开诊断页时只写入有界日志缓冲，不刷新日志控件；回到诊断时一次补齐。已排队的回调也检查可见性。
- 保留键盘操作和控件可访问名称；图像替代文字说明其为校园主题插画。浅深色正文、辅助文字、错误色和按钮文字按 4.5:1 验证，输入边界按 3:1 验证。

## 插画来源

`drcom4scutGUI/Assets/campus-courtyard.jpg` 由内置 imagegen 生成，随后仅作等比例缩小和 JPEG 编码优化。它是想象中的岭南校园，不宣称是华工某处建筑的实景照片。

生成提示：

> Use case: illustration-story. Asset type: a restrained panoramic artwork inside a Chinese university campus-network desktop app, not a UI mockup. Create an elegant, observed-from-life ink and gouache editorial illustration of a quiet subtropical southern Chinese university campus: an expansive old banyan tree providing shade, a modest pale grey campus building and open corridor in the distance, two tiny casually dressed students walking, one parked bicycle, sunlit pavement with soft leaf shadows. The human figures are small and natural, no faces needed. It should feel like a page from a skilled illustrator's campus sketchbook, loose but accurate drawing, muted leafy green, desaturated blue, grey stone and a very small warm ochre note, near-white sky. Horizontal panorama, aspect ratio 3:1, with main subjects in a central horizontal strip so it can fit a shallow banner. Edge-to-edge image, no frame, no captions, no lettering, no logos, no watermark, no computer or cable imagery, no UI icons, no gradients or 3D renders, no cute mascot, no fantasy architecture. This is an imagined Lingnan campus scene, not a claimed depiction of any exact university landmark. The composition should read well at 460x120 pixels and stay calm behind no text.

## 验证边界

Headless 渲染检查浅深色、连接前、在线、重连、失败、网页认证、设置、诊断和短窗口。截图使用示例账号，不认证。自动检查控件横向溢出、主要按钮位置、语义颜色对比和后台日志暂停/恢复。原生启动和实际网络连接单独验证；Windows 结果不代表 macOS/Linux 原生结果。
