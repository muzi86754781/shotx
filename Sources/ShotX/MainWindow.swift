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

                // 3. 快捷键卡片
                SettingsCard(
                    icon: "command",
                    tint: .blue,
                    title: "快捷键",
                    subtitle: "自定义全部 11 种核心功能的全局热键"
                ) {
                    VStack(alignment: .leading, spacing: 11) {
                        shortcutRow(
                            title: "区域截图",
                            desc: "划选屏幕任意矩形区域并展开标注栏与长截图",
                            binding: $shortcuts.shortcut,
                            defaultVal: .default
                        )
                        dividerLine
                        shortcutRow(
                            title: "截取全屏",
                            desc: "一键截取当前整个主屏幕并在正中央提示",
                            binding: $shortcuts.fullscreenShortcut,
                            defaultVal: .defaultFullscreen
                        )
                        dividerLine
                        shortcutRow(
                            title: "截取窗口",
                            desc: "移动鼠标悬停并自动识别窗口轮廓截取",
                            binding: $shortcuts.windowCaptureShortcut,
                            defaultVal: .defaultWindow
                        )
                        dividerLine
                        shortcutRow(
                            title: "截取上一区域",
                            desc: "💡功能说明：重新截取上一次框选的屏幕坐标与尺寸，无需重复用鼠标手动划选",
                            binding: $shortcuts.previousAreaShortcut,
                            defaultVal: .defaultPrevious
                        )
                        dividerLine
                        shortcutRow(
                            title: "提取文字 (OCR)",
                            desc: "划选屏幕后直接提取文本并复制到剪贴板，无标注栏打扰",
                            binding: $shortcuts.ocrShortcut,
                            defaultVal: .defaultOCR
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
                            title: "音频录制",
                            desc: "开启/结束系统麦克风高音质录音，悬浮药丸实时显示时长",
                            binding: $shortcuts.recordAudioShortcut,
                            defaultVal: .defaultRecordAudio
                        )
                        dividerLine
                        shortcutRow(
                            title: "贴图置顶",
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
                            title: "显示/隐藏桌面图标",
                            desc: "💡功能说明：一键隐藏或恢复桌面上所有文件与快捷方式，录屏或截图时桌面整洁干净",
                            binding: $shortcuts.desktopIconsShortcut,
                            defaultVal: .defaultDesktopIcons
                        )
                        dividerLine
                        shortcutRow(
                            title: "全能截图",
                            desc: "集放大镜取色、九宫格比例参考、预设尺寸于一体的辅助截图模式",
                            binding: $shortcuts.allInOneShortcut,
                            defaultVal: .defaultAllInOne
                        )

                        Divider().padding(.vertical, 4)

                        HStack {
                            Text("点击输入框并按下新的组合键。按 Esc 键取消。")
                                .font(.system(size: 11))
                                .foregroundStyle(.secondary)
                            Spacer()
                            Button("恢复全部默认快捷键") {
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
                    title: "关于",
                    subtitle: "ShotX · 轻量高效的屏幕截图与录屏工具"
                ) {
                    VStack(alignment: .leading, spacing: 10) {
                        HStack {
                            VStack(alignment: .leading, spacing: 4) {
                                Text("版本 \(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.9.3")")
                                    .font(.system(size: 13, weight: .medium))
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
                            }
                            Spacer()
                            HStack(spacing: 8) {
                                Button("检查更新…") {
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
            Button("重置") {
                binding.wrappedValue = defaultVal
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
