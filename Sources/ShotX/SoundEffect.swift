import Cocoa
import AudioToolbox

/// 负责播放系统级截图快门音效。
/// 采用 AudioServicesPlaySystemSound(1108) 系统声音服务播放原生相机快门音，
/// 严格避免直接加载 CoreAudio.component 文件或初始化全双工音频输入设备，
/// 从根本上杜绝 macOS TCC 弹出“ShotX 想要访问你的麦克风”误报与权限锁死问题。
final class SoundEffect {
    static let shared = SoundEffect()

    private init() {}

    func playShutter() {
        // 1108 为 macOS / iOS 原生相机快门音 SystemSoundID，完全通过系统守护进程播放，纯输出无需麦克风权限
        AudioServicesPlaySystemSound(1108)
    }
}
