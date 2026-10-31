import Cocoa
import SwiftUI
import NaturalLanguage
import AVFoundation

struct TranslationRecord: Identifiable, Codable, Equatable {
    let id: UUID
    let createdAt: Date
    let sourceText: String
    let targetText: String
    let sourceLang: String
    let targetLang: String
    let engine: String

    init(
        id: UUID = UUID(),
        createdAt: Date = Date(),
        sourceText: String,
        targetText: String,
        sourceLang: String,
        targetLang: String,
        engine: String = "百度翻译"
    ) {
        self.id = id
        self.createdAt = createdAt
        self.sourceText = sourceText
        self.targetText = targetText
        self.sourceLang = sourceLang
        self.targetLang = targetLang
        self.engine = engine
    }
}

final class TranslationStore: ObservableObject {
    static let shared = TranslationStore()

    @Published private(set) var entries: [TranslationRecord] = []
    private let indexURL: URL
    private let maxEntries = 100

    private init() {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        let root = appSupport.appendingPathComponent("ShotX", isDirectory: true)
        indexURL = root.appendingPathComponent("translation_history.json")
        try? FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        load()
    }

    func add(source: String, target: String, from: String, to: String, engine: String) {
        guard !source.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        entries.removeAll { $0.sourceText == source && $0.targetText == target }
        let entry = TranslationRecord(
            sourceText: source,
            targetText: target,
            sourceLang: from,
            targetLang: to,
            engine: engine
        )
        entries.insert(entry, at: 0)
        if entries.count > maxEntries {
            entries = Array(entries.prefix(maxEntries))
        }
        save()
    }

    func remove(_ entry: TranslationRecord) {
        entries.removeAll { $0.id == entry.id }
        save()
    }

    func clear() {
        entries.removeAll()
        save()
    }

    private func load() {
        guard let data = try? Data(contentsOf: indexURL),
              let decoded = try? JSONDecoder().decode([TranslationRecord].self, from: data) else {
            return
        }
        entries = decoded
    }

    private func save() {
        guard let data = try? JSONEncoder().encode(entries) else { return }
        try? data.write(to: indexURL, options: .atomic)
    }
}

// MARK: - Translation Service (API)

enum TranslationLanguage: String, CaseIterable, Identifiable {
    case auto = "auto"
    case zh = "zh"
    case en = "en"
    case ja = "ja"
    case ko = "ko"
    case fr = "fr"
    case de = "de"
    case es = "es"
    case ru = "ru"

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .auto: return "自动检测"
        case .zh: return "中文 (简体)"
        case .en: return "英语"
        case .ja: return "日语"
        case .ko: return "韩语"
        case .fr: return "法语"
        case .de: return "德语"
        case .es: return "西班牙语"
        case .ru: return "俄语"
        }
    }
}

enum TranslationService {
    static func translate(
        text: String,
        from source: String = "auto",
        to target: String = "auto"
    ) async -> (translatedText: String, detectedSource: String) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return ("", "") }

        // Determine destination language if auto
        var destLang = target
        if destLang == "auto" {
            // If text contains Chinese characters, translate to English; otherwise translate to Chinese
            let containsChinese = trimmed.unicodeScalars.contains { $0.value >= 0x4E00 && $0.value <= 0x9FA5 }
            destLang = containsChinese ? "en" : "zh"
        }

        let sourceLang = (source == "auto") ? "auto" : source

        // 1. Google Translate API free endpoint
        if let encoded = trimmed.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) {
            let urlString = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=\(sourceLang)&tl=\(destLang)&dt=t&q=\(encoded)"
            if let url = URL(string: urlString) {
                var request = URLRequest(url: url)
                request.timeoutInterval = 6.0
                if let (data, _) = try? await URLSession.shared.data(for: request) {
                    if let json = try? JSONSerialization.jsonObject(with: data) as? [Any],
                       let sentences = json.first as? [Any] {
                        var result = ""
                        for s in sentences {
                            if let piece = s as? [Any], let textPiece = piece.first as? String {
                                result += textPiece
                            }
                        }
                        let detected = (json.count > 1 ? json[1] as? String : nil) ?? "auto"
                        if !result.isEmpty {
                            return (result, detected)
                        }
                    }
                }
            }
        }

        // 2. Fallback: MyMemory API
        if let encoded = trimmed.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) {
            let pair = (source == "auto" ? "autodetect" : source) + "|\(destLang)"
            let urlString = "https://api.mymemory.translated.net/get?q=\(encoded)&langpair=\(pair)"
            if let url = URL(string: urlString),
               let (data, _) = try? await URLSession.shared.data(from: url),
               let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
               let resData = json["responseData"] as? [String: Any],
               let translation = resData["translatedText"] as? String,
               !translation.isEmpty {
                return (translation, source)
            }
        }

        return ("翻译服务连接中，请稍候重试…", source)
    }

    private static let speechSynthesizer = AVSpeechSynthesizer()

    static func speak(text: String, language: String) {
        let utterance = AVSpeechUtterance(string: text)
        let bcp47: String
        switch language {
        case "zh": bcp47 = "zh-CN"
        case "en": bcp47 = "en-US"
        case "ja": bcp47 = "ja-JP"
        case "ko": bcp47 = "ko-KR"
        case "fr": bcp47 = "fr-FR"
        case "de": bcp47 = "de-DE"
        case "es": bcp47 = "es-ES"
        case "ru": bcp47 = "ru-RU"
        default:
            let containsChinese = text.unicodeScalars.contains { $0.value >= 0x4E00 && $0.value <= 0x9FA5 }
            bcp47 = containsChinese ? "zh-CN" : "en-US"
        }
        utterance.voice = AVSpeechSynthesisVoice(language: bcp47)
        utterance.rate = 0.5
        speechSynthesizer.stopSpeaking(at: .immediate)
        speechSynthesizer.speak(utterance)
    }
}

// MARK: - Translation Window Controller

final class TranslationController {
    static let shared = TranslationController()
    private var window: NSPanel?
    private var hosting: NSHostingController<TranslationWindowView>?

    func show(text: String = "") {
        if let w = window {
            if let h = hosting {
                h.rootView.inputText = text
                if !text.isEmpty {
                    h.rootView.performTranslation()
                }
            }
            w.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }

        var panelRef: NSPanel?

        let view = TranslationWindowView(
            initialText: text,
            onClose: { [weak self] in
                self?.dismiss()
            },
            onPinToggled: { pinned in
                panelRef?.level = pinned ? .floating : .normal
            }
        )
        let h = NSHostingController(rootView: view)
        self.hosting = h

        let size = NSSize(width: 480, height: 420)
        h.view.frame = NSRect(origin: .zero, size: size)

        let panel = NSPanel(
            contentRect: NSRect(origin: .zero, size: size),
            styleMask: [.borderless, .resizable],
            backing: .buffered,
            defer: false
        )
        panel.isMovableByWindowBackground = true
        panel.isFloatingPanel = true
        panel.level = .floating
        panel.contentViewController = h
        panel.isReleasedWhenClosed = false
        panel.hasShadow = true
        panel.backgroundColor = .clear
        panel.isOpaque = false

        panelRef = panel

        let mouse = NSEvent.mouseLocation
        if let screen = NSScreen.main {
            var origin = NSPoint(x: mouse.x - size.width / 2, y: mouse.y - size.height / 2)
            origin.x = max(screen.frame.minX + 20, min(screen.frame.maxX - size.width - 20, origin.x))
            origin.y = max(screen.frame.minY + 20, min(screen.frame.maxY - size.height - 20, origin.y))
            panel.setFrame(NSRect(origin: origin, size: size), display: true)
        }

        panel.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        self.window = panel
    }

    func dismiss() {
        window?.orderOut(nil)
        window = nil
        hosting = nil
    }
}

// MARK: - Translation View (444.jpg)

struct TranslationWindowView: View {
    @State var inputText: String
    let onClose: () -> Void
    var onPinToggled: ((Bool) -> Void)? = nil

    @State var translatedText: String = ""
    @State var sourceLanguage: TranslationLanguage = .auto
    @State var targetLanguage: TranslationLanguage = .auto
    @State var isPinned: Bool = true
    @State var isTranslating: Bool = false
    @State var engineName: String = "百度翻译"

    private let speechSynth = AVSpeechSynthesizer()

    init(
        initialText: String = "",
        onClose: @escaping () -> Void,
        onPinToggled: ((Bool) -> Void)? = nil
    ) {
        _inputText = State(initialValue: initialText)
        self.onClose = onClose
        self.onPinToggled = onPinToggled
    }

    var body: some View {
        VStack(spacing: 12) {
            // Top Bar: Pin & Action buttons (444.jpg: Pin on left, History & Settings & Close on right)
            HStack(spacing: 10) {
                Button {
                    isPinned.toggle()
                    onPinToggled?(isPinned)
                } label: {
                    Image(systemName: isPinned ? "pin.fill" : "pin")
                        .font(.system(size: 16, weight: .semibold))
                        .foregroundStyle(isPinned ? Color.primary : Color.secondary)
                        .rotationEffect(.degrees(45))
                }
                .buttonStyle(.plain)
                .help(isPinned ? "已置顶（点击取消置顶）" : "点击置顶窗口")

                Spacer()

                HStack(spacing: 8) {
                    // 历史记录按钮
                    Button {
                        if let appDelegate = NSApp.delegate as? AppDelegate {
                            appDelegate.showHistory()
                        }
                    } label: {
                        ZStack {
                            Circle()
                                .fill(Color.blue)
                                .frame(width: 26, height: 26)
                            Image(systemName: "clock.arrow.circlepath")
                                .font(.system(size: 12, weight: .bold))
                                .foregroundStyle(.white)
                        }
                    }
                    .buttonStyle(.plain)
                    .help("翻译历史记录")

                    // 设置按钮
                    Button {
                        if let appDelegate = NSApp.delegate as? AppDelegate {
                            appDelegate.showSettings()
                        }
                    } label: {
                        ZStack {
                            Circle()
                                .fill(Color.blue)
                                .frame(width: 26, height: 26)
                            Image(systemName: "gearshape.fill")
                                .font(.system(size: 12, weight: .bold))
                                .foregroundStyle(.white)
                        }
                    }
                    .buttonStyle(.plain)
                    .help("偏好设置")

                    // 关闭按钮
                    Button {
                        onClose()
                    } label: {
                        Image(systemName: "xmark")
                            .font(.system(size: 10, weight: .bold))
                            .foregroundStyle(.secondary)
                            .frame(width: 24, height: 24)
                            .background(Color.primary.opacity(0.06))
                            .clipShape(Circle())
                    }
                    .buttonStyle(.plain)
                    .help("关闭翻译窗口")
                }
            }
            .padding(.horizontal, 16)
            .padding(.top, 14)

            // Source Text Card
            VStack(alignment: .leading, spacing: 6) {
                TextEditor(text: $inputText)
                    .font(.system(size: 14))
                    .frame(minHeight: 80, maxHeight: 110)
                    .scrollContentBackground(.hidden)
                    .background(Color.clear)
                    .onChange(of: inputText) { _ in
                        debounceTranslation()
                    }

                HStack(spacing: 14) {
                    Button {
                        TranslationService.speak(text: inputText, language: sourceLanguage.rawValue)
                    } label: {
                        Image(systemName: "speaker.wave.2")
                            .font(.system(size: 14))
                            .foregroundStyle(.secondary)
                    }
                    .buttonStyle(.plain)
                    .help("朗读原文")

                    Button {
                        copyToClipboard(inputText)
                    } label: {
                        Image(systemName: "doc.on.doc")
                            .font(.system(size: 13))
                            .foregroundStyle(.secondary)
                    }
                    .buttonStyle(.plain)
                    .help("复制原文")

                    Spacer()

                    Text("\(inputText.count) 字")
                        .font(.system(size: 11))
                        .foregroundStyle(.tertiary)
                }
                .padding(.top, 2)
            }
            .padding(12)
            .background(Color(NSColor.textBackgroundColor))
            .clipShape(RoundedRectangle(cornerRadius: 12))
            .padding(.horizontal, 16)

            // Language Selector Bar (444.jpg)
            HStack(spacing: 12) {
                Spacer()

                Menu {
                    ForEach(TranslationLanguage.allCases) { lang in
                        Button(lang.displayName) {
                            sourceLanguage = lang
                            performTranslation()
                        }
                    }
                } label: {
                    HStack(spacing: 4) {
                        Text(sourceLanguage.displayName)
                            .font(.system(size: 13, weight: .medium))
                        Image(systemName: "chevron.down")
                            .font(.system(size: 9))
                    }
                    .foregroundStyle(.primary)
                }
                .menuStyle(.borderlessButton)

                Button {
                    swapLanguages()
                } label: {
                    Image(systemName: "arrow.left.arrow.right")
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(Color.accentColor)
                        .padding(.horizontal, 4)
                }
                .buttonStyle(.plain)
                .help("互换语言并重新翻译")

                Menu {
                    ForEach(TranslationLanguage.allCases.filter { $0 != .auto }) { lang in
                        Button(lang.displayName) {
                            targetLanguage = lang
                            performTranslation()
                        }
                    }
                } label: {
                    HStack(spacing: 4) {
                        Text(targetLanguage == .auto ? "自动选择" : targetLanguage.displayName)
                            .font(.system(size: 13, weight: .medium))
                        Image(systemName: "chevron.down")
                            .font(.system(size: 9))
                    }
                    .foregroundStyle(.primary)
                }
                .menuStyle(.borderlessButton)

                Spacer()
            }
            .padding(.vertical, 6)
            .background(Color(NSColor.controlBackgroundColor).opacity(0.6))
            .clipShape(RoundedRectangle(cornerRadius: 8))
            .padding(.horizontal, 16)

            // Translation Result Card (444.jpg)
            VStack(alignment: .leading, spacing: 8) {
                // Engine Header
                HStack(spacing: 6) {
                    ZStack {
                        Circle()
                            .fill(Color.blue)
                            .frame(width: 18, height: 18)
                        Text("译")
                            .font(.system(size: 10, weight: .bold))
                            .foregroundStyle(.white)
                    }

                    Text(engineName)
                        .font(.system(size: 12, weight: .medium))
                        .foregroundStyle(.secondary)

                    Spacer()

                    if isTranslating {
                        ProgressView()
                            .scaleEffect(0.6)
                            .frame(width: 16, height: 16)
                    }
                }

                // Translated Content
                ScrollView {
                    Text(translatedText.isEmpty ? (isTranslating ? "正在翻译中…" : "翻译结果将显示在此处") : translatedText)
                        .font(.system(size: 14))
                        .foregroundStyle(translatedText.isEmpty ? .secondary : .primary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .textSelection(.enabled)
                }
                .frame(minHeight: 70, maxHeight: 110)

                // Actions: Speak & Copy
                HStack(spacing: 14) {
                    Button {
                        TranslationService.speak(text: translatedText, language: targetLanguage.rawValue)
                    } label: {
                        Image(systemName: "speaker.wave.2")
                            .font(.system(size: 14))
                            .foregroundStyle(.secondary)
                    }
                    .buttonStyle(.plain)
                    .help("朗读译文")
                    .disabled(translatedText.isEmpty)

                    Button {
                        copyToClipboard(translatedText)
                    } label: {
                        Image(systemName: "doc.on.doc")
                            .font(.system(size: 13))
                            .foregroundStyle(.secondary)
                    }
                    .buttonStyle(.plain)
                    .help("复制译文")
                    .disabled(translatedText.isEmpty)

                    Spacer()
                }
            }
            .padding(12)
            .background(Color(NSColor.textBackgroundColor))
            .clipShape(RoundedRectangle(cornerRadius: 12))
            .padding(.horizontal, 16)
            .padding(.bottom, 14)
        }
        .frame(width: 480, height: 420)
        .background(
            RoundedRectangle(cornerRadius: 16)
                .fill(Color(NSColor.windowBackgroundColor))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 16)
                .strokeBorder(Color.primary.opacity(0.12), lineWidth: 1)
        )
        .clipShape(RoundedRectangle(cornerRadius: 16))
        .onAppear {
            if !inputText.isEmpty {
                performTranslation()
            }
        }
    }

    private func swapLanguages() {
        // Determine effective source and destination
        var curSrc = sourceLanguage
        var curDst = targetLanguage

        if curSrc == .auto {
            let containsChinese = inputText.unicodeScalars.contains { $0.value >= 0x4E00 && $0.value <= 0x9FA5 }
            curSrc = containsChinese ? .zh : .en
        }
        if curDst == .auto {
            curDst = (curSrc == .zh) ? .en : .zh
        }

        sourceLanguage = curDst
        targetLanguage = curSrc

        if !translatedText.isEmpty {
            let temp = inputText
            inputText = translatedText
            translatedText = temp
        }

        performTranslation()
    }

    private func debounceTranslation() {
        NSObject.cancelPreviousPerformRequests(withTarget: Debouncer.shared)
        Debouncer.shared.action = {
            performTranslation()
        }
        Debouncer.shared.perform(#selector(Debouncer.fire), with: nil, afterDelay: 0.35)
    }

    func performTranslation() {
        let text = inputText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else {
            translatedText = ""
            return
        }
        isTranslating = true
        Task {
            let res = await TranslationService.translate(
                text: text,
                from: sourceLanguage.rawValue,
                to: targetLanguage.rawValue
            )
            await MainActor.run {
                self.translatedText = res.translatedText
                self.isTranslating = false
                TranslationStore.shared.add(
                    source: text,
                    target: res.translatedText,
                    from: sourceLanguage.displayName,
                    to: targetLanguage.displayName,
                    engine: engineName
                )
            }
        }
    }

    private func copyToClipboard(_ str: String) {
        guard !str.isEmpty else { return }
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(str, forType: .string)
        ToastController.shared.show(
            message: "已复制到剪贴板",
            icon: "checkmark.circle.fill",
            tint: .systemGreen,
            duration: 1.5,
            position: .center
        )
    }
}

private final class Debouncer: NSObject {
    static let shared = Debouncer()
    var action: (() -> Void)?
    @objc func fire() {
        action?()
    }
}
