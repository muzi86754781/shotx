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

    override func mouseMoved(with event: NSEvent) {
        mousePosition = convert(event.locationInWindow, from: nil)
        needsDisplay = true
    }

    override func mouseEntered(with event: NSEvent) {
        mousePosition = convert(event.locationInWindow, from: nil)
        needsDisplay = true
    }

    override func mouseDown(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        startPoint = p
        currentRect = .zero
        mousePosition = p
        needsDisplay = true
    }

    override func mouseDragged(with event: NSEvent) {
        guard let start = startPoint else { return }
        let p = convert(event.locationInWindow, from: nil)
        currentRect = NSRect(
            x: min(start.x, p.x),
            y: min(start.y, p.y),
            width: abs(p.x - start.x),
            height: abs(p.y - start.y)
        )
        mousePosition = p
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        defer {
            startPoint = nil
            currentRect = .zero
            needsDisplay = true
        }
        if currentRect.width < 5 || currentRect.height < 5 {
            onCancel?()
            return
        }
        onSelect?(currentRect)
    }

    override func rightMouseDown(with event: NSEvent) {
        onCancel?()
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 { // Escape
            onCancel?()
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
            NSColor.white.setStroke()
            let border = NSBezierPath(rect: currentRect.insetBy(dx: 0.5, dy: 0.5))
            border.lineWidth = 1.5
            border.stroke()

            drawDimensions(for: currentRect)
        }

        // 3. Draw crosshair cursor
        if let pos = mousePosition {
            drawCrosshair(at: pos)
        }
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

