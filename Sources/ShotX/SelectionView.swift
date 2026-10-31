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
        case scrolling // 长截图 (222.jpg)
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
            .scrolling,
            .save,
            .copy
        ]
    }

    var screenSnapshot: CGImage?
    var targetScreen: NSScreen

    var isOCRMode: Bool = false
    var onOCRSelected: ((NSImage) -> Void)?

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
    private var isDraggingCornerSlider: Bool = false

    // Scrolling screenshot state (333.jpg)
    private var isScrollingMode: Bool = false
    private var stitchedLongImage: NSImage?
    private var lastCapturedFrame: NSImage?
    private var scrollingScrollMonitor: Any?
    private var scrollingMouseMonitor: Any?
    private var scrollingKeyMonitor: Any?
    private var scrollingTimer: Timer?
    private var lastScrollCaptureTime: TimeInterval = 0

    private var currentToolbarItems: [ToolbarItem] {
        if isScrollingMode {
            return [.cancel, .save, .copy]
        } else {
            return ToolbarItem.allItems
        }
    }

    override var acceptsFirstResponder: Bool { true }
    override var isFlipped: Bool { false }

    init(frame: NSRect, screen: NSScreen, snapshot: CGImage?, isOCR: Bool = false) {
        self.targetScreen = screen
        self.screenSnapshot = snapshot
        self.isOCRMode = isOCR
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
            let items = currentToolbarItems
            for (idx, _) in items.enumerated() {
                let r = NSRect(x: tbRect.minX + 8 + CGFloat(idx) * 33, y: tbRect.minY + 6, width: 30, height: 30)
                if r.contains(p) {
                    newIndex = idx
                    break
                }
            }
        }
        hoveredButtonIndex = newIndex
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
        // 1. Check Top Bar click (Corner Radius Slider & Shadow toggle)
        let topRect = topBarRect()
        if topRect.contains(p) {
            // Slider area: between dimension text and shadow toggle
            let sliderMinX = topRect.minX + 85
            let sliderMaxX = topRect.minX + 150
            if p.x >= sliderMinX && p.x <= sliderMaxX {
                isDraggingCornerSlider = true
                updateCornerRadius(at: p.x, minX: sliderMinX, maxX: sliderMaxX)
                return
            }

            // Shadow switch area
            if p.x >= topRect.minX + 155 {
                PreferencesStore.shared.includeShadow.toggle()
                needsDisplay = true
                return
            }
        }

        // 2. Check Bottom Toolbar click
        let tbRect = bottomToolbarRect()
        if tbRect.contains(p) {
            let items = currentToolbarItems
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
            let w = abs(p.x - start.x)
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
        if isDraggingCornerSlider {
            let topRect = topBarRect()
            let sliderMinX = topRect.minX + 85
            let sliderMaxX = topRect.minX + 150
            updateCornerRadius(at: p.x, minX: sliderMinX, maxX: sliderMaxX)
            return
        }

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
        if isDraggingCornerSlider {
            isDraggingCornerSlider = false
            return
        }

        if mode == .selecting {
            defer {
                startPoint = nil
            }
            if currentRect.width >= 5 && currentRect.height >= 5 {
                selectionRect = currentRect

                // If in OCR-only mode, directly finish without entering annotating
                if isOCRMode {
                    if let image = generateBaseCroppedImage() {
                        onOCRSelected?(image)
                    }
                    onFinish?()
                    return
                }

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

    // MARK: - Corner Radius Slider

    private func updateCornerRadius(at currentX: CGFloat, minX: CGFloat, maxX: CGFloat) {
        let width = maxX - minX
        guard width > 0 else { return }
        let ratio = max(0, min(1, (currentX - minX) / width))
        PreferencesStore.shared.cornerRadius = round(ratio * 36.0)
        needsDisplay = true
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
        case .scrolling:
            handleScrollingCapture()
        case .save:
            handleSave()
        case .copy:
            handleCopy()
        }
    }

    private func handleSelectPrevious() {
        guard let stored = LastCaptureStore.load() else {
            NSSound.beep()
            showToast("暂无上一次截图区域")
            return
        }
        let localRect = stored.rect.offsetBy(dx: -targetScreen.frame.origin.x, dy: -targetScreen.frame.origin.y)
        if bounds.intersects(localRect) {
            selectionRect = localRect.intersection(bounds)

            if isOCRMode {
                if let image = generateBaseCroppedImage() {
                    onOCRSelected?(image)
                }
                onFinish?()
                return
            }

            mode = .annotating
            onActive?()
            NSCursor.unhide()
            needsDisplay = true
        }
    }

    private func handleSelectFullscreen() {
        selectionRect = bounds
        if isOCRMode {
            if let image = generateBaseCroppedImage() {
                onOCRSelected?(image)
            }
            onFinish?()
            return
        }
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
        cleanupScrollingMode()
        onCancel?()
    }

    private func handleCopy() {
        activeTextEditor?.commit()
        let imageToCopy = isScrollingMode ? (stitchedLongImage ?? generateFinalImage()) : generateFinalImage()
        cleanupScrollingMode()
        guard let image = imageToCopy else { return }
        SoundEffect.shared.playShutter()
        ImageSaver.copyToClipboard(image)
        HistoryStore.shared.add(image)

        let globalRect = selectionRect.offsetBy(dx: targetScreen.frame.origin.x, dy: targetScreen.frame.origin.y)
        LastCaptureStore.save(rect: globalRect, screen: targetScreen)

        ToastController.shared.show(
            message: "已复制到剪贴板并记录",
            icon: "checkmark.circle.fill",
            tint: .systemGreen,
            duration: 1.5,
            position: .center
        )

        onFinish?()
    }

    private func handleSave() {
        activeTextEditor?.commit()
        let imageToSave = isScrollingMode ? (stitchedLongImage ?? generateFinalImage()) : generateFinalImage()
        cleanupScrollingMode()
        guard let image = imageToSave else { return }
        SoundEffect.shared.playShutter()
        _ = ImageSaver.saveToPreferencesFolder(image)
        ImageSaver.copyToClipboard(image)
        HistoryStore.shared.add(image)

        let globalRect = selectionRect.offsetBy(dx: targetScreen.frame.origin.x, dy: targetScreen.frame.origin.y)
        LastCaptureStore.save(rect: globalRect, screen: targetScreen)

        ToastController.shared.show(
            message: "已保存到文件夹与剪贴板",
            icon: "arrow.down.circle.fill",
            tint: .systemGreen,
            duration: 1.8,
            position: .center
        )

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
        guard let image = generateBaseCroppedImage() else { return }
        onFinish?()
        if let cg = OCR.cgImage(from: image) {
            Task { @MainActor in
                if let text = await OCR.recognize(in: cg) {
                    ImageSaver.copyTextToClipboard(text)
                    HistoryStore.shared.addOCR(text)
                    SoundEffect.shared.playShutter()
                    ToastController.shared.show(
                        message: "已识别完成并复制到剪贴板",
                        icon: "checkmark.circle.fill",
                        tint: .systemGreen,
                        duration: 3.0,
                        position: .center
                    )
                } else {
                    ToastController.shared.show(
                        message: "未识别到文字内容",
                        icon: "exclamationmark.triangle.fill",
                        tint: .systemOrange,
                        duration: 2.5,
                        position: .center
                    )
                }
            }
        }
    }

    override func viewWillMove(toWindow newWindow: NSWindow?) {
        super.viewWillMove(toWindow: newWindow)
        if newWindow == nil {
            cleanupScrollingMode()
        }
    }

    deinit {
        cleanupScrollingMode()
    }

    private func cleanupScrollingMode() {
        isScrollingMode = false
        scrollingTimer?.invalidate()
        scrollingTimer = nil
        if let m = scrollingScrollMonitor {
            NSEvent.removeMonitor(m)
            scrollingScrollMonitor = nil
        }
        if let m = scrollingMouseMonitor {
            NSEvent.removeMonitor(m)
            scrollingMouseMonitor = nil
        }
        if let m = scrollingKeyMonitor {
            NSEvent.removeMonitor(m)
            scrollingKeyMonitor = nil
        }
        window?.ignoresMouseEvents = false
    }

    private func updateMouseIgnoreState() {
        guard isScrollingMode, let win = window else { return }
        let screenMouseLoc = NSEvent.mouseLocation
        let p = NSPoint(x: screenMouseLoc.x - targetScreen.frame.origin.x, y: screenMouseLoc.y - targetScreen.frame.origin.y)

        let toolbarRect = bottomToolbarRect().insetBy(dx: -12, dy: -12)
        let previewRect = scrollingPreviewPanelRect().insetBy(dx: -12, dy: -12)

        let isOverControls = toolbarRect.contains(p) || previewRect.contains(p)
        let isInsideSelection = selectionRect.contains(p)

        if isOverControls || !isInsideSelection {
            if win.ignoresMouseEvents {
                win.ignoresMouseEvents = false
            }
        } else {
            if !win.ignoresMouseEvents {
                win.ignoresMouseEvents = true
            }
        }
    }

    private func setupScrollingMonitors() {
        cleanupScrollingMode()
        isScrollingMode = true

        // 1. 每 40ms 检测鼠标位置，动态在选区内放行原生鼠标/滚轮给底层软件，在工具栏上拦截以便点击
        scrollingTimer = Timer.scheduledTimer(withTimeInterval: 0.04, repeats: true) { [weak self] _ in
            self?.updateMouseIgnoreState()
        }

        // 2. 全局滚轮监听：当底层 App 原生滚动时触发捕获与比对拼接
        scrollingScrollMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.scrollWheel]) { [weak self] event in
            guard let self = self, self.isScrollingMode else { return }
            self.handleExternalScroll(event: event)
        }

        // 3. 全局鼠标移动监听：即时切换事件穿透状态
        scrollingMouseMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.mouseMoved, .leftMouseDragged]) { [weak self] _ in
            self?.updateMouseIgnoreState()
        }

        // 4. 全局键盘监听：无论哪个 App 处于激活状态，按 ESC 键（keyCode 53）均直接退出长截图
        scrollingKeyMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.keyDown]) { [weak self] event in
            guard let self = self, self.isScrollingMode else { return }
            if event.keyCode == 53 {
                DispatchQueue.main.async {
                    self.handleCancel()
                }
            }
        }
    }

    private func handleExternalScroll(event: NSEvent) {
        let now = Date().timeIntervalSinceReferenceDate
        // 节流采样：滚动期间约 12 FPS 比对拼接，停止后执行尾帧捕捉
        guard now - lastScrollCaptureTime >= 0.08 else {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) { [weak self] in
                self?.captureAndStitchSlice(deltaY: event.scrollingDeltaY)
            }
            return
        }
        lastScrollCaptureTime = now
        DispatchQueue.main.async { [weak self] in
            self?.captureAndStitchSlice(deltaY: event.scrollingDeltaY)
        }
    }

    override func scrollWheel(with event: NSEvent) {
        if isScrollingMode {
            let p = convert(event.locationInWindow, from: nil)
            if selectionRect.contains(p) {
                updateMouseIgnoreState()
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.06) { [weak self] in
                    self?.captureAndStitchSlice(deltaY: event.scrollingDeltaY)
                }
                return
            }
        }
        super.scrollWheel(with: event)
    }

    private func handleScrollingCapture() {
        // Scrolling screenshot (长截图) mode (333.jpg)
        let globalRect = selectionRect.offsetBy(dx: targetScreen.frame.origin.x, dy: targetScreen.frame.origin.y)
        if let initial = ScreenCapture.capture(rectInScreenCoords: globalRect, screen: targetScreen) ?? generateBaseCroppedImage() {
            stitchedLongImage = initial
            lastCapturedFrame = initial
        }
        setupScrollingMonitors()
        ToastController.shared.show(
            message: "已开启长截图：在选框内滚动页面拼接，点击对勾完成，按 Esc 或叉号直接退出",
            icon: "arrow.up.and.down",
            tint: .systemBlue,
            duration: 3.5,
            position: .center
        )
        needsDisplay = true
    }

    private func captureAndStitchSlice(deltaY: CGFloat) {
        let globalRect = selectionRect.offsetBy(dx: targetScreen.frame.origin.x, dy: targetScreen.frame.origin.y)
        guard let fresh = ScreenCapture.capture(rectInScreenCoords: globalRect, screen: targetScreen) else { return }

        guard let base = stitchedLongImage, let last = lastCapturedFrame else {
            self.stitchedLongImage = fresh
            self.lastCapturedFrame = fresh
            needsDisplay = true
            return
        }

        let movement = detectScrollMovement(oldImage: last, newImage: fresh)
        // 关键：截图区域内有滚动内容变化时才拼接长图，不是滑动就拼接长图
        guard movement.hasMovement, movement.offsetY > 2 else {
            return
        }

        let appendHeight = CGFloat(movement.offsetY)
        let newTotalHeight = base.size.height + appendHeight
        let newSize = NSSize(width: base.size.width, height: newTotalHeight)

        let composite = NSImage(size: newSize)
        composite.lockFocus()

        base.draw(in: NSRect(x: 0, y: appendHeight, width: base.size.width, height: base.size.height),
                  from: NSRect(origin: .zero, size: base.size),
                  operation: .copy, fraction: 1.0)

        let freshSliceRect = NSRect(x: 0, y: 0, width: fresh.size.width, height: appendHeight)
        fresh.draw(in: NSRect(x: 0, y: 0, width: base.size.width, height: appendHeight),
                   from: freshSliceRect,
                   operation: .sourceOver, fraction: 1.0)

        composite.unlockFocus()
        self.stitchedLongImage = composite
        self.lastCapturedFrame = fresh
        needsDisplay = true
    }

    private func detectScrollMovement(oldImage: NSImage, newImage: NSImage) -> (hasMovement: Bool, offsetY: Int) {
        guard let oldCG = oldImage.cgImage(forProposedRect: nil, context: nil, hints: nil),
              let newCG = newImage.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
            return (false, 0)
        }

        let sampleW = 40
        let sampleH = min(200, max(60, oldCG.height / 2))
        guard let oldBuf = grayscaleSample(cgImage: oldCG, width: sampleW, height: sampleH),
              let newBuf = grayscaleSample(cgImage: newCG, width: sampleW, height: sampleH) else {
            return (false, 0)
        }

        // 1. Calculate overall mean difference between old and new frame
        var totalDiff: Int = 0
        let count = sampleW * sampleH
        for i in 0..<count {
            totalDiff += abs(Int(oldBuf[i]) - Int(newBuf[i]))
        }
        let meanDiff = Double(totalDiff) / Double(count)

        // If overall difference is under 2.2 (out of 255), content is virtually identical -> no scrolling!
        if meanDiff < 2.2 {
            return (false, 0)
        }

        // 2. Find row shift dy where old[y + dy] matches new[y]
        let maxDy = min(sampleH / 2, 70)
        var bestDy: Int = 0
        var bestError: Double = Double.greatestFiniteMagnitude

        for dy in 2...maxDy {
            var diffSum: Int = 0
            var sampleCount: Int = 0
            for y in stride(from: 0, to: sampleH - dy, by: 2) {
                let oldRowStart = (y + dy) * sampleW
                let newRowStart = y * sampleW
                for x in stride(from: 0, to: sampleW, by: 3) {
                    diffSum += abs(Int(oldBuf[oldRowStart + x]) - Int(newBuf[newRowStart + x]))
                    sampleCount += 1
                }
            }
            if sampleCount > 0 {
                let err = Double(diffSum) / Double(sampleCount)
                if err < bestError {
                    bestError = err
                    bestDy = dy
                }
            }
        }

        let scale = CGFloat(newImage.size.height) / CGFloat(sampleH)

        // If a clear matching row offset was found with low error
        if bestError < 15.0 && bestDy >= 2 {
            let realDy = Int(round(CGFloat(bestDy) * scale))
            return (true, max(4, realDy))
        }

        // If image clearly changed (meanDiff >= 15.0) but row matching had higher error (e.g. fast swipe)
        if meanDiff >= 15.0 {
            let fallbackDy = Int(round(CGFloat(min(sampleH / 5, 25)) * scale))
            return (true, max(10, fallbackDy))
        }

        return (false, 0)
    }

    private func grayscaleSample(cgImage: CGImage, width: Int, height: Int) -> [UInt8]? {
        var buffer = [UInt8](repeating: 0, count: width * height)
        let colorSpace = CGColorSpaceCreateDeviceGray()
        guard let ctx = CGContext(
            data: &buffer,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: width,
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.none.rawValue
        ) else {
            return nil
        }
        ctx.interpolationQuality = .low
        ctx.draw(cgImage, in: CGRect(x: 0, y: 0, width: width, height: height))
        return buffer
    }

    private func scrollingPreviewPanelRect() -> NSRect {
        guard isScrollingMode, selectionRect.width > 0, selectionRect.height > 0 else { return .zero }
        let previewWidth: CGFloat = min(240, max(140, selectionRect.width * 0.8))
        let previewHeight: CGFloat = min(bounds.height - 60, max(380, selectionRect.height * 1.5))

        let fitsOnLeft = (selectionRect.minX >= previewWidth + 24)
        let x: CGFloat = fitsOnLeft ? (selectionRect.minX - previewWidth - 16) : (selectionRect.maxX + 16)
        let y: CGFloat = max(20, min(bounds.height - previewHeight - 20, selectionRect.midY - previewHeight / 2))
        return NSRect(x: x, y: y, width: previewWidth, height: previewHeight)
    }

    private func drawScrollingPreviewPanel() {
        let r = scrollingPreviewPanelRect()
        guard r.width > 0, r.height > 0 else { return }

        // Drop shadow & White Card Background
        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.35)
        shadow.shadowBlurRadius = 12
        shadow.shadowOffset = NSSize(width: 0, height: -3)
        shadow.set()

        NSColor.white.setFill()
        let card = NSBezierPath(roundedRect: r, xRadius: 8, yRadius: 8)
        card.fill()
        NSGraphicsContext.restoreGraphicsState()

        // Border
        NSColor(white: 0.85, alpha: 1.0).setStroke()
        let border = NSBezierPath(roundedRect: r, xRadius: 8, yRadius: 8)
        border.lineWidth = 1.0
        border.stroke()

        // Render Stitched Image inside Card
        if let img = stitchedLongImage {
            NSGraphicsContext.saveGraphicsState()
            card.addClip()

            let imgSize = img.size
            if imgSize.width > 0 && imgSize.height > 0 {
                let innerWidth = r.width - 12
                let scale = innerWidth / imgSize.width
                let drawHeight = imgSize.height * scale
                let drawRect = NSRect(x: r.minX + 6, y: r.maxY - drawHeight - 6, width: innerWidth, height: drawHeight)
                img.draw(in: drawRect, from: NSRect(origin: .zero, size: imgSize), operation: .sourceOver, fraction: 1.0)
            }
            NSGraphicsContext.restoreGraphicsState()
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

    private func generateBaseCroppedImage() -> NSImage? {
        guard selectionRect.width > 2, selectionRect.height > 2, let snapshot = screenSnapshot else { return nil }
        let scale = window?.backingScaleFactor ?? 2.0
        let pixelX = selectionRect.minX * scale
        let pixelY = (bounds.height - selectionRect.maxY) * scale
        let pixelW = selectionRect.width * scale
        let pixelH = selectionRect.height * scale
        let cropRect = CGRect(x: pixelX, y: pixelY, width: pixelW, height: pixelH)
        guard let croppedCG = snapshot.cropping(to: cropRect) else { return nil }
        return NSImage(cgImage: croppedCG, size: selectionRect.size)
    }

    private func generateFinalImage() -> NSImage? {
        guard let baseImage = generateBaseCroppedImage() else { return nil }

        let annotated = NSImage(size: selectionRect.size)
        annotated.lockFocus()
        baseImage.draw(in: NSRect(origin: .zero, size: selectionRect.size))

        for ann in annotations {
            let localAnn = ann.translatedBy(dx: -selectionRect.minX, dy: -selectionRect.minY)
            AnnotationRenderer.draw(localAnn, baseImage: baseImage)
        }
        annotated.unlockFocus()

        // Apply Corner Radius if > 0
        let cornerRadius = PreferencesStore.shared.cornerRadius
        let finalImage: NSImage
        if cornerRadius > 0 {
            let rounded = NSImage(size: selectionRect.size)
            rounded.lockFocus()
            let clip = NSBezierPath(roundedRect: NSRect(origin: .zero, size: selectionRect.size), xRadius: cornerRadius, yRadius: cornerRadius)
            clip.addClip()
            annotated.draw(in: NSRect(origin: .zero, size: selectionRect.size))
            rounded.unlockFocus()
            finalImage = rounded
        } else {
            finalImage = annotated
        }

        if PreferencesStore.shared.includeShadow {
            return addShadow(to: finalImage, cornerRadius: cornerRadius)
        }

        return finalImage
    }

    private func addShadow(to image: NSImage, cornerRadius: CGFloat) -> NSImage {
        let padding: CGFloat = 32
        let newSize = NSSize(width: image.size.width + padding * 2, height: image.size.height + padding * 2)
        let shadowImage = NSImage(size: newSize)
        shadowImage.lockFocus()

        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.38)
        shadow.shadowOffset = NSSize(width: 0, height: -6)
        shadow.shadowBlurRadius = 18
        shadow.set()

        let destRect = NSRect(x: padding, y: padding, width: image.size.width, height: image.size.height)
        let r = max(cornerRadius, 4)
        let path = NSBezierPath(roundedRect: destRect, xRadius: r, yRadius: r)
        NSColor.black.setFill()
        path.fill()
        NSGraphicsContext.restoreGraphicsState()

        let clipPath = NSBezierPath(roundedRect: destRect, xRadius: r, yRadius: r)
        clipPath.addClip()
        image.draw(in: destRect, from: .zero, operation: .sourceOver, fraction: 1.0)

        shadowImage.unlockFocus()
        return shadowImage
    }

    // MARK: - Drawing

    override func draw(_ dirtyRect: NSRect) {
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

        NSColor(srgbRed: 0.18, green: 0.53, blue: 1.0, alpha: 1.0).setStroke()
        let outer = NSBezierPath(rect: loupeRect)
        outer.lineWidth = 2.0
        outer.stroke()

        let color = currentPixelColor()

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

        // 1. Punch out the selection area (with corner radius)
        let cornerRadius = PreferencesStore.shared.cornerRadius
        NSColor.clear.setFill()
        if cornerRadius > 0 {
            let ctx = NSGraphicsContext.current
            let prevOp = ctx?.compositingOperation ?? .sourceOver
            ctx?.compositingOperation = .copy
            let path = NSBezierPath(roundedRect: selectionRect, xRadius: cornerRadius, yRadius: cornerRadius)
            path.fill()
            ctx?.compositingOperation = prevOp
        } else {
            selectionRect.fill(using: .copy)
        }

        // 2. Draw annotations inside selection
        NSGraphicsContext.saveGraphicsState()
        let clip: NSBezierPath
        if cornerRadius > 0 {
            clip = NSBezierPath(roundedRect: selectionRect, xRadius: cornerRadius, yRadius: cornerRadius)
        } else {
            clip = NSBezierPath(rect: selectionRect)
        }
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

        // 4. Draw Top Pill (Size, Corner Radius Slider, and Shadow Switch)
        drawTopBar()

        // 5. Draw Bottom White Capsule Toolbar
        drawBottomToolbar()

        // 6. Draw Live Preview Panel if Scrolling Screenshot (333.jpg)
        if isScrollingMode {
            drawScrollingPreviewPanel()
        }
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
        let w: CGFloat = 225
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

        // 1. Size text: "W × H"
        let sizeText = "\(Int(selectionRect.width)) × \(Int(selectionRect.height))"
        let sizeAttrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 11.5, weight: .bold),
            .foregroundColor: NSColor.white
        ]
        sizeText.draw(at: NSPoint(x: r.minX + 8, y: r.minY + 6.5), withAttributes: sizeAttrs)

        // 2. Corner Radius Draggable Slider (圆角度调节条, 222.jpg)
        let sliderMinX = r.minX + 85
        let sliderWidth: CGFloat = 55
        let trackRect = NSRect(x: sliderMinX, y: r.minY + 12, width: sliderWidth, height: 4)
        NSColor.white.withAlphaComponent(0.35).setFill()
        NSBezierPath(roundedRect: trackRect, xRadius: 2, yRadius: 2).fill()

        let currentRadius = PreferencesStore.shared.cornerRadius
        let knobRatio = max(0, min(1, currentRadius / 36.0))
        let knobX = sliderMinX + knobRatio * sliderWidth
        let knobRect = NSRect(x: knobX - 6, y: r.minY + 8, width: 12, height: 12)
        NSColor.white.setFill()
        NSBezierPath(ovalIn: knobRect).fill()

        // 3. Shadow Switch & Label (阴影开关, 222.jpg)
        let switchRect = NSRect(x: r.minX + 155, y: r.minY + 7, width: 22, height: 14)
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

        let shadowText = "阴影"
        let shadowAttrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 11, weight: .semibold),
            .foregroundColor: NSColor.white
        ]
        shadowText.draw(at: NSPoint(x: r.minX + 182, y: r.minY + 6.5), withAttributes: shadowAttrs)
    }

    private func bottomToolbarRect() -> NSRect {
        let itemCount = currentToolbarItems.count
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
        let items = currentToolbarItems
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
        case .scrolling:
            // Custom Scrolling / Long Screenshot Icon [ ↕ ] (222.jpg)
            tint.setStroke()
            let box = NSRect(x: rect.minX + 9, y: rect.minY + 5, width: 12, height: 20)
            let bp = NSBezierPath(roundedRect: box, xRadius: 2.5, yRadius: 2.5)
            bp.lineWidth = 1.5
            bp.stroke()

            let arrowPath = NSBezierPath()
            arrowPath.lineWidth = 1.3
            arrowPath.move(to: NSPoint(x: box.midX, y: box.minY + 4))
            arrowPath.line(to: NSPoint(x: box.midX, y: box.maxY - 4))
            arrowPath.move(to: NSPoint(x: box.midX - 2.5, y: box.maxY - 7))
            arrowPath.line(to: NSPoint(x: box.midX, y: box.maxY - 4))
            arrowPath.line(to: NSPoint(x: box.midX + 2.5, y: box.maxY - 7))
            arrowPath.move(to: NSPoint(x: box.midX - 2.5, y: box.minY + 7))
            arrowPath.line(to: NSPoint(x: box.midX, y: box.minY + 4))
            arrowPath.line(to: NSPoint(x: box.midX + 2.5, y: box.minY + 7))
            arrowPath.stroke()
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
