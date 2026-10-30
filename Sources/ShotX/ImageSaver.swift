import Cocoa

/// PNG + TIFF bytes produced from a single rasterization of a capture.
struct EncodedImage {
    let png: Data
    let tiff: Data?
}

enum ImageSaver {
    /// Rasterizes an NSImage exactly once.
    ///
    /// The obvious route — `image.tiffRepresentation` → `NSBitmapImageRep(data:)`
    /// — materializes a full uncompressed TIFF and then re-parses it into a
    /// second full-size buffer. For a 5K capture that's ~120 MB of transient
    /// allocation *per call*, and the capture path used to call it three times
    /// (history PNG, clipboard PNG, clipboard TIFF). Wrapping the backing
    /// CGImage skips both copies.
    static func bitmapRep(from image: NSImage) -> NSBitmapImageRep? {
        var rect = NSRect(origin: .zero, size: image.size)
        if let cg = image.cgImage(forProposedRect: &rect, context: nil, hints: nil) {
            return bitmapRep(from: cg, pointSize: image.size)
        }
        // Images with no CGImage backing (vector art opened via "Open…").
        guard let tiff = image.tiffRepresentation else { return nil }
        return NSBitmapImageRep(data: tiff)
    }

    static func bitmapRep(from cgImage: CGImage, pointSize: NSSize) -> NSBitmapImageRep {
        let rep = NSBitmapImageRep(cgImage: cgImage)
        // Keep the point size so the PNG carries the right DPI for Retina captures.
        if pointSize.width > 0, pointSize.height > 0 {
            rep.size = pointSize
        }
        return rep
    }

    static func pngData(from image: NSImage) -> Data? {
        autoreleasepool {
            bitmapRep(from: image)?.representation(using: .png, properties: [:])
        }
    }

    static func jpegData(from image: NSImage) -> Data? {
        autoreleasepool {
            bitmapRep(from: image)?.representation(using: .jpeg, properties: [.compressionFactor: 0.92])
        }
    }

    static func heicData(from image: NSImage) -> Data? {
        autoreleasepool {
            var rect = NSRect(origin: .zero, size: image.size)
            guard let cg = image.cgImage(forProposedRect: &rect, context: nil, hints: nil) else {
                return pngData(from: image)
            }
            let data = NSMutableData()
            guard let dest = CGImageDestinationCreateWithData(data as CFMutableData, "public.heic" as CFString, 1, nil) else {
                return pngData(from: image)
            }
            CGImageDestinationAddImage(dest, cg, nil)
            guard CGImageDestinationFinalize(dest) else { return pngData(from: image) }
            return data as Data
        }
    }

    /// PNG and TIFF from one rasterization.
    static func encode(_ image: NSImage) -> EncodedImage? {
        autoreleasepool {
            guard let rep = bitmapRep(from: image),
                  let png = rep.representation(using: .png, properties: [:])
            else { return nil }
            return EncodedImage(png: png, tiff: rep.tiffRepresentation)
        }
    }

    /// Encodes off the main thread and calls back on it.
    ///
    /// PNG compression of a full-resolution capture takes long enough to be
    /// visible as a hang; the capture flow now shows its popup first and lets
    /// the bytes catch up.
    static func encodeInBackground(_ image: NSImage, completion: @escaping (EncodedImage?) -> Void) {
        // Pull the CGImage out here, on the caller's thread. NSImage isn't
        // thread-safe; CGImage is immutable and safe to hand across.
        var rect = NSRect(origin: .zero, size: image.size)
        let cg = image.cgImage(forProposedRect: &rect, context: nil, hints: nil)
        let pointSize = image.size

        guard let cg = cg else {
            // Rare fallback path (no CGImage backing) — encode inline.
            let encoded = encode(image)
            DispatchQueue.main.async { completion(encoded) }
            return
        }

        DispatchQueue.global(qos: .userInitiated).async {
            let encoded: EncodedImage? = autoreleasepool {
                let rep = bitmapRep(from: cg, pointSize: pointSize)
                guard let png = rep.representation(using: .png, properties: [:]) else { return nil }
                return EncodedImage(png: png, tiff: rep.tiffRepresentation)
            }
            DispatchQueue.main.async { completion(encoded) }
        }
    }

    @discardableResult
    static func saveToDesktop(_ image: NSImage) -> URL? {
        saveToPreferencesFolder(image)
    }

    @discardableResult
    static func saveToDesktop(pngData: Data) -> URL? {
        saveToPreferencesFolder(pngData: pngData)
    }

    @discardableResult
    static func saveToPreferencesFolder(_ image: NSImage) -> URL? {
        let fmt = PreferencesStore.shared.screenshotFormat
        let data: Data?
        switch fmt {
        case .png:
            data = pngData(from: image)
        case .jpg:
            data = jpegData(from: image)
        case .heic:
            data = heicData(from: image)
        }
        guard let fileData = data else { return nil }
        return saveToPreferencesFolder(data: fileData, fileExtension: fmt.fileExtension)
    }

    @discardableResult
    static func saveToPreferencesFolder(pngData: Data) -> URL? {
        saveToPreferencesFolder(data: pngData, fileExtension: "png")
    }

    @discardableResult
    static func saveToPreferencesFolder(data: Data, fileExtension: String) -> URL? {
        let folder = PreferencesStore.shared.saveDirectoryURL
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd 'at' HH.mm.ss"
        let url = folder.appendingPathComponent("ShotX \(formatter.string(from: Date())).\(fileExtension)")
        do {
            try data.write(to: url)
            ToastController.shared.show(
                message: "已保存至: \(url.lastPathComponent)",
                icon: "checkmark.circle.fill",
                tint: .systemGreen,
                position: .center
            )
            return url
        } catch {
            ToastController.shared.show(
                message: "保存失败: \(error.localizedDescription)",
                icon: "exclamationmark.triangle.fill",
                tint: .systemRed,
                position: .center
            )
            return nil
        }
    }

    static func copyToClipboard(_ image: NSImage) {
        guard let encoded = encode(image) else { return }
        copyToClipboard(encoded)
    }

    /// Expose both PNG and TIFF. Writing the NSImage alone only provides
    /// TIFF, which many chat apps, browsers, and Electron tools silently
    /// reject when pasting — they look for PNG. Offering both makes the
    /// screenshot pasteable everywhere.
    static func copyToClipboard(_ encoded: EncodedImage) {
        let pb = NSPasteboard.general
        pb.clearContents()
        let item = NSPasteboardItem()
        item.setData(encoded.png, forType: .png)
        if let tiff = encoded.tiff {
            item.setData(tiff, forType: .tiff)
        }
        pb.writeObjects([item])
    }

    static func copyTextToClipboard(_ text: String) {
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(text, forType: .string)
    }
}
