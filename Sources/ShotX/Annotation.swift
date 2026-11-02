import Cocoa
import Combine
import SwiftUI

enum AnnotationTool: String, CaseIterable, Identifiable {
    case rectangle, oval, line, arrow, pen, mosaic, text, step
    var id: String { rawValue }
    var symbolName: String {
        switch self {
        case .rectangle: return "rectangle"
        case .oval: return "circle"
        case .line: return "line.diagonal"
        case .arrow: return "arrow.up.right"
        case .pen: return "pencil.tip"
        case .mosaic: return "checkerboard.rectangle"
        case .text: return "textformat"
        case .step: return "1.circle.fill"
        }
    }
    var label: String {
        switch self {
        case .rectangle: return "矩形"
        case .oval: return "圆形"
        case .line: return "直线"
        case .arrow: return "箭头"
        case .pen: return "画笔"
        case .mosaic: return "马赛克"
        case .text: return "文本"
        case .step: return "序号"
        }
    }
}

struct Annotation: Identifiable {
    enum Shape {
        case rectangle(CGRect)
        case oval(CGRect)
        case line(from: CGPoint, to: CGPoint)
        case arrow(from: CGPoint, to: CGPoint)
        case pen(points: [CGPoint])
        case mosaic(CGRect)
        case text(origin: CGPoint, string: String, fontSize: CGFloat)
        case step(center: CGPoint, number: Int)
    }
    let id = UUID()
    var shape: Shape
    var color: NSColor
    var thickness: CGFloat
    var isFilled: Bool = false
    var isRounded: Bool = true
    var isCurved: Bool = false
    var isHighlighter: Bool = false
    var isMosaicBlur: Bool = false
    var textStyle: Int = 1 // 0: none, 1: outline, 2: background
    var secondaryColor: NSColor = .white
    var stepIsFilled: Bool = true
    var opacity: CGFloat = 1.0

    func translatedBy(dx: CGFloat, dy: CGFloat) -> Annotation {
        var copy = self
        switch copy.shape {
        case .rectangle(let r):
            copy.shape = .rectangle(r.offsetBy(dx: dx, dy: dy))
        case .oval(let r):
            copy.shape = .oval(r.offsetBy(dx: dx, dy: dy))
        case .line(let from, let to):
            copy.shape = .line(from: CGPoint(x: from.x + dx, y: from.y + dy),
                               to: CGPoint(x: to.x + dx, y: to.y + dy))
        case .arrow(let from, let to):
            copy.shape = .arrow(from: CGPoint(x: from.x + dx, y: from.y + dy),
                                to: CGPoint(x: to.x + dx, y: to.y + dy))
        case .pen(let points):
            copy.shape = .pen(points: points.map { CGPoint(x: $0.x + dx, y: $0.y + dy) })
        case .mosaic(let r):
            copy.shape = .mosaic(r.offsetBy(dx: dx, dy: dy))
        case .text(let origin, let string, let fontSize):
            copy.shape = .text(origin: CGPoint(x: origin.x + dx, y: origin.y + dy),
                               string: string, fontSize: fontSize)
        case .step(let center, let number):
            copy.shape = .step(center: CGPoint(x: center.x + dx, y: center.y + dy),
                               number: number)
        }
        return copy
    }
}

enum AnnotationRenderer {
    static func draw(
        _ ann: Annotation,
        snapshotImage: CGImage? = nil,
        viewBounds: CGRect = .zero,
        baseImage: NSImage? = nil
    ) {
        let drawColor = ann.color.withAlphaComponent(ann.color.alphaComponent * ann.opacity)
        switch ann.shape {
        case .arrow(let from, let to):
            drawArrow(from: from, to: to, thickness: ann.thickness, color: drawColor, isCurved: ann.isCurved)
        case .rectangle(let r):
            drawRectangle(r, thickness: ann.thickness, color: drawColor, isFilled: ann.isFilled, isRounded: ann.isRounded)
        case .oval(let r):
            drawOval(r, thickness: ann.thickness, color: drawColor, isFilled: ann.isFilled)
        case .line(let from, let to):
            drawLine(from: from, to: to, thickness: ann.thickness, color: drawColor)
        case .pen(let points):
            drawPen(points: points, thickness: ann.thickness, color: drawColor, isHighlighter: ann.isHighlighter)
        case .mosaic(let r):
            drawMosaic(r, snapshotImage: snapshotImage, viewBounds: viewBounds, baseImage: baseImage, isBlur: ann.isMosaicBlur)
        case .text(let origin, let string, let fontSize):
            drawText(string, at: origin, fontSize: fontSize, color: drawColor, style: ann.textStyle, secondaryColor: ann.secondaryColor)
        case .step(let center, let number):
            drawStep(at: center, number: number, color: drawColor, isFilled: ann.stepIsFilled)
        }
    }

    private static func drawArrow(from: CGPoint, to: CGPoint, thickness: CGFloat, color: NSColor, isCurved: Bool = false) {
        let dx = to.x - from.x
        let dy = to.y - from.y
        let length = hypot(dx, dy)
        guard length > 4 else { return }

        if isCurved {
            // Draw a curved Bezier arrow
            let midX = (from.x + to.x) / 2
            let midY = (from.y + to.y) / 2
            let normX = -dy / length
            let normY = dx / length
            let curveOffset = min(40, max(-40, length * 0.25))
            let ctrlPt = CGPoint(x: midX + normX * curveOffset, y: midY + normY * curveOffset)

            let path = NSBezierPath()
            path.move(to: from)
            path.curve(to: to, controlPoint1: ctrlPt, controlPoint2: ctrlPt)
            path.lineWidth = max(3, thickness)
            path.lineCapStyle = .round

            // Arrow head
            let headAngle = atan2(to.y - ctrlPt.y, to.x - ctrlPt.x)
            let headLen = max(14, thickness * 4)
            let a1 = headAngle + .pi * 0.82
            let a2 = headAngle - .pi * 0.82
            let p1 = CGPoint(x: to.x + cos(a1) * headLen, y: to.y + sin(a1) * headLen)
            let p2 = CGPoint(x: to.x + cos(a2) * headLen, y: to.y + sin(a2) * headLen)

            let headPath = NSBezierPath()
            headPath.move(to: p1)
            headPath.line(to: to)
            headPath.line(to: p2)
            headPath.close()

            NSGraphicsContext.saveGraphicsState()
            let shadow = NSShadow()
            shadow.shadowColor = NSColor.black.withAlphaComponent(0.3)
            shadow.shadowBlurRadius = 3
            shadow.shadowOffset = NSSize(width: 0, height: -1)
            shadow.set()
            color.setStroke()
            color.setFill()
            path.stroke()
            headPath.fill()
            NSGraphicsContext.restoreGraphicsState()
            return
        }

        let angle = atan2(dy, dx)
        let shaftWidth = max(4, thickness * 1.6)
        let headLength = max(18, thickness * 5)
        let headWidth = max(22, thickness * 6)

        let effectiveHeadLength = min(headLength, length * 0.65)
        let headRatio = effectiveHeadLength / headLength
        let effectiveHeadWidth = headWidth * headRatio

        let shaftEnd = CGPoint(
            x: to.x - cos(angle) * effectiveHeadLength,
            y: to.y - sin(angle) * effectiveHeadLength
        )
        let px = -sin(angle)
        let py = cos(angle)

        let path = NSBezierPath()
        path.move(to: CGPoint(x: from.x + px * shaftWidth / 2, y: from.y + py * shaftWidth / 2))
        path.line(to: CGPoint(x: shaftEnd.x + px * shaftWidth / 2, y: shaftEnd.y + py * shaftWidth / 2))
        path.line(to: CGPoint(x: shaftEnd.x + px * effectiveHeadWidth / 2, y: shaftEnd.y + py * effectiveHeadWidth / 2))
        path.line(to: to)
        path.line(to: CGPoint(x: shaftEnd.x - px * effectiveHeadWidth / 2, y: shaftEnd.y - py * effectiveHeadWidth / 2))
        path.line(to: CGPoint(x: shaftEnd.x - px * shaftWidth / 2, y: shaftEnd.y - py * shaftWidth / 2))
        path.line(to: CGPoint(x: from.x - px * shaftWidth / 2, y: from.y - py * shaftWidth / 2))
        path.close()

        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.35)
        shadow.shadowBlurRadius = 4
        shadow.shadowOffset = NSSize(width: 0, height: -1)
        shadow.set()
        color.setFill()
        path.fill()
        NSGraphicsContext.restoreGraphicsState()
    }

    private static func drawRectangle(_ rect: CGRect, thickness: CGFloat, color: NSColor, isFilled: Bool = false, isRounded: Bool = true) {
        let path = isRounded ? NSBezierPath(roundedRect: rect, xRadius: 6, yRadius: 6) : NSBezierPath(rect: rect)

        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.25)
        shadow.shadowBlurRadius = 3
        shadow.shadowOffset = NSSize(width: 0, height: -1)
        shadow.set()
        if isFilled {
            color.setFill()
            path.fill()
        } else {
            path.lineWidth = max(1.0, thickness)
            path.lineJoinStyle = .round
            color.setStroke()
            path.stroke()
        }
        NSGraphicsContext.restoreGraphicsState()
    }

    private static func drawOval(_ rect: CGRect, thickness: CGFloat, color: NSColor, isFilled: Bool = false) {
        let path = NSBezierPath(ovalIn: rect)

        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.25)
        shadow.shadowBlurRadius = 3
        shadow.shadowOffset = NSSize(width: 0, height: -1)
        shadow.set()
        if isFilled {
            color.setFill()
            path.fill()
        } else {
            path.lineWidth = max(1.0, thickness)
            color.setStroke()
            path.stroke()
        }
        NSGraphicsContext.restoreGraphicsState()
    }

    private static func drawLine(from: CGPoint, to: CGPoint, thickness: CGFloat, color: NSColor) {
        let path = NSBezierPath()
        path.move(to: from)
        path.line(to: to)
        path.lineWidth = max(1.0, thickness)
        path.lineCapStyle = .round

        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.25)
        shadow.shadowBlurRadius = 3
        shadow.shadowOffset = NSSize(width: 0, height: -1)
        shadow.set()
        color.setStroke()
        path.stroke()
        NSGraphicsContext.restoreGraphicsState()
    }

    private static func drawPen(points: [CGPoint], thickness: CGFloat, color: NSColor, isHighlighter: Bool = false) {
        guard points.count > 1 else {
            if let first = points.first {
                let actualColor = isHighlighter ? color.withAlphaComponent(0.4) : color
                actualColor.setFill()
                let sz = isHighlighter ? max(16, thickness * 2.5) : max(1.0, thickness)
                NSBezierPath(ovalIn: CGRect(x: first.x - sz / 2, y: first.y - sz / 2, width: sz, height: sz)).fill()
            }
            return
        }
        let path = NSBezierPath()
        path.move(to: points[0])
        for pt in points.dropFirst() {
            path.line(to: pt)
        }
        path.lineWidth = isHighlighter ? max(16, thickness * 2.5) : max(1.0, thickness)
        path.lineCapStyle = .round
        path.lineJoinStyle = .round

        NSGraphicsContext.saveGraphicsState()
        if !isHighlighter {
            let shadow = NSShadow()
            shadow.shadowColor = NSColor.black.withAlphaComponent(0.25)
            shadow.shadowBlurRadius = 3
            shadow.shadowOffset = NSSize(width: 0, height: -1)
            shadow.set()
        }
        let actualColor = isHighlighter ? color.withAlphaComponent(0.4) : color
        actualColor.setStroke()
        path.stroke()
        NSGraphicsContext.restoreGraphicsState()
    }

    private static func drawMosaic(
        _ rect: CGRect,
        snapshotImage: CGImage? = nil,
        viewBounds: CGRect = .zero,
        baseImage: NSImage? = nil,
        isBlur: Bool = false
    ) {
        guard rect.width > 2, rect.height > 2 else { return }

        // 获取底层原始图像切片
        var sliceCG: CGImage? = nil
        var scaleFactor: CGFloat = 2.0

        if let snap = snapshotImage, viewBounds.width > 0, viewBounds.height > 0 {
            let scaleX = CGFloat(snap.width) / viewBounds.width
            let scaleY = CGFloat(snap.height) / viewBounds.height
            scaleFactor = scaleX
            let pixelX = rect.minX * scaleX
            let pixelY = (viewBounds.height - rect.maxY) * scaleY
            let pixelW = rect.width * scaleX
            let pixelH = rect.height * scaleY
            let cropRect = CGRect(x: pixelX, y: pixelY, width: pixelW, height: pixelH).intersection(CGRect(x: 0, y: 0, width: snap.width, height: snap.height))
            if cropRect.width > 0 && cropRect.height > 0 {
                sliceCG = snap.cropping(to: cropRect)
            }
        } else if let base = baseImage, let fullCG = base.cgImage(forProposedRect: nil, context: nil, hints: nil) {
            let scaleX = CGFloat(fullCG.width) / base.size.width
            let scaleY = CGFloat(fullCG.height) / base.size.height
            scaleFactor = scaleX
            let pixelX = rect.minX * scaleX
            let pixelY = (base.size.height - rect.maxY) * scaleY
            let pixelW = rect.width * scaleX
            let pixelH = rect.height * scaleY
            let cropRect = CGRect(x: pixelX, y: pixelY, width: pixelW, height: pixelH).intersection(CGRect(x: 0, y: 0, width: fullCG.width, height: fullCG.height))
            if cropRect.width > 0 && cropRect.height > 0 {
                sliceCG = fullCG.cropping(to: cropRect)
            }
        }

        NSGraphicsContext.saveGraphicsState()
        let clipPath = NSBezierPath(rect: rect)
        clipPath.addClip()

        if let slice = sliceCG {
            let ciImage = CIImage(cgImage: slice)
            if isBlur {
                // 毛玻璃模式：精致高斯模糊 + 柔和磨砂质感
                let clamped = ciImage.clampedToExtent()
                if let blurFilter = CIFilter(name: "CIGaussianBlur") {
                    blurFilter.setValue(clamped, forKey: kCIInputImageKey)
                    blurFilter.setValue(16.0 * scaleFactor, forKey: kCIInputRadiusKey)
                    if let output = blurFilter.outputImage?.cropped(to: CGRect(x: 0, y: 0, width: slice.width, height: slice.height)) {
                        let rep = NSCIImageRep(ciImage: output)
                        let resultImg = NSImage(size: rect.size)
                        resultImg.addRepresentation(rep)
                        resultImg.draw(in: rect, from: NSRect(origin: .zero, size: rect.size), operation: .sourceOver, fraction: 1.0)

                        // 叠加微弱磨砂高光层
                        NSColor(white: 1.0, alpha: 0.14).setFill()
                        clipPath.fill()

                        NSGraphicsContext.restoreGraphicsState()
                        return
                    }
                }
            } else {
                // 马赛克模式 (参考 iShot 经典方块像素化)
                if let pixFilter = CIFilter(name: "CIPixellate") {
                    pixFilter.setValue(ciImage, forKey: kCIInputImageKey)
                    let blockScale = max(8.0, 13.0 * scaleFactor)
                    pixFilter.setValue(blockScale, forKey: kCIInputScaleKey)
                    pixFilter.setValue(CIVector(x: 0, y: 0), forKey: kCIInputCenterKey)
                    if let output = pixFilter.outputImage {
                        let rep = NSCIImageRep(ciImage: output)
                        let resultImg = NSImage(size: rect.size)
                        resultImg.addRepresentation(rep)
                        resultImg.draw(in: rect, from: NSRect(origin: .zero, size: rect.size), operation: .sourceOver, fraction: 1.0)
                        NSGraphicsContext.restoreGraphicsState()
                        return
                    }
                }
            }
        }

        // 备用兜底
        if isBlur {
            NSColor(white: 0.9, alpha: 0.85).setFill()
            clipPath.fill()
        } else {
            let blockSize: CGFloat = 12
            var isDark = false
            var y = rect.minY
            while y < rect.maxY {
                var x = rect.minX
                var rowDark = isDark
                while x < rect.maxX {
                    let blockRect = CGRect(x: x, y: y, width: blockSize, height: blockSize).intersection(rect)
                    let color = rowDark ? NSColor(white: 0.45, alpha: 0.85) : NSColor(white: 0.75, alpha: 0.85)
                    color.setFill()
                    blockRect.fill()
                    rowDark.toggle()
                    x += blockSize
                }
                isDark.toggle()
                y += blockSize
            }
        }
        NSGraphicsContext.restoreGraphicsState()
    }

    private static func drawText(
        _ string: String,
        at origin: CGPoint,
        fontSize: CGFloat,
        color: NSColor,
        style: Int = 1,
        secondaryColor: NSColor = .white
    ) {
        let font = NSFont.systemFont(ofSize: fontSize, weight: .bold)
        var attrs: [NSAttributedString.Key: Any] = [
            .font: font,
            .foregroundColor: color
        ]

        if style == 1 { // 描边
            attrs[.strokeColor] = secondaryColor
            attrs[.strokeWidth] = -3
        }

        let sz = (string as NSString).size(withAttributes: attrs)
        let textRect = CGRect(origin: origin, size: sz)

        if style == 2 { // 背景框
            NSGraphicsContext.saveGraphicsState()
            secondaryColor.withAlphaComponent(0.92).setFill()
            let bgPath = NSBezierPath(roundedRect: textRect.insetBy(dx: -6, dy: -3), xRadius: 4, yRadius: 4)
            bgPath.fill()
            NSGraphicsContext.restoreGraphicsState()
        }

        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.35)
        shadow.shadowBlurRadius = 3
        shadow.shadowOffset = NSSize(width: 0, height: -1)
        shadow.set()
        string.draw(at: origin, withAttributes: attrs)
        NSGraphicsContext.restoreGraphicsState()
    }

    private static func drawStep(at center: CGPoint, number: Int, color: NSColor, isFilled: Bool = true) {
        let radius: CGFloat = 18
        let rect = NSRect(
            x: center.x - radius, y: center.y - radius,
            width: radius * 2, height: radius * 2
        )

        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.35)
        shadow.shadowBlurRadius = 4
        shadow.shadowOffset = NSSize(width: 0, height: -2)
        shadow.set()

        if isFilled {
            color.setFill()
            NSBezierPath(ovalIn: rect).fill()
            NSGraphicsContext.restoreGraphicsState()

            NSColor.white.setStroke()
            let outline = NSBezierPath(ovalIn: rect.insetBy(dx: 2, dy: 2))
            outline.lineWidth = 2
            outline.stroke()

            let text = "\(number)"
            let attrs: [NSAttributedString.Key: Any] = [
                .font: NSFont.systemFont(ofSize: 16, weight: .heavy),
                .foregroundColor: NSColor.white
            ]
            let size = text.size(withAttributes: attrs)
            text.draw(
                at: NSPoint(x: center.x - size.width / 2, y: center.y - size.height / 2),
                withAttributes: attrs
            )
        } else {
            // 描边模式 (白底 + 彩色描边 + 彩色数字)
            NSColor.white.setFill()
            NSBezierPath(ovalIn: rect).fill()
            NSGraphicsContext.restoreGraphicsState()

            color.setStroke()
            let outline = NSBezierPath(ovalIn: rect.insetBy(dx: 1.5, dy: 1.5))
            outline.lineWidth = 2.5
            outline.stroke()

            let text = "\(number)"
            let attrs: [NSAttributedString.Key: Any] = [
                .font: NSFont.systemFont(ofSize: 16, weight: .heavy),
                .foregroundColor: color
            ]
            let size = text.size(withAttributes: attrs)
            text.draw(
                at: NSPoint(x: center.x - size.width / 2, y: center.y - size.height / 2),
                withAttributes: attrs
            )
        }
    }
}

final class AnnotationState: ObservableObject {
    @Published var tool: AnnotationTool = .arrow
    @Published var colorIndex: Int = 0
    @Published var zoom: CGFloat = 1.0

    static let palette: [NSColor] = [
        .systemRed, NSColor(srgbRed: 1.0, green: 0.5, blue: 0.1, alpha: 1),
        .systemYellow, NSColor(srgbRed: 0.2, green: 0.8, blue: 0.4, alpha: 1),
        .systemBlue, NSColor(srgbRed: 0.6, green: 0.3, blue: 0.9, alpha: 1),
        NSColor(white: 0.1, alpha: 1), .white
    ]

    var color: NSColor { AnnotationState.palette[colorIndex] }
}

final class InlineTextEditor: NSView, NSTextFieldDelegate {
    var onCommit: ((String, CGPoint, CGFloat) -> Void)?
    var onCancel: (() -> Void)?

    private let textField = NSTextField()
    private let padding: CGFloat = 4

    private var currentFontSize: CGFloat
    private let fontSizeMin: CGFloat = 10
    private let fontSizeMax: CGFloat = 120

    private var committed = false

    init(origin: NSPoint, color: NSColor, fontSize: CGFloat, initialText: String? = nil) {
        self.currentFontSize = fontSize
        let initialSize = NSSize(width: 140, height: 36)
        super.init(frame: NSRect(
            x: origin.x - padding,
            y: origin.y - 2,
            width: initialSize.width,
            height: initialSize.height
        ))
        wantsLayer = true
        layer?.cornerRadius = 4
        layer?.borderColor = NSColor.controlAccentColor.withAlphaComponent(0.8).cgColor
        layer?.borderWidth = 1.0
        layer?.backgroundColor = NSColor.white.withAlphaComponent(0.12).cgColor

        textField.isBezeled = false
        textField.drawsBackground = false
        textField.focusRingType = .none
        textField.font = NSFont.systemFont(ofSize: currentFontSize, weight: .bold)
        textField.textColor = color
        textField.placeholderString = "输入文本..."
        textField.delegate = self
        textField.cell?.wraps = false
        textField.cell?.isScrollable = true

        if let txt = initialText, !txt.isEmpty {
            textField.stringValue = txt
        }

        addSubview(textField)
        autoSize()
    }

    required init?(coder: NSCoder) { fatalError() }

    override func layout() {
        super.layout()
        textField.frame = bounds.insetBy(dx: padding, dy: 2)
    }

    override var isFlipped: Bool { false }
    override var acceptsFirstResponder: Bool { false }

    func beginEditing() {
        DispatchQueue.main.async { [weak self] in
            guard let self = self, let window = self.window else { return }
            window.makeFirstResponder(self.textField)
            if !self.textField.stringValue.isEmpty {
                self.textField.selectText(nil)
            }
        }
    }

    override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(textField)
    }

    private func autoSize(preserveOrigin: Bool = true) {
        let text = textField.stringValue.isEmpty ? (textField.placeholderString ?? "输入文本...") : textField.stringValue
        let font = NSFont.systemFont(ofSize: currentFontSize, weight: .bold)
        let attrs: [NSAttributedString.Key: Any] = [.font: font]
        let size = (text as NSString).size(withAttributes: attrs)
        let newW = max(size.width + padding * 2 + 14, 80)
        let newH = max(size.height + 6, 28)
        let originalOrigin = frame.origin
        frame = NSRect(x: originalOrigin.x, y: originalOrigin.y, width: newW, height: newH)
        textField.frame = bounds.insetBy(dx: padding, dy: 2)
    }

    // MARK: - NSTextFieldDelegate

    func controlTextDidChange(_ obj: Notification) {
        autoSize()
    }

    func control(_ control: NSControl, textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
        if commandSelector == #selector(NSResponder.cancelOperation(_:)) {
            cancel()
            return true
        }
        if commandSelector == #selector(NSResponder.insertNewline(_:)) {
            commit()
            return true
        }
        return false
    }

    func commit() {
        guard !committed else { return }
        committed = true
        let text = textField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.isEmpty {
            onCancel?()
        } else {
            let origin = NSPoint(
                x: frame.origin.x + padding,
                y: frame.origin.y + 2
            )
            onCommit?(text, origin, currentFontSize)
        }
    }

    func cancel() {
        guard !committed else { return }
        committed = true
        onCancel?()
    }

    func updateColor(_ color: NSColor) {
        textField.textColor = color
    }
}

final class AnnotationCanvas: NSView {
    var image: NSImage? {
        didSet {
            if let img = image { setFrameSize(img.size) }
            needsDisplay = true
        }
    }
    var annotations: [Annotation] = [] { didSet { needsDisplay = true } }
    var currentTool: AnnotationTool = .arrow { didSet { resetCursorRects() } }
    var currentColor: NSColor = .systemRed {
        didSet { activeTextEditor?.updateColor(currentColor) }
    }
    var thickness: CGFloat = 5

    var onAnnotationsChanged: (([Annotation]) -> Void)?

    private var inProgress: Annotation?
    private var dragStart: NSPoint?
    private var nextStep: Int = 1
    private var activeTextEditor: InlineTextEditor?

    override var isFlipped: Bool { false }
    override var acceptsFirstResponder: Bool { true }

    override func resetCursorRects() {
        discardCursorRects()
        let cursor: NSCursor = (currentTool == .text) ? .iBeam : .crosshair
        addCursorRect(bounds, cursor: cursor)
    }

    override func mouseDown(with event: NSEvent) {
        // If an editor is open, clicking outside commits it and swallows the click.
        if let editor = activeTextEditor {
            let p = convert(event.locationInWindow, from: nil)
            if !editor.frame.contains(p) {
                editor.commit()
                return
            }
        }

        let p = convert(event.locationInWindow, from: nil)
        dragStart = p

        switch currentTool {
        case .arrow:
            inProgress = Annotation(shape: .arrow(from: p, to: p), color: currentColor, thickness: thickness)
        case .rectangle:
            inProgress = Annotation(shape: .rectangle(CGRect(origin: p, size: .zero)), color: currentColor, thickness: thickness)
        case .oval:
            inProgress = Annotation(shape: .oval(CGRect(origin: p, size: .zero)), color: currentColor, thickness: thickness)
        case .line:
            inProgress = Annotation(shape: .line(from: p, to: p), color: currentColor, thickness: thickness)
        case .pen:
            inProgress = Annotation(shape: .pen(points: [p]), color: currentColor, thickness: thickness)
        case .mosaic:
            inProgress = Annotation(shape: .mosaic(CGRect(origin: p, size: .zero)), color: currentColor, thickness: thickness)
        case .step:
            let ann = Annotation(shape: .step(center: p, number: nextStep), color: currentColor, thickness: thickness)
            nextStep += 1
            annotations.append(ann)
            onAnnotationsChanged?(annotations)
            inProgress = nil
        case .text:
            inProgress = nil
            startTextEditor(at: p)
        }
        needsDisplay = true
    }

    override func mouseDragged(with event: NSEvent) {
        guard var ann = inProgress, let start = dragStart else { return }
        let p = convert(event.locationInWindow, from: nil)
        switch ann.shape {
        case .arrow:
            ann.shape = .arrow(from: start, to: p)
        case .rectangle:
            ann.shape = .rectangle(CGRect(
                x: min(start.x, p.x), y: min(start.y, p.y),
                width: abs(p.x - start.x), height: abs(p.y - start.y)
            ))
        case .oval:
            ann.shape = .oval(CGRect(
                x: min(start.x, p.x), y: min(start.y, p.y),
                width: abs(p.x - start.x), height: abs(p.y - start.y)
            ))
        case .line:
            ann.shape = .line(from: start, to: p)
        case .pen(var points):
            points.append(p)
            ann.shape = .pen(points: points)
        case .mosaic:
            ann.shape = .mosaic(CGRect(
                x: min(start.x, p.x), y: min(start.y, p.y),
                width: abs(p.x - start.x), height: abs(p.y - start.y)
            ))
        default: break
        }
        inProgress = ann
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        defer {
            inProgress = nil
            dragStart = nil
            needsDisplay = true
        }
        guard let ann = inProgress else { return }
        switch ann.shape {
        case .arrow(let from, let to):
            if hypot(to.x - from.x, to.y - from.y) < 5 { return }
        case .rectangle(let r), .oval(let r), .mosaic(let r):
            if r.width < 5 || r.height < 5 { return }
        case .line(let from, let to):
            if hypot(to.x - from.x, to.y - from.y) < 5 { return }
        case .pen(let pts):
            if pts.isEmpty { return }
        default: break
        }
        annotations.append(ann)
        onAnnotationsChanged?(annotations)
    }

    func undo() {
        guard !annotations.isEmpty else { return }
        let removed = annotations.removeLast()
        if case .step = removed.shape {
            nextStep = max(1, nextStep - 1)
        }
        onAnnotationsChanged?(annotations)
        needsDisplay = true
    }

    func commitActiveTextEditor() {
        activeTextEditor?.commit()
    }

    private func startTextEditor(at point: NSPoint) {
        activeTextEditor?.commit()
        let editor = InlineTextEditor(origin: point, color: currentColor, fontSize: 24)
        editor.onCommit = { [weak self] text, origin, fontSize in
            guard let self = self else { return }
            let ann = Annotation(
                shape: .text(origin: origin, string: text, fontSize: fontSize),
                color: self.currentColor,
                thickness: self.thickness
            )
            self.annotations.append(ann)
            self.onAnnotationsChanged?(self.annotations)
            self.activeTextEditor?.removeFromSuperview()
            self.activeTextEditor = nil
            self.needsDisplay = true
        }
        editor.onCancel = { [weak self] in
            self?.activeTextEditor?.removeFromSuperview()
            self?.activeTextEditor = nil
            self?.needsDisplay = true
        }
        addSubview(editor)
        activeTextEditor = editor
        editor.beginEditing()
    }

    override func draw(_ dirtyRect: NSRect) {
        NSColor.windowBackgroundColor.setFill()
        bounds.fill()
        image?.draw(in: bounds)
        for ann in annotations { AnnotationRenderer.draw(ann, baseImage: image) }
        if let in_ = inProgress { AnnotationRenderer.draw(in_, baseImage: image) }
    }

    func flattenedImage() -> NSImage? {
        commitActiveTextEditor()

        guard let image = image,
              let cgImage = image.cgImage(forProposedRect: nil, context: nil, hints: nil)
        else { return nil }

        let pixelW = cgImage.width
        let pixelH = cgImage.height
        let colorSpace = CGColorSpaceCreateDeviceRGB()
        guard let ctx = CGContext(
            data: nil,
            width: pixelW, height: pixelH,
            bitsPerComponent: 8, bytesPerRow: 0,
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return nil }

        ctx.draw(cgImage, in: CGRect(x: 0, y: 0, width: pixelW, height: pixelH))

        let scaleX = CGFloat(pixelW) / image.size.width
        let scaleY = CGFloat(pixelH) / image.size.height
        ctx.scaleBy(x: scaleX, y: scaleY)

        let nsCtx = NSGraphicsContext(cgContext: ctx, flipped: false)
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = nsCtx
        for ann in annotations { AnnotationRenderer.draw(ann, baseImage: image) }
        NSGraphicsContext.restoreGraphicsState()

        guard let output = ctx.makeImage() else { return nil }
        return NSImage(cgImage: output, size: image.size)
    }
}

struct AnnotationToolbar: View {
    @ObservedObject var state: AnnotationState
    let onUndo: () -> Void
    let onCancel: () -> Void
    let onSave: () -> Void
    let onCopy: () -> Void
    let onZoomIn: () -> Void
    let onZoomOut: () -> Void
    let onZoomReset: () -> Void

    var body: some View {
        HStack(spacing: 14) {
            toolGroup
            colorGroup
            Spacer(minLength: 8)
            zoomGroup
            undoButton
            verticalDivider
            Button("取消", action: onCancel)
                .buttonStyle(ToolbarActionStyle(kind: .subtle))
                .fixedSize()
            Button(action: onSave) {
                Label("保存", systemImage: "square.and.arrow.down")
                    .lineLimit(1)
            }
            .buttonStyle(ToolbarActionStyle(kind: .secondary))
            .fixedSize()
            Button(action: onCopy) {
                Label("复制", systemImage: "doc.on.doc")
                    .lineLimit(1)
            }
            .buttonStyle(ToolbarActionStyle(kind: .primary))
            .fixedSize()
            .keyboardShortcut(.defaultAction)
        }
        .padding(.horizontal, 18)
        .padding(.vertical, 12)
        .frame(maxWidth: .infinity)
        .background(toolbarBackground)
    }

    private var toolbarBackground: some View {
        ZStack {
            Rectangle().fill(.ultraThinMaterial)
            LinearGradient(
                colors: [Color.white.opacity(0.03), Color.clear],
                startPoint: .top,
                endPoint: .bottom
            )
        }
        .overlay(alignment: .bottom) {
            Rectangle()
                .fill(Color.primary.opacity(0.1))
                .frame(height: 0.5)
        }
    }

    private var verticalDivider: some View {
        Rectangle()
            .fill(Color.primary.opacity(0.14))
            .frame(width: 1, height: 22)
            .padding(.horizontal, 2)
    }

    private var toolGroup: some View {
        HStack(spacing: 3) {
            ForEach(AnnotationTool.allCases) { tool in
                ToolChip(
                    systemName: tool.symbolName,
                    isSelected: state.tool == tool,
                    tooltip: tool.label
                ) {
                    state.tool = tool
                }
            }
        }
        .padding(4)
        .background(GroupCapsuleBackground())
        .fixedSize()
    }

    private var colorGroup: some View {
        HStack(spacing: 8) {
            ForEach(AnnotationState.palette.indices, id: \.self) { idx in
                ColorDot(
                    color: Color(nsColor: AnnotationState.palette[idx]),
                    isSelected: state.colorIndex == idx
                ) {
                    state.colorIndex = idx
                }
            }
        }
        .padding(.horizontal, 9)
        .padding(.vertical, 6)
        .background(GroupCapsuleBackground())
        .fixedSize()
    }

    private var undoButton: some View {
        Button(action: onUndo) {
            Image(systemName: "arrow.uturn.backward")
                .font(.system(size: 13, weight: .semibold))
                .frame(width: 34, height: 30)
        }
        .buttonStyle(ToolbarActionStyle(kind: .iconOnly))
        .keyboardShortcut("z", modifiers: .command)
        .help("撤销 (⌘Z)")
        .fixedSize()
    }

    private var zoomGroup: some View {
        HStack(spacing: 2) {
            ZoomButton(icon: "minus", action: onZoomOut)
                .keyboardShortcut("-", modifiers: .command)
            Button(action: onZoomReset) {
                Text("\(Int((state.zoom * 100).rounded()))%")
                    .font(.system(size: 11, weight: .semibold, design: .rounded))
                    .monospacedDigit()
                    .foregroundStyle(.primary)
                    .frame(width: 42, height: 26)
            }
            .buttonStyle(.plain)
            .help("适应窗口")
            ZoomButton(icon: "plus", action: onZoomIn)
                .keyboardShortcut("=", modifiers: .command)
        }
        .padding(4)
        .background(GroupCapsuleBackground())
        .fixedSize()
    }
}

private struct ZoomButton: View {
    let icon: String
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Image(systemName: icon)
                .font(.system(size: 11, weight: .bold))
                .frame(width: 24, height: 26)
                .foregroundStyle(hovering ? Color.primary : Color.primary.opacity(0.8))
                .background(
                    RoundedRectangle(cornerRadius: 6)
                        .fill(hovering ? Color.primary.opacity(0.12) : .clear)
                )
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .animation(.easeOut(duration: 0.1), value: hovering)
    }
}

private struct GroupCapsuleBackground: View {
    var body: some View {
        RoundedRectangle(cornerRadius: 11)
            .fill(Color.black.opacity(0.22))
            .overlay(
                RoundedRectangle(cornerRadius: 11)
                    .strokeBorder(Color.white.opacity(0.05), lineWidth: 0.5)
            )
    }
}

private struct ToolChip: View {
    let systemName: String
    let isSelected: Bool
    let tooltip: String
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Image(systemName: systemName)
                .font(.system(size: 15, weight: .semibold))
                .frame(width: 40, height: 30)
                .foregroundStyle(foreground)
                .background(background)
                .shadow(
                    color: isSelected ? Color.accentColor.opacity(0.35) : .clear,
                    radius: 4, y: 1
                )
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .help(tooltip)
        .animation(.easeOut(duration: 0.14), value: isSelected)
        .animation(.easeOut(duration: 0.12), value: hovering)
    }

    private var foreground: Color {
        if isSelected { return .white }
        return hovering ? .primary : .primary.opacity(0.78)
    }

    @ViewBuilder private var background: some View {
        if isSelected {
            RoundedRectangle(cornerRadius: 8)
                .fill(
                    LinearGradient(
                        colors: [
                            Color.accentColor,
                            Color.accentColor.opacity(0.82)
                        ],
                        startPoint: .top,
                        endPoint: .bottom
                    )
                )
        } else if hovering {
            RoundedRectangle(cornerRadius: 8)
                .fill(Color.primary.opacity(0.1))
        } else {
            Color.clear
        }
    }
}

private struct ColorDot: View {
    let color: Color
    let isSelected: Bool
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            ZStack {
                Circle()
                    .strokeBorder(Color.white.opacity(isSelected ? 0.95 : 0), lineWidth: 2)
                    .frame(width: 24, height: 24)
                Circle()
                    .fill(color)
                    .frame(width: isSelected ? 15 : 18, height: isSelected ? 15 : 18)
                    .overlay(
                        Circle()
                            .strokeBorder(Color.black.opacity(0.25), lineWidth: 0.5)
                    )
                    .shadow(color: color.opacity(isSelected ? 0.55 : 0), radius: 4, y: 1)
                    .scaleEffect(hovering && !isSelected ? 1.14 : 1.0)
            }
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .animation(.spring(response: 0.28, dampingFraction: 0.72), value: isSelected)
        .animation(.easeOut(duration: 0.12), value: hovering)
    }
}

enum ToolbarActionKind {
    case subtle     // Cancel — text-only, low emphasis
    case secondary  // Save — bordered, medium emphasis
    case primary    // Copy — filled accent, high emphasis
    case iconOnly   // Undo — compact square
}

struct ToolbarActionStyle: ButtonStyle {
    let kind: ToolbarActionKind
    @State private var hovering = false

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 12.5, weight: .semibold))
            .padding(.horizontal, kind == .iconOnly ? 0 : 14)
            .padding(.vertical, kind == .iconOnly ? 0 : 8)
            .foregroundStyle(foreground(pressed: configuration.isPressed))
            .background(background(pressed: configuration.isPressed))
            .overlay(
                RoundedRectangle(cornerRadius: 8)
                    .strokeBorder(borderColor, lineWidth: borderWidth)
            )
            .shadow(color: shadowColor, radius: 4, y: 1)
            .contentShape(RoundedRectangle(cornerRadius: 8))
            .onHover { hovering = $0 }
            .animation(.easeOut(duration: 0.12), value: hovering)
            .animation(.easeOut(duration: 0.08), value: configuration.isPressed)
    }

    private func foreground(pressed: Bool) -> Color {
        switch kind {
        case .primary: return .white
        case .secondary, .subtle, .iconOnly: return .primary
        }
    }

    @ViewBuilder private func background(pressed: Bool) -> some View {
        switch kind {
        case .primary:
            RoundedRectangle(cornerRadius: 8)
                .fill(
                    LinearGradient(
                        colors: [
                            Color.accentColor.opacity(pressed ? 0.78 : 1.0),
                            Color.accentColor.opacity(pressed ? 0.65 : 0.82)
                        ],
                        startPoint: .top,
                        endPoint: .bottom
                    )
                )
        case .secondary:
            RoundedRectangle(cornerRadius: 8)
                .fill(Color.primary.opacity(pressed ? 0.14 : (hovering ? 0.1 : 0.07)))
        case .subtle:
            RoundedRectangle(cornerRadius: 8)
                .fill(Color.primary.opacity(pressed ? 0.12 : (hovering ? 0.07 : 0)))
        case .iconOnly:
            RoundedRectangle(cornerRadius: 8)
                .fill(Color.primary.opacity(pressed ? 0.14 : (hovering ? 0.1 : 0.06)))
        }
    }

    private var borderColor: Color {
        switch kind {
        case .secondary: return Color.primary.opacity(hovering ? 0.14 : 0.08)
        case .iconOnly: return Color.primary.opacity(0.05)
        default: return .clear
        }
    }

    private var borderWidth: CGFloat {
        switch kind {
        case .secondary, .iconOnly: return 0.5
        default: return 0
        }
    }

    private var shadowColor: Color {
        switch kind {
        case .primary: return Color.accentColor.opacity(0.32)
        default: return .clear
        }
    }
}

final class CenteringClipView: NSClipView {
    override func constrainBoundsRect(_ proposedBounds: NSRect) -> NSRect {
        var rect = super.constrainBoundsRect(proposedBounds)
        guard let docView = documentView else { return rect }
        if rect.width > docView.frame.width {
            rect.origin.x = (docView.frame.width - rect.width) / 2
        }
        if rect.height > docView.frame.height {
            rect.origin.y = (docView.frame.height - rect.height) / 2
        }
        return rect
    }
}

final class AnnotationWindowController: NSObject, NSWindowDelegate {
    private var window: NSWindow?
    private var canvas: AnnotationCanvas?
    private var scrollView: NSScrollView?
    private var onClose: (() -> Void)?

    private let state = AnnotationState()
    private var cancellables = Set<AnyCancellable>()
    private var magnificationObservation: NSKeyValueObservation?

    func open(image: NSImage, onClose: @escaping () -> Void) {
        self.onClose = onClose

        let canvas = AnnotationCanvas()
        canvas.image = image
        canvas.currentTool = state.tool
        canvas.currentColor = state.color
        self.canvas = canvas

        state.$tool
            .sink { [weak canvas] in canvas?.currentTool = $0 }
            .store(in: &cancellables)
        state.$colorIndex
            .sink { [weak canvas] idx in
                canvas?.currentColor = AnnotationState.palette[idx]
            }
            .store(in: &cancellables)

        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.hasHorizontalScroller = true
        scroll.autohidesScrollers = true
        scroll.borderType = .noBorder
        scroll.drawsBackground = true
        scroll.backgroundColor = NSColor(white: 0.11, alpha: 1)

        // Enable zoom: pinch on trackpad to zoom, or Cmd+scroll.
        scroll.allowsMagnification = true
        scroll.minMagnification = 0.1
        scroll.maxMagnification = 5.0

        let centering = CenteringClipView(frame: scroll.contentView.bounds)
        centering.drawsBackground = true
        centering.backgroundColor = NSColor(white: 0.11, alpha: 1)
        scroll.contentView = centering

        scroll.documentView = canvas
        scroll.translatesAutoresizingMaskIntoConstraints = false
        scrollView = scroll

        let toolbarView = AnnotationToolbar(
            state: state,
            onUndo: { [weak self] in self?.canvas?.undo() },
            onCancel: { [weak self] in self?.window?.close() },
            onSave: { [weak self] in self?.saveToDesktop() },
            onCopy: { [weak self] in self?.copyToClipboard() },
            onZoomIn: { [weak self] in self?.zoomIn() },
            onZoomOut: { [weak self] in self?.zoomOut() },
            onZoomReset: { [weak self] in self?.fitImageToWindow() }
        )
        let hosting = NSHostingView(rootView: toolbarView)
        hosting.translatesAutoresizingMaskIntoConstraints = false

        let container = NSView()
        container.addSubview(hosting)
        container.addSubview(scroll)
        NSLayoutConstraint.activate([
            hosting.topAnchor.constraint(equalTo: container.topAnchor),
            hosting.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            hosting.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            scroll.topAnchor.constraint(equalTo: hosting.bottomAnchor),
            scroll.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            scroll.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            scroll.bottomAnchor.constraint(equalTo: container.bottomAnchor)
        ])

        let horizontalPadding: CGFloat = 200
        let verticalPadding: CGFloat = 220
        let contentW = min(max(image.size.width + horizontalPadding, 920), 1400)
        let contentH = min(max(image.size.height + verticalPadding, 560), 960)
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: contentW, height: contentH),
            styleMask: [.titled, .closable, .resizable, .miniaturizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        window.title = "Annotate"
        window.titlebarAppearsTransparent = true
        window.titleVisibility = .hidden
        window.contentMinSize = NSSize(width: 880, height: 400)
        window.contentView = container
        window.delegate = self
        window.isReleasedWhenClosed = false
        window.center()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        self.window = window

        // Fit image to available space on open so the user doesn't need to scroll.
        DispatchQueue.main.async { [weak self] in
            self?.fitImageToWindow()
        }

        // Mirror NSScrollView.magnification into state.zoom so pinch-to-zoom
        // updates the toolbar percentage.
        magnificationObservation = scroll.observe(\.magnification, options: [.new]) { [weak self] scroll, _ in
            let mag = scroll.magnification
            DispatchQueue.main.async {
                self?.state.zoom = mag
            }
        }
    }

    private func fitImageToWindow() {
        guard let scroll = scrollView,
              let imageSize = canvas?.image?.size,
              imageSize.width > 0, imageSize.height > 0
        else { return }
        let content = scroll.contentSize
        guard content.width > 0, content.height > 0 else { return }
        let fitW = content.width / imageSize.width
        let fitH = content.height / imageSize.height
        // 5% breathing room, never upscale beyond 1:1 by default.
        let target = min(1.0, min(fitW, fitH) * 0.95)
        scroll.animator().magnification = target
        state.zoom = target
    }

    private func zoomIn() {
        guard let scroll = scrollView else { return }
        let newMag = min(scroll.maxMagnification, scroll.magnification * 1.25)
        scroll.animator().magnification = newMag
        state.zoom = newMag
    }

    private func zoomOut() {
        guard let scroll = scrollView else { return }
        let newMag = max(scroll.minMagnification, scroll.magnification / 1.25)
        scroll.animator().magnification = newMag
        state.zoom = newMag
    }

    private func saveToDesktop() {
        guard let image = canvas?.flattenedImage() else { return }
        ImageSaver.saveToDesktop(image)
        HistoryStore.shared.add(image)
        window?.close()
    }

    private func copyToClipboard() {
        guard let image = canvas?.flattenedImage() else { return }
        ImageSaver.copyToClipboard(image)
        HistoryStore.shared.add(image)
        window?.close()
    }

    func windowWillClose(_ notification: Notification) {
        onClose?()
    }
}
