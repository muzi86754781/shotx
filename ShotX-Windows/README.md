# ShotX for Windows (Windows 原生版)

ShotX for Windows 是针对 Windows 10 / Windows 11 开发的高性能原生截图、长截图、标注与贴图工具。

## 🎯 核心特性
- **全屏与多屏幕高清截屏**：采用 GDI+ / DXGI 物理像素级抓取，Per-Monitor V2 高 DPI 感知，支持高低分辨率多显示器联合截取；
- **放大镜 (Loupe)**：9x 像素级放大镜，支持实时 RGB 与 HEX 颜色采集；
- **全套标注图元**：矩形、椭圆、直线、折线、箭头、画笔、马赛克、文字双色、步骤序号球，支持撤销 (Ctrl+Z) 与重做 (Ctrl+Y)；
- **滚动长截图**：1:1 移植 ShotX macOS 的核心绝对底部水位线（Watermark）匹配引擎，向下滑动顺畅拼接，向上回滚不重叠，支持侧边预览与高度实时计数；
- **离线 OCR 文字识别**：接入 Windows 10/11 原生 `Windows.Media.Ocr`，离线秒级提取图片文字；
- **快捷贴图 (Pin)**：双击或快捷键将截图悬浮置顶，支持滚轮平滑缩放、透明度调节；
- **全局快捷键**：
  - 区域截图：`Alt + A`
  - 滚动长截图：`Alt + S`
  - 剪贴板与翻译：`Alt + V`

## 🛠️ 技术栈
- **运行时 / SDK**：.NET 8.0 (`net8.0-windows10.0.19041.0`)
- **UI 引擎**：WPF (Windows Presentation Foundation) + XAML + Direct2D / DrawingVisual
- **系统底层**：Win32 API (User32 / GDI32 / RegisterHotKey) + WinRT OCR
- **安装包生成器**：Inno Setup 6

## 📦 本地编译与发布
```bash
# 进入工程目录
cd ShotX-Windows/ShotX.Windows

# 编译并发布独立运行版本 (无需用户安装 .NET 运行时)
dotnet publish -c Release -r win-x64 --self-contained true
```
发布产物将位于：`ShotX-Windows/ShotX.Windows/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish`。
