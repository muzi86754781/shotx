import Cocoa
import Combine
import SwiftUI

struct ClipboardEntry: Identifiable, Codable, Equatable {
    enum Kind: String, Codable {
        case text
        case file
    }

    let id: UUID
    let createdAt: Date
    let kind: Kind
    let content: String
    let filePaths: [String]

    init(
        id: UUID = UUID(),
        createdAt: Date = Date(),
        kind: Kind = .text,
        content: String,
        filePaths: [String] = []
    ) {
        self.id = id
        self.createdAt = createdAt
        self.kind = kind
        self.content = content
        self.filePaths = filePaths
    }

    var displayTitle: String {
        if kind == .file {
            if filePaths.count == 1 {
                return (filePaths.first as NSString?)?.lastPathComponent ?? "文件"
            } else {
                return "\(filePaths.count) 个文件"
            }
        }
        let lines = content.components(separatedBy: .newlines)
        return lines.first?.trimmingCharacters(in: .whitespaces) ?? "文本"
    }

    var displaySubtitle: String {
        if kind == .file {
            return filePaths.joined(separator: ", ")
        }
        return content
    }
}

final class ClipboardStore: ObservableObject {
    static let shared = ClipboardStore()

    @Published private(set) var entries: [ClipboardEntry] = []

    private var lastChangeCount: Int = 0
    private var timer: Timer?
    private let indexURL: URL
    private let maxEntries = 200

    private init() {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        let root = appSupport.appendingPathComponent("ShotX", isDirectory: true)
        indexURL = root.appendingPathComponent("clipboard_history.json")
        try? FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)

        load()
        lastChangeCount = NSPasteboard.general.changeCount
        startMonitoring()
    }

    func startMonitoring() {
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            self?.checkForNewClipboardItem()
        }
    }

    func stopMonitoring() {
        timer?.invalidate()
        timer = nil
    }

    private func checkForNewClipboardItem() {
        let currentCount = NSPasteboard.general.changeCount
        guard currentCount != lastChangeCount else { return }
        lastChangeCount = currentCount

        let pb = NSPasteboard.general

        // 1. Check for files
        if let urls = pb.readObjects(forClasses: [NSURL.self], options: nil) as? [URL], !urls.isEmpty {
            let paths = urls.filter { $0.isFileURL }.map { $0.path }
            if !paths.isEmpty {
                addFiles(paths)
                return
            }
        }

        // 2. Check for text
        if let str = pb.string(forType: .string), !str.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            addText(str)
        }
    }

    func addText(_ text: String) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }

        // Deduplicate: remove older identical entry
        entries.removeAll { $0.kind == .text && $0.content == trimmed }

        let entry = ClipboardEntry(kind: .text, content: trimmed)
        entries.insert(entry, at: 0)
        trimAndSave()
    }

    func addFiles(_ paths: [String]) {
        guard !paths.isEmpty else { return }
        entries.removeAll { $0.kind == .file && $0.filePaths == paths }

        let entry = ClipboardEntry(kind: .file, content: paths.joined(separator: "\n"), filePaths: paths)
        entries.insert(entry, at: 0)
        trimAndSave()
    }

    func copyToClipboard(_ entry: ClipboardEntry) {
        let pb = NSPasteboard.general
        pb.clearContents()
        if entry.kind == .file {
            let urls = entry.filePaths.map { URL(fileURLWithPath: $0) as NSURL }
            pb.writeObjects(urls)
        } else {
            pb.setString(entry.content, forType: .string)
        }
        lastChangeCount = pb.changeCount
        ToastController.shared.show(
            message: "已复制到剪贴板，可按 ⌘V 粘贴",
            icon: "doc.on.clipboard.fill",
            tint: .systemBlue,
            duration: 2.0,
            position: .center
        )
    }

    func remove(_ entry: ClipboardEntry) {
        entries.removeAll { $0.id == entry.id }
        save()
    }

    func clear() {
        entries.removeAll()
        save()
    }

    private func trimAndSave() {
        if entries.count > maxEntries {
            entries = Array(entries.prefix(maxEntries))
        }
        save()
    }

    private func load() {
        guard let data = try? Data(contentsOf: indexURL),
              let decoded = try? JSONDecoder().decode([ClipboardEntry].self, from: data) else {
            return
        }
        entries = decoded
    }

    private func save() {
        guard let data = try? JSONEncoder().encode(entries) else { return }
        try? data.write(to: indexURL, options: .atomic)
    }
}

// MARK: - Clipboard History Floating Panel

final class ClipboardHistoryController {
    static let shared = ClipboardHistoryController()
    private var panel: NSPanel?

    func toggle() {
        if let p = panel, p.isVisible {
            dismiss()
        } else {
            show()
        }
    }

    func show() {
        dismiss()

        let view = ClipboardHistoryView(
            store: ClipboardStore.shared,
            onClose: { [weak self] in self?.dismiss() }
        )
        let hosting = NSHostingController(rootView: view)
        let size = NSSize(width: 420, height: 480)
        hosting.view.frame = NSRect(origin: .zero, size: size)

        let p = NSPanel(
            contentRect: NSRect(origin: .zero, size: size),
            styleMask: [.titled, .closable, .fullSizeContentView, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        p.title = "记录复制 (剪贴板历史)"
        p.titleVisibility = .hidden
        p.titlebarAppearsTransparent = true
        p.isMovableByWindowBackground = true
        p.isFloatingPanel = true
        p.level = .floating
        p.contentViewController = hosting
        p.isReleasedWhenClosed = false
        p.hasShadow = true
        p.backgroundColor = .clear

        if let screen = NSScreen.main {
            let origin = NSPoint(
                x: screen.frame.midX - size.width / 2,
                y: screen.frame.midY - size.height / 2
            )
            p.setFrame(NSRect(origin: origin, size: size), display: true)
        }
        p.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        self.panel = p
    }

    func dismiss() {
        panel?.orderOut(nil)
        panel = nil
    }
}

struct ClipboardHistoryView: View {
    @ObservedObject var store: ClipboardStore
    let onClose: () -> Void

    @State private var searchText: String = ""

    var filteredEntries: [ClipboardEntry] {
        if searchText.trimmingCharacters(in: .whitespaces).isEmpty {
            return store.entries
        }
        return store.entries.filter {
            $0.content.localizedCaseInsensitiveContains(searchText) ||
            $0.filePaths.contains { $0.localizedCaseInsensitiveContains(searchText) }
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            // Header
            HStack(spacing: 10) {
                Image(systemName: "doc.on.clipboard.fill")
                    .foregroundStyle(.blue)
                    .font(.system(size: 16))
                Text("记录复制 (剪贴板历史)")
                    .font(.system(size: 14, weight: .semibold))
                Spacer()
                Button {
                    onClose()
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .font(.system(size: 16))
                        .foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
            }
            .padding(.horizontal, 16)
            .padding(.top, 14)
            .padding(.bottom, 10)

            // Search Bar
            HStack(spacing: 8) {
                Image(systemName: "magnifyingglass")
                    .foregroundStyle(.secondary)
                    .font(.system(size: 13))
                TextField("搜索剪贴板记录…", text: $searchText)
                    .textFieldStyle(.plain)
                    .font(.system(size: 12.5))
                if !searchText.isEmpty {
                    Button {
                        searchText = ""
                    } label: {
                        Image(systemName: "xmark.circle.fill")
                            .foregroundStyle(.secondary)
                            .font(.system(size: 12))
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 7)
            .background(Color(NSColor.textBackgroundColor))
            .clipShape(RoundedRectangle(cornerRadius: 8))
            .padding(.horizontal, 16)
            .padding(.bottom, 10)

            Divider()

            // List
            if filteredEntries.isEmpty {
                VStack(spacing: 8) {
                    Spacer()
                    Image(systemName: "clipboard")
                        .font(.system(size: 36))
                        .foregroundStyle(.secondary.opacity(0.6))
                    Text(searchText.isEmpty ? "暂无复制记录" : "未找到匹配项")
                        .font(.system(size: 13))
                        .foregroundStyle(.secondary)
                    Spacer()
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView {
                    LazyVStack(spacing: 6) {
                        ForEach(filteredEntries) { entry in
                            ClipboardRowView(entry: entry) {
                                store.copyToClipboard(entry)
                                onClose()
                            } onDelete: {
                                store.remove(entry)
                            }
                        }
                    }
                    .padding(12)
                }
            }

            Divider()

            // Footer
            HStack {
                Text("\(store.entries.count) 条记录")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                Spacer()
                if !store.entries.isEmpty {
                    Button(role: .destructive) {
                        store.clear()
                    } label: {
                        Text("清空所有")
                            .font(.system(size: 11))
                            .foregroundStyle(.red)
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 8)
        }
        .frame(width: 420, height: 480)
        .background(VisualEffectView(material: .sidebar, blendingMode: .behindWindow))
    }
}

private struct ClipboardRowView: View {
    let entry: ClipboardEntry
    let onCopy: () -> Void
    let onDelete: () -> Void

    @State private var isHovering = false

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: entry.kind == .file ? "doc.fill" : "text.alignleft")
                .font(.system(size: 14))
                .foregroundStyle(entry.kind == .file ? .orange : .blue)
                .frame(width: 20, height: 20)

            VStack(alignment: .leading, spacing: 3) {
                Text(entry.displayTitle)
                    .font(.system(size: 12.5, weight: .medium))
                    .lineLimit(1)
                    .foregroundStyle(.primary)

                Text(entry.displaySubtitle)
                    .font(.system(size: 11))
                    .lineLimit(2)
                    .foregroundStyle(.secondary)

                Text(entry.createdAt, style: .time)
                    .font(.system(size: 9.5))
                    .foregroundStyle(.tertiary)
            }

            Spacer()

            if isHovering {
                HStack(spacing: 6) {
                    Button {
                        onCopy()
                    } label: {
                        Image(systemName: "doc.on.doc")
                            .font(.system(size: 11))
                            .padding(5)
                            .background(Color.blue.opacity(0.15))
                            .clipShape(Circle())
                    }
                    .buttonStyle(.plain)
                    .help("复制此项")

                    Button {
                        onDelete()
                    } label: {
                        Image(systemName: "trash")
                            .font(.system(size: 11))
                            .foregroundStyle(.red)
                            .padding(5)
                            .background(Color.red.opacity(0.1))
                            .clipShape(Circle())
                    }
                    .buttonStyle(.plain)
                    .help("删除此项")
                }
            }
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
        .background(
            RoundedRectangle(cornerRadius: 8)
                .fill(isHovering ? Color.accentColor.opacity(0.08) : Color(NSColor.controlBackgroundColor).opacity(0.6))
        )
        .onHover { isHovering = $0 }
        .onTapGesture {
            onCopy()
        }
    }
}
