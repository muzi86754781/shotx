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

enum OCRPlatform: String, CaseIterable, Identifiable {
    case apple = "apple"
    case baidu = "baidu"
    case youdao = "youdao"
    case google = "google"
    case tencent = "tencent"

    var id: String { rawValue }
    var displayName: String {
        switch self {
        case .apple: return "Apple Vision (系统离线 · 默认推荐)"
        case .baidu: return "百度智能云 OCR"
        case .youdao: return "有道智云 OCR"
        case .google: return "谷歌 Cloud Vision OCR"
        case .tencent: return "腾讯云文字识别 OCR"
        }
    }
}

enum TranslationPlatform: String, CaseIterable, Identifiable {
    case google = "google"
    case baidu = "baidu"
    case youdao = "youdao"
    case tencent = "tencent"

    var id: String { rawValue }
    var displayName: String {
        switch self {
        case .google: return "谷歌翻译 (免密钥 · 默认推荐)"
        case .baidu: return "百度通用翻译 API"
        case .youdao: return "有道智云翻译 API"
        case .tencent: return "腾讯云机器翻译 TMT"
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

    // OCR Keys
    private let ocrPlatformKey = "com.shotx.ocrPlatform"
    private let baiduOCRAPIKeyKey = "com.shotx.baiduOCRAPIKey"
    private let baiduOCRSecretKeyKey = "com.shotx.baiduOCRSecretKey"
    private let youdaoOCRAppKeyKey = "com.shotx.youdaoOCRAppKey"
    private let youdaoOCRAppSecretKey = "com.shotx.youdaoOCRAppSecret"
    private let googleOCRAPIKeyKey = "com.shotx.googleOCRAPIKey"
    private let tencentOCRSecretIdKey = "com.shotx.tencentOCRSecretId"
    private let tencentOCRSecretKeyKey = "com.shotx.tencentOCRSecretKey"

    // Translation Keys
    private let translationPlatformKey = "com.shotx.translationPlatform"
    private let baiduTranslateAppIdKey = "com.shotx.baiduTranslateAppId"
    private let baiduTranslateSecretKeyKey = "com.shotx.baiduTranslateSecretKey"
    private let youdaoTranslateAppKeyKey = "com.shotx.youdaoTranslateAppKey"
    private let youdaoTranslateAppSecretKey = "com.shotx.youdaoTranslateAppSecret"
    private let tencentTranslateSecretIdKey = "com.shotx.tencentTranslateSecretId"
    private let tencentTranslateSecretKeyKey = "com.shotx.tencentTranslateSecretKey"

    @Published var saveDirectoryURL: URL {
        didSet { UserDefaults.standard.set(saveDirectoryURL.path, forKey: saveFolderKey) }
    }

    @Published var screenRecordingSaveDirectoryURL: URL {
        didSet { UserDefaults.standard.set(screenRecordingSaveDirectoryURL.path, forKey: screenRecordingSaveFolderKey) }
    }

    @Published var audioRecordingSaveDirectoryURL: URL {
        didSet { UserDefaults.standard.set(audioRecordingSaveDirectoryURL.path, forKey: audioRecordingSaveFolderKey) }
    }

    @Published var screenshotFormat: ScreenshotFormat {
        didSet { UserDefaults.standard.set(screenshotFormat.rawValue, forKey: screenshotFormatKey) }
    }

    @Published var includeShadow: Bool {
        didSet { UserDefaults.standard.set(includeShadow, forKey: includeShadowKey) }
    }

    @Published var cornerRadius: CGFloat {
        didSet { UserDefaults.standard.set(Double(cornerRadius), forKey: cornerRadiusKey) }
    }

    // OCR Published
    @Published var ocrPlatform: OCRPlatform {
        didSet { UserDefaults.standard.set(ocrPlatform.rawValue, forKey: ocrPlatformKey) }
    }
    @Published var baiduOCRAPIKey: String {
        didSet { UserDefaults.standard.set(baiduOCRAPIKey, forKey: baiduOCRAPIKeyKey) }
    }
    @Published var baiduOCRSecretKey: String {
        didSet { UserDefaults.standard.set(baiduOCRSecretKey, forKey: baiduOCRSecretKeyKey) }
    }
    @Published var youdaoOCRAppKey: String {
        didSet { UserDefaults.standard.set(youdaoOCRAppKey, forKey: youdaoOCRAppKeyKey) }
    }
    @Published var youdaoOCRAppSecret: String {
        didSet { UserDefaults.standard.set(youdaoOCRAppSecret, forKey: youdaoOCRAppSecretKey) }
    }
    @Published var googleOCRAPIKey: String {
        didSet { UserDefaults.standard.set(googleOCRAPIKey, forKey: googleOCRAPIKeyKey) }
    }
    @Published var tencentOCRSecretId: String {
        didSet { UserDefaults.standard.set(tencentOCRSecretId, forKey: tencentOCRSecretIdKey) }
    }
    @Published var tencentOCRSecretKey: String {
        didSet { UserDefaults.standard.set(tencentOCRSecretKey, forKey: tencentOCRSecretKeyKey) }
    }

    // Translation Published
    @Published var translationPlatform: TranslationPlatform {
        didSet { UserDefaults.standard.set(translationPlatform.rawValue, forKey: translationPlatformKey) }
    }
    @Published var baiduTranslateAppId: String {
        didSet { UserDefaults.standard.set(baiduTranslateAppId, forKey: baiduTranslateAppIdKey) }
    }
    @Published var baiduTranslateSecretKey: String {
        didSet { UserDefaults.standard.set(baiduTranslateSecretKey, forKey: baiduTranslateSecretKeyKey) }
    }
    @Published var youdaoTranslateAppKey: String {
        didSet { UserDefaults.standard.set(youdaoTranslateAppKey, forKey: youdaoTranslateAppKeyKey) }
    }
    @Published var youdaoTranslateAppSecret: String {
        didSet { UserDefaults.standard.set(youdaoTranslateAppSecret, forKey: youdaoTranslateAppSecretKey) }
    }
    @Published var tencentTranslateSecretId: String {
        didSet { UserDefaults.standard.set(tencentTranslateSecretId, forKey: tencentTranslateSecretIdKey) }
    }
    @Published var tencentTranslateSecretKey: String {
        didSet { UserDefaults.standard.set(tencentTranslateSecretKey, forKey: tencentTranslateSecretKeyKey) }
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

        // Load OCR
        if let ocrRaw = UserDefaults.standard.string(forKey: ocrPlatformKey),
           let p = OCRPlatform(rawValue: ocrRaw) {
            ocrPlatform = p
        } else {
            ocrPlatform = .apple
        }
        baiduOCRAPIKey = UserDefaults.standard.string(forKey: baiduOCRAPIKeyKey) ?? ""
        baiduOCRSecretKey = UserDefaults.standard.string(forKey: baiduOCRSecretKeyKey) ?? ""
        youdaoOCRAppKey = UserDefaults.standard.string(forKey: youdaoOCRAppKeyKey) ?? ""
        youdaoOCRAppSecret = UserDefaults.standard.string(forKey: youdaoOCRAppSecretKey) ?? ""
        googleOCRAPIKey = UserDefaults.standard.string(forKey: googleOCRAPIKeyKey) ?? ""
        tencentOCRSecretId = UserDefaults.standard.string(forKey: tencentOCRSecretIdKey) ?? ""
        tencentOCRSecretKey = UserDefaults.standard.string(forKey: tencentOCRSecretKeyKey) ?? ""

        // Load Translation
        if let transRaw = UserDefaults.standard.string(forKey: translationPlatformKey),
           let p = TranslationPlatform(rawValue: transRaw) {
            translationPlatform = p
        } else {
            translationPlatform = .google
        }
        baiduTranslateAppId = UserDefaults.standard.string(forKey: baiduTranslateAppIdKey) ?? ""
        baiduTranslateSecretKey = UserDefaults.standard.string(forKey: baiduTranslateSecretKeyKey) ?? ""
        youdaoTranslateAppKey = UserDefaults.standard.string(forKey: youdaoTranslateAppKeyKey) ?? ""
        youdaoTranslateAppSecret = UserDefaults.standard.string(forKey: youdaoTranslateAppSecretKey) ?? ""
        tencentTranslateSecretId = UserDefaults.standard.string(forKey: tencentTranslateSecretIdKey) ?? ""
        tencentTranslateSecretKey = UserDefaults.standard.string(forKey: tencentTranslateSecretKeyKey) ?? ""
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
