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

struct HistoryView: View {
    @EnvironmentObject var history: HistoryStore
    @EnvironmentObject var shortcuts: ShortcutStore
    private let cols = [GridItem(.adaptive(minimum: 210, maximum: 260), spacing: 16)]

    var body: some View {
        VStack(spacing: 0) {
            if !history.entries.isEmpty {
                header
                Divider()
            }
            content
        }
        .background(Color(NSColor.windowBackgroundColor))
    }

    private var header: some View {
        HStack(spacing: 8) {
            Text("\(history.entries.count) 张截图")
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(.secondary)
            Spacer()
            Button(role: .destructive) {
                history.clear()
            } label: {
                Label("清空所有", systemImage: "trash")
                    .font(.system(size: 12, weight: .medium))
            }
            .buttonStyle(.borderless)
            .foregroundStyle(.red)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 9)
    }

    private var content: some View {
        Group {
            if history.entries.isEmpty {
                emptyState
            } else {
                ScrollView {
                    LazyVGrid(columns: cols, spacing: 18) {
                        ForEach(history.entries) { entry in
                            HistoryCard(entry: entry)
                        }
                    }
                    .padding(20)
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
                Image(systemName: "photo.on.rectangle.angled")
                    .font(.system(size: 34, weight: .light))
                    .foregroundStyle(.tint)
            }
            Text("暂无截图记录")
                .font(.system(size: 15, weight: .semibold))
            Text("在任意界面按下 \(ShortcutFormatter.describe(shortcuts.shortcut)) 即可截取区域")
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
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

    var body: some View {
        ScrollView {
            VStack(spacing: 14) {
                SettingsCard(
                    icon: "command",
                    tint: .blue,
                    title: "快捷键",
                    subtitle: "自定义全局热键"
                ) {
                    VStack(alignment: .leading, spacing: 12) {
                        HStack {
                            Text("区域截图")
                                .font(.system(size: 13))
                            Spacer()
                            ShortcutRecorder(shortcut: $shortcuts.shortcut)
                                .frame(width: 150, height: 28)
                            Button("重置") { shortcuts.shortcut = .default }
                                .buttonStyle(.borderless)
                                .foregroundStyle(.secondary)
                                .font(.system(size: 12))
                        }
                        Rectangle()
                            .fill(Color.primary.opacity(0.08))
                            .frame(height: 0.5)
                        HStack {
                            Text("截取全屏")
                                .font(.system(size: 13))
                            Spacer()
                            ShortcutRecorder(shortcut: $shortcuts.fullscreenShortcut)
                                .frame(width: 150, height: 28)
                            Button("重置") { shortcuts.fullscreenShortcut = .defaultFullscreen }
                                .buttonStyle(.borderless)
                                .foregroundStyle(.secondary)
                                .font(.system(size: 12))
                        }
                        Rectangle()
                            .fill(Color.primary.opacity(0.08))
                            .frame(height: 0.5)
                        HStack {
                            Text("拾色器")
                                .font(.system(size: 13))
                            Spacer()
                            ShortcutRecorder(shortcut: $shortcuts.colorPickerShortcut)
                                .frame(width: 150, height: 28)
                            Button("重置") { shortcuts.colorPickerShortcut = .defaultColorPicker }
                                .buttonStyle(.borderless)
                                .foregroundStyle(.secondary)
                                .font(.system(size: 12))
                        }
                        Rectangle()
                            .fill(Color.primary.opacity(0.08))
                            .frame(height: 0.5)
                        HStack {
                            Text("提取文字 (OCR)")
                                .font(.system(size: 13))
                            Spacer()
                            ShortcutRecorder(shortcut: $shortcuts.ocrShortcut)
                                .frame(width: 150, height: 28)
                            Button("重置") { shortcuts.ocrShortcut = .defaultOCR }
                                .buttonStyle(.borderless)
                                .foregroundStyle(.secondary)
                                .font(.system(size: 12))
                        }
                        Text("点击输入框并按下新的组合键。按 Esc 键取消。")
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                    }
                }

                SettingsCard(
                    icon: "info.circle.fill",
                    tint: .gray,
                    title: "关于",
                    subtitle: "ShotX · 轻量高效的屏幕截图与录屏工具"
                ) {
                    HStack {
                        Text("版本 \(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "—")")
                            .font(.system(size: 13))
                            .foregroundStyle(.secondary)
                        Spacer()
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
            }
            .padding(20)
        }
        .background(Color(NSColor.windowBackgroundColor))
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
