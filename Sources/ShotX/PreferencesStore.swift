import Cocoa
import SwiftUI

enum ScreenshotFormat: String, CaseIterable, Identifiable {
    case png = "PNG"
    case jpg = "JPG"
    case heic = "HEIC"

    var id: String { rawValue }
    var fileExtension: String {
        switch self {
        case .png: return "png"
        case .jpg: return "jpg"
        case .heic: return "heic"
        }
    }
    var displayName: String {
        switch self {
        case .png: return "PNG (无损高质量)"
        case .jpg: return "JPG (文件小巧)"
        case .heic: return "HEIC (高效压缩)"
        }
    }
}

final class PreferencesStore: ObservableObject {
    static let shared = PreferencesStore()

    private let saveFolderKey = "com.shotx.saveFolder"
    private let screenRecordingSaveFolderKey = "com.shotx.screenRecordingSaveFolder"
    private let audioRecordingSaveFolderKey = "com.shotx.audioRecordingSaveFolder"
    private let screenshotFormatKey = "com.shotx.screenshotFormat"
    private let includeShadowKey = "com.shotx.includeShadow"
    private let cornerRadiusKey = "com.shotx.cornerRadius"

    @Published var saveDirectoryURL: URL {
        didSet {
            UserDefaults.standard.set(saveDirectoryURL.path, forKey: saveFolderKey)
        }
    }

    @Published var screenRecordingSaveDirectoryURL: URL {
        didSet {
            UserDefaults.standard.set(screenRecordingSaveDirectoryURL.path, forKey: screenRecordingSaveFolderKey)
        }
    }

    @Published var audioRecordingSaveDirectoryURL: URL {
        didSet {
            UserDefaults.standard.set(audioRecordingSaveDirectoryURL.path, forKey: audioRecordingSaveFolderKey)
        }
    }

    @Published var screenshotFormat: ScreenshotFormat {
        didSet {
            UserDefaults.standard.set(screenshotFormat.rawValue, forKey: screenshotFormatKey)
        }
    }

    @Published var includeShadow: Bool {
        didSet {
            UserDefaults.standard.set(includeShadow, forKey: includeShadowKey)
        }
    }

    @Published var cornerRadius: CGFloat {
        didSet {
            UserDefaults.standard.set(Double(cornerRadius), forKey: cornerRadiusKey)
        }
    }

    private init() {
        let desktop = FileManager.default.urls(for: .desktopDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Desktop")
        let movies = FileManager.default.urls(for: .moviesDirectory, in: .userDomainMask).first ?? desktop
        let music = FileManager.default.urls(for: .musicDirectory, in: .userDomainMask).first ?? desktop

        if let path = UserDefaults.standard.string(forKey: saveFolderKey),
           FileManager.default.fileExists(atPath: path) {
            saveDirectoryURL = URL(fileURLWithPath: path)
        } else {
            saveDirectoryURL = desktop
        }

        if let path = UserDefaults.standard.string(forKey: screenRecordingSaveFolderKey),
           FileManager.default.fileExists(atPath: path) {
            screenRecordingSaveDirectoryURL = URL(fileURLWithPath: path)
        } else {
            screenRecordingSaveDirectoryURL = movies
        }

        if let path = UserDefaults.standard.string(forKey: audioRecordingSaveFolderKey),
           FileManager.default.fileExists(atPath: path) {
            audioRecordingSaveDirectoryURL = URL(fileURLWithPath: path)
        } else {
            audioRecordingSaveDirectoryURL = music
        }

        if let fmtRaw = UserDefaults.standard.string(forKey: screenshotFormatKey),
           let fmt = ScreenshotFormat(rawValue: fmtRaw) {
            screenshotFormat = fmt
        } else {
            screenshotFormat = .png
        }

        includeShadow = UserDefaults.standard.bool(forKey: includeShadowKey)
        let savedRadius = UserDefaults.standard.double(forKey: cornerRadiusKey)
        cornerRadius = (savedRadius > 0) ? CGFloat(savedRadius) : 0
    }

    func selectSaveFolder() {
        chooseFolder(prompt: "请选择截图默认保存路径") { [weak self] url in
            self?.saveDirectoryURL = url
        }
    }

    func selectScreenRecordingSaveFolder() {
        chooseFolder(prompt: "请选择录屏默认保存路径") { [weak self] url in
            self?.screenRecordingSaveDirectoryURL = url
        }
    }

    func selectAudioRecordingSaveFolder() {
        chooseFolder(prompt: "请选择录音默认保存路径") { [weak self] url in
            self?.audioRecordingSaveDirectoryURL = url
        }
    }

    private func chooseFolder(prompt: String, onChosen: @escaping (URL) -> Void) {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        panel.canCreateDirectories = true
        panel.prompt = "选择"
        panel.message = prompt
        NSApp.activate(ignoringOtherApps: true)
        panel.begin { response in
            if response == .OK, let url = panel.url {
                DispatchQueue.main.async {
                    onChosen(url)
                }
            }
        }
    }
}
