import Cocoa
import Combine
import AVFoundation
import ImageIO

struct CaptureEntry: Identifiable, Codable, Equatable {
    enum Kind: String, Codable {
        case image, video, gif
    }

    let id: UUID
    let filename: String
    let createdAt: Date
    let width: CGFloat
    let height: CGFloat
    let kind: Kind
    let duration: TimeInterval?

    init(
        id: UUID = UUID(),
        filename: String,
        createdAt: Date,
        width: CGFloat,
        height: CGFloat,
        kind: Kind = .image,
        duration: TimeInterval? = nil
    ) {
        self.id = id
        self.filename = filename
        self.createdAt = createdAt
        self.width = width
        self.height = height
        self.kind = kind
        self.duration = duration
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(UUID.self, forKey: .id)
        filename = try c.decode(String.self, forKey: .filename)
        createdAt = try c.decode(Date.self, forKey: .createdAt)
        width = try c.decode(CGFloat.self, forKey: .width)
        height = try c.decode(CGFloat.self, forKey: .height)
        // Backwards compat: older entries lack `kind` and `duration`.
        kind = (try? c.decode(Kind.self, forKey: .kind)) ?? .image
        duration = try? c.decode(TimeInterval.self, forKey: .duration)
    }
}

struct OCREntry: Identifiable, Codable, Equatable {
    let id: UUID
    let createdAt: Date
    let text: String

    init(id: UUID = UUID(), createdAt: Date = Date(), text: String) {
        self.id = id
        self.createdAt = createdAt
        self.text = text
    }
}

struct AudioEntry: Identifiable, Codable, Equatable {
    let id: UUID
    let createdAt: Date
    let filename: String
    let filePath: String
    let duration: TimeInterval

    init(id: UUID = UUID(), createdAt: Date = Date(), filename: String, filePath: String, duration: TimeInterval) {
        self.id = id
        self.createdAt = createdAt
        self.filename = filename
        self.filePath = filePath
        self.duration = duration
    }
}

final class HistoryStore: ObservableObject {
    static let shared = HistoryStore()

    @Published private(set) var entries: [CaptureEntry] = []
    @Published private(set) var ocrEntries: [OCREntry] = []
    @Published private(set) var audioEntries: [AudioEntry] = []

    private let maxEntries = 100
    private let historyDir: URL
    private let indexURL: URL
    private let ocrIndexURL: URL
    private let audioIndexURL: URL

    private static let thumbnailByteBudget = 64 * 1024 * 1024

    private let thumbnailCache: NSCache<NSString, NSImage> = {
        let c = NSCache<NSString, NSImage>()
        c.countLimit = 120
        c.totalCostLimit = HistoryStore.thumbnailByteBudget
        return c
    }()

    init() {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        let root = appSupport.appendingPathComponent("ShotX", isDirectory: true)
        historyDir = root.appendingPathComponent("History", isDirectory: true)
        indexURL = root.appendingPathComponent("history.json")
        ocrIndexURL = root.appendingPathComponent("ocr_history.json")
        audioIndexURL = root.appendingPathComponent("audio_history.json")
        try? FileManager.default.createDirectory(at: historyDir, withIntermediateDirectories: true)
        load()
    }

    private func load() {
        if let data = try? Data(contentsOf: indexURL),
           let decoded = try? JSONDecoder().decode([CaptureEntry].self, from: data) {
            entries = decoded
        }
        if let data = try? Data(contentsOf: ocrIndexURL),
           let decoded = try? JSONDecoder().decode([OCREntry].self, from: data) {
            ocrEntries = decoded
        }
        if let data = try? Data(contentsOf: audioIndexURL),
           let decoded = try? JSONDecoder().decode([AudioEntry].self, from: data) {
            audioEntries = decoded
        }
    }

    private func save() {
        if let data = try? JSONEncoder().encode(entries) {
            try? data.write(to: indexURL, options: .atomic)
        }
    }

    private func saveOCR() {
        if let data = try? JSONEncoder().encode(ocrEntries) {
            try? data.write(to: ocrIndexURL, options: .atomic)
        }
    }

    private func saveAudio() {
        if let data = try? JSONEncoder().encode(audioEntries) {
            try? data.write(to: audioIndexURL, options: .atomic)
        }
    }

    func addOCR(_ text: String) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        let entry = OCREntry(text: trimmed)
        ocrEntries.insert(entry, at: 0)
        if ocrEntries.count > maxEntries {
            ocrEntries = Array(ocrEntries.prefix(maxEntries))
        }
        saveOCR()
    }

    func removeOCR(_ entry: OCREntry) {
        ocrEntries.removeAll { $0.id == entry.id }
        saveOCR()
    }

    func clearOCR() {
        ocrEntries.removeAll()
        saveOCR()
    }

    func addAudio(url: URL, duration: TimeInterval) {
        let entry = AudioEntry(
            filename: url.lastPathComponent,
            filePath: url.path,
            duration: duration
        )
        audioEntries.insert(entry, at: 0)
        if audioEntries.count > maxEntries {
            audioEntries = Array(audioEntries.prefix(maxEntries))
        }
        saveAudio()
    }

    func removeAudio(_ entry: AudioEntry) {
        audioEntries.removeAll { $0.id == entry.id }
        saveAudio()
    }

    func clearAudio() {
        audioEntries.removeAll()
        saveAudio()
    }

    @discardableResult
    func add(_ image: NSImage) -> CaptureEntry? {
        guard let png = ImageSaver.pngData(from: image) else { return nil }
        return add(png: png, size: image.size)
    }

    /// Stores PNG bytes that were already encoded elsewhere, so the capture flow
    /// doesn't rasterize the same screenshot twice.
    @discardableResult
    func add(png: Data, size: CGSize) -> CaptureEntry? {
        let entry = CaptureEntry(
            filename: "\(UUID().uuidString).png",
            createdAt: Date(),
            width: size.width,
            height: size.height,
            kind: .image
        )
        let url = historyDir.appendingPathComponent(entry.filename)
        do { try png.write(to: url) } catch { return nil }
        register(entry)
        return entry
    }

    @discardableResult
    func add(videoAt sourceURL: URL, duration: TimeInterval, dimensions: CGSize) -> CaptureEntry? {
        let entry = CaptureEntry(
            filename: "\(UUID().uuidString).mp4",
            createdAt: Date(),
            width: dimensions.width,
            height: dimensions.height,
            kind: .video,
            duration: duration
        )
        return moveIntoHistory(sourceURL: sourceURL, entry: entry)
    }

    @discardableResult
    func add(gifAt sourceURL: URL, duration: TimeInterval, dimensions: CGSize) -> CaptureEntry? {
        let entry = CaptureEntry(
            filename: "\(UUID().uuidString).gif",
            createdAt: Date(),
            width: dimensions.width,
            height: dimensions.height,
            kind: .gif,
            duration: duration
        )
        return moveIntoHistory(sourceURL: sourceURL, entry: entry)
    }

    private func moveIntoHistory(sourceURL: URL, entry: CaptureEntry) -> CaptureEntry? {
        let dest = historyDir.appendingPathComponent(entry.filename)
        do {
            if FileManager.default.fileExists(atPath: dest.path) {
                try FileManager.default.removeItem(at: dest)
            }
            try FileManager.default.moveItem(at: sourceURL, to: dest)
        } catch {
            // Fall back to copy if move fails (different volumes)
            do { try FileManager.default.copyItem(at: sourceURL, to: dest) }
            catch { return nil }
        }
        register(entry)
        return entry
    }

    private func register(_ entry: CaptureEntry) {
        entries.insert(entry, at: 0)
        if entries.count > maxEntries {
            for old in entries.suffix(from: maxEntries) {
                try? FileManager.default.removeItem(at: fileURL(for: old))
            }
            entries = Array(entries.prefix(maxEntries))
        }
        save()
    }

    func fileURL(for entry: CaptureEntry) -> URL {
        historyDir.appendingPathComponent(entry.filename)
    }

    func image(for entry: CaptureEntry) -> NSImage? {
        let url = fileURL(for: entry)
        switch entry.kind {
        case .image, .gif:
            return NSImage(contentsOf: url)
        case .video:
            return VideoThumbnail.firstFrame(of: url)
        }
    }

    /// Returns a small, decoded, cached thumbnail suitable for the history grid.
    /// Decodes on a background queue; first call per entry hits disk, subsequent
    /// calls return from the in-memory cache. `@MainActor` so NSImage? doesn't
    /// have to cross an actor boundary when returned to the SwiftUI caller.
    @MainActor
    func thumbnail(for entry: CaptureEntry, maxDim: CGFloat = 300) async -> NSImage? {
        let key = entry.id.uuidString as NSString
        if let cached = thumbnailCache.object(forKey: key) {
            return cached
        }

        let url = fileURL(for: entry)
        let kind = entry.kind

        let image: NSImage? = await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                let img: NSImage?
                switch kind {
                case .image, .gif:
                    img = HistoryStore.downsampledImage(at: url, maxDim: maxDim)
                case .video:
                    img = HistoryStore.downsampledVideoFrame(at: url, maxDim: maxDim)
                }
                continuation.resume(returning: img)
            }
        }

        if let image = image {
            thumbnailCache.setObject(image, forKey: key, cost: HistoryStore.byteCost(of: image))
        }
        return image
    }

    private static func downsampledImage(at url: URL, maxDim: CGFloat) -> NSImage? {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        let opts: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceThumbnailMaxPixelSize: maxDim * 2, // 2x for Retina
            kCGImageSourceShouldCacheImmediately: true
        ]
        guard let cg = CGImageSourceCreateThumbnailAtIndex(src, 0, opts as CFDictionary) else { return nil }
        let pointSize = NSSize(width: CGFloat(cg.width) / 2, height: CGFloat(cg.height) / 2)
        return NSImage(cgImage: cg, size: pointSize)
    }

    /// Decoded size in bytes, used as the NSCache eviction cost.
    private static func byteCost(of image: NSImage) -> Int {
        guard let rep = image.representations.first else { return 1 }
        return max(1, rep.pixelsWide * rep.pixelsHigh * 4)
    }

    private static func downsampledVideoFrame(at url: URL, maxDim: CGFloat) -> NSImage? {
        let asset = AVURLAsset(url: url)
        let gen = AVAssetImageGenerator(asset: asset)
        gen.appliesPreferredTrackTransform = true
        gen.maximumSize = CGSize(width: maxDim * 2, height: maxDim * 2)
        gen.requestedTimeToleranceBefore = .zero
        gen.requestedTimeToleranceAfter = .zero
        let time = CMTime(seconds: 0.1, preferredTimescale: 600)
        guard let cg = try? gen.copyCGImage(at: time, actualTime: nil) else { return nil }
        let pointSize = NSSize(width: CGFloat(cg.width) / 2, height: CGFloat(cg.height) / 2)
        return NSImage(cgImage: cg, size: pointSize)
    }

    func remove(_ entry: CaptureEntry) {
        try? FileManager.default.removeItem(at: fileURL(for: entry))
        thumbnailCache.removeObject(forKey: entry.id.uuidString as NSString)
        entries.removeAll { $0.id == entry.id }
        save()
    }

    func clear() {
        for e in entries {
            try? FileManager.default.removeItem(at: fileURL(for: e))
        }
        thumbnailCache.removeAllObjects()
        entries.removeAll()
        save()
    }
}
