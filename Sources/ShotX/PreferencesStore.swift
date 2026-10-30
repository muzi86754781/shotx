import Cocoa
import SwiftUI

final class PreferencesStore: ObservableObject {
    static let shared = PreferencesStore()

    private let saveFolderKey = "com.shotx.saveFolder"
    private let includeShadowKey = "com.shotx.includeShadow"

    @Published var saveDirectoryURL: URL {
        didSet {
            UserDefaults.standard.set(saveDirectoryURL.path, forKey: saveFolderKey)
        }
    }

    @Published var includeShadow: Bool {
        didSet {
            UserDefaults.standard.set(includeShadow, forKey: includeShadowKey)
        }
    }

    private init() {
        if let path = UserDefaults.standard.string(forKey: saveFolderKey),
           FileManager.default.fileExists(atPath: path) {
            saveDirectoryURL = URL(fileURLWithPath: path)
        } else {
            let desktop = FileManager.default.urls(for: .desktopDirectory, in: .userDomainMask).first
                ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Desktop")
            saveDirectoryURL = desktop
        }

        includeShadow = UserDefaults.standard.bool(forKey: includeShadowKey)
    }

    func selectSaveFolder() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        panel.canCreateDirectories = true
        panel.prompt = "选择"
        panel.message = "请选择截图默认保存路径"
        NSApp.activate(ignoringOtherApps: true)
        panel.begin { [weak self] response in
            if response == .OK, let url = panel.url {
                DispatchQueue.main.async {
                    self?.saveDirectoryURL = url
                }
            }
        }
    }
}
