import Cocoa
import Carbon.HIToolbox
import Combine

struct Shortcut: Codable, Equatable {
    var keyCode: UInt32
    var modifiers: UInt32

    static let `default` = Shortcut(
        keyCode: UInt32(kVK_ANSI_X),
        modifiers: UInt32(optionKey)
    )

    static let defaultFullscreen = Shortcut(
        keyCode: UInt32(kVK_ANSI_X),
        modifiers: UInt32(optionKey | shiftKey)
    )

    static let defaultWindow = Shortcut(
        keyCode: UInt32(kVK_ANSI_W),
        modifiers: UInt32(optionKey)
    )

    static let defaultPrevious = Shortcut(
        keyCode: UInt32(kVK_ANSI_P),
        modifiers: UInt32(optionKey)
    )

    static let defaultOCR = Shortcut(
        keyCode: UInt32(kVK_ANSI_T),
        modifiers: UInt32(optionKey | shiftKey)
    )

    static let defaultRecordScreen = Shortcut(
        keyCode: UInt32(kVK_ANSI_R),
        modifiers: UInt32(optionKey)
    )

    static let defaultRecordAudio = Shortcut(
        keyCode: UInt32(kVK_ANSI_A),
        modifiers: UInt32(optionKey)
    )

    static let defaultPin = Shortcut(
        keyCode: UInt32(kVK_ANSI_P),
        modifiers: UInt32(optionKey | shiftKey)
    )

    static let defaultColorPicker = Shortcut(
        keyCode: UInt32(kVK_ANSI_C),
        modifiers: UInt32(optionKey | shiftKey)
    )

    static let defaultDesktopIcons = Shortcut(
        keyCode: UInt32(kVK_ANSI_D),
        modifiers: UInt32(optionKey)
    )

    static let defaultAllInOne = Shortcut(
        keyCode: UInt32(kVK_ANSI_S),
        modifiers: UInt32(optionKey)
    )

    static let defaultSelectionTranslation = Shortcut(
        keyCode: UInt32(kVK_ANSI_T),
        modifiers: UInt32(optionKey)
    )

    static let defaultScreenshotTranslation = Shortcut(
        keyCode: UInt32(kVK_ANSI_T),
        modifiers: UInt32(optionKey | controlKey)
    )

    static let defaultClipboardHistory = Shortcut(
        keyCode: UInt32(kVK_ANSI_V),
        modifiers: UInt32(optionKey)
    )
}

final class ShortcutStore: ObservableObject {
    static let shared = ShortcutStore()

    @Published var shortcut: Shortcut {
        didSet { Self.persist(shortcut, key: captureKey) }
    }

    @Published var fullscreenShortcut: Shortcut {
        didSet { Self.persist(fullscreenShortcut, key: fullscreenKey) }
    }

    @Published var windowCaptureShortcut: Shortcut {
        didSet { Self.persist(windowCaptureShortcut, key: windowCaptureKey) }
    }

    @Published var previousAreaShortcut: Shortcut {
        didSet { Self.persist(previousAreaShortcut, key: previousAreaKey) }
    }

    @Published var ocrShortcut: Shortcut {
        didSet { Self.persist(ocrShortcut, key: ocrKey) }
    }

    @Published var recordScreenShortcut: Shortcut {
        didSet { Self.persist(recordScreenShortcut, key: recordScreenKey) }
    }

    @Published var recordAudioShortcut: Shortcut {
        didSet { Self.persist(recordAudioShortcut, key: recordAudioKey) }
    }

    @Published var pinShortcut: Shortcut {
        didSet { Self.persist(pinShortcut, key: pinKey) }
    }

    @Published var colorPickerShortcut: Shortcut {
        didSet { Self.persist(colorPickerShortcut, key: colorPickerKey) }
    }

    @Published var desktopIconsShortcut: Shortcut {
        didSet { Self.persist(desktopIconsShortcut, key: desktopIconsKey) }
    }

    @Published var allInOneShortcut: Shortcut {
        didSet { Self.persist(allInOneShortcut, key: allInOneKey) }
    }

    @Published var selectionTranslationShortcut: Shortcut {
        didSet { Self.persist(selectionTranslationShortcut, key: selectionTranslationKey) }
    }

    @Published var screenshotTranslationShortcut: Shortcut {
        didSet { Self.persist(screenshotTranslationShortcut, key: screenshotTranslationKey) }
    }

    @Published var clipboardHistoryShortcut: Shortcut {
        didSet { Self.persist(clipboardHistoryShortcut, key: clipboardHistoryKey) }
    }

    private let captureKey = "com.shotx.shortcut"
    private let fullscreenKey = "com.shotx.fullscreenShortcut"
    private let windowCaptureKey = "com.shotx.windowCaptureShortcut"
    private let previousAreaKey = "com.shotx.previousAreaShortcut"
    private let ocrKey = "com.shotx.ocrShortcut"
    private let recordScreenKey = "com.shotx.recordScreenShortcut"
    private let recordAudioKey = "com.shotx.recordAudioShortcut"
    private let pinKey = "com.shotx.pinShortcut"
    private let colorPickerKey = "com.shotx.colorPickerShortcut"
    private let desktopIconsKey = "com.shotx.desktopIconsShortcut"
    private let allInOneKey = "com.shotx.allInOneShortcut"
    private let selectionTranslationKey = "com.shotx.selectionTranslationShortcut"
    private let screenshotTranslationKey = "com.shotx.screenshotTranslationShortcut"
    private let clipboardHistoryKey = "com.shotx.clipboardHistoryShortcut"

    init() {
        shortcut = Self.load(key: captureKey) ?? .default
        fullscreenShortcut = Self.load(key: fullscreenKey) ?? .defaultFullscreen
        windowCaptureShortcut = Self.load(key: windowCaptureKey) ?? .defaultWindow
        previousAreaShortcut = Self.load(key: previousAreaKey) ?? .defaultPrevious
        ocrShortcut = Self.load(key: ocrKey) ?? .defaultOCR
        recordScreenShortcut = Self.load(key: recordScreenKey) ?? .defaultRecordScreen
        recordAudioShortcut = Self.load(key: recordAudioKey) ?? .defaultRecordAudio
        pinShortcut = Self.load(key: pinKey) ?? .defaultPin
        colorPickerShortcut = Self.load(key: colorPickerKey) ?? .defaultColorPicker
        desktopIconsShortcut = Self.load(key: desktopIconsKey) ?? .defaultDesktopIcons
        allInOneShortcut = Self.load(key: allInOneKey) ?? .defaultAllInOne
        selectionTranslationShortcut = Self.load(key: selectionTranslationKey) ?? .defaultSelectionTranslation
        screenshotTranslationShortcut = Self.load(key: screenshotTranslationKey) ?? .defaultScreenshotTranslation
        clipboardHistoryShortcut = Self.load(key: clipboardHistoryKey) ?? .defaultClipboardHistory
    }

    private static func load(key: String) -> Shortcut? {
        guard let data = UserDefaults.standard.data(forKey: key),
              let decoded = try? JSONDecoder().decode(Shortcut.self, from: data) else {
            return nil
        }
        return decoded
    }

    private static func persist(_ shortcut: Shortcut, key: String) {
        guard let data = try? JSONEncoder().encode(shortcut) else { return }
        UserDefaults.standard.set(data, forKey: key)
    }
}

enum ShortcutFormatter {
    static func describe(_ s: Shortcut) -> String {
        var parts: [String] = []
        if s.modifiers & UInt32(controlKey) != 0 { parts.append("⌃") }
        if s.modifiers & UInt32(optionKey) != 0 { parts.append("⌥") }
        if s.modifiers & UInt32(shiftKey) != 0 { parts.append("⇧") }
        if s.modifiers & UInt32(cmdKey) != 0 { parts.append("⌘") }
        parts.append(keyName(for: s.keyCode))
        return parts.joined()
    }

    static func keyName(for keyCode: UInt32) -> String {
        if let named = specialKeyNames[keyCode] { return named }
        return letterOrDigit(for: keyCode) ?? "Key \(keyCode)"
    }

    static func menuKeyEquivalent(for keyCode: UInt32) -> String? {
        letterOrDigit(for: keyCode)?.lowercased()
    }

    static func nsModifierFlags(from carbon: UInt32) -> NSEvent.ModifierFlags {
        var flags: NSEvent.ModifierFlags = []
        if carbon & UInt32(cmdKey) != 0 { flags.insert(.command) }
        if carbon & UInt32(optionKey) != 0 { flags.insert(.option) }
        if carbon & UInt32(controlKey) != 0 { flags.insert(.control) }
        if carbon & UInt32(shiftKey) != 0 { flags.insert(.shift) }
        return flags
    }

    private static func letterOrDigit(for keyCode: UInt32) -> String? {
        // Simple ANSI layout ASCII mapping for common keys
        switch Int(keyCode) {
        case kVK_ANSI_A: return "A"
        case kVK_ANSI_B: return "B"
        case kVK_ANSI_C: return "C"
        case kVK_ANSI_D: return "D"
        case kVK_ANSI_E: return "E"
        case kVK_ANSI_F: return "F"
        case kVK_ANSI_G: return "G"
        case kVK_ANSI_H: return "H"
        case kVK_ANSI_I: return "I"
        case kVK_ANSI_J: return "J"
        case kVK_ANSI_K: return "K"
        case kVK_ANSI_L: return "L"
        case kVK_ANSI_M: return "M"
        case kVK_ANSI_N: return "N"
        case kVK_ANSI_O: return "O"
        case kVK_ANSI_P: return "P"
        case kVK_ANSI_Q: return "Q"
        case kVK_ANSI_R: return "R"
        case kVK_ANSI_S: return "S"
        case kVK_ANSI_T: return "T"
        case kVK_ANSI_U: return "U"
        case kVK_ANSI_V: return "V"
        case kVK_ANSI_W: return "W"
        case kVK_ANSI_X: return "X"
        case kVK_ANSI_Y: return "Y"
        case kVK_ANSI_Z: return "Z"
        case kVK_ANSI_0: return "0"
        case kVK_ANSI_1: return "1"
        case kVK_ANSI_2: return "2"
        case kVK_ANSI_3: return "3"
        case kVK_ANSI_4: return "4"
        case kVK_ANSI_5: return "5"
        case kVK_ANSI_6: return "6"
        case kVK_ANSI_7: return "7"
        case kVK_ANSI_8: return "8"
        case kVK_ANSI_9: return "9"
        default: return nil
        }
    }

    private static let specialKeyNames: [UInt32: String] = [
        UInt32(kVK_Space): "Space",
        UInt32(kVK_Return): "↩",
        UInt32(kVK_Tab): "⇥",
        UInt32(kVK_Delete): "⌫",
        UInt32(kVK_ForwardDelete): "⌦",
        UInt32(kVK_Escape): "Esc",
        UInt32(kVK_UpArrow): "↑",
        UInt32(kVK_DownArrow): "↓",
        UInt32(kVK_LeftArrow): "←",
        UInt32(kVK_RightArrow): "→",
        UInt32(kVK_F1): "F1", UInt32(kVK_F2): "F2", UInt32(kVK_F3): "F3",
        UInt32(kVK_F4): "F4", UInt32(kVK_F5): "F5", UInt32(kVK_F6): "F6",
        UInt32(kVK_F7): "F7", UInt32(kVK_F8): "F8", UInt32(kVK_F9): "F9",
        UInt32(kVK_F10): "F10", UInt32(kVK_F11): "F11", UInt32(kVK_F12): "F12"
    ]
}
