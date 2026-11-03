import Cocoa

final class RecordingSelectionController {
    private var windows: [RecordingSelectionWindow] = []
    private var completion: ((CGRect?, NSScreen?) -> Void)?
    private var finished = false
    private var cursorHidden = false

    func begin(completion: @escaping (CGRect?, NSScreen?) -> Void) {
        self.completion = completion

        NSCursor.hide()
        cursorHidden = true

        for screen in NSScreen.screens {
            let window = RecordingSelectionWindow(
                screen: screen,
                onSelect: { [weak self] rectInScreen, screen in
                    self?.finish(with: rectInScreen, screen: screen)
                },
                onCancel: { [weak self] in
                    self?.finish(with: nil, screen: nil)
                }
            )
            windows.append(window)
            window.orderFrontRegardless()
            CaptureExclusions.register(window)
        }

        NSApp.activate(ignoringOtherApps: true)
        windows.first?.makeKey()
    }

    private func finish(with rectInScreen: CGRect?, screen: NSScreen?) {
        guard !finished else { return }
        finished = true

        closeWindows()
        completion?(rectInScreen, screen)
    }

    private func closeWindows() {
        if cursorHidden {
            NSCursor.unhide()
            cursorHidden = false
        }
        for w in windows {
            CaptureExclusions.unregister(w)
            w.orderOut(nil)
            w.contentView = nil
            w.close()
        }
        windows.removeAll()
    }

    deinit {
        if cursorHidden {
            NSCursor.unhide()
        }
    }
}

final class RecordingSelectionWindow: NSWindow {
    private let selectionView: RecordingSelectionView
    private let targetScreen: NSScreen

    init(
        screen: NSScreen,
        onSelect: @escaping (CGRect, NSScreen) -> Void,
        onCancel: @escaping () -> Void
    ) {
        self.targetScreen = screen
        self.selectionView = RecordingSelectionView(frame: NSRect(origin: .zero, size: screen.frame.size))

        super.init(
            contentRect: screen.frame,
            styleMask: [.borderless],
            backing: .buffered,
            defer: false
        )
        setFrame(screen.frame, display: false)

        isReleasedWhenClosed = false
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        level = .floating
        ignoresMouseEvents = false
        isMovable = false
        acceptsMouseMovedEvents = true
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]

        selectionView.onSelect = { [weak self] rectInView in
            guard let self = self else { return }
            let global = CGRect(
                x: self.targetScreen.frame.origin.x + rectInView.origin.x,
                y: self.targetScreen.frame.origin.y + rectInView.origin.y,
                width: rectInView.width,
                height: rectInView.height
            )
            onSelect(global, self.targetScreen)
        }
        selectionView.onCancel = onCancel
        contentView = selectionView

        makeFirstResponder(selectionView)
    }

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }

    override func resignKey() {
        super.resignKey()
        NSCursor.unhide()
    }
}

final class RecordingSelectionView: NSView {
    var onSelect: ((CGRect) -> Void)?
    var onCancel: (() -> Void)?

    private var startPoint: NSPoint?
    private var currentRect: NSRect = .zero
    private var mousePosition: NSPoint?
    private var trackingArea: NSTrackingArea?

    // 选区锁定与4个角调整状态
    private var isBoxed: Bool = false
    private enum ActiveCorner {
        case topLeft
        case topRight
        case bottomLeft
        case bottomRight
    }
    private var activeCorner: ActiveCorner? = nil
    private var cornerAnchorPoint: NSPoint = .zero
    private var isDraggingWholeBox: Bool = false
    private var dragBoxStartPoint: NSPoint = .zero
    private var dragBoxStartRect: NSRect = .zero

    override var acceptsFirstResponder: Bool { true }
    override var isFlipped: Bool { false }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let t = trackingArea { removeTrackingArea(t) }
        let t = NSTrackingArea(
            rect: bounds,
            options: [.activeAlways, .inVisibleRect, .mouseMoved, .mouseEnteredAndExited],
            owner: self,
            userInfo: nil
        )
        addTrackingArea(t)
        trackingArea = t
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        guard let win = window else { return }
        let global = NSEvent.mouseLocation
        mousePosition = NSPoint(
            x: global.x - win.frame.origin.x,
            y: global.y - win.frame.origin.y
        )
        needsDisplay = true
    }

    private func hitTestCorner(at p: NSPoint) -> ActiveCorner? {
        guard isBoxed, currentRect.width > 0, currentRect.height > 0 else { return nil }
        let threshold: CGFloat = 16.0
        let r = currentRect
        if abs(p.x - r.minX) <= threshold && abs(p.y - r.maxY) <= threshold {
            return .topLeft
        }
        if abs(p.x - r.maxX) <= threshold && abs(p.y - r.maxY) <= threshold {
            return .topRight
        }
        if abs(p.x - r.minX) <= threshold && abs(p.y - r.minY) <= threshold {
            return .bottomLeft
        }
        if abs(p.x - r.maxX) <= threshold && abs(p.y - r.minY) <= threshold {
            return .bottomRight
        }
        return nil
    }

    private func actionBarRect() -> NSRect {
        guard isBoxed, currentRect.width > 0, currentRect.height > 0 else { return .zero }
        let w: CGFloat = 170
        let h: CGFloat = 34
        var x = currentRect.midX - w / 2
        x = max(10, min(bounds.width - w - 10, x))
        var y = currentRect.minY - h - 10
        if y < 10 {
            y = currentRect.maxY + 10
        }
        return NSRect(x: x, y: y, width: w, height: h)
    }

    private func cancelBtnRect() -> NSRect {
        let r = actionBarRect()
        guard r.width > 0 else { return .zero }
        return NSRect(x: r.minX + 6, y: r.minY + 4, width: 70, height: 26)
    }

    private func confirmBtnRect() -> NSRect {
        let r = actionBarRect()
        guard r.width > 0 else { return .zero }
        return NSRect(x: r.maxX - 84, y: r.minY + 4, width: 78, height: 26)
    }

    override func mouseMoved(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        mousePosition = p
        if isBoxed {
            if hitTestCorner(at: p) != nil {
                NSCursor.crosshair.set()
            } else if currentRect.contains(p) {
                NSCursor.openHand.set()
            } else {
                NSCursor.arrow.set()
            }
        }
        needsDisplay = true
    }

    override func mouseEntered(with event: NSEvent) {
        mousePosition = convert(event.locationInWindow, from: nil)
        needsDisplay = true
    }

    override func mouseDown(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        mousePosition = p

        if isBoxed {
            // 1. 检查点击确认按钮
            if confirmBtnRect().contains(p) {
                onSelect?(currentRect)
                return
            }
            // 2. 检查点击取消按钮
            if cancelBtnRect().contains(p) {
                onCancel?()
                return
            }
            // 3. 双击选区内部直接确认
            if event.clickCount == 2 && currentRect.contains(p) {
                onSelect?(currentRect)
                return
            }
            // 4. 检查是否点击 4 个角进行调整
            if let corner = hitTestCorner(at: p) {
                activeCorner = corner
                switch corner {
                case .topLeft:
                    cornerAnchorPoint = NSPoint(x: currentRect.maxX, y: currentRect.minY)
                case .topRight:
                    cornerAnchorPoint = NSPoint(x: currentRect.minX, y: currentRect.minY)
                case .bottomLeft:
                    cornerAnchorPoint = NSPoint(x: currentRect.maxX, y: currentRect.maxY)
                case .bottomRight:
                    cornerAnchorPoint = NSPoint(x: currentRect.minX, y: currentRect.maxY)
                }
                return
            }
            // 5. 点击选区内部可拖动整体选区
            if currentRect.contains(p) {
                isDraggingWholeBox = true
                dragBoxStartPoint = p
                dragBoxStartRect = currentRect
                NSCursor.closedHand.set()
                return
            }
            // 6. 点击选区外部：重新开始划选
            isBoxed = false
            currentRect = .zero
            startPoint = p
            needsDisplay = true
            return
        }

        startPoint = p
        currentRect = .zero
        needsDisplay = true
    }

    override func mouseDragged(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        mousePosition = p

        if isBoxed {
            if let corner = activeCorner {
                let clampedP = NSPoint(
                    x: max(0, min(bounds.width, p.x)),
                    y: max(0, min(bounds.height, p.y))
                )
                let minX = min(cornerAnchorPoint.x, clampedP.x)
                let maxX = max(cornerAnchorPoint.x, clampedP.x)
                let minY = min(cornerAnchorPoint.y, clampedP.y)
                let maxY = max(cornerAnchorPoint.y, clampedP.y)
                if maxX - minX >= 10 && maxY - minY >= 10 {
                    currentRect = NSRect(x: minX, y: minY, width: maxX - minX, height: maxY - minY)
                    needsDisplay = true
                }
                return
            }
            if isDraggingWholeBox {
                let dx = p.x - dragBoxStartPoint.x
                let dy = p.y - dragBoxStartPoint.y
                var newX = dragBoxStartRect.minX + dx
                var newY = dragBoxStartRect.minY + dy
                newX = max(0, min(bounds.width - dragBoxStartRect.width, newX))
                newY = max(0, min(bounds.height - dragBoxStartRect.height, newY))
                currentRect.origin = NSPoint(x: newX, y: newY)
                needsDisplay = true
                return
            }
            return
        }

        guard let start = startPoint else { return }
        currentRect = NSRect(
            x: min(start.x, p.x),
            y: min(start.y, p.y),
            width: abs(p.x - start.x),
            height: abs(p.y - start.y)
        )
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        if isBoxed {
            if activeCorner != nil {
                activeCorner = nil
                needsDisplay = true
                return
            }
            if isDraggingWholeBox {
                isDraggingWholeBox = false
                NSCursor.openHand.set()
                needsDisplay = true
                return
            }
            return
        }

        defer {
            startPoint = nil
            needsDisplay = true
        }

        if currentRect.width < 15 || currentRect.height < 15 {
            onCancel?()
            return
        }

        // 框选完成后进入锁定与4角可拖拽状态，不立即关闭窗口
        isBoxed = true
        NSCursor.unhide()
    }

    override func rightMouseDown(with event: NSEvent) {
        onCancel?()
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 { // Escape
            onCancel?()
        } else if event.keyCode == 36 || event.keyCode == 76 { // Return / Enter
            if isBoxed && currentRect.width >= 15 && currentRect.height >= 15 {
                onSelect?(currentRect)
            }
        } else {
            super.keyDown(with: event)
        }
    }

    override func draw(_ dirtyRect: NSRect) {
        // 1. Semi-transparent black scrim
        NSColor.black.withAlphaComponent(0.35).setFill()
        bounds.fill()

        // 2. Punch out the selection area
        if currentRect.width > 0, currentRect.height > 0 {
            NSColor.clear.setFill()
            currentRect.fill(using: .copy)

            // Outline
            let strokeCol = isBoxed ? NSColor(srgbRed: 0.18, green: 0.53, blue: 1.0, alpha: 1.0) : NSColor.white
            strokeCol.setStroke()
            let border = NSBezierPath(rect: currentRect.insetBy(dx: 0.5, dy: 0.5))
            border.lineWidth = 1.5
            border.stroke()

            // 绘制4个角的微型拖动手柄与L角标
            if isBoxed {
                drawCornerHandles(for: currentRect)
                drawActionBar()
            }

            drawDimensions(for: currentRect)
        }

        // 3. Draw crosshair cursor (仅在初次框选时)
        if !isBoxed, let pos = mousePosition {
            drawCrosshair(at: pos)
        }
    }

    private func drawCornerHandles(for r: NSRect) {
        let len: CGFloat = 16
        let color = NSColor(srgbRed: 0.18, green: 0.53, blue: 1.0, alpha: 1.0)
        color.setStroke()

        let path = NSBezierPath()
        path.lineWidth = 3.0
        path.lineCapStyle = .square

        // Top-left
        path.move(to: NSPoint(x: r.minX, y: r.maxY - len))
        path.line(to: NSPoint(x: r.minX, y: r.maxY))
        path.line(to: NSPoint(x: r.minX + len, y: r.maxY))

        // Top-right
        path.move(to: NSPoint(x: r.maxX - len, y: r.maxY))
        path.line(to: NSPoint(x: r.maxX, y: r.maxY))
        path.line(to: NSPoint(x: r.maxX, y: r.maxY - len))

        // Bottom-left
        path.move(to: NSPoint(x: r.minX, y: r.minY + len))
        path.line(to: NSPoint(x: r.minX, y: r.minY))
        path.line(to: NSPoint(x: r.minX + len, y: r.minY))

        // Bottom-right
        path.move(to: NSPoint(x: r.maxX - len, y: r.minY))
        path.line(to: NSPoint(x: r.maxX, y: r.minY))
        path.line(to: NSPoint(x: r.maxX, y: r.minY + len))

        path.stroke()

        // 4角小白点
        let dotR: CGFloat = 3.5
        let corners = [
            NSPoint(x: r.minX, y: r.maxY),
            NSPoint(x: r.maxX, y: r.maxY),
            NSPoint(x: r.minX, y: r.minY),
            NSPoint(x: r.maxX, y: r.minY)
        ]
        NSColor.white.setFill()
        color.setStroke()
        for c in corners {
            let dotRect = NSRect(x: c.x - dotR, y: c.y - dotR, width: dotR * 2, height: dotR * 2)
            let dotPath = NSBezierPath(ovalIn: dotRect)
            dotPath.fill()
            dotPath.lineWidth = 1.5
            dotPath.stroke()
        }
    }

    private func drawActionBar() {
        let barRect = actionBarRect()
        guard barRect.width > 0 else { return }

        // Background pill
        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.35)
        shadow.shadowBlurRadius = 8
        shadow.shadowOffset = NSSize(width: 0, height: -2)
        shadow.set()

        NSColor(white: 0.15, alpha: 0.95).setFill()
        let pill = NSBezierPath(roundedRect: barRect, xRadius: barRect.height / 2, yRadius: barRect.height / 2)
        pill.fill()
        NSGraphicsContext.restoreGraphicsState()

        // Border
        NSColor.white.withAlphaComponent(0.2).setStroke()
        pill.lineWidth = 1.0
        pill.stroke()

        // 取消按钮
        let cRect = cancelBtnRect()
        NSColor.white.withAlphaComponent(0.12).setFill()
        let cPath = NSBezierPath(roundedRect: cRect, xRadius: cRect.height / 2, yRadius: cRect.height / 2)
        cPath.fill()

        let cancelStr = "✕ 取消"
        let cancelAttrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 11, weight: .medium),
            .foregroundColor: NSColor.white.withAlphaComponent(0.85)
        ]
        let cSize = cancelStr.size(withAttributes: cancelAttrs)
        cancelStr.draw(at: NSPoint(x: cRect.midX - cSize.width / 2, y: cRect.midY - cSize.height / 2), withAttributes: cancelAttrs)

        // 确定按钮
        let okRect = confirmBtnRect()
        NSColor(srgbRed: 0.18, green: 0.53, blue: 1.0, alpha: 1.0).setFill()
        let okPath = NSBezierPath(roundedRect: okRect, xRadius: okRect.height / 2, yRadius: okRect.height / 2)
        okPath.fill()

        let okStr = "✓ 确定"
        let okAttrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 11, weight: .bold),
            .foregroundColor: NSColor.white
        ]
        let okSize = okStr.size(withAttributes: okAttrs)
        okStr.draw(at: NSPoint(x: okRect.midX - okSize.width / 2, y: okRect.midY - okSize.height / 2), withAttributes: okAttrs)
    }

    private func drawCrosshair(at p: NSPoint) {
        let arm: CGFloat = 11
        let gap: CGFloat = 3
        let lineWidth: CGFloat = 1.5

        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.55)
        shadow.shadowBlurRadius = 2
        shadow.shadowOffset = .zero
        shadow.set()

        NSColor.white.setStroke()
        let path = NSBezierPath()
        path.lineWidth = lineWidth

        path.move(to: NSPoint(x: p.x, y: p.y + gap))
        path.line(to: NSPoint(x: p.x, y: p.y + arm))
        path.move(to: NSPoint(x: p.x, y: p.y - gap))
        path.line(to: NSPoint(x: p.x, y: p.y - arm))
        path.move(to: NSPoint(x: p.x - gap, y: p.y))
        path.line(to: NSPoint(x: p.x - arm, y: p.y))
        path.move(to: NSPoint(x: p.x + gap, y: p.y))
        path.line(to: NSPoint(x: p.x + arm, y: p.y))

        path.stroke()
        NSGraphicsContext.restoreGraphicsState()
    }

    private func drawDimensions(for rect: NSRect) {
        let label = "\(Int(rect.width)) × \(Int(rect.height))"
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 11, weight: .semibold),
            .foregroundColor: NSColor.white
        ]
        let size = label.size(withAttributes: attrs)
        let padX: CGFloat = 8
        let padY: CGFloat = 4
        let pillW = size.width + padX * 2
        let pillH = size.height + padY * 2

        var originX = rect.midX - pillW / 2
        originX = max(10, min(bounds.width - pillW - 10, originX))
        var originY = rect.minY - pillH - 6
        if originY < 10 {
            originY = rect.maxY + 6
        }

        let pillRect = NSRect(x: originX, y: originY, width: pillW, height: pillH)
        NSColor.black.withAlphaComponent(0.75).setFill()
        let pill = NSBezierPath(roundedRect: pillRect, xRadius: 5, yRadius: 5)
        pill.fill()

        label.draw(at: NSPoint(x: pillRect.minX + padX, y: pillRect.minY + padY), withAttributes: attrs)
    }
}

