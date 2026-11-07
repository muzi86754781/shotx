import Cocoa
import CoreGraphics

enum ColorFormat: Int, CaseIterable {
    case hex = 0
    case rgb = 1
    case hsl = 2
    case hexa = 3

    var name: String {
        switch self {
        case .hex: return "HEX"
        case .rgb: return "RGB"
        case .hsl: return "HSL"
        case .hexa: return "HEXA"
        }
    }

    func format(r: Int, g: Int, b: Int, a: Double = 1.0) -> String {
        switch self {
        case .hex:
            return String(format: "#%02X%02X%02X", r, g, b)
        case .rgb:
            return "rgb(\(r), \(g), \(b))"
        case .hsl:
            let (h, s, l) = rgbToHsl(r: r, g: g, b: b)
            return "hsl(\(Int(h.rounded())), \(Int((s * 100).rounded()))%, \(Int((l * 100).rounded()))%)"
        case .hexa:
            return String(format: "#%02X%02X%02X%02X", r, g, b, Int((a * 255).rounded()))
        }
    }

    private func rgbToHsl(r: Int, g: Int, b: Int) -> (Double, Double, Double) {
        let rf = Double(r) / 255.0
        let gf = Double(g) / 255.0
        let bf = Double(b) / 255.0

        let maxVal = max(rf, gf, bf)
        let minVal = min(rf, gf, bf)
        let delta = maxVal - minVal

        var h: Double = 0.0
        var s: Double = 0.0
        let l: Double = (maxVal + minVal) / 2.0

        if delta > 0.00001 {
            s = l > 0.5 ? delta / (2.0 - maxVal - minVal) : delta / (maxVal + minVal)
            if maxVal == rf {
                h = ((gf - bf) / delta) + (gf < bf ? 6.0 : 0.0)
            } else if maxVal == gf {
                h = ((bf - rf) / delta) + 2.0
            } else {
                h = ((rf - gf) / delta) + 4.0
            }
            h *= 60.0
        }

        return (h, s, l)
    }
}

enum ColorPicker {
    @MainActor
    static func pick() {
        ColorPickerOverlayController.shared.show()
    }
}

final class ColorPickerWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

@MainActor
final class ColorPickerOverlayController {
    static let shared = ColorPickerOverlayController()

    private var windows: [ColorPickerWindow] = []
    private var overlayViews: [ColorPickerOverlayView] = []
    private var activeFormat: ColorFormat = .hex
    private var keyMonitor: Any?

    func show() {
        dismiss()

        for screen in NSScreen.screens {
            guard let cgImage = CGWindowListCreateImage(
                screen.frame,
                .optionOnScreenOnly,
                kCGNullWindowID,
                [.bestResolution]
            ) else { continue }

            let overlayView = ColorPickerOverlayView(
                frame: NSRect(origin: .zero, size: screen.frame.size),
                screen: screen,
                screenshot: cgImage,
                initialFormat: activeFormat,
                onColorSelected: { [weak self] formattedColor, color in
                    self?.copyColorAndFinish(formattedColor: formattedColor, color: color)
                },
                onCancel: { [weak self] in
                    self?.dismiss()
                }
            )

            let win = ColorPickerWindow(
                contentRect: screen.frame,
                styleMask: [.borderless],
                backing: .buffered,
                defer: false
            )
            win.level = .floating
            win.backgroundColor = .clear
            win.isOpaque = false
            win.hasShadow = false
            win.ignoresMouseEvents = false
            win.acceptsMouseMovedEvents = true
            win.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
            win.contentView = overlayView

            CaptureExclusions.register(win)
            win.orderFrontRegardless()
            windows.append(win)
            overlayViews.append(overlayView)
        }

        NSApp.activate(ignoringOtherApps: true)
        if let mainWin = windows.first(where: { $0.screen == NSScreen.main }) ?? windows.first {
            mainWin.makeKeyAndOrderFront(nil)
            if let view = mainWin.contentView as? ColorPickerOverlayView {
                mainWin.makeFirstResponder(view)
            }
        }

        // 注册本地键盘全局监听，确保 Tab/C/Space/ESC 零延迟精准拦截切换格式
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self = self, !self.windows.isEmpty else { return event }
            switch event.keyCode {
            case 53: // ESC
                self.dismiss()
                return nil
            case 48, 8, 49: // Tab (48), C (8), Space (49)
                self.cycleFormat()
                return nil
            case 36, 76: // Enter / Return
                self.confirmActiveColor()
                return nil
            default:
                return event
            }
        }
    }

    func cycleFormat() {
        let all = ColorFormat.allCases
        let nextIndex = (activeFormat.rawValue + 1) % all.count
        activeFormat = all[nextIndex]
        for view in overlayViews {
            view.updateFormat(activeFormat)
        }
    }

    private func confirmActiveColor() {
        for view in overlayViews {
            if view.mouseInsideView {
                view.confirmCurrentColor()
                return
            }
        }
        overlayViews.first?.confirmCurrentColor()
    }

    private func copyColorAndFinish(formattedColor: String, color: NSColor) {
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(formattedColor, forType: .string)

        dismiss()

        SoundEffect.shared.playShutter()
        ToastController.shared.show(
            message: "已复制 \(formattedColor)",
            icon: "eyedropper.halffull",
            tint: color
        )
    }

    func dismiss() {
        if let monitor = keyMonitor {
            NSEvent.removeMonitor(monitor)
            keyMonitor = nil
        }
        for w in windows {
            CaptureExclusions.unregister(w)
            w.orderOut(nil)
            w.contentView = nil
            w.close()
        }
        windows.removeAll()
        overlayViews.removeAll()
    }
}

private final class ColorPickerOverlayView: NSView {
    private let targetScreen: NSScreen
    private let screenshot: CGImage
    private let onColorSelected: (String, NSColor) -> Void
    private let onCancel: () -> Void

    private var currentMousePoint: NSPoint = .zero
    private var activeFormat: ColorFormat = .hex
    private var trackingArea: NSTrackingArea?
    var mouseInsideView: Bool = false

    init(
        frame: NSRect,
        screen: NSScreen,
        screenshot: CGImage,
        initialFormat: ColorFormat,
        onColorSelected: @escaping (String, NSColor) -> Void,
        onCancel: @escaping () -> Void
    ) {
        self.targetScreen = screen
        self.screenshot = screenshot
        self.activeFormat = initialFormat
        self.onColorSelected = onColorSelected
        self.onCancel = onCancel
        super.init(frame: frame)
    }

    required init?(coder: NSCoder) { fatalError() }

    override var acceptsFirstResponder: Bool { true }

    func updateFormat(_ format: ColorFormat) {
        self.activeFormat = format
        needsDisplay = true
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let ta = trackingArea { removeTrackingArea(ta) }
        let ta = NSTrackingArea(
            rect: bounds,
            options: [.mouseMoved, .mouseEnteredAndExited, .activeAlways, .inVisibleRect],
            owner: self,
            userInfo: nil
        )
        addTrackingArea(ta)
        self.trackingArea = ta
    }

    override func mouseEntered(with event: NSEvent) {
        mouseInsideView = true
    }

    override func mouseExited(with event: NSEvent) {
        mouseInsideView = false
    }

    override func mouseMoved(with event: NSEvent) {
        currentMousePoint = convert(event.locationInWindow, from: nil)
        mouseInsideView = true
        needsDisplay = true
    }

    override func mouseDown(with event: NSEvent) {
        confirmCurrentColor()
    }

    override func rightMouseDown(with event: NSEvent) {
        onCancel()
    }

    func confirmCurrentColor() {
        let (r, g, b, a, color) = getPixelColor(at: currentMousePoint)
        let formatted = activeFormat.format(r: r, g: g, b: b, a: a)
        onColorSelected(formatted, color)
    }

    private func getPixelColor(at point: NSPoint) -> (Int, Int, Int, Double, NSColor) {
        let scaleX = Double(screenshot.width) / Double(bounds.width)
        let scaleY = Double(screenshot.height) / Double(bounds.height)

        let px = max(0, min(screenshot.width - 1, Int(point.x * scaleX)))
        // CoreGraphics CGImage coordinate has (0,0) at top-left
        let py = max(0, min(screenshot.height - 1, Int((bounds.height - point.y) * scaleY)))

        guard let data = screenshot.dataProvider?.data,
              let ptr = CFDataGetBytePtr(data) else {
            return (0, 0, 0, 1.0, .black)
        }

        let bpr = screenshot.bytesPerRow
        let bpp = screenshot.bitsPerPixel / 8
        let offset = py * bpr + px * bpp

        var r = 0, g = 0, b = 0, a = 255
        if bpp >= 4 {
            let b0 = Int(ptr[offset])
            let b1 = Int(ptr[offset + 1])
            let b2 = Int(ptr[offset + 2])
            let b3 = Int(ptr[offset + 3])

            let info = screenshot.bitmapInfo
            let byteOrder = info.contains(.byteOrder32Little)
            if byteOrder {
                b = b0; g = b1; r = b2; a = b3
            } else {
                r = b0; g = b1; b = b2; a = b3
            }
        } else if bpp == 3 {
            r = Int(ptr[offset])
            g = Int(ptr[offset + 1])
            b = Int(ptr[offset + 2])
        }

        let alpha = Double(a) / 255.0
        let color = NSColor(
            srgbRed: CGFloat(r) / 255.0,
            green: CGFloat(g) / 255.0,
            blue: CGFloat(b) / 255.0,
            alpha: CGFloat(alpha)
        )
        return (r, g, b, alpha, color)
    }

    override func draw(_ dirtyRect: NSRect) {
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }

        let p = currentMousePoint

        // 1. 全屏十字瞄准线 (柔和半透明虚线)
        ctx.saveGState()
        ctx.setStrokeColor(NSColor.white.withAlphaComponent(0.45).cgColor)
        ctx.setLineWidth(1.0)
        let dashes: [CGFloat] = [4, 4]
        ctx.setLineDash(phase: 0, lengths: dashes)
        ctx.strokeLineSegments(between: [
            CGPoint(x: 0, y: p.y), CGPoint(x: bounds.width, y: p.y),
            CGPoint(x: p.x, y: 0), CGPoint(x: p.x, y: bounds.height)
        ])
        ctx.restoreGState()

        // 2. 放大镜尺寸与坐标计算
        let loupeSize: CGFloat = 138
        var lx = p.x + 22
        var ly = p.y - loupeSize - 50 // 默认置于鼠标右下方
        if lx + loupeSize + 10 > bounds.width { lx = p.x - loupeSize - 22 }
        if ly < 10 { ly = p.y + 22 }

        // 3. 放大采样网格 (9x9)
        let gridSize = 9
        let cellSize = loupeSize / CGFloat(gridSize)
        let half = gridSize / 2

        let (curR, curG, curB, curA, curColor) = getPixelColor(at: p)

        let loupeRect = CGRect(x: lx, y: ly + 46, width: loupeSize, height: loupeSize)
        let clipPath = CGPath(roundedRect: loupeRect, cornerWidth: 8, cornerHeight: 8, transform: nil)

        ctx.saveGState()
        ctx.addPath(clipPath)
        ctx.clip()

        for gy in 0..<gridSize {
            for gx in 0..<gridSize {
                let samplePt = NSPoint(
                    x: p.x + CGFloat(gx - half),
                    y: p.y + CGFloat(half - gy)
                )
                let (sr, sg, sb, _, _) = getPixelColor(at: samplePt)
                let cellRect = CGRect(
                    x: lx + CGFloat(gx) * cellSize,
                    y: ly + 46 + CGFloat(gy) * cellSize,
                    width: cellSize,
                    height: cellSize
                )
                ctx.setFillColor(NSColor(srgbRed: CGFloat(sr)/255.0, green: CGFloat(sg)/255.0, blue: CGFloat(sb)/255.0, alpha: 1.0).cgColor)
                ctx.fill(cellRect)

                ctx.setStrokeColor(NSColor.black.withAlphaComponent(0.12).cgColor)
                ctx.setLineWidth(0.5)
                ctx.stroke(cellRect)
            }
        }

        // 中心瞄准红框 (准星)
        let centerRect = CGRect(
            x: lx + CGFloat(half) * cellSize,
            y: ly + 46 + CGFloat(half) * cellSize,
            width: cellSize,
            height: cellSize
        )
        ctx.setStrokeColor(NSColor.systemRed.cgColor)
        ctx.setLineWidth(1.8)
        ctx.stroke(centerRect)

        ctx.restoreGState()

        // 放大镜外轮廓描边
        ctx.saveGState()
        ctx.addPath(clipPath)
        ctx.setStrokeColor(NSColor.white.cgColor)
        ctx.setLineWidth(2.0)
        ctx.strokePath()
        ctx.restoreGState()

        // 4. 底部颜色信息与格式指示卡片 (深色卡片)
        let infoRect = CGRect(x: lx, y: ly, width: loupeSize, height: 42)
        let infoPath = CGPath(roundedRect: infoRect, cornerWidth: 6, cornerHeight: 6, transform: nil)

        ctx.saveGState()
        ctx.addPath(infoPath)
        ctx.setFillColor(NSColor(red: 25/255.0, green: 27/255.0, blue: 32/255.0, alpha: 0.94).cgColor)
        ctx.fillPath()

        ctx.addPath(infoPath)
        ctx.setStrokeColor(NSColor.white.withAlphaComponent(0.2).cgColor)
        ctx.setLineWidth(1.0)
        ctx.strokePath()
        ctx.restoreGState()

        // 颜色方块预览
        let swatchRect = CGRect(x: lx + 7, y: ly + 21, width: 14, height: 14)
        ctx.setFillColor(curColor.cgColor)
        ctx.fill(swatchRect)
        ctx.setStrokeColor(NSColor.white.cgColor)
        ctx.setLineWidth(1.0)
        ctx.stroke(swatchRect)

        // 格式化颜色代码文字
        let formattedStr = activeFormat.format(r: curR, g: curG, b: curB, a: curA)
        let textAttrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedSystemFont(ofSize: 10.5, weight: .bold),
            .foregroundColor: NSColor.white
        ]
        let attrString = NSAttributedString(string: formattedStr, attributes: textAttrs)
        attrString.draw(at: NSPoint(x: lx + 26, y: ly + 20))

        // 底部快捷键切换提示
        let tipAttrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 8.5, weight: .medium),
            .foregroundColor: NSColor(white: 0.72, alpha: 1.0)
        ]
        let tipString = NSAttributedString(string: "[Tab/C/空格] 切换 · 点击复制", attributes: tipAttrs)
        tipString.draw(at: NSPoint(x: lx + 7, y: ly + 5))
    }
}
