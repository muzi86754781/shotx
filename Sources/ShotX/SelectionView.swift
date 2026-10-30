import Cocoa

final class SelectionView: NSView {
    enum SelectionMode {
        case selecting
        case annotating
    }

    enum ToolbarItem: Equatable {
        case tool(AnnotationTool)
        case pin
        case undo
        case ocr
        case cancel
        case divider
        case save
        case copy

        static let allItems: [ToolbarItem] = [
            .tool(.rectangle),
            .tool(.oval),
            .tool(.line),
            .tool(.arrow),
            .tool(.pen),
            .tool(.mosaic),
            .tool(.text),
            .tool(.step),
            .pin,
            .undo,
            .ocr,
            .cancel,
            .divider,
            .save,
            .copy
        ]
    }

    var screenSnapshot: CGImage?
    var targetScreen: NSScreen

    var onActive: (() -> Void)?
    var onFinish: (() -> Void)?
    var onCancel: (() -> Void)?

    private var mode: SelectionMode = .selecting
    private var mousePosition: NSPoint?
    private var startPoint: NSPoint?
    private var currentRect: NSRect = .zero
    private var lockedRatio: CGFloat? = nil
    private var trackingArea: NSTrackingArea?

    // Annotating state
    private var selectionRect: NSRect = .zero
    private var currentTool: AnnotationTool = .rectangle
    private var currentColor: NSColor = .systemRed
    private var thickness: CGFloat = 4
    private var annotations: [Annotation] = []
    private var inProgressAnnotation: Annotation?
    private var annotationDragStart: NSPoint?
    private var activeTextEditor: InlineTextEditor?
    private var nextStepNumber: Int = 1
    private var hoveredButtonIndex: Int? = nil
    private var isHoveringShadowToggle: Bool = false

    override var acceptsFirstResponder: Bool { true }
    override var isFlipped: Bool { false }

    init(frame: NSRect, screen: NSScreen, snapshot: CGImage?) {
        self.targetScreen = screen
        self.screenSnapshot = snapshot
        super.init(frame: frame)
    }

    required init?(coder: NSCoder) { fatalError() }

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

    // MARK: - Mouse Tracking

    override func mouseMoved(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        mousePosition = p

        if mode == .annotating {
            updateToolbarHover(at: p)
        }
        needsDisplay = true
    }

    override func mouseEntered(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        mousePosition = p
        needsDisplay = true
    }

    private func updateToolbarHover(at p: NSPoint) {
        let tbRect = bottomToolbarRect()
        var newIndex: Int? = nil
        if tbRect.contains(p) {
            let items = ToolbarItem.allItems
            for (idx, _) in items.enumerated() {
                let r = NSRect(x: tbRect.minX + 8 + CGFloat(idx) * 33, y: tbRect.minY + 6, width: 30, height: 30)
                if r.contains(p) {
                    newIndex = idx
                    break
                }
            }
        }
        hoveredButtonIndex = newIndex

        let topRect = topBarRect()
        isHoveringShadowToggle = topRect.contains(p) && p.x >= topRect.minX + 105
    }

    // MARK: - Mouse Events

    override func mouseDown(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)

        if mode == .selecting {
            startPoint = p
            currentRect = .zero
            needsDisplay = true
            return
        }

        // Mode is .annotating
        // 1. Check Top Bar click (Shadow toggle)
        let topRect = topBarRect()
        if topRect.contains(p) {
            if p.x >= topRect.minX + 100 {
                PreferencesStore.shared.includeShadow.toggle()
                needsDisplay = true
                return
            }
        }

        // 2. Check Bottom Toolbar click
        let tbRect = bottomToolbarRect()
        if tbRect.contains(p) {
            let items = ToolbarItem.allItems
            for (idx, item) in items.enumerated() {
                let r = NSRect(x: tbRect.minX + 8 + CGFloat(idx) * 33, y: tbRect.minY + 6, width: 30, height: 30)
                if r.contains(p) {
                    handleToolbarItemClick(item)
                    return
                }
            }
            return
        }

        // 3. Double-click inside selection commits and finishes!
        if event.clickCount == 2 && selectionRect.contains(p) {
            handleCopy()
            return
        }

        // 4. Drawing inside selectionRect
        if selectionRect.contains(p) {
            if let editor = activeTextEditor {
                if !editor.frame.contains(p) {
                    editor.commit()
                    return
                }
            }

            annotationDragStart = p
            switch currentTool {
            case .arrow:
                inProgressAnnotation = Annotation(shape: .arrow(from: p, to: p), color: currentColor, thickness: thickness)
            case .rectangle:
                inProgressAnnotation = Annotation(shape: .rectangle(CGRect(origin: p, size: .zero)), color: currentColor, thickness: thickness)
            case .oval:
                inProgressAnnotation = Annotation(shape: .oval(CGRect(origin: p, size: .zero)), color: currentColor, thickness: thickness)
            case .line:
                inProgressAnnotation = Annotation(shape: .line(from: p, to: p), color: currentColor, thickness: thickness)
            case .pen:
                inProgressAnnotation = Annotation(shape: .pen(points: [p]), color: currentColor, thickness: thickness)
            case .mosaic:
                inProgressAnnotation = Annotation(shape: .mosaic(CGRect(origin: p, size: .zero)), color: currentColor, thickness: thickness)
            case .step:
                let ann = Annotation(shape: .step(center: p, number: nextStepNumber), color: currentColor, thickness: thickness)
                nextStepNumber += 1
                annotations.append(ann)
                inProgressAnnotation = nil
            case .text:
                inProgressAnnotation = nil
                startInlineTextEditor(at: p)
            }
            needsDisplay = true
        }
    }

    override func mouseDragged(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        mousePosition = p

        if mode == .selecting {
            guard let start = startPoint else { return }
            var w = abs(p.x - start.x)
            var h = abs(p.y - start.y)

            if let ratio = lockedRatio, ratio > 0 {
                h = w / ratio
            }

            let x = (p.x >= start.x) ? start.x : start.x - w
            let y = (p.y >= start.y) ? start.y : start.y - h

            currentRect = NSRect(x: x, y: y, width: w, height: h).intersection(bounds)
            needsDisplay = true
            return
        }

        // Mode is .annotating
        guard var ann = inProgressAnnotation, let start = annotationDragStart else { return }
        let clampedP = NSPoint(
            x: max(selectionRect.minX, min(selectionRect.maxX, p.x)),
            y: max(selectionRect.minY, min(selectionRect.maxY, p.y))
        )

        switch ann.shape {
        case .arrow:
            ann.shape = .arrow(from: start, to: clampedP)
        case .rectangle:
            ann.shape = .rectangle(CGRect(
                x: min(start.x, clampedP.x), y: min(start.y, clampedP.y),
                width: abs(clampedP.x - start.x), height: abs(clampedP.y - start.y)
            ))
        case .oval:
            ann.shape = .oval(CGRect(
                x: min(start.x, clampedP.x), y: min(start.y, clampedP.y),
                width: abs(clampedP.x - start.x), height: abs(clampedP.y - start.y)
            ))
        case .line:
            ann.shape = .line(from: start, to: clampedP)
        case .pen(var points):
            points.append(clampedP)
            ann.shape = .pen(points: points)
        case .mosaic:
            ann.shape = .mosaic(CGRect(
                x: min(start.x, clampedP.x), y: min(start.y, clampedP.y),
                width: abs(clampedP.x - start.x), height: abs(clampedP.y - start.y)
            ))
        default:
            break
        }

        inProgressAnnotation = ann
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        if mode == .selecting {
            defer {
                startPoint = nil
            }
            if currentRect.width >= 5 && currentRect.height >= 5 {
                selectionRect = currentRect
                mode = .annotating
                onActive?()
                NSCursor.unhide()
                needsDisplay = true
                return
            }
            currentRect = .zero
            needsDisplay = true
            return
        }

        // Mode is .annotating
        defer {
            inProgressAnnotation = nil
            annotationDragStart = nil
            needsDisplay = true
        }

        guard let ann = inProgressAnnotation else { return }
        switch ann.shape {
        case .arrow(let from, let to):
            if hypot(to.x - from.x, to.y - from.y) < 4 { return }
        case .rectangle(let r), .oval(let r), .mosaic(let r):
            if r.width < 4 || r.height < 4 { return }
        case .line(let from, let to):
            if hypot(to.x - from.x, to.y - from.y) < 4 { return }
        case .pen(let pts):
            if pts.isEmpty { return }
        default: break
        }
        annotations.append(ann)
    }

    override func rightMouseDown(with event: NSEvent) {
        handleCancel()
    }

    // MARK: - Keyboard Shortcuts

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 { // ESC
            handleCancel()
            return
        }

        if mode == .annotating {
            // Return / Enter -> Copy to clipboard & Finish
            if event.keyCode == 36 || event.keyCode == 76 {
                handleCopy()
                return
            }
            // Cmd+C -> Copy
            if event.modifierFlags.contains(.command) && event.charactersIgnoringModifiers == "c" {
                handleCopy()
                return
            }
            // Cmd+S -> Save
            if event.modifierFlags.contains(.command) && event.charactersIgnoringModifiers == "s" {
                handleSave()
                return
            }
            // Cmd+Z -> Undo
            if event.modifierFlags.contains(.command) && event.charactersIgnoringModifiers == "z" {
                handleUndo()
                return
            }
        }

        if mode == .selecting {
            let char = event.charactersIgnoringModifiers?.lowercased()
            switch char {
            case "d":
                handleSelectPrevious()
                return
            case "f":
                handleSelectFullscreen()
                return
            case "r":
                handleCopyRGB()
                return
            case "h":
                handleCopyHEX()
                return
            case "z":
                handleCopyCustom()
                return
            case "0":
                lockedRatio = nil
                showToast("已切换为自由比例 (Free)")
                needsDisplay = true
                return
            case "1":
                lockedRatio = 1.0 / 1.0
                showToast("已锁定比例 1:1")
                needsDisplay = true
                return
            case "2":
                lockedRatio = 2.0 / 3.0
                showToast("已锁定比例 2:3")
                needsDisplay = true
                return
            case "3":
                lockedRatio = 3.0 / 4.0
                showToast("已锁定比例 3:4")
                needsDisplay = true
                return
            case "4":
                lockedRatio = 4.0 / 5.0
                showToast("已锁定比例 4:5")
                needsDisplay = true
                return
            case "5":
                lockedRatio = 5.0 / 7.0
                showToast("已锁定比例 5:7")
                needsDisplay = true
                return
            case "6":
                lockedRatio = 9.0 / 16.0
                showToast("已锁定比例 9:16")
                needsDisplay = true
                return
            case "7":
                lockedRatio = 16.0 / 9.0
                showToast("已锁定比例 16:9")
                needsDisplay = true
                return
            case "8":
                lockedRatio = 3.0 / 2.0
                showToast("已锁定比例 3:2")
                needsDisplay = true
                return
            case "9":
                lockedRatio = 4.0 / 3.0
                showToast("已锁定比例 4:3")
                needsDisplay = true
                return
            default:
                break
            }
        }

        super.keyDown(with: event)
    }

    // MARK: - Actions

    private func handleToolbarItemClick(_ item: ToolbarItem) {
        switch item {
        case .tool(let tool):
            currentTool = tool
            needsDisplay = true
        case .pin:
            handlePin()
        case .undo:
            handleUndo()
        case .ocr:
            handleOCR()
        case .cancel:
            handleCancel()
        case .divider:
            break
        case .save:
            handleSave()
        case .copy:
            handleCopy()
        }
    }

    private func handleSelectPrevious() {
        guard let stored = LastCaptureStore.load() else {
            NSSound.beep()
            return
        }
        let localRect = stored.rect.offsetBy(dx: -targetScreen.frame.origin.x, dy: -targetScreen.frame.origin.y)
        if bounds.intersects(localRect) {
            selectionRect = localRect.intersection(bounds)
            mode = .annotating
            onActive?()
            NSCursor.unhide()
            needsDisplay = true
        }
    }

    private func handleSelectFullscreen() {
        selectionRect = bounds
        mode = .annotating
        onActive?()
        NSCursor.unhide()
        needsDisplay = true
    }

    private func handleCopyRGB() {
        let color = currentPixelColor()
        let rgbString = "\(color.r), \(color.g), \(color.b)"
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(rgbString, forType: .string)
        showToast("已拷贝 RGB: \(rgbString)")
    }

    private func handleCopyHEX() {
        let color = currentPixelColor()
        let hexString = "#\(color.hex)"
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(hexString, forType: .string)
        showToast("已拷贝 HEX: \(hexString)")
    }

    private func handleCopyCustom() {
        let color = currentPixelColor()
        let customString = "rgba(\(color.r), \(color.g), \(color.b), 1.0)"
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(customString, forType: .string)
        showToast("已拷贝颜色: \(customString)")
    }

    private func handleUndo() {
        guard !annotations.isEmpty else { return }
        let removed = annotations.removeLast()
        if case .step = removed.shape {
            nextStepNumber = max(1, nextStepNumber - 1)
        }
        needsDisplay = true
    }

    private func handleCancel() {
        activeTextEditor?.cancel()
        onCancel?()
    }

    private func handleCopy() {
        activeTextEditor?.commit()
        guard let image = generateFinalImage() else { return }
        SoundEffect.shared.playShutter()
        ImageSaver.copyToClipboard(image)
        HistoryStore.shared.add(image)

        let globalRect = selectionRect.offsetBy(dx: targetScreen.frame.origin.x, dy: targetScreen.frame.origin.y)
        LastCaptureStore.save(rect: globalRect, screen: targetScreen)

        onFinish?()
    }

    private func handleSave() {
        activeTextEditor?.commit()
        guard let image = generateFinalImage() else { return }
        SoundEffect.shared.playShutter()
        _ = ImageSaver.saveToPreferencesFolder(image)
        ImageSaver.copyToClipboard(image)
        HistoryStore.shared.add(image)

        let globalRect = selectionRect.offsetBy(dx: targetScreen.frame.origin.x, dy: targetScreen.frame.origin.y)
        LastCaptureStore.save(rect: globalRect, screen: targetScreen)

        onFinish?()
    }

    private func handlePin() {
        activeTextEditor?.commit()
        guard let image = generateFinalImage() else { return }
        SoundEffect.shared.playShutter()
        PinnedImageController.shared.pin(image)
        HistoryStore.shared.add(image)
        onFinish?()
    }

    private func handleOCR() {
        activeTextEditor?.commit()
        guard let image = generateFinalImage() else { return }
        onFinish?()
        if let cg = OCR.cgImage(from: image) {
            Task { @MainActor in
                await OCR.runAndCopy(from: cg)
            }
        }
    }

    private func showToast(_ msg: String) {
        ToastController.shared.show(
            message: msg,
            icon: "eyedropper.halffull",
            tint: .systemBlue,
            duration: 1.5
        )
    }

    private func startInlineTextEditor(at point: NSPoint) {
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

    // MARK: - Final Image Generation

    private func generateFinalImage() -> NSImage? {
        guard selectionRect.width > 2, selectionRect.height > 2, let snapshot = screenSnapshot else { return nil }

        let scale = window?.backingScaleFactor ?? 2.0
        let pixelX = selectionRect.minX * scale
        let pixelY = (bounds.height - selectionRect.maxY) * scale
        let pixelW = selectionRect.width * scale
        let pixelH = selectionRect.height * scale
        let cropRect = CGRect(x: pixelX, y: pixelY, width: pixelW, height: pixelH)

        guard let croppedCG = snapshot.cropping(to: cropRect) else { return nil }
        let baseImage = NSImage(cgImage: croppedCG, size: selectionRect.size)

        let annotated = NSImage(size: selectionRect.size)
        annotated.lockFocus()
        baseImage.draw(in: NSRect(origin: .zero, size: selectionRect.size))

        for ann in annotations {
            let localAnn = ann.translatedBy(dx: -selectionRect.minX, dy: -selectionRect.minY)
            AnnotationRenderer.draw(localAnn, baseImage: baseImage)
        }
        annotated.unlockFocus()

        if PreferencesStore.shared.includeShadow {
            return addShadow(to: annotated)
        }

        return annotated
    }

    private func addShadow(to image: NSImage) -> NSImage {
        let padding: CGFloat = 30
        let newSize = NSSize(width: image.size.width + padding * 2, height: image.size.height + padding * 2)
        let shadowImage = NSImage(size: newSize)
        shadowImage.lockFocus()

        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.35)
        shadow.shadowOffset = NSSize(width: 0, height: -5)
        shadow.shadowBlurRadius = 16
        shadow.set()

        let destRect = NSRect(x: padding, y: padding, width: image.size.width, height: image.size.height)
        let path = NSBezierPath(roundedRect: destRect, xRadius: 6, yRadius: 6)
        NSColor.black.setFill()
        path.fill()
        NSGraphicsContext.restoreGraphicsState()

        let clipPath = NSBezierPath(roundedRect: destRect, xRadius: 6, yRadius: 6)
        clipPath.addClip()
        image.draw(in: destRect, from: .zero, operation: .sourceOver, fraction: 1.0)

        shadowImage.unlockFocus()
        return shadowImage
    }

    // MARK: - Drawing

    override func draw(_ dirtyRect: NSRect) {
        // Dim the whole display
        let dimAlpha: CGFloat = (mode == .annotating) ? 0.45 : 0.28
        NSColor.black.withAlphaComponent(dimAlpha).setFill()
        bounds.fill()

        if mode == .selecting {
            drawSelectingState()
        } else {
            drawAnnotatingState()
        }
    }

    // MARK: - Selecting State (111.jpg)

    private func drawSelectingState() {
        // If user is dragging rect
        if currentRect.width > 0 && currentRect.height > 0 {
            NSColor.clear.setFill()
            currentRect.fill(using: .copy)

            NSColor(srgbRed: 0.18, green: 0.53, blue: 1.0, alpha: 1.0).setStroke()
            let border = NSBezierPath(rect: currentRect)
            border.lineWidth = 1.5
            border.stroke()
            return
        }

        guard let pos = mousePosition else { return }

        // 1. Fullscreen Crosshairs
        drawFullscreenCrosshair(at: pos)

        // 2. Magnifier Loupe
        drawLoupe(at: pos)

        // 3. Lower-Left HUD Card (matching 111.jpg)
        drawHUDCard()
    }

    private func drawFullscreenCrosshair(at p: NSPoint) {
        let path = NSBezierPath()
        path.lineWidth = 1.0

        path.move(to: NSPoint(x: 0, y: p.y))
        path.line(to: NSPoint(x: bounds.width, y: p.y))

        path.move(to: NSPoint(x: p.x, y: 0))
        path.line(to: NSPoint(x: p.x, y: bounds.height))

        NSColor(srgbRed: 0.25, green: 0.55, blue: 0.95, alpha: 0.8).setStroke()
        path.stroke()
    }

    private func drawLoupe(at p: NSPoint) {
        let loupeSize: CGFloat = 140
        var lx = p.x + 18
        var ly = p.y + 18

        if lx + loupeSize > bounds.width - 15 {
            lx = p.x - loupeSize - 18
        }
        if ly + loupeSize > bounds.height - 15 {
            ly = p.y - loupeSize - 18
        }

        let loupeRect = NSRect(x: lx, y: ly, width: loupeSize, height: loupeSize)

        // Draw pixel grid from screen snapshot
        let scale = window?.backingScaleFactor ?? 2.0
        let pixelX = Int(p.x * scale)
        let pixelY = Int((bounds.height - p.y) * scale)

        NSGraphicsContext.saveGraphicsState()
        let clip = NSBezierPath(rect: loupeRect)
        clip.addClip()

        if let snapshot = screenSnapshot {
            let cropRect = CGRect(x: pixelX - 8, y: pixelY - 8, width: 17, height: 17)
            if let patch = snapshot.cropping(to: cropRect) {
                NSGraphicsContext.current?.imageInterpolation = .none
                let patchImage = NSImage(cgImage: patch, size: NSSize(width: loupeSize, height: loupeSize))
                patchImage.draw(in: loupeRect)
            }
        }

        // Draw grid lines
        let cellW = loupeSize / 17.0
        let gridPath = NSBezierPath()
        gridPath.lineWidth = 0.5
        for i in 1..<17 {
            let x = loupeRect.minX + CGFloat(i) * cellW
            gridPath.move(to: NSPoint(x: x, y: loupeRect.minY))
            gridPath.line(to: NSPoint(x: x, y: loupeRect.maxY))
            let y = loupeRect.minY + CGFloat(i) * cellW
            gridPath.move(to: NSPoint(x: loupeRect.minX, y: y))
            gridPath.line(to: NSPoint(x: loupeRect.maxX, y: y))
        }
        NSColor(white: 0.0, alpha: 0.15).setStroke()
        gridPath.stroke()

        // Center crosshair & cell highlight
        let centerCell = NSRect(
            x: loupeRect.minX + 8 * cellW,
            y: loupeRect.minY + 8 * cellW,
            width: cellW,
            height: cellW
        )
        let centerCross = NSBezierPath()
        centerCross.lineWidth = 1.5
        centerCross.move(to: NSPoint(x: loupeRect.minX, y: centerCell.midY))
        centerCross.line(to: NSPoint(x: loupeRect.maxX, y: centerCell.midY))
        centerCross.move(to: NSPoint(x: centerCell.midX, y: loupeRect.minY))
        centerCross.line(to: NSPoint(x: centerCell.midX, y: loupeRect.maxY))
        NSColor(srgbRed: 0.18, green: 0.53, blue: 1.0, alpha: 0.85).setStroke()
        centerCross.stroke()

        let box = NSBezierPath(rect: centerCell)
        box.lineWidth = 1.5
        box.stroke()

        NSGraphicsContext.restoreGraphicsState()

        // Loupe outer border
        NSColor(srgbRed: 0.18, green: 0.53, blue: 1.0, alpha: 1.0).setStroke()
        let outer = NSBezierPath(rect: loupeRect)
        outer.lineWidth = 2.0
        outer.stroke()

        let color = currentPixelColor()

        // Top Header (Chinese Color Name)
        let headerRect = NSRect(x: loupeRect.minX, y: loupeRect.maxY - 22, width: loupeSize, height: 22)
        NSColor.white.withAlphaComponent(0.92).setFill()
        headerRect.fill()

        let nameAttrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 12, weight: .bold),
            .foregroundColor: NSColor(white: 0.1, alpha: 1.0)
        ]
        let nameSize = color.name.size(withAttributes: nameAttrs)
        color.name.draw(
            at: NSPoint(x: headerRect.midX - nameSize.width / 2, y: headerRect.midY - nameSize.height / 2),
            withAttributes: nameAttrs
        )

        // Bottom Bar (Coordinates and RGB / HEX)
        let bottomRect = NSRect(x: loupeRect.minX, y: loupeRect.minY, width: loupeSize, height: 28)
        NSColor.white.withAlphaComponent(0.92).setFill()
        bottomRect.fill()

        let globalX = Int(targetScreen.frame.origin.x + p.x)
        let globalY = Int(targetScreen.frame.origin.y + p.y)
        let coordText = "\(globalX),\(globalY)"
        let coordAttrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedSystemFont(ofSize: 10, weight: .medium),
            .foregroundColor: NSColor(white: 0.15, alpha: 1.0)
        ]
        coordText.draw(at: NSPoint(x: bottomRect.minX + 4, y: bottomRect.minY + 7), withAttributes: coordAttrs)

        let rgbText = "\(color.r),\(color.g),\(color.b)"
        let hexText = color.hex
        let valAttrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedSystemFont(ofSize: 9, weight: .semibold),
            .foregroundColor: NSColor(white: 0.15, alpha: 1.0)
        ]
        let rgbSize = rgbText.size(withAttributes: valAttrs)
        let hexSize = hexText.size(withAttributes: valAttrs)
        rgbText.draw(at: NSPoint(x: bottomRect.maxX - rgbSize.width - 4, y: bottomRect.minY + 14), withAttributes: valAttrs)
        hexText.draw(at: NSPoint(x: bottomRect.maxX - hexSize.width - 4, y: bottomRect.minY + 2), withAttributes: valAttrs)
    }

    private func drawHUDCard() {
        let hudRect = NSRect(x: 24, y: 24, width: 310, height: 200)

        NSColor(white: 0.08, alpha: 0.94).setFill()
        let bg = NSBezierPath(roundedRect: hudRect, xRadius: 8, yRadius: 8)
        bg.fill()

        let lines: [String] = [
            "单击 D 选择上一次截图区域",
            "单击 F 选择全屏",
            "点按数字按键锁定框选区域宽度:高度比例",
            "0=Free  1=1:1  2=2:3   3=3:4  4=4:5",
            "5=5:7   6=9:16 7=16:9  8=3:2  9=4:3",
            "单击 ESC 或者右键退出截图",
            "",
            "单击 R 拷贝 RGB 颜色",
            "单击 H 拷贝 HEX 颜色",
            "单击 Z 拷贝自定义颜色"
        ]

        let textColor = NSColor(srgbRed: 1.0, green: 0.42, blue: 0.42, alpha: 1.0)
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedSystemFont(ofSize: 11.5, weight: .medium),
            .foregroundColor: textColor
        ]

        var y = hudRect.maxY - 20
        for line in lines {
            if !line.isEmpty {
                line.draw(at: NSPoint(x: hudRect.minX + 14, y: y), withAttributes: attrs)
            }
            y -= 18
        }
    }

    // MARK: - Annotating State (222.jpg)

    private func drawAnnotatingState() {
        guard selectionRect.width > 0, selectionRect.height > 0 else { return }

        // 1. Punch out the selection area
        NSColor.clear.setFill()
        selectionRect.fill(using: .copy)

        // 2. Draw annotations inside selection
        NSGraphicsContext.saveGraphicsState()
        let clip = NSBezierPath(rect: selectionRect)
        clip.addClip()
        for ann in annotations {
            AnnotationRenderer.draw(ann, baseImage: nil)
        }
        if let inProg = inProgressAnnotation {
            AnnotationRenderer.draw(inProg, baseImage: nil)
        }
        NSGraphicsContext.restoreGraphicsState()

        // 3. Draw 4 Blue L-Corner Brackets
        drawCornerBrackets()

        // 4. Draw Top Pill (Size and Shadow Switch)
        drawTopBar()

        // 5. Draw Bottom White Capsule Toolbar
        drawBottomToolbar()
    }

    private func drawCornerBrackets() {
        let len: CGFloat = 18
        let r = selectionRect
        let color = NSColor(srgbRed: 0.18, green: 0.53, blue: 1.0, alpha: 1.0)
        color.setStroke()

        let path = NSBezierPath()
        path.lineWidth = 3.5
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
    }

    private func topBarRect() -> NSRect {
        let w: CGFloat = 175
        let h: CGFloat = 28
        let x = max(10, min(bounds.width - w - 10, selectionRect.minX))
        var y = selectionRect.maxY + 6
        if y + h > bounds.height - 8 {
            y = selectionRect.maxY - h - 6
        }
        return NSRect(x: x, y: y, width: w, height: h)
    }

    private func drawTopBar() {
        let r = topBarRect()

        NSColor(srgbRed: 0.12, green: 0.53, blue: 0.98, alpha: 1.0).setFill()
        let pill = NSBezierPath(roundedRect: r, xRadius: 6, yRadius: 6)
        pill.fill()

        // Size text: "W × H"
        let sizeText = "\(Int(selectionRect.width))  ×  \(Int(selectionRect.height))"
        let sizeAttrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 12, weight: .bold),
            .foregroundColor: NSColor.white
        ]
        sizeText.draw(at: NSPoint(x: r.minX + 10, y: r.minY + 6), withAttributes: sizeAttrs)

        // Dot separator
        NSColor.white.setFill()
        let dot = NSBezierPath(ovalIn: NSRect(x: r.minX + 96, y: r.minY + 11, width: 6, height: 6))
        dot.fill()

        // Shadow Switch & Label
        let shadowText = "阴影"
        let shadowAttrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 11, weight: .semibold),
            .foregroundColor: NSColor.white
        ]
        shadowText.draw(at: NSPoint(x: r.minX + 138, y: r.minY + 6.5), withAttributes: shadowAttrs)

        // Toggle Switch Icon
        let switchRect = NSRect(x: r.minX + 112, y: r.minY + 7, width: 22, height: 14)
        let isShadowOn = PreferencesStore.shared.includeShadow

        let switchPath = NSBezierPath(roundedRect: switchRect, xRadius: 7, yRadius: 7)
        if isShadowOn {
            NSColor.white.setFill()
            switchPath.fill()
            let knob = NSBezierPath(ovalIn: NSRect(x: switchRect.maxX - 13, y: switchRect.minY + 1.5, width: 11, height: 11))
            NSColor(srgbRed: 0.12, green: 0.53, blue: 0.98, alpha: 1.0).setFill()
            knob.fill()
        } else {
            NSColor.white.withAlphaComponent(0.3).setFill()
            switchPath.fill()
            let knob = NSBezierPath(ovalIn: NSRect(x: switchRect.minX + 2, y: switchRect.minY + 1.5, width: 11, height: 11))
            NSColor.white.setFill()
            knob.fill()
        }
    }

    private func bottomToolbarRect() -> NSRect {
        let itemCount = ToolbarItem.allItems.count
        let w = CGFloat(itemCount * 33 + 16)
        let h: CGFloat = 42

        let x = max(10, min(bounds.width - w - 10, selectionRect.midX - w / 2))
        var y = selectionRect.minY - 50
        if y < 10 {
            y = (selectionRect.height > 140) ? (selectionRect.minY + 12) : (selectionRect.maxY + 40)
        }
        return NSRect(x: x, y: y, width: w, height: h)
    }

    private func drawBottomToolbar() {
        let r = bottomToolbarRect()

        // Soft drop shadow
        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.25)
        shadow.shadowBlurRadius = 8
        shadow.shadowOffset = NSSize(width: 0, height: -2)
        shadow.set()

        NSColor.white.setFill()
        let bg = NSBezierPath(roundedRect: r, xRadius: 12, yRadius: 12)
        bg.fill()
        NSGraphicsContext.restoreGraphicsState()

        NSColor(white: 0.88, alpha: 1.0).setStroke()
        let border = NSBezierPath(roundedRect: r, xRadius: 12, yRadius: 12)
        border.lineWidth = 1.0
        border.stroke()

        // Draw buttons
        let items = ToolbarItem.allItems
        for (idx, item) in items.enumerated() {
            let btnRect = NSRect(x: r.minX + 8 + CGFloat(idx) * 33, y: r.minY + 6, width: 30, height: 30)

            let isHovered = (hoveredButtonIndex == idx)
            var isSelected = false
            if case .tool(let t) = item, t == currentTool {
                isSelected = true
            }

            if isSelected {
                NSColor(srgbRed: 0.18, green: 0.53, blue: 1.0, alpha: 0.18).setFill()
                NSBezierPath(roundedRect: btnRect, xRadius: 6, yRadius: 6).fill()
            } else if isHovered {
                NSColor.black.withAlphaComponent(0.06).setFill()
                NSBezierPath(roundedRect: btnRect, xRadius: 6, yRadius: 6).fill()
            }

            drawToolbarItem(item, in: btnRect, isSelected: isSelected)
        }
    }

    private func drawToolbarItem(_ item: ToolbarItem, in rect: NSRect, isSelected: Bool) {
        let tint = isSelected ? NSColor(srgbRed: 0.12, green: 0.53, blue: 0.98, alpha: 1.0) : NSColor(white: 0.25, alpha: 1.0)

        switch item {
        case .tool(let tool):
            switch tool {
            case .rectangle:
                tint.setStroke()
                let p = NSBezierPath(rect: rect.insetBy(dx: 7, dy: 7))
                p.lineWidth = 1.8
                p.stroke()
            case .oval:
                tint.setStroke()
                let p = NSBezierPath(ovalIn: rect.insetBy(dx: 6, dy: 6))
                p.lineWidth = 1.8
                p.stroke()
            case .line:
                tint.setStroke()
                let p = NSBezierPath()
                p.lineWidth = 1.8
                p.move(to: NSPoint(x: rect.minX + 7, y: rect.minY + 7))
                p.line(to: NSPoint(x: rect.maxX - 7, y: rect.maxY - 7))
                p.stroke()
            case .arrow:
                drawSystemIcon("arrow.up.right", in: rect, tint: tint)
            case .pen:
                drawSystemIcon("pencil", in: rect, tint: tint)
            case .mosaic:
                if NSImage(systemSymbolName: "checkerboard.rectangle", accessibilityDescription: nil) != nil {
                    drawSystemIcon("checkerboard.rectangle", in: rect, tint: tint)
                } else {
                    let sq = rect.insetBy(dx: 8, dy: 8)
                    tint.setFill()
                    NSRect(x: sq.minX, y: sq.midY, width: sq.width / 2, height: sq.height / 2).fill()
                    NSRect(x: sq.midX, y: sq.minY, width: sq.width / 2, height: sq.height / 2).fill()
                    tint.setStroke()
                    let b = NSBezierPath(rect: sq)
                    b.lineWidth = 1.2
                    b.stroke()
                }
            case .text:
                let attrs: [NSAttributedString.Key: Any] = [
                    .font: NSFont.systemFont(ofSize: 15, weight: .bold),
                    .foregroundColor: tint
                ]
                let sz = "A".size(withAttributes: attrs)
                "A".draw(at: NSPoint(x: rect.midX - sz.width / 2, y: rect.midY - sz.height / 2), withAttributes: attrs)
            case .step:
                let attrs: [NSAttributedString.Key: Any] = [
                    .font: NSFont.systemFont(ofSize: 14, weight: .bold),
                    .foregroundColor: tint
                ]
                let sz = "①".size(withAttributes: attrs)
                "①".draw(at: NSPoint(x: rect.midX - sz.width / 2, y: rect.midY - sz.height / 2), withAttributes: attrs)
            }
        case .pin:
            drawSystemIcon("pin.fill", in: rect, tint: tint)
        case .undo:
            drawSystemIcon("arrow.uturn.backward", in: rect, tint: tint)
        case .ocr:
            // Custom OCR Badge (as shown in 222.jpg)
            let badgeRect = NSRect(x: rect.minX + 3, y: rect.minY + 6, width: 24, height: 18)
            NSColor(white: 0.15, alpha: 1.0).setFill()
            NSBezierPath(roundedRect: badgeRect, xRadius: 4, yRadius: 4).fill()
            let attrs: [NSAttributedString.Key: Any] = [
                .font: NSFont.systemFont(ofSize: 8.5, weight: .heavy),
                .foregroundColor: NSColor.white
            ]
            let sz = "OCR".size(withAttributes: attrs)
            "OCR".draw(at: NSPoint(x: badgeRect.midX - sz.width / 2, y: badgeRect.midY - sz.height / 2), withAttributes: attrs)
        case .cancel:
            drawSystemIcon("xmark", in: rect, tint: tint)
        case .divider:
            NSColor(white: 0.85, alpha: 1.0).setFill()
            NSRect(x: rect.midX - 0.5, y: rect.minY + 6, width: 1, height: 18).fill()
        case .save:
            if NSImage(systemSymbolName: "arrow.down.to.line", accessibilityDescription: nil) != nil {
                drawSystemIcon("arrow.down.to.line", in: rect, tint: tint)
            } else {
                drawSystemIcon("square.and.arrow.down", in: rect, tint: tint)
            }
        case .copy:
            drawSystemIcon("checkmark", in: rect, tint: NSColor.systemGreen)
        }
    }

    private func drawSystemIcon(_ name: String, in rect: NSRect, tint: NSColor) {
        let config = NSImage.SymbolConfiguration(pointSize: 14, weight: .semibold)
            .applying(.init(paletteColors: [tint]))
        if let icon = NSImage(systemSymbolName: name, accessibilityDescription: nil)?.withSymbolConfiguration(config) {
            let iconSize = icon.size
            let origin = NSPoint(
                x: rect.midX - iconSize.width / 2,
                y: rect.midY - iconSize.height / 2
            )
            NSGraphicsContext.saveGraphicsState()
            tint.setFill()
            icon.draw(at: origin, from: .zero, operation: .sourceOver, fraction: 1.0)
            NSGraphicsContext.restoreGraphicsState()
        }
    }

    // MARK: - Color Calculation

    private func currentPixelColor() -> (r: Int, g: Int, b: Int, hex: String, name: String) {
        guard let pos = mousePosition, let snapshot = screenSnapshot else {
            return (255, 255, 255, "FFFFFF", "白色")
        }
        let scale = window?.backingScaleFactor ?? 2.0
        let pixelX = Int(pos.x * scale)
        let pixelY = Int((bounds.height - pos.y) * scale)
        guard pixelX >= 0, pixelX < snapshot.width, pixelY >= 0, pixelY < snapshot.height else {
            return (255, 255, 255, "FFFFFF", "白色")
        }

        var pixel: [UInt8] = [0, 0, 0, 0]
        let colorSpace = CGColorSpaceCreateDeviceRGB()
        let bitmapInfo = CGImageAlphaInfo.premultipliedLast.rawValue
        if let ctx = CGContext(data: &pixel, width: 1, height: 1, bitsPerComponent: 8, bytesPerRow: 4, space: colorSpace, bitmapInfo: bitmapInfo),
           let cropped = snapshot.cropping(to: CGRect(x: pixelX, y: pixelY, width: 1, height: 1)) {
            ctx.draw(cropped, in: CGRect(x: 0, y: 0, width: 1, height: 1))
            let r = Int(pixel[0])
            let g = Int(pixel[1])
            let b = Int(pixel[2])
            let hex = String(format: "%02X%02X%02X", r, g, b)
            let name = chineseColorName(r: r, g: g, b: b)
            return (r, g, b, hex, name)
        }
        return (255, 255, 255, "FFFFFF", "白色")
    }

    private func chineseColorName(r: Int, g: Int, b: Int) -> String {
        let rf = Double(r) / 255.0
        let gf = Double(g) / 255.0
        let bf = Double(b) / 255.0
        let maxV = max(rf, gf, bf)
        let minV = min(rf, gf, bf)
        let delta = maxV - minV

        if delta < 0.08 {
            if maxV > 0.88 { return "白色" }
            if maxV > 0.65 { return "浅灰" }
            if maxV > 0.35 { return "灰色" }
            if maxV > 0.12 { return "深灰" }
            return "黑色"
        }

        var hue: Double = 0
        if rf == maxV {
            hue = 60.0 * (((gf - bf) / delta).truncatingRemainder(dividingBy: 6))
        } else if gf == maxV {
            hue = 60.0 * (((bf - rf) / delta) + 2)
        } else {
            hue = 60.0 * (((rf - gf) / delta) + 4)
        }
        if hue < 0 { hue += 360 }

        let brightness = maxV
        let saturation = delta / maxV

        if brightness < 0.18 { return "黑色" }
        if saturation < 0.15 {
            if brightness > 0.8 { return "白色" }
            return "灰色"
        }

        switch hue {
        case 0..<15, 345...360:
            return brightness > 0.8 && saturation < 0.6 ? "粉色" : "红色"
        case 15..<45:
            return saturation > 0.5 ? "橙色" : "棕色"
        case 45..<70:
            return "黄色"
        case 70..<165:
            return "绿色"
        case 165..<200:
            return "青色"
        case 200..<260:
            return "蓝色"
        case 260..<300:
            return "紫色"
        case 300..<345:
            return "洋红"
        default:
            return "彩色"
        }
    }
}
