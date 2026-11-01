import Cocoa
import SwiftUI

final class MainWindowState: ObservableObject {
    @Published var tab: MainView.Tab = .history
}

final class MainWindowController {
    private var window: NSWindow?
    let state = MainWindowState()

    func show(tab: MainView.Tab? = nil) {
        if let tab = tab { state.tab = tab }

        if let window = window {
            window.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }

        let root = MainView(state: state)
            .environmentObject(HistoryStore.shared)
            .environmentObject(ShortcutStore.shared)
            .environmentObject(PreferencesStore.shared)
            .environmentObject(TranslationStore.shared)
            .environmentObject(ClipboardStore.shared)
        let hosting = NSHostingController(rootView: root)

        let window = NSWindow(contentViewController: hosting)
        window.title = "ShotX"
        window.styleMask = [.titled, .closable, .miniaturizable, .resizable]
        window.setContentSize(NSSize(width: 720, height: 520))
        window.isReleasedWhenClosed = false
        window.center()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        self.window = window
    }
}

struct MainView: View {
    enum Tab: String, Hashable, CaseIterable {
        case history, settings
        var title: String { self == .history ? "历史记录" : "偏好设置" }
    }

    @ObservedObject var state: MainWindowState

    var body: some View {
        VStack(spacing: 0) {
            Picker("", selection: $state.tab) {
                ForEach(Tab.allCases, id: \.self) { t in
                    Text(t.title).tag(t)
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .frame(maxWidth: 300)
            .padding(.vertical, 10)

            Divider()

            Group {
                switch state.tab {
                case .history: HistoryView()
                case .settings: SettingsView()
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }
}

enum HistoryCategory: String, CaseIterable, Identifiable {
    case all = "全部"
    case screenshot = "截图"
    case recording = "录屏"
    case audio = "录音"
    case ocr = "提取文字"
    case translation = "翻译"
    case clipboard = "复制记录"

    var id: String { rawValue }

    var icon: String {
        switch self {
        case .all: return "square.grid.2x2"
        case .screenshot: return "photo"
        case .recording: return "video"
        case .audio: return "mic"
        case .ocr: return "text.viewfinder"
        case .translation: return "globe"
        case .clipboard: return "doc.on.clipboard"
        }
    }
}

struct HistoryView: View {
    @EnvironmentObject var history: HistoryStore
    @EnvironmentObject var translationStore: TranslationStore
    @EnvironmentObject var clipboardStore: ClipboardStore
    @EnvironmentObject var shortcuts: ShortcutStore

    @State private var selectedCategory: HistoryCategory = .all
    @State private var searchText: String = ""

    private let cols = [GridItem(.adaptive(minimum: 210, maximum: 260), spacing: 16)]

    private var screenshotEntries: [CaptureEntry] {
        history.entries.filter { $0.kind == .image }
    }

    private var recordingEntries: [CaptureEntry] {
        history.entries.filter { $0.kind == .video || $0.kind == .gif }
    }

    var body: some View {
        VStack(spacing: 0) {
            // Category Segmented Control & Search
            HStack(spacing: 10) {
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 6) {
                        ForEach(HistoryCategory.allCases) { cat in
                            Button {
                                selectedCategory = cat
                            } label: {
                                HStack(spacing: 4) {
                                    Image(systemName: cat.icon)
                                        .font(.system(size: 11))
                                    Text(cat.rawValue)
                                        .font(.system(size: 12, weight: selectedCategory == cat ? .semibold : .regular))
                                }
                                .padding(.horizontal, 10)
                                .padding(.vertical, 5)
                                .background(
                                    Capsule()
                                        .fill(selectedCategory == cat ? Color.accentColor : Color.primary.opacity(0.06))
                                )
                                .foregroundStyle(selectedCategory == cat ? .white : .primary)
                            }
                            .buttonStyle(.plain)
                        }
                    }
                    .padding(.vertical, 4)
                }

                Spacer()

                // Search field
                HStack(spacing: 6) {
                    Image(systemName: "magnifyingglass")
                        .foregroundStyle(.secondary)
                        .font(.system(size: 11))
                    TextField("搜索…", text: $searchText)
                        .textFieldStyle(.plain)
                        .font(.system(size: 11.5))
                        .frame(width: 110)
                    if !searchText.isEmpty {
                        Button {
                            searchText = ""
                        } label: {
                            Image(systemName: "xmark.circle.fill")
                                .foregroundStyle(.secondary)
                                .font(.system(size: 11))
                        }
                        .buttonStyle(.plain)
                    }
                }
                .padding(.horizontal, 8)
                .padding(.vertical, 4)
                .background(Color(NSColor.textBackgroundColor))
                .clipShape(Capsule())
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 8)

            Divider()

            header
            Divider()

            content
        }
        .background(Color(NSColor.windowBackgroundColor))
    }

    private var currentCount: Int {
        switch selectedCategory {
        case .all:
            return history.entries.count + history.audioEntries.count + history.ocrEntries.count + translationStore.entries.count + clipboardStore.entries.count
        case .screenshot:
            return screenshotEntries.count
        case .recording:
            return recordingEntries.count
        case .audio:
            return history.audioEntries.count
        case .ocr:
            return history.ocrEntries.count
        case .translation:
            return translationStore.entries.count
        case .clipboard:
            return clipboardStore.entries.count
        }
    }

    private var header: some View {
        HStack(spacing: 8) {
            Text("\(selectedCategory.rawValue) · \(currentCount) 条记录")
                .font(.system(size: 11.5, weight: .medium))
                .foregroundStyle(.secondary)
            Spacer()
            if currentCount > 0 {
                Button(role: .destructive) {
                    clearCurrentCategory()
                } label: {
                    Label("清空此类", systemImage: "trash")
                        .font(.system(size: 11.5, weight: .medium))
                }
                .buttonStyle(.borderless)
                .foregroundStyle(.red)
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 7)
    }

    private func clearCurrentCategory() {
        switch selectedCategory {
        case .all:
            history.clear()
            history.clearAudio()
            history.clearOCR()
            translationStore.clear()
            clipboardStore.clear()
        case .screenshot:
            for entry in screenshotEntries { history.remove(entry) }
        case .recording:
            for entry in recordingEntries { history.remove(entry) }
        case .audio:
            history.clearAudio()
        case .ocr:
            history.clearOCR()
        case .translation:
            translationStore.clear()
        case .clipboard:
            clipboardStore.clear()
        }
    }

    private var content: some View {
        Group {
            if currentCount == 0 {
                emptyState
            } else {
                ScrollView {
                    VStack(spacing: 14) {
                        switch selectedCategory {
                        case .all:
                            allContent
                        case .screenshot:
                            screenshotsGrid(screenshotEntries)
                        case .recording:
                            screenshotsGrid(recordingEntries)
                        case .audio:
                            audioList
                        case .ocr:
                            ocrList
                        case .translation:
                            translationList
                        case .clipboard:
                            clipboardList
                        }
                    }
                    .padding(16)
                }
            }
        }
    }

    @ViewBuilder
    private var allContent: some View {
        if !screenshotEntries.isEmpty {
            sectionHeader("截图", count: screenshotEntries.count, icon: "photo")
            screenshotsGrid(Array(screenshotEntries.prefix(6)))
        }
        if !recordingEntries.isEmpty {
            sectionHeader("录屏", count: recordingEntries.count, icon: "video")
            screenshotsGrid(Array(recordingEntries.prefix(4)))
        }
        if !history.audioEntries.isEmpty {
            sectionHeader("录音", count: history.audioEntries.count, icon: "mic")
            audioList
        }
        if !history.ocrEntries.isEmpty {
            sectionHeader("提取文字", count: history.ocrEntries.count, icon: "text.viewfinder")
            ocrList
        }
        if !translationStore.entries.isEmpty {
            sectionHeader("翻译", count: translationStore.entries.count, icon: "globe")
            translationList
        }
        if !clipboardStore.entries.isEmpty {
            sectionHeader("复制记录", count: clipboardStore.entries.count, icon: "doc.on.clipboard")
            clipboardList
        }
    }

    private func sectionHeader(_ title: String, count: Int, icon: String) -> some View {
        HStack(spacing: 6) {
            Image(systemName: icon)
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
            Text(title)
                .font(.system(size: 13, weight: .semibold))
            Text("(\(count))")
                .font(.system(size: 11))
                .foregroundStyle(.tertiary)
            Spacer()
        }
        .padding(.top, 4)
    }

    private func screenshotsGrid(_ entries: [CaptureEntry]) -> some View {
        LazyVGrid(columns: cols, spacing: 16) {
            ForEach(entries) { entry in
                HistoryCard(entry: entry)
            }
        }
    }

    private var audioList: some View {
        LazyVStack(spacing: 8) {
            ForEach(history.audioEntries) { entry in
                AudioHistoryRow(entry: entry) {
                    history.removeAudio(entry)
                }
            }
        }
    }

    private var ocrList: some View {
        LazyVStack(spacing: 8) {
            ForEach(history.ocrEntries) { entry in
                OCRHistoryRow(entry: entry) {
                    history.removeOCR(entry)
                }
            }
        }
    }

    private var translationList: some View {
        LazyVStack(spacing: 8) {
            ForEach(translationStore.entries) { entry in
                TranslationHistoryRow(entry: entry) {
                    translationStore.remove(entry)
                }
            }
        }
    }

    private var clipboardList: some View {
        LazyVStack(spacing: 8) {
            ForEach(clipboardStore.entries) { entry in
                ClipboardHistoryListRow(entry: entry) {
                    clipboardStore.remove(entry)
                }
            }
        }
    }

    private var emptyState: some View {
        VStack(spacing: 12) {
            ZStack {
                RoundedRectangle(cornerRadius: 20)
                    .fill(Color.accentColor.opacity(0.1))
                    .frame(width: 80, height: 80)
                Image(systemName: selectedCategory.icon)
                    .font(.system(size: 34, weight: .light))
                    .foregroundStyle(.tint)
            }
            Text("暂无\(selectedCategory.rawValue)记录")
                .font(.system(size: 15, weight: .semibold))
            Text("使用 ShotX 的各项功能后，历史记录将自动汇总于此")
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

// MARK: - Row Views for New History Types

struct AudioHistoryRow: View {
    let entry: AudioEntry
    let onDelete: () -> Void

    @State private var isHovering = false

    var body: some View {
        HStack(spacing: 12) {
            ZStack {
                Circle()
                    .fill(Color.green.opacity(0.15))
                    .frame(width: 36, height: 36)
                Image(systemName: "waveform")
                    .font(.system(size: 16))
                    .foregroundStyle(.green)
            }

            VStack(alignment: .leading, spacing: 3) {
                Text(entry.filename)
                    .font(.system(size: 13, weight: .medium))
                    .lineLimit(1)
                HStack(spacing: 8) {
                    Text(formatDuration(entry.duration))
                        .font(.system(size: 11, weight: .medium))
                        .foregroundStyle(.secondary)
                    Text("·")
                        .foregroundStyle(.tertiary)
                    Text(entry.createdAt, style: .time)
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                }
            }

            Spacer()

            HStack(spacing: 8) {
                Button {
                    let url = URL(fileURLWithPath: entry.filePath)
                    NSWorkspace.shared.open(url)
                } label: {
                    Label("播放", systemImage: "play.fill")
                        .font(.system(size: 11.5))
                }
                .buttonStyle(.bordered)
                .controlSize(.small)

                Button {
                    let url = URL(fileURLWithPath: entry.filePath)
                    NSWorkspace.shared.activateFileViewerSelecting([url])
                } label: {
                    Image(systemName: "folder")
                        .font(.system(size: 12))
                }
                .buttonStyle(.borderless)
                .help("在访达中显示")

                Button {
                    onDelete()
                } label: {
                    Image(systemName: "trash")
                        .font(.system(size: 11))
                        .foregroundStyle(.red)
                }
                .buttonStyle(.borderless)
                .help("删除")
            }
        }
        .padding(10)
        .background(
            RoundedRectangle(cornerRadius: 10)
                .fill(Color(NSColor.controlBackgroundColor))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 10)
                .strokeBorder(Color.primary.opacity(0.06), lineWidth: 1)
        )
    }

    private func formatDuration(_ t: TimeInterval) -> String {
        let total = Int(t)
        let m = total / 60
        let s = total % 60
        return String(format: "%d:%02d", m, s)
    }
}

struct OCRHistoryRow: View {
    let entry: OCREntry
    let onDelete: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            ZStack {
                Circle()
                    .fill(Color.blue.opacity(0.12))
                    .frame(width: 34, height: 34)
                Image(systemName: "text.viewfinder")
                    .font(.system(size: 15))
                    .foregroundStyle(.blue)
            }

            VStack(alignment: .leading, spacing: 5) {
                Text(entry.text)
                    .font(.system(size: 13))
                    .lineLimit(3)
                    .textSelection(.enabled)

                HStack(spacing: 8) {
                    Text("\(entry.text.count) 字")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                    Text("·")
                        .foregroundStyle(.tertiary)
                    Text(entry.createdAt, style: .time)
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                }
            }

            Spacer()

            HStack(spacing: 8) {
                Button {
                    let pb = NSPasteboard.general
                    pb.clearContents()
                    pb.setString(entry.text, forType: .string)
                    ToastController.shared.show(
                        message: "已复制文字到剪贴板",
                        icon: "checkmark.circle.fill",
                        tint: .systemGreen,
                        duration: 1.5,
                        position: .center
                    )
                } label: {
                    Label("复制", systemImage: "doc.on.doc")
                        .font(.system(size: 11.5))
                }
                .buttonStyle(.bordered)
                .controlSize(.small)

                Button {
                    onDelete()
                } label: {
                    Image(systemName: "trash")
                        .font(.system(size: 11))
                        .foregroundStyle(.red)
                }
                .buttonStyle(.borderless)
            }
        }
        .padding(10)
        .background(
            RoundedRectangle(cornerRadius: 10)
                .fill(Color(NSColor.controlBackgroundColor))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 10)
                .strokeBorder(Color.primary.opacity(0.06), lineWidth: 1)
        )
    }
}

struct TranslationHistoryRow: View {
    let entry: TranslationRecord
    let onDelete: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            ZStack {
                Circle()
                    .fill(Color.purple.opacity(0.12))
                    .frame(width: 34, height: 34)
                Image(systemName: "globe")
                    .font(.system(size: 15))
                    .foregroundStyle(.purple)
            }

            VStack(alignment: .leading, spacing: 5) {
                HStack(spacing: 6) {
                    Text(entry.sourceLang)
                        .font(.system(size: 10.5, weight: .semibold))
                        .padding(.horizontal, 6)
                        .padding(.vertical, 2)
                        .background(Capsule().fill(Color.primary.opacity(0.08)))
                    Image(systemName: "arrow.right")
                        .font(.system(size: 9))
                        .foregroundStyle(.secondary)
                    Text(entry.targetLang)
                        .font(.system(size: 10.5, weight: .semibold))
                        .padding(.horizontal, 6)
                        .padding(.vertical, 2)
                        .background(Capsule().fill(Color.accentColor.opacity(0.15)))
                        .foregroundStyle(Color.accentColor)

                    Spacer()
                    Text(entry.createdAt, style: .time)
                        .font(.system(size: 11))
                        .foregroundStyle(.tertiary)
                }

                Text("原文: \(entry.sourceText)")
                    .font(.system(size: 12.5))
                    .lineLimit(2)
                    .foregroundStyle(.secondary)

                Text("译文: \(entry.targetText)")
                    .font(.system(size: 13, weight: .medium))
                    .lineLimit(2)
                    .foregroundStyle(.primary)
            }

            Spacer()

            HStack(spacing: 8) {
                Button {
                    let pb = NSPasteboard.general
                    pb.clearContents()
                    pb.setString(entry.targetText, forType: .string)
                    ToastController.shared.show(
                        message: "已复制译文到剪贴板",
                        icon: "checkmark.circle.fill",
                        tint: .systemGreen,
                        duration: 1.5,
                        position: .center
                    )
                } label: {
                    Label("复制译文", systemImage: "doc.on.doc")
                        .font(.system(size: 11.5))
                }
                .buttonStyle(.bordered)
                .controlSize(.small)

                Button {
                    onDelete()
                } label: {
                    Image(systemName: "trash")
                        .font(.system(size: 11))
                        .foregroundStyle(.red)
                }
                .buttonStyle(.borderless)
            }
        }
        .padding(10)
        .background(
            RoundedRectangle(cornerRadius: 10)
                .fill(Color(NSColor.controlBackgroundColor))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 10)
                .strokeBorder(Color.primary.opacity(0.06), lineWidth: 1)
        )
    }
}

struct ClipboardHistoryListRow: View {
    let entry: ClipboardEntry
    let onDelete: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            ZStack {
                Circle()
                    .fill(Color.orange.opacity(0.12))
                    .frame(width: 34, height: 34)
                Image(systemName: entry.kind == .file ? "doc.fill" : "doc.on.clipboard")
                    .font(.system(size: 15))
                    .foregroundStyle(.orange)
            }

            VStack(alignment: .leading, spacing: 4) {
                Text(entry.displayTitle)
                    .font(.system(size: 13, weight: .medium))
                    .lineLimit(1)
                Text(entry.displaySubtitle)
                    .font(.system(size: 11.5))
                    .lineLimit(2)
                    .foregroundStyle(.secondary)
                Text(entry.createdAt, style: .time)
                    .font(.system(size: 10.5))
                    .foregroundStyle(.tertiary)
            }

            Spacer()

            HStack(spacing: 8) {
                Button {
                    ClipboardStore.shared.copyToClipboard(entry)
                } label: {
                    Label("复制", systemImage: "doc.on.doc")
                        .font(.system(size: 11.5))
                }
                .buttonStyle(.bordered)
                .controlSize(.small)

                Button {
                    onDelete()
                } label: {
                    Image(systemName: "trash")
                        .font(.system(size: 11))
                        .foregroundStyle(.red)
                }
                .buttonStyle(.borderless)
            }
        }
        .padding(10)
        .background(
            RoundedRectangle(cornerRadius: 10)
                .fill(Color(NSColor.controlBackgroundColor))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 10)
                .strokeBorder(Color.primary.opacity(0.06), lineWidth: 1)
        )
    }
}

struct HistoryCard: View {
    let entry: CaptureEntry
    @EnvironmentObject var history: HistoryStore
    @State private var image: NSImage?
    @State private var hovering = false

    private var isMedia: Bool { entry.kind != .image }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            thumbnailArea
            footer
        }
        .task(id: entry.id) {
            image = await history.thumbnail(for: entry)
        }
        .onHover { hovering = $0 }
        .animation(.easeOut(duration: 0.15), value: hovering)
        .contextMenu { contextMenuContent }
    }

    @ViewBuilder
    private var contextMenuContent: some View {
        if isMedia {
            Button("打开") { play() }
            Button("复制文件") { copyFile() }
            Button("保存到桌面") { saveFile() }
        } else {
            Button("编辑…") { edit() }
            Button("复制") { copy() }
            Button("保存到桌面") { saveToDesktop() }
        }
        Button("在访达中显示") { reveal() }
        Divider()
        Button("删除", role: .destructive) { history.remove(entry) }
    }

    private var thumbnailArea: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 10)
                .fill(
                    LinearGradient(
                        colors: [
                            Color.primary.opacity(0.04),
                            Color.primary.opacity(0.09)
                        ],
                        startPoint: .topLeading,
                        endPoint: .bottomTrailing
                    )
                )

            if let image = image {
                Image(nsImage: image)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .padding(8)
            }

            // Centered play overlay for video / GIF
            if isMedia {
                Image(systemName: "play.circle.fill")
                    .font(.system(size: 32, weight: .semibold))
                    .foregroundStyle(.white)
                    .shadow(color: .black.opacity(0.55), radius: 4, y: 1)
            }

            // Hover scrim + action row, anchored to bottom
            if hovering {
                VStack(spacing: 0) {
                    Spacer()
                    ZStack {
                        LinearGradient(
                            colors: [.clear, .black.opacity(0.6)],
                            startPoint: .top,
                            endPoint: .bottom
                        )
                        HStack(spacing: 8) {
                            if isMedia {
                                HistoryActionButton(icon: "play.fill", tooltip: "播放", action: play)
                                HistoryActionButton(icon: "doc.on.doc", tooltip: "复制文件", action: copyFile)
                                HistoryActionButton(icon: "square.and.arrow.down", tooltip: "保存到桌面", action: saveFile)
                            } else {
                                HistoryActionButton(icon: "pencil.tip", tooltip: "编辑", action: edit)
                                HistoryActionButton(icon: "doc.on.doc", tooltip: "复制", action: copy)
                                HistoryActionButton(icon: "square.and.arrow.down", tooltip: "保存到桌面", action: saveToDesktop)
                            }
                        }
                        .padding(.bottom, 8)
                    }
                    .frame(height: 60)
                }
                .clipShape(RoundedRectangle(cornerRadius: 10))
            }

            // Type badge in top-left corner
            VStack {
                HStack {
                    kindBadge
                    Spacer()
                }
                Spacer()
            }
            .padding(7)
            .allowsHitTesting(false)
        }
        .frame(height: 140)
        .overlay(
            RoundedRectangle(cornerRadius: 10)
                .strokeBorder(Color.primary.opacity(0.1), lineWidth: 1)
        )
        .shadow(color: .black.opacity(hovering ? 0.18 : 0.0), radius: 10, y: 4)
        .onTapGesture(count: 2) {
            if isMedia { play() } else { copy() }
        }
    }

    @ViewBuilder
    private var kindBadge: some View {
        if entry.kind == .gif {
            badge(text: "GIF", color: .systemPurple)
        } else if entry.kind == .video, let duration = entry.duration {
            badge(text: formatDuration(duration), color: .systemRed)
        }
    }

    private func badge(text: String, color: NSColor) -> some View {
        Text(text)
            .font(.system(size: 9.5, weight: .heavy))
            .foregroundStyle(.white)
            .padding(.horizontal, 6)
            .padding(.vertical, 2.5)
            .background(
                Capsule().fill(Color(nsColor: color))
            )
            .shadow(color: .black.opacity(0.4), radius: 2, y: 1)
    }

    private var footer: some View {
        HStack(spacing: 8) {
            Text(entry.createdAt, style: .time)
                .font(.system(size: 11.5, weight: .medium))
                .foregroundStyle(.primary)
            Text("·")
                .foregroundStyle(.tertiary)
            Text(entry.createdAt, style: .date)
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
            Spacer()
            Text("\(Int(entry.width))×\(Int(entry.height))")
                .font(.system(size: 10.5, weight: .medium))
                .foregroundStyle(.tertiary)
                .monospacedDigit()
        }
        .padding(.horizontal, 2)
    }

    // MARK: - Actions (image)
    //
    // `image` is the downsampled thumbnail used for the grid. These actions
    // need full-resolution pixels, so they re-load the source file each time.

    private var fullImage: NSImage? {
        history.image(for: entry)
    }

    private func copy() {
        guard let img = fullImage else { return }
        ImageSaver.copyToClipboard(img)
    }

    private func saveToDesktop() {
        guard let img = fullImage else { return }
        ImageSaver.saveToDesktop(img)
    }

    private func edit() {
        guard let img = fullImage,
              let appDelegate = NSApp.delegate as? AppDelegate else { return }
        appDelegate.openAnnotator(with: img)
    }

    // MARK: - Actions (video / gif)

    private func play() {
        NSWorkspace.shared.open(history.fileURL(for: entry))
    }

    private func copyFile() {
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.writeObjects([history.fileURL(for: entry) as NSURL])
    }

    private func saveFile() {
        let url = history.fileURL(for: entry)
        let desktop = FileManager.default.urls(for: .desktopDirectory, in: .userDomainMask).first!
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd 'at' HH.mm.ss"
        let ext = url.pathExtension.isEmpty ? "mp4" : url.pathExtension
        let dest = desktop.appendingPathComponent("ShotX Recording \(formatter.string(from: Date())).\(ext)")
        do {
            try FileManager.default.copyItem(at: url, to: dest)
            NSWorkspace.shared.activateFileViewerSelecting([dest])
        } catch { /* silent */ }
    }

    // MARK: - Shared

    private func reveal() {
        NSWorkspace.shared.activateFileViewerSelecting([history.fileURL(for: entry)])
    }

    private func formatDuration(_ t: TimeInterval) -> String {
        let total = Int(t)
        let m = total / 60
        let s = total % 60
        return String(format: "%d:%02d", m, s)
    }
}

private struct HistoryActionButton: View {
    let icon: String
    let tooltip: String
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Image(systemName: icon)
                .font(.system(size: 13, weight: .bold))
                .foregroundStyle(.white)
                .frame(width: 34, height: 34)
                .background(
                    Circle()
                        .fill(hovering ? Color.accentColor : Color.black.opacity(0.6))
                )
                .overlay(
                    Circle()
                        .strokeBorder(Color.white.opacity(0.18), lineWidth: 0.5)
                )
                .shadow(color: .black.opacity(0.35), radius: 4, y: 2)
        }
        .buttonStyle(.plain)
        .help(tooltip)
        .onHover { hovering = $0 }
        .animation(.easeOut(duration: 0.12), value: hovering)
    }
}

struct SettingsView: View {
    @EnvironmentObject var shortcuts: ShortcutStore
    @ObservedObject var preferences = PreferencesStore.shared

    var body: some View {
        ScrollView {
            VStack(spacing: 16) {
                // 1. 截图设置
                SettingsCard(
                    icon: "camera.viewfinder",
                    tint: .blue,
                    title: "截图设置",
                    subtitle: "配置截图格式与保存目录"
                ) {
                    VStack(alignment: .leading, spacing: 14) {
                        // 格式选择
                        HStack {
                            VStack(alignment: .leading, spacing: 3) {
                                Text("保存格式")
                                    .font(.system(size: 13, weight: .medium))
                                Text("支持 PNG（无损透明）、JPG（体积小）、HEIC（高效压缩）")
                                    .font(.system(size: 11))
                                    .foregroundStyle(.secondary)
                            }
                            Spacer()
                            Picker("", selection: $preferences.screenshotFormat) {
                                ForEach(ScreenshotFormat.allCases) { format in
                                    Text(format.displayName).tag(format)
                                }
                            }
                            .frame(width: 170)
                        }

                        Divider()

                        // 保存路径
                        HStack(alignment: .center, spacing: 10) {
                            VStack(alignment: .leading, spacing: 4) {
                                Text("保存路径")
                                    .font(.system(size: 13, weight: .medium))
                                Text(preferences.saveDirectoryURL.path)
                                    .font(.system(size: 12, design: .monospaced))
                                    .lineLimit(1)
                                    .truncationMode(.middle)
                                    .foregroundStyle(.primary)
                                Text("截图后不会自动保存到磁盘，仅在点击标注栏中的“保存”按钮时才会写入此路径。")
                                    .font(.system(size: 11))
                                    .foregroundStyle(.secondary)
                            }
                            Spacer(minLength: 8)
                            Button("更改…") {
                                preferences.selectSaveFolder()
                            }
                            .buttonStyle(.bordered)
                            .controlSize(.small)

                            Button("打开") {
                                NSWorkspace.shared.open(preferences.saveDirectoryURL)
                            }
                            .buttonStyle(.borderless)
                            .controlSize(.small)
                        }
                    }
                }

                // 2. 录屏与录音保存设置
                SettingsCard(
                    icon: "video.fill",
                    tint: .purple,
                    title: "录屏与录音设置",
                    subtitle: "配置屏幕录制与麦克风录音的默认保存目录"
                ) {
                    VStack(alignment: .leading, spacing: 14) {
                        // 录屏路径
                        HStack(alignment: .center, spacing: 10) {
                            VStack(alignment: .leading, spacing: 4) {
                                HStack(spacing: 6) {
                                    Image(systemName: "record.circle")
                                        .foregroundStyle(.red)
                                    Text("录屏保存路径")
                                        .font(.system(size: 13, weight: .medium))
                                }
                                Text(preferences.screenRecordingSaveDirectoryURL.path)
                                    .font(.system(size: 12, design: .monospaced))
                                    .lineLimit(1)
                                    .truncationMode(.middle)
                                    .foregroundStyle(.primary)
                                Text("屏幕录制完成后，视频文件（MP4）将自动保存到此路径。")
                                    .font(.system(size: 11))
                                    .foregroundStyle(.secondary)
                            }
                            Spacer(minLength: 8)
                            Button("更改…") {
                                preferences.selectScreenRecordingSaveFolder()
                            }
                            .buttonStyle(.bordered)
                            .controlSize(.small)

                            Button("打开") {
                                NSWorkspace.shared.open(preferences.screenRecordingSaveDirectoryURL)
                            }
                            .buttonStyle(.borderless)
                            .controlSize(.small)
                        }

                        Divider()

                        // 录音路径
                        HStack(alignment: .center, spacing: 10) {
                            VStack(alignment: .leading, spacing: 4) {
                                HStack(spacing: 6) {
                                    Image(systemName: "mic.fill")
                                        .foregroundStyle(.green)
                                    Text("录音保存路径")
                                        .font(.system(size: 13, weight: .medium))
                                }
                                Text(preferences.audioRecordingSaveDirectoryURL.path)
                                    .font(.system(size: 12, design: .monospaced))
                                    .lineLimit(1)
                                    .truncationMode(.middle)
                                    .foregroundStyle(.primary)
                                Text("麦克风录音结束后，音频文件（M4A）将自动保存到此路径。")
                                    .font(.system(size: 11))
                                    .foregroundStyle(.secondary)
                            }
                            Spacer(minLength: 8)
                            Button("更改…") {
                                preferences.selectAudioRecordingSaveFolder()
                            }
                            .buttonStyle(.bordered)
                            .controlSize(.small)

                            Button("打开") {
                                NSWorkspace.shared.open(preferences.audioRecordingSaveDirectoryURL)
                            }
                            .buttonStyle(.borderless)
                            .controlSize(.small)
                        }
                    }
                }

                // 3. 文本识别 (OCR) 平台
                SettingsCard(
                    icon: "text.viewfinder",
                    tint: .orange,
                    title: "文本识别 (OCR) 平台",
                    subtitle: "配置离线或云端文字识别服务平台与密钥"
                ) {
                    VStack(alignment: .leading, spacing: 14) {
                        HStack {
                            VStack(alignment: .leading, spacing: 3) {
                                Text("识别服务平台")
                                    .font(.system(size: 13, weight: .medium))
                                Text("支持 Apple Vision、百度智能云、有道智云、谷歌、腾讯云")
                                    .font(.system(size: 11))
                                    .foregroundStyle(.secondary)
                            }
                            Spacer()
                            Picker("", selection: $preferences.ocrPlatform) {
                                ForEach(OCRPlatform.allCases) { p in
                                    Text(p.displayName).tag(p)
                                }
                            }
                            .frame(width: 260)
                        }

                        Divider()

                        switch preferences.ocrPlatform {
                        case .apple:
                            HStack(spacing: 8) {
                                Image(systemName: "checkmark.shield.fill")
                                    .foregroundStyle(.green)
                                Text("使用 macOS 原生 Vision 离线模型，无需配置密钥，保护隐私且识别极速。")
                                    .font(.system(size: 11.5))
                                    .foregroundStyle(.secondary)
                            }
                        case .baidu:
                            VStack(spacing: 10) {
                                HStack {
                                    Text("API Key")
                                        .font(.system(size: 12, weight: .medium))
                                        .frame(width: 80, alignment: .leading)
                                    TextField("请输入百度智能云 API Key", text: $preferences.baiduOCRAPIKey)
                                        .textFieldStyle(.roundedBorder)
                                }
                                HStack {
                                    Text("Secret Key")
                                        .font(.system(size: 12, weight: .medium))
                                        .frame(width: 80, alignment: .leading)
                                    SecureField("请输入百度智能云 Secret Key", text: $preferences.baiduOCRSecretKey)
                                        .textFieldStyle(.roundedBorder)
                                }
                            }
                        case .youdao:
                            VStack(spacing: 10) {
                                HStack {
                                    Text("应用 ID (AppKey)")
                                        .font(.system(size: 12, weight: .medium))
                                        .frame(width: 120, alignment: .leading)
                                    TextField("请输入有道智云应用 ID", text: $preferences.youdaoOCRAppKey)
                                        .textFieldStyle(.roundedBorder)
                                }
                                HStack {
                                    Text("应用密钥 (Secret)")
                                        .font(.system(size: 12, weight: .medium))
                                        .frame(width: 120, alignment: .leading)
                                    SecureField("请输入有道智云应用密钥", text: $preferences.youdaoOCRAppSecret)
                                        .textFieldStyle(.roundedBorder)
                                }
                            }
                        case .google:
                            HStack {
                                Text("API Key")
                                    .font(.system(size: 12, weight: .medium))
                                    .frame(width: 80, alignment: .leading)
                                SecureField("请输入 Google Cloud Vision API Key", text: $preferences.googleOCRAPIKey)
                                    .textFieldStyle(.roundedBorder)
                            }
                        case .tencent:
                            VStack(spacing: 10) {
                                HStack {
                                    Text("SecretId")
                                        .font(.system(size: 12, weight: .medium))
                                        .frame(width: 80, alignment: .leading)
                                    TextField("请输入腾讯云 SecretId", text: $preferences.tencentOCRSecretId)
                                        .textFieldStyle(.roundedBorder)
                                }
                                HStack {
                                    Text("SecretKey")
                                        .font(.system(size: 12, weight: .medium))
                                        .frame(width: 80, alignment: .leading)
                                    SecureField("请输入腾讯云 SecretKey", text: $preferences.tencentOCRSecretKey)
                                        .textFieldStyle(.roundedBorder)
                                }
                            }
                        }
                    }
                }

                // 4. 文本翻译平台
                SettingsCard(
                    icon: "globe",
                    tint: .indigo,
                    title: "文本翻译平台",
                    subtitle: "配置多语言机器翻译服务平台与密钥"
                ) {
                    VStack(alignment: .leading, spacing: 14) {
                        HStack {
                            VStack(alignment: .leading, spacing: 3) {
                                Text("翻译服务平台")
                                    .font(.system(size: 13, weight: .medium))
                                Text("支持谷歌翻译、百度通用翻译、有道智云、腾讯云机器翻译")
                                    .font(.system(size: 11))
                                    .foregroundStyle(.secondary)
                            }
                            Spacer()
                            Picker("", selection: $preferences.translationPlatform) {
                                ForEach(TranslationPlatform.allCases) { p in
                                    Text(p.displayName).tag(p)
                                }
                            }
                            .frame(width: 260)
                        }

                        Divider()

                        switch preferences.translationPlatform {
                        case .google:
                            HStack(spacing: 8) {
                                Image(systemName: "checkmark.circle.fill")
                                    .foregroundStyle(.green)
                                Text("使用 Google Translate 免费翻译接口，开箱即用，免配置 ID 与密钥。")
                                    .font(.system(size: 11.5))
                                    .foregroundStyle(.secondary)
                            }
                        case .baidu:
                            VStack(spacing: 10) {
                                HStack {
                                    Text("APP ID")
                                        .font(.system(size: 12, weight: .medium))
                                        .frame(width: 80, alignment: .leading)
                                    TextField("请输入百度翻译开放平台 APP ID", text: $preferences.baiduTranslateAppId)
                                        .textFieldStyle(.roundedBorder)
                                }
                                HStack {
                                    Text("密钥 (Secret)")
                                        .font(.system(size: 12, weight: .medium))
                                        .frame(width: 80, alignment: .leading)
                                    SecureField("请输入百度翻译开放平台密钥", text: $preferences.baiduTranslateSecretKey)
                                        .textFieldStyle(.roundedBorder)
                                }
                            }
                        case .youdao:
                            VStack(spacing: 10) {
                                HStack {
                                    Text("应用 ID (AppKey)")
                                        .font(.system(size: 12, weight: .medium))
                                        .frame(width: 120, alignment: .leading)
                                    TextField("请输入有道智云应用 ID", text: $preferences.youdaoTranslateAppKey)
                                        .textFieldStyle(.roundedBorder)
                                }
                                HStack {
                                    Text("应用密钥 (Secret)")
                                        .font(.system(size: 12, weight: .medium))
                                        .frame(width: 120, alignment: .leading)
                                    SecureField("请输入有道智云应用密钥", text: $preferences.youdaoTranslateAppSecret)
                                        .textFieldStyle(.roundedBorder)
                                }
                            }
                        case .tencent:
                            VStack(spacing: 10) {
                                HStack {
                                    Text("SecretId")
                                        .font(.system(size: 12, weight: .medium))
                                        .frame(width: 80, alignment: .leading)
                                    TextField("请输入腾讯云 SecretId", text: $preferences.tencentTranslateSecretId)
                                        .textFieldStyle(.roundedBorder)
                                }
                                HStack {
                                    Text("SecretKey")
                                        .font(.system(size: 12, weight: .medium))
                                        .frame(width: 80, alignment: .leading)
                                    SecureField("请输入腾讯云 SecretKey", text: $preferences.tencentTranslateSecretKey)
                                        .textFieldStyle(.roundedBorder)
                                }
                            }
                        }
                    }
                }

                // 5. 快捷键卡片（顺序与状态栏右键菜单保持一致）
                SettingsCard(
                    icon: "command",
                    tint: .blue,
                    title: "快捷键",
                    subtitle: "自定义全部 14 种核心功能的全局热键（顺序与菜单一致）"
                ) {
                    VStack(alignment: .leading, spacing: 11) {
                        shortcutRow(
                            title: "全能截图",
                            desc: "集放大镜取色、九宫格比例参考、预设尺寸于一体的辅助截图模式",
                            binding: $shortcuts.allInOneShortcut,
                            defaultVal: .defaultAllInOne
                        )
                        dividerLine
                        shortcutRow(
                            title: "区域截图",
                            desc: "划选屏幕任意矩形区域并展开标注栏与长截图",
                            binding: $shortcuts.shortcut,
                            defaultVal: .default
                        )
                        dividerLine
                        shortcutRow(
                            title: "上次区域截图",
                            desc: "💡功能说明：重新截取上一次框选的屏幕坐标与尺寸，无需重复用鼠标手动划选",
                            binding: $shortcuts.previousAreaShortcut,
                            defaultVal: .defaultPrevious
                        )
                        dividerLine
                        shortcutRow(
                            title: "全屏截图",
                            desc: "一键截取当前整个主屏幕并在正中央提示",
                            binding: $shortcuts.fullscreenShortcut,
                            defaultVal: .defaultFullscreen
                        )
                        dividerLine
                        shortcutRow(
                            title: "窗口截图",
                            desc: "移动鼠标悬停并自动识别窗口轮廓截取",
                            binding: $shortcuts.windowCaptureShortcut,
                            defaultVal: .defaultWindow
                        )
                        dividerLine
                        shortcutRow(
                            title: "截图OCR",
                            desc: "划选屏幕后直接提取文本并复制到剪贴板，无标注栏打扰",
                            binding: $shortcuts.ocrShortcut,
                            defaultVal: .defaultOCR
                        )
                        dividerLine
                        shortcutRow(
                            title: "从剪贴板贴图",
                            desc: "将剪贴板或截图钉在屏幕最上层悬浮展示，方便对比对照",
                            binding: $shortcuts.pinShortcut,
                            defaultVal: .defaultPin
                        )
                        dividerLine
                        shortcutRow(
                            title: "拾色器",
                            desc: "放大镜精准吸管取色，按 R 键拷贝 RGB，按 H 键拷贝 HEX",
                            binding: $shortcuts.colorPickerShortcut,
                            defaultVal: .defaultColorPicker
                        )
                        dividerLine
                        shortcutRow(
                            title: "屏幕录制",
                            desc: "划选区域开始录屏（支持声音、摄像头与按键显示）",
                            binding: $shortcuts.recordScreenShortcut,
                            defaultVal: .defaultRecordScreen
                        )
                        dividerLine
                        shortcutRow(
                            title: "声音录制",
                            desc: "开启/结束系统麦克风高音质录音，悬浮药丸实时显示时长",
                            binding: $shortcuts.recordAudioShortcut,
                            defaultVal: .defaultRecordAudio
                        )
                        dividerLine
                        shortcutRow(
                            title: "划词翻译",
                            desc: "选中任意屏幕文本后按快捷键，自动填入翻译框并立即翻译",
                            binding: $shortcuts.selectionTranslationShortcut,
                            defaultVal: .defaultSelectionTranslation
                        )
                        dividerLine
                        shortcutRow(
                            title: "截图翻译",
                            desc: "框选屏幕区域后自动提取文字并立即打开翻译框进行翻译",
                            binding: $shortcuts.screenshotTranslationShortcut,
                            defaultVal: .defaultScreenshotTranslation
                        )
                        dividerLine
                        shortcutRow(
                            title: "剪贴板历史",
                            desc: "随时呼出历史剪贴板记录，支持搜索、双击直接粘贴在光标处",
                            binding: $shortcuts.clipboardHistoryShortcut,
                            defaultVal: .defaultClipboardHistory
                        )
                        dividerLine
                        shortcutRow(
                            title: "隐藏桌面图标",
                            desc: "💡功能说明：一键隐藏或恢复桌面上所有文件与快捷方式，录屏或截图时桌面整洁干净",
                            binding: $shortcuts.desktopIconsShortcut,
                            defaultVal: .defaultDesktopIcons
                        )

                        Divider().padding(.vertical, 4)

                        HStack {
                            Text("点击输入框并按下新的组合键。按 Esc 键取消。")
                                .font(.system(size: 11))
                                .foregroundStyle(.secondary)
                            Spacer()
                            Button("清空全部") {
                                shortcuts.shortcut = .none
                                shortcuts.fullscreenShortcut = .none
                                shortcuts.windowCaptureShortcut = .none
                                shortcuts.previousAreaShortcut = .none
                                shortcuts.ocrShortcut = .none
                                shortcuts.recordScreenShortcut = .none
                                shortcuts.recordAudioShortcut = .none
                                shortcuts.pinShortcut = .none
                                shortcuts.colorPickerShortcut = .none
                                shortcuts.desktopIconsShortcut = .none
                                shortcuts.allInOneShortcut = .none
                                shortcuts.selectionTranslationShortcut = .none
                                shortcuts.screenshotTranslationShortcut = .none
                                shortcuts.clipboardHistoryShortcut = .none
                            }
                            .buttonStyle(.bordered)
                            .controlSize(.small)

                            Button("恢复全部默认") {
                                shortcuts.shortcut = .default
                                shortcuts.fullscreenShortcut = .defaultFullscreen
                                shortcuts.windowCaptureShortcut = .defaultWindow
                                shortcuts.previousAreaShortcut = .defaultPrevious
                                shortcuts.ocrShortcut = .defaultOCR
                                shortcuts.recordScreenShortcut = .defaultRecordScreen
                                shortcuts.recordAudioShortcut = .defaultRecordAudio
                                shortcuts.pinShortcut = .defaultPin
                                shortcuts.colorPickerShortcut = .defaultColorPicker
                                shortcuts.desktopIconsShortcut = .defaultDesktopIcons
                                shortcuts.allInOneShortcut = .defaultAllInOne
                                shortcuts.selectionTranslationShortcut = .defaultSelectionTranslation
                                shortcuts.screenshotTranslationShortcut = .defaultScreenshotTranslation
                                shortcuts.clipboardHistoryShortcut = .defaultClipboardHistory
                            }
                            .buttonStyle(.bordered)
                            .controlSize(.small)
                        }
                    }
                }

                // 4. 关于
                SettingsCard(
                    icon: "info.circle.fill",
                    tint: .gray,
                    title: "关于 ShotX",
                    subtitle: "现代 macOS 全能屏幕截图、就地标注、长截图与录屏录音工具"
                ) {
                    VStack(alignment: .leading, spacing: 14) {
                        HStack(alignment: .top) {
                            VStack(alignment: .leading, spacing: 4) {
                                Text("ShotX  v\(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.9.13")")
                                    .font(.system(size: 14, weight: .bold))
                                HStack(spacing: 8) {
                                    Text("开发者:")
                                        .font(.system(size: 12))
                                        .foregroundStyle(.secondary)
                                    Link("@muzi86754781", destination: URL(string: "https://github.com/muzi86754781")!)
                                        .font(.system(size: 12))
                                    Text("·")
                                        .foregroundStyle(.tertiary)
                                    Link("github.com/muzi86754781/shotx", destination: URL(string: "https://github.com/muzi86754781/shotx")!)
                                        .font(.system(size: 12))
                                }
                                Text("QQ: 285351298")
                                    .font(.system(size: 11.5, design: .monospaced))
                                    .foregroundStyle(.secondary)
                            }

                            Spacer()

                            HStack(spacing: 8) {
                                Button {
                                    SupportAuthorController.shared.show()
                                } label: {
                                    Label("支持作者", systemImage: "cup.and.saucer.fill")
                                        .font(.system(size: 12, weight: .medium))
                                }
                                .buttonStyle(.borderedProminent)
                                .tint(.orange)
                                .controlSize(.small)

                                Button("检查更新") {
                                    UpdaterController.shared.checkForUpdates()
                                }
                                .buttonStyle(.bordered)
                                .controlSize(.small)

                                Button {
                                    NSApp.terminate(nil)
                                } label: {
                                    Label("退出 ShotX", systemImage: "power")
                                        .font(.system(size: 12, weight: .medium))
                                }
                                .buttonStyle(.bordered)
                                .controlSize(.small)
                            }
                        }

                        Divider()

                        // 功能介绍详细卡片
                        VStack(alignment: .leading, spacing: 6) {
                            Text("功能亮点与介绍：")
                                .font(.system(size: 12.5, weight: .semibold))

                            Group {
                                Text("• 全能辅助截图：支持放大镜取色、九宫格比例网格与常用分辨率快速框选。")
                                Text("• 区域 / 窗口 / 全屏 / 定时截图：多维度即时捕捉，支持阴影与圆角实时调整。")
                                Text("• 滚动长截图：在截图区域内滑动鼠标，实时检测滚动变化并无缝拼接长图。")
                                Text("• 截图 OCR：框选屏幕即刻离线识别文字并自动复制到剪贴板，无多余干扰。")
                                Text("• 屏幕与音频录制：高清录屏（摄像头/声音/光标/按键高亮），以及独立麦克风录音。")
                                Text("• 多模态翻译：支持划词翻译、截图翻译与输入翻译窗口，中英互换与 TTS 朗读。")
                                Text("• 剪贴板历史：自动记录复制内容，支持按类检索、快速粘贴与多格式管理。")
                                Text("• 贴图置顶 & 拾色器 & 隐藏桌面图标：让桌面井然有序，提升日常生产力。")
                            }
                            .font(.system(size: 11.5))
                            .foregroundStyle(.secondary)
                        }
                    }
                }
            }
            .padding(20)
        }
        .background(Color(NSColor.windowBackgroundColor))
    }

    private var dividerLine: some View {
        Rectangle()
            .fill(Color.primary.opacity(0.06))
            .frame(height: 0.5)
    }

    private func shortcutRow(
        title: String,
        desc: String,
        binding: Binding<Shortcut>,
        defaultVal: Shortcut
    ) -> some View {
        HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.system(size: 13, weight: .medium))
                Text(desc)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }
            Spacer(minLength: 8)
            ShortcutRecorder(shortcut: binding)
                .frame(width: 140, height: 26)
            Button("清除") {
                binding.wrappedValue = .none
            }
            .buttonStyle(.borderless)
            .foregroundStyle(.secondary)
            .font(.system(size: 11))
        }
    }
}

struct SettingsCard<Content: View>: View {
    let icon: String
    let tint: Color
    let title: String
    let subtitle: String
    @ViewBuilder var content: () -> Content

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 11) {
                Image(systemName: icon)
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundStyle(tint)
                    .frame(width: 34, height: 34)
                    .background(
                        RoundedRectangle(cornerRadius: 9)
                            .fill(tint.opacity(0.14))
                    )
                VStack(alignment: .leading, spacing: 1) {
                    Text(title)
                        .font(.system(size: 14, weight: .semibold))
                    Text(subtitle)
                        .font(.system(size: 11.5))
                        .foregroundStyle(.secondary)
                }
                Spacer()
            }

            Divider()

            content()
        }
        .padding(16)
        .background(
            RoundedRectangle(cornerRadius: 12)
                .fill(Color(NSColor.controlBackgroundColor))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(Color.primary.opacity(0.08), lineWidth: 1)
        )
    }
}
