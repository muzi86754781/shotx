import Carbon.HIToolbox
import Cocoa

/// 快捷键冲突预警信息
struct ShortcutConflict: Equatable {
    let systemFeature: String
    let message: String
}

/// 系统快捷键冲突检测器：在录制与配置时识别与 macOS 系统保留热键的重叠，避免热键注册静默失效
enum ShortcutConflictChecker {
    static func check(_ shortcut: Shortcut) -> ShortcutConflict? {
        let code = Int(shortcut.keyCode)
        let mods = shortcut.modifiers

        let hasCmd = (mods & UInt32(cmdKey)) != 0
        let hasOpt = (mods & UInt32(optionKey)) != 0
        let hasCtrl = (mods & UInt32(controlKey)) != 0
        let hasShift = (mods & UInt32(shiftKey)) != 0

        // 1. Spotlight 聚焦搜索 (Cmd + Space)
        if code == kVK_Space && hasCmd && !hasOpt && !hasCtrl && !hasShift {
            return ShortcutConflict(
                systemFeature: "Spotlight 聚焦搜索",
                message: "该快捷键与 macOS 系统默认聚焦搜索 (⌘Space) 冲突"
            )
        }

        // 2. Spotlight 访达窗口搜索 (Cmd + Option + Space)
        if code == kVK_Space && hasCmd && hasOpt && !hasCtrl && !hasShift {
            return ShortcutConflict(
                systemFeature: "访达搜索窗口",
                message: "该快捷键与 macOS 访达搜索窗口 (⌥⌘Space) 冲突"
            )
        }

        // 3. 输入法切换 (Ctrl + Space / Ctrl + Option + Space)
        if code == kVK_Space && hasCtrl && !hasCmd && !hasShift {
            return ShortcutConflict(
                systemFeature: "输入法切换",
                message: "该快捷键与系统输入法切换快捷键冲突"
            )
        }

        // 4. macOS 系统原生截图 (Cmd + Shift + 3 / 4 / 5)
        if hasCmd && hasShift && !hasOpt && !hasCtrl {
            if code == kVK_ANSI_3 {
                return ShortcutConflict(
                    systemFeature: "系统全屏截图",
                    message: "该快捷键与 macOS 默认全屏截图 (⇧⌘3) 冲突"
                )
            }
            if code == kVK_ANSI_4 {
                return ShortcutConflict(
                    systemFeature: "系统区域截图",
                    message: "该快捷键与 macOS 默认区域截图 (⇧⌘4) 冲突"
                )
            }
            if code == kVK_ANSI_5 {
                return ShortcutConflict(
                    systemFeature: "系统截屏工具",
                    message: "该快捷键与 macOS 默认截图与录屏面板 (⇧⌘5) 冲突"
                )
            }
        }

        // 5. 强制退出应用 (Cmd + Option + Esc)
        if code == kVK_Escape && hasCmd && hasOpt && !hasCtrl && !hasShift {
            return ShortcutConflict(
                systemFeature: "强制退出应用",
                message: "该快捷键与系统强制退出面板 (⌥⌘Esc) 冲突"
            )
        }

        // 6. 调度中心 (Mission Control: Ctrl + 方向键)
        if hasCtrl && !hasCmd && !hasOpt && !hasShift {
            if code == kVK_UpArrow || code == kVK_DownArrow || code == kVK_LeftArrow || code == kVK_RightArrow {
                return ShortcutConflict(
                    systemFeature: "调度中心 / 桌面切换",
                    message: "该快捷键与 macOS 调度中心或空间切换手势冲突"
                )
            }
        }

        // 7. 锁屏 (Cmd + Ctrl + Q)
        if code == kVK_ANSI_Q && hasCmd && hasCtrl && !hasOpt && !hasShift {
            return ShortcutConflict(
                systemFeature: "锁定屏幕",
                message: "该快捷键与 macOS 锁定屏幕快捷键 (⌃⌘Q) 冲突"
            )
        }

        return nil
    }
}

