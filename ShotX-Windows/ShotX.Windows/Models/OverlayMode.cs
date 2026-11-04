namespace ShotX.Windows.Models;

public enum OverlayMode
{
    AllInOne,       // 全能截图 (智能吸附窗口 + 自由框选)
    Area,           // 区域截图
    Window,         // 窗口截图
    OCR,            // 截图 OCR (框选后直接识别复制并退出)
    Translate,      // 截图翻译 (框选后直接识别并弹出翻译窗)
    ColorPicker     // 拾色器 (全屏放大镜取色，点击或按 C 复制色值并退出)
}
