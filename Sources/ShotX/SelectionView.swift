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
        case redo
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
            .redo,
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
    var onOCRSelected: ((NSImage?) -> Void)?

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
    private var currentTool: AnnotationTool? = nil
    private var currentColor: NSColor = .systemRed
    private var thickness: CGFloat = 4
    private var annotations: [Annotation] = []
    private var undoneAnnotations: [Annotation] = []
    private var inProgressAnnotation: Annotation?
    private var annotationDragStart: NSPoint?
    private var activeTextEditor: InlineTextEditor?
    private var nextStepNumber: Int = 1
    private var hoveredButtonIndex: Int? = nil
    private var isDraggingCornerSlider: Bool = false

    // Corner resizing state (拖动选框4个角)
    private enum SelectionCorner {
        case topLeft
        case topRight
        case bottomLeft
        case bottomRight
    }
    private var activeCorner: SelectionCorner? = nil
    private var cornerAnchorPoint: NSPoint = .zero

    // Sub-toolbar state (Child tools for first 8 tools)
    private var isSubToolbarVisible: Bool = false
    private var subToolbarTool: AnnotationTool? = nil
    private var currentDrawColor: NSColor = NSColor(srgbRed: 1.0, green: 0.23, blue: 0.19, alpha: 1.0) // #FF3B30
    private var textSecondaryColor: NSColor = .white
    private var textIsSelectingSecondary: Bool = false
    private var currentOpacity: CGFloat = 1.0
    private var currentSize: CGFloat = 5.0
    private var isRectFilled: Bool = false
    private var isRectRounded: Bool = true
    private var isOvalFilled: Bool = false
    private var isArrowCurved: Bool = false
    private var penIsHighlighter: Bool = false
    private var mosaicIsBlur: Bool = false
    private var textStyle: Int = 1 // 0: 无, 1: 描边, 2: 背景
    private var stepIsFilled: Bool = true
    private var isDraggingSizeSlider: Bool = false
    private var isDraggingOpacitySlider: Bool = false

    // Scrolling screenshot state (333.jpg)
    private var isScrollingMode: Bool = false
    private var stitchedLongImage: NSImage?
    private var lastStitchedCG: CGImage?
    private var stitchedLongCG: CGImage?
    private var isStitchingSlice: Bool = false
    private var scrollingScrollMonitor: Any?
    private var scrollingMouseMonitor: Any?
    private var scrollingKeyMonitor: Any?
    private var scrollingTimer: Timer?
    private var lastScrollCaptureTime: TimeInterval = 0
    private var cachedHUDImage: NSImage?
    private var needsMouseUpdate: Bool = false

    // Buttery-smooth mouse tracking & Color Debounce (iShot-grade smoothness)
    private var isMouseMovingFast: Bool = false
    private var colorDebounceTimer: Timer?
    private var cachedLoupeColor: (r: Int, g: Int, b: Int, hex: String, name: String)?

    private var currentToolbarItems: [ToolbarItem] {
        if isScrollingMode {
            return [.cancel, .save, .copy]
        } else {
            return ToolbarItem.allItems
        }
    }

    override var acceptsFirstResponder: Bool { true }
    override var isFlipped: Bool { false }

    private var snapshotDataProvider: CFData?
    private var snapshotBytesPerRow: Int = 0

    init(frame: NSRect, screen: NSScreen, snapshot: CGImage?, isOCR: Bool = false) {
        self.targetScreen = screen
        self.screenSnapshot = snapshot
        self.isOCRMode = isOCR
        if let data = snapshot?.dataProvider?.data {
            self.snapshotDataProvider = data
            self.snapshotBytesPerRow = snapshot?.bytesPerRow ?? 0
        }
        super.init(frame: frame)
        self.wantsLayer = true
        self.layerContentsRedrawPolicy = .onSetNeedsDisplay
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
        guard let win = window else {
            NotificationCenter.default.removeObserver(self, name: NSWindow.didBecomeKeyNotification, object: nil)
            return
        }
        NotificationCenter.default.addObserver(forName: NSWindow.didBecomeKeyNotification, object: win, queue: .main) { [weak self] _ in
            self?.closeColorPickerIfNeeded()
        }
        let global = NSEvent.mouseLocation
        mousePosition = NSPoint(
            x: global.x - win.frame.origin.x,
            y: global.y - win.frame.origin.y
        )
        needsDisplay = true
    }

    // MARK: - Mouse Tracking

    private func loupeRect(for p: NSPoint) -> NSRect {
        let loupeSize: CGFloat = 140
        var lx = p.x + 18
        var ly = p.y + 18
        if lx + loupeSize > bounds.width - 15 {
            lx = p.x - loupeSize - 18
        }
        if ly + loupeSize > bounds.height - 15 {
            ly = p.y - loupeSize - 18
        }
        return NSRect(x: lx, y: ly, width: loupeSize, height: loupeSize)
    }

    private func invalidateSelectionCursor(oldPos: NSPoint?, newPos: NSPoint) {
        if let old = oldPos {
            setNeedsDisplay(loupeRect(for: old).insetBy(dx: -8, dy: -8))
            setNeedsDisplay(NSRect(x: 0, y: old.y - 2, width: bounds.width, height: 4))
            setNeedsDisplay(NSRect(x: old.x - 2, y: 0, width: 4, height: bounds.height))
        }
        setNeedsDisplay(loupeRect(for: newPos).insetBy(dx: -8, dy: -8))
        setNeedsDisplay(NSRect(x: 0, y: newPos.y - 2, width: bounds.width, height: 4))
        setNeedsDisplay(NSRect(x: newPos.x - 2, y: 0, width: 4, height: bounds.height))
    }

    private func hitTestCorner(at p: NSPoint) -> SelectionCorner? {
        guard mode == .annotating, selectionRect.width > 0, selectionRect.height > 0 else { return nil }
        let threshold: CGFloat = 16.0
        let r = selectionRect
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

    override func mouseMoved(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        let oldPos = mousePosition
        if let prev = oldPos, abs(prev.x - p.x) < 0.5 && abs(prev.y - p.y) < 0.5 {
            return
        }
        mousePosition = p

        if mode == .annotating {
            updateToolbarHover(at: p)
            if hitTestCorner(at: p) != nil {
                NSCursor.crosshair.set()
            } else {
                NSCursor.arrow.set()
            }
            needsDisplay = true
            return
        }

        // Mode is .selecting
        if currentRect.isEmpty {
            // 鼠标高速移动状态：快速移动时不采集耗时颜色，只刷新十字线和放大镜基础外框
            isMouseMovingFast = true
            invalidateSelectionCursor(oldPos: oldPos, newPos: p)

            // 停下来（25ms 防抖）立刻精准采色并局部渲染放大镜细节
            colorDebounceTimer?.invalidate()
            colorDebounceTimer = Timer.scheduledTimer(withTimeInterval: 0.025, repeats: false) { [weak self] _ in
                guard let self = self, self.mode == .selecting, self.currentRect.isEmpty else { return }
                self.isMouseMovingFast = false
                if let pos = self.mousePosition {
                    self.cachedLoupeColor = self.currentPixelColor()
                    self.setNeedsDisplay(self.loupeRect(for: pos).insetBy(dx: -8, dy: -8))
                }
            }
        } else {
            // 正在拖拽选框，直接刷新
            needsDisplay = true
        }
    }

    override func mouseEntered(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        mousePosition = p
        needsDisplay = true
    }

    override func mouseExited(with event: NSEvent) {
        hoveredButtonIndex = nil
        mousePosition = nil
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
        closeColorPickerIfNeeded()
        let p = convert(event.locationInWindow, from: nil)

        if mode == .selecting {
            startPoint = p
            currentRect = .zero
            needsDisplay = true
            return
        }

        // Mode is .annotating
        // 0. Long screenshot control buttons
        if isScrollingMode {
            if scrollingCancelButtonRect().contains(p) {
                handleCancel()
                return
            }
            if scrollingDoneButtonRect().contains(p) {
                handleCopy()
                return
            }
        }

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

        // 2. Check Sub-Toolbar click (Child tools)
        let subRect = subToolbarRect()
        if isSubToolbarVisible && subRect.contains(p) {
            handleSubToolbarClick(at: p)
            needsDisplay = true
            return
        }

        // 3. Check Bottom Toolbar click
        let tbRect = bottomToolbarRect()
        if tbRect.contains(p) {
            hoveredButtonIndex = nil
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

        // 3.5 Check Corner dragging to resize selection (拖动选框4个角)
        if let corner = hitTestCorner(at: p) {
            activeCorner = corner
            switch corner {
            case .topLeft:
                cornerAnchorPoint = NSPoint(x: selectionRect.maxX, y: selectionRect.minY)
            case .topRight:
                cornerAnchorPoint = NSPoint(x: selectionRect.minX, y: selectionRect.minY)
            case .bottomLeft:
                cornerAnchorPoint = NSPoint(x: selectionRect.maxX, y: selectionRect.maxY)
            case .bottomRight:
                cornerAnchorPoint = NSPoint(x: selectionRect.minX, y: selectionRect.maxY)
            }
            return
        }

        // 4. Double-click inside selection commits and finishes!
        if event.clickCount == 2 && selectionRect.contains(p) {
            handleCopy()
            return
        }

        // 5. Drawing inside selectionRect
        if selectionRect.contains(p) {
            if let editor = activeTextEditor {
                if !editor.frame.contains(p) {
                    editor.commit()
                    return
                }
            }

            // 检查是否点击已输入的文字标注进行再次编辑
            var clickedTextIdx: Int? = nil
            for (idx, ann) in annotations.enumerated().reversed() {
                if case .text(let origin, let string, let fontSize) = ann.shape {
                    let font = NSFont.systemFont(ofSize: fontSize, weight: .bold)
                    let sz = (string as NSString).size(withAttributes: [.font: font])
                    let hitRect = CGRect(x: origin.x - 4, y: origin.y - 4, width: sz.width + 8, height: sz.height + 8)
                    if hitRect.contains(p) {
                        clickedTextIdx = idx
                        break
                    }
                }
            }

            if let idx = clickedTextIdx {
                let ann = annotations.remove(at: idx)
                if case .text(let origin, let string, let fontSize) = ann.shape {
                    currentTool = .text
                    subToolbarTool = .text
                    isSubToolbarVisible = true
                    self.textStyle = ann.textStyle
                    self.currentDrawColor = ann.color
                    self.textSecondaryColor = ann.secondaryColor
                    self.currentSize = fontSize
                    startInlineTextEditor(at: origin, initialText: string, fontSize: fontSize, color: ann.color, textStyle: ann.textStyle, secondaryColor: ann.secondaryColor)
                    needsDisplay = true
                    return
                }
            }

            guard let tool = currentTool else { return }

            undoneAnnotations.removeAll()
            annotationDragStart = p
            switch tool {
            case .arrow:
                var ann = Annotation(shape: .arrow(from: p, to: p), color: currentDrawColor, thickness: currentSize)
                ann.isCurved = isArrowCurved
                ann.opacity = currentOpacity
                inProgressAnnotation = ann
            case .rectangle:
                var ann = Annotation(shape: .rectangle(CGRect(origin: p, size: .zero)), color: currentDrawColor, thickness: currentSize)
                ann.isFilled = isRectFilled
                ann.isRounded = isRectRounded
                ann.opacity = currentOpacity
                inProgressAnnotation = ann
            case .oval:
                var ann = Annotation(shape: .oval(CGRect(origin: p, size: .zero)), color: currentDrawColor, thickness: currentSize)
                ann.isFilled = isOvalFilled
                ann.opacity = currentOpacity
                inProgressAnnotation = ann
            case .line:
                var ann = Annotation(shape: .line(from: p, to: p), color: currentDrawColor, thickness: currentSize)
                ann.opacity = currentOpacity
                inProgressAnnotation = ann
            case .pen:
                var ann = Annotation(shape: .pen(points: [p]), color: currentDrawColor, thickness: currentSize)
                ann.isHighlighter = penIsHighlighter
                ann.opacity = currentOpacity
                inProgressAnnotation = ann
            case .mosaic:
                var ann = Annotation(shape: .mosaic(CGRect(origin: p, size: .zero)), color: currentDrawColor, thickness: currentSize)
                ann.isMosaicBlur = mosaicIsBlur
                ann.opacity = currentOpacity
                inProgressAnnotation = ann
            case .step:
                var ann = Annotation(shape: .step(center: p, number: nextStepNumber), color: currentDrawColor, thickness: currentSize)
                ann.stepIsFilled = stepIsFilled
                ann.opacity = currentOpacity
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
                selectionRect = NSRect(x: minX, y: minY, width: maxX - minX, height: maxY - minY)
                needsDisplay = true
            }
            return
        }

        if isDraggingCornerSlider {
            let topRect = topBarRect()
            let sliderMinX = topRect.minX + 85
            let sliderMaxX = topRect.minX + 150
            updateCornerRadius(at: p.x, minX: sliderMinX, maxX: sliderMaxX)
            return
        }

        if isDraggingSizeSlider {
            updateSubToolbarSize(at: p.x)
            return
        }

        if isDraggingOpacitySlider {
            updateSubToolbarOpacity(at: p.x)
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
        if activeCorner != nil {
            activeCorner = nil
            needsDisplay = true
            return
        }

        if isDraggingCornerSlider {
            isDraggingCornerSlider = false
            return
        }
        if isDraggingSizeSlider || isDraggingOpacitySlider {
            isDraggingSizeSlider = false
            isDraggingOpacitySlider = false
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
                    let image = generateBaseCroppedImage()
                    onOCRSelected?(image)
                    onFinish?()
                    return
                }

                mode = .annotating
                currentTool = nil
                subToolbarTool = nil
                isSubToolbarVisible = false
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
            // Cmd+Shift+Z -> Redo
            if event.modifierFlags.contains(.command) && event.modifierFlags.contains(.shift) && (event.charactersIgnoringModifiers?.lowercased() == "z") {
                handleRedo()
                return
            }
            // Cmd+Z -> Undo
            if event.modifierFlags.contains(.command) && (event.charactersIgnoringModifiers?.lowercased() == "z") {
                handleUndo()
                return
            }

            if activeTextEditor == nil && !event.modifierFlags.contains(.command) {
                switch event.charactersIgnoringModifiers?.lowercased() {
                case "r":
                    handleToolbarItemClick(.tool(.rectangle))
                    return
                case "o":
                    handleToolbarItemClick(.tool(.oval))
                    return
                case "l":
                    handleToolbarItemClick(.tool(.line))
                    return
                case "a":
                    handleToolbarItemClick(.tool(.arrow))
                    return
                case "p":
                    handleToolbarItemClick(.tool(.pen))
                    return
                case "m":
                    handleToolbarItemClick(.tool(.mosaic))
                    return
                case "t":
                    handleToolbarItemClick(.tool(.text))
                    return
                case "s":
                    handleToolbarItemClick(.tool(.step))
                    return
                case "f":
                    handleToolbarItemClick(.pin)
                    return
                default:
                    break
                }
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
        hoveredButtonIndex = nil
        switch item {
        case .tool(let tool):
            if currentTool == tool {
                currentTool = nil
                subToolbarTool = nil
                isSubToolbarVisible = false
            } else {
                currentTool = tool
                subToolbarTool = tool
                isSubToolbarVisible = true
            }
            if tool == .text && currentSize < 12 {
                currentSize = 24
            } else if tool != .text && currentSize > 30 {
                currentSize = 5
            }
            needsDisplay = true
        case .pin:
            isSubToolbarVisible = false
            handlePin()
        case .undo:
            handleUndo()
        case .redo:
            handleRedo()
        case .ocr:
            isSubToolbarVisible = false
            handleOCR()
        case .cancel:
            isSubToolbarVisible = false
            handleCancel()
        case .scrolling:
            isSubToolbarVisible = false
            handleScrollingCapture()
        case .save:
            isSubToolbarVisible = false
            handleSave()
        case .copy:
            isSubToolbarVisible = false
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
            currentTool = nil
            subToolbarTool = nil
            isSubToolbarVisible = false
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
        currentTool = nil
        subToolbarTool = nil
        isSubToolbarVisible = false
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
        undoneAnnotations.append(removed)
        if case .step = removed.shape {
            nextStepNumber = max(1, nextStepNumber - 1)
        }
        needsDisplay = true
    }

    private func handleRedo() {
        guard !undoneAnnotations.isEmpty else { return }
        let restored = undoneAnnotations.removeLast()
        annotations.append(restored)
        if case .step = restored.shape {
            nextStepNumber += 1
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
        isStitchingSlice = false
        lastStitchedCG = nil
        stitchedLongCG = nil
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

        // 1. 定时器：以 100ms 周期检测鼠标位置以即时更新穿透状态
        scrollingTimer = Timer.scheduledTimer(withTimeInterval: 0.10, repeats: true) { [weak self] _ in
            self?.updateMouseIgnoreState()
        }

        // 2. 全局滚轮监听：当底层 App 原生滚动时触发捕获与比对拼接
        scrollingScrollMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.scrollWheel]) { [weak self] event in
            guard let self = self, self.isScrollingMode else { return }
            self.handleExternalScroll(event: event)
        }

        // 3. 全局鼠标移动与点击监听：无论焦点在哪个 App，点击面板按钮均直接响应
        scrollingMouseMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.mouseMoved, .leftMouseDragged, .leftMouseDown]) { [weak self] event in
            guard let self = self, self.isScrollingMode else { return }
            self.updateMouseIgnoreState()
            if event.type == .leftMouseDown {
                let screenLoc = NSEvent.mouseLocation
                let localP = NSPoint(x: screenLoc.x - self.targetScreen.frame.origin.x, y: screenLoc.y - self.targetScreen.frame.origin.y)
                if self.scrollingCancelButtonRect().contains(localP) {
                    DispatchQueue.main.async { self.handleCancel() }
                } else if self.scrollingDoneButtonRect().contains(localP) {
                    DispatchQueue.main.async { self.handleCopy() }
                }
            }
        }

        // 4. 全局键盘监听：无论哪个 App 处于激活状态，按 ESC 键均退出，按 Enter 键均完成
        scrollingKeyMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.keyDown]) { [weak self] event in
            guard let self = self, self.isScrollingMode else { return }
            if event.keyCode == 53 { // ESC
                DispatchQueue.main.async { self.handleCancel() }
            } else if event.keyCode == 36 || event.keyCode == 76 { // Return / Enter
                DispatchQueue.main.async { self.handleCopy() }
            }
        }
    }

    private func handleExternalScroll(event: NSEvent) {
        // 关键防护：如果用户反向回滚（Mac 触控板向下滑动 deltaY > 0.5），绝对不进行反向拼接
        if event.scrollingDeltaY > 0.5 { return }

        let now = Date().timeIntervalSinceReferenceDate
        guard now - lastScrollCaptureTime >= 0.07 else {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.08) { [weak self] in
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
            if event.scrollingDeltaY > 0.5 { return }
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
        if let initialCG = ScreenCapture.captureCGImage(rectInScreenCoords: globalRect, screen: targetScreen) {
            lastStitchedCG = initialCG
            stitchedLongCG = initialCG
            stitchedLongImage = NSImage(cgImage: initialCG, size: selectionRect.size)
        } else if let initial = generateBaseCroppedImage() {
            var r = NSRect(origin: .zero, size: initial.size)
            if let cg = initial.cgImage(forProposedRect: &r, context: nil, hints: nil) {
                lastStitchedCG = cg
                stitchedLongCG = cg
            }
            stitchedLongImage = initial
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
        if isStitchingSlice { return }
        isStitchingSlice = true
        defer { isStitchingSlice = false }

        if deltaY > 0.5 { return }
        let globalRect = selectionRect.offsetBy(dx: targetScreen.frame.origin.x, dy: targetScreen.frame.origin.y)
        guard let freshCG = ScreenCapture.captureCGImage(rectInScreenCoords: globalRect, screen: targetScreen) else { return }

        guard let baseCG = lastStitchedCG, let currentLong = stitchedLongCG else {
            self.lastStitchedCG = freshCG
            self.stitchedLongCG = freshCG
            self.stitchedLongImage = NSImage(cgImage: freshCG, size: selectionRect.size)
            needsDisplay = true
            return
        }

        if currentLong.height >= 32000 {
            ToastController.shared.show(message: "已达长截图最大高度限制 (32000px)", icon: "exclamationmark.triangle", tint: .systemOrange, duration: 2.0)
            return
        }

        let (hasMovement, S) = detectScrollMovement(baseCG: baseCG, freshCG: freshCG)
        guard hasMovement, S >= 3 else { return }

        // The newly revealed bottom slice is rows (freshCG.height - S) to freshCG.height
        let sliceRect = CGRect(x: 0, y: freshCG.height - S, width: freshCG.width, height: S)
        guard let sliceCG = freshCG.cropping(to: sliceRect) else { return }

        let newH = currentLong.height + S
        let cs = CGColorSpaceCreateDeviceRGB()
        guard let ctx = CGContext(
            data: nil,
            width: currentLong.width,
            height: newH,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: cs,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return }

        // In CGContext (0,0 is bottom-left):
        // Upper/earlier content at y = S, newest bottom slice at y = 0
        ctx.draw(currentLong, in: CGRect(x: 0, y: S, width: currentLong.width, height: currentLong.height))
        ctx.draw(sliceCG, in: CGRect(x: 0, y: 0, width: sliceCG.width, height: S))
        guard let newCompositeCG = ctx.makeImage() else { return }

        self.stitchedLongCG = newCompositeCG
        self.lastStitchedCG = freshCG

        let scale = CGFloat(newH) / CGFloat(freshCG.height)
        let newPointSize = NSSize(width: selectionRect.width, height: selectionRect.height * scale)
        self.stitchedLongImage = NSImage(cgImage: newCompositeCG, size: newPointSize)
        needsDisplay = true
    }

    private func detectScrollMovement(baseCG: CGImage, freshCG: CGImage) -> (hasMovement: Bool, deltaY: Int) {
        let w = baseCG.width
        let h = baseCG.height
        guard w == freshCG.width, h == freshCG.height, h > 60, w > 40 else { return (false, 0) }

        guard let bufA = getGrayscalePixelBuffer(cg: baseCG),
              let bufB = getGrayscalePixelBuffer(cg: freshCG) else { return (false, 0) }

        // 1. 横向宽幅 32 列采样：避开左右边距（各保留 16px 边距，杜绝垂直滚动条干扰）
        let marginX = min(20, w / 10)
        let sampleWidth = w - 2 * marginX
        guard sampleWidth > 32 else { return (false, 0) }
        var sampleCols: [Int] = []
        let colStep = sampleWidth / 32
        for i in 0..<32 {
            sampleCols.append(marginX + i * colStep)
        }

        // 2. 锚定带（Anchor）：选取 baseCG 底部上方一段具有明确特征的内容条带
        let anchorH = min(56, h / 3)
        let marginBottom = 8
        var anchorTop = h - marginBottom - anchorH

        // 寻找下半区对比度最强的文本行作为锚点
        var bestVar = 0.0
        var bestTop = anchorTop
        let searchMinTop = max(10, h / 2)
        for candTop in stride(from: anchorTop, through: searchMinTop, by: -10) {
            var sum = 0.0
            var sumSq = 0.0
            var cnt = 0
            for r in stride(from: 0, to: anchorH, by: 4) {
                let row = (candTop + r) * w
                for c in sampleCols {
                    let val = Double(bufA[row + c])
                    sum += val
                    sumSq += val * val
                    cnt += 1
                }
            }
            if cnt > 0 {
                let mean = sum / Double(cnt)
                let variance = (sumSq / Double(cnt)) - (mean * mean)
                if variance > bestVar {
                    bestVar = variance
                    bestTop = candTop
                }
            }
        }

        // 纯平坦区域（纯白或纯色背景）方差极小，不作盲目误匹配
        if bestVar < 2.0 {
            return (false, 0)
        }
        anchorTop = bestTop

        // 3. 快速零位移检测（静止时直接返回，彻底消除静态重复行）
        var zeroDiff = 0.0
        var zeroCount = 0
        for r in stride(from: 0, to: anchorH, by: 2) {
            let rowA = (anchorTop + r) * w
            for c in sampleCols {
                zeroDiff += abs(Double(bufA[rowA + c]) - Double(bufB[rowA + c]))
                zeroCount += 1
            }
        }
        if zeroCount > 0 && (zeroDiff / Double(zeroCount)) < 1.2 {
            return (false, 0)
        }

        // 4. 在 freshCG 中向上搜索该 Anchor 的匹配位置
        let maxS = min(Int(CGFloat(h) * 0.45), anchorTop)
        guard maxS >= 3 else { return (false, 0) }

        var bestS = 0
        var bestErr = Double.greatestFiniteMagnitude

        for S in 3...maxS {
            let y = anchorTop - S
            guard y >= 0 else { break }

            var diff = 0.0
            var count = 0
            for r in stride(from: 0, to: anchorH, by: 2) {
                let rowA = (anchorTop + r) * w
                let rowB = (y + r) * w
                for c in sampleCols {
                    diff += abs(Double(bufA[rowA + c]) - Double(bufB[rowB + c]))
                    count += 1
                }
            }
            if count > 0 {
                let err = diff / Double(count)
                if err < bestErr {
                    bestErr = err
                    bestS = S
                }
            }
        }

        // 5. 严格像素级吻合判定：平均误差小于 3.5 级灰度且位移 >= 3px
        if bestErr < 3.5 && bestS >= 3 {
            return (true, bestS)
        }
        return (false, 0)
    }

    private func getGrayscalePixelBuffer(cg: CGImage) -> [UInt8]? {
        let w = cg.width
        let h = cg.height
        var buffer = [UInt8](repeating: 0, count: w * h)
        let colorSpace = CGColorSpaceCreateDeviceGray()
        guard let ctx = CGContext(
            data: &buffer,
            width: w,
            height: h,
            bitsPerComponent: 8,
            bytesPerRow: w,
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.none.rawValue
        ) else { return nil }
        ctx.interpolationQuality = .none
        ctx.draw(cg, in: CGRect(x: 0, y: 0, width: w, height: h))
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

    private func scrollingCancelButtonRect() -> NSRect {
        let r = scrollingPreviewPanelRect()
        guard r.width > 0, r.height > 0 else { return .zero }
        let btnW = (r.width - 18) / 2
        return NSRect(x: r.minX + 6, y: r.minY + 6, width: btnW, height: 26)
    }

    private func scrollingDoneButtonRect() -> NSRect {
        let r = scrollingPreviewPanelRect()
        guard r.width > 0, r.height > 0 else { return .zero }
        let btnW = (r.width - 18) / 2
        return NSRect(x: r.minX + 12 + btnW, y: r.minY + 6, width: btnW, height: 26)
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
                let innerHeight = r.height - 68
                let scale = min(innerWidth / imgSize.width, innerHeight / imgSize.height)
                let drawWidth = imgSize.width * scale
                let drawHeight = imgSize.height * scale
                let drawRect = NSRect(
                    x: r.midX - drawWidth / 2,
                    y: r.maxY - drawHeight - 6,
                    width: drawWidth,
                    height: drawHeight
                )
                img.draw(in: drawRect, from: NSRect(origin: .zero, size: imgSize), operation: .sourceOver, fraction: 1.0)
            }
            NSGraphicsContext.restoreGraphicsState()

            // Realtime preview badge showing height: "长图实时预览 • \(Int(img.size.height)) px"
            let badgeRect = NSRect(x: r.minX + 6, y: r.minY + 36, width: r.width - 12, height: 20)
            NSColor(white: 0.15, alpha: 0.82).setFill()
            let badgePath = NSBezierPath(roundedRect: badgeRect, xRadius: 4, yRadius: 4)
            badgePath.fill()

            let badgeText = "长图高度: \(Int(img.size.height)) px"
            let attrs: [NSAttributedString.Key: Any] = [
                .font: NSFont.systemFont(ofSize: 9.5, weight: .semibold),
                .foregroundColor: NSColor.white
            ]
            let textSize = badgeText.size(withAttributes: attrs)
            badgeText.draw(
                at: NSPoint(x: badgeRect.midX - textSize.width / 2, y: badgeRect.midY - textSize.height / 2),
                withAttributes: attrs
            )

            // Cancel Button (Red/Dark)
            let cancelRect = scrollingCancelButtonRect()
            NSColor(srgbRed: 0.90, green: 0.28, blue: 0.28, alpha: 0.95).setFill()
            let cancelPath = NSBezierPath(roundedRect: cancelRect, xRadius: 5, yRadius: 5)
            cancelPath.fill()
            let cancelText = "✖ 退出"
            let cancelAttrs: [NSAttributedString.Key: Any] = [
                .font: NSFont.systemFont(ofSize: 11, weight: .bold),
                .foregroundColor: NSColor.white
            ]
            let cSize = cancelText.size(withAttributes: cancelAttrs)
            cancelText.draw(
                at: NSPoint(x: cancelRect.midX - cSize.width / 2, y: cancelRect.midY - cSize.height / 2),
                withAttributes: cancelAttrs
            )

            // Done Button (Blue)
            let doneRect = scrollingDoneButtonRect()
            NSColor(srgbRed: 0.12, green: 0.53, blue: 0.98, alpha: 1.0).setFill()
            let donePath = NSBezierPath(roundedRect: doneRect, xRadius: 5, yRadius: 5)
            donePath.fill()
            let doneText = "✔ 完成"
            let doneAttrs: [NSAttributedString.Key: Any] = [
                .font: NSFont.systemFont(ofSize: 11, weight: .bold),
                .foregroundColor: NSColor.white
            ]
            let dSize = doneText.size(withAttributes: doneAttrs)
            doneText.draw(
                at: NSPoint(x: doneRect.midX - dSize.width / 2, y: doneRect.midY - dSize.height / 2),
                withAttributes: doneAttrs
            )
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

    private func startInlineTextEditor(
        at point: NSPoint,
        initialText: String? = nil,
        fontSize: CGFloat? = nil,
        color: NSColor? = nil,
        textStyle: Int? = nil,
        secondaryColor: NSColor? = nil
    ) {
        activeTextEditor?.commit()
        let drawCol = color ?? currentDrawColor
        let sz = fontSize ?? currentSize
        let secCol = secondaryColor ?? textSecondaryColor
        let st = textStyle ?? self.textStyle

        let editor = InlineTextEditor(origin: point, color: drawCol, fontSize: sz, initialText: initialText)
        editor.onCommit = { [weak self] text, origin, fSize in
            guard let self = self else { return }
            var ann = Annotation(
                shape: .text(origin: origin, string: text, fontSize: fSize),
                color: drawCol,
                thickness: fSize
            )
            ann.textStyle = st
            ann.secondaryColor = secCol
            ann.opacity = self.currentOpacity
            self.undoneAnnotations.removeAll()
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
        let scaleX = bounds.width > 0 ? (CGFloat(snapshot.width) / bounds.width) : (window?.backingScaleFactor ?? 2.0)
        let scaleY = bounds.height > 0 ? (CGFloat(snapshot.height) / bounds.height) : (window?.backingScaleFactor ?? 2.0)
        let pixelX = selectionRect.minX * scaleX
        let pixelY = (bounds.height - selectionRect.maxY) * scaleY
        let pixelW = selectionRect.width * scaleX
        let pixelH = selectionRect.height * scaleY
        let cropRect = CGRect(x: pixelX, y: pixelY, width: pixelW, height: pixelH)
        let fullRect = CGRect(x: 0, y: 0, width: snapshot.width, height: snapshot.height)
        let clampedRect = cropRect.intersection(fullRect)
        guard clampedRect.width > 0, clampedRect.height > 0,
              let croppedCG = snapshot.cropping(to: clampedRect) else { return nil }
        return NSImage(cgImage: croppedCG, size: selectionRect.size)
    }

    private func generateFinalImage() -> NSImage? {
        guard let baseImage = generateBaseCroppedImage() else { return nil }

        let annotated = NSImage(size: selectionRect.size)
        annotated.lockFocus()
        baseImage.draw(in: NSRect(origin: .zero, size: selectionRect.size))

        for ann in annotations {
            let localAnn = ann.translatedBy(dx: -selectionRect.minX, dy: -selectionRect.minY)
            AnnotationRenderer.draw(localAnn, snapshotImage: nil, viewBounds: .zero, baseImage: baseImage)
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
        dirtyRect.fill()

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

        let scaleX = bounds.width > 0 ? (CGFloat(screenSnapshot?.width ?? 0) / bounds.width) : 2.0
        let scaleY = bounds.height > 0 ? (CGFloat(screenSnapshot?.height ?? 0) / bounds.height) : 2.0
        let pixelX = Int(p.x * scaleX)
        let pixelY = Int((bounds.height - p.y) * scaleY)

        NSGraphicsContext.saveGraphicsState()
        let clip = NSBezierPath(rect: loupeRect)
        clip.addClip()

        if let snapshot = screenSnapshot {
            let cropRect = CGRect(x: pixelX - 8, y: pixelY - 8, width: 17, height: 17)
            if let patch = snapshot.cropping(to: cropRect), let ctx = NSGraphicsContext.current?.cgContext {
                ctx.interpolationQuality = .none
                ctx.draw(patch, in: loupeRect)
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

        let color: (r: Int, g: Int, b: Int, hex: String, name: String)
        if isMouseMovingFast {
            // 鼠标移动中：使用极速缓存颜色，跳过字典查找与耗时测量，保证 120 FPS 丝滑顺畅
            color = cachedLoupeColor ?? (255, 255, 255, "FFFFFF", "白色")
        } else {
            color = cachedLoupeColor ?? currentPixelColor()
        }

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

        if let cached = cachedHUDImage {
            cached.draw(in: hudRect)
            return
        }

        let img = NSImage(size: hudRect.size)
        img.lockFocus()

        NSColor(white: 0.08, alpha: 0.94).setFill()
        let bg = NSBezierPath(roundedRect: NSRect(origin: .zero, size: hudRect.size), xRadius: 8, yRadius: 8)
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

        var y = hudRect.height - 20
        for line in lines {
            if !line.isEmpty {
                line.draw(at: NSPoint(x: 14, y: y), withAttributes: attrs)
            }
            y -= 18
        }
        img.unlockFocus()

        self.cachedHUDImage = img
        img.draw(in: hudRect)
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
            AnnotationRenderer.draw(ann, snapshotImage: screenSnapshot, viewBounds: bounds, baseImage: nil)
        }
        if let inProg = inProgressAnnotation {
            AnnotationRenderer.draw(inProg, snapshotImage: screenSnapshot, viewBounds: bounds, baseImage: nil)
        }
        NSGraphicsContext.restoreGraphicsState()

        // 3. Draw 4 Blue L-Corner Brackets
        drawCornerBrackets()

        // 4. Draw Top Pill (Size, Corner Radius Slider, and Shadow Switch)
        drawTopBar()

        // 5. Draw Bottom White Capsule Toolbar
        drawBottomToolbar()

        // 6. Draw Sub-Toolbar Panel if visible (Child tools)
        if isSubToolbarVisible && !isScrollingMode {
            drawSubToolbar()
        }

        // 7. Draw Live Preview Panel if Scrolling Screenshot (333.jpg)
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

        // 绘制4个角的微型拖动手柄圆点
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
            if let active = currentTool, case .tool(let t) = item, t == active {
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

        // Tooltip bubble when mouse hovers over a tool
        if let hoverIdx = hoveredButtonIndex, hoverIdx < items.count {
            let btnRect = NSRect(x: r.minX + 8 + CGFloat(hoverIdx) * 33, y: r.minY + 6, width: 30, height: 30)
            drawTooltipBubble(text: tooltipText(for: items[hoverIdx]), above: btnRect)
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
        case .redo:
            drawSystemIcon("arrow.uturn.forward", in: rect, tint: tint)
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

    // MARK: - Tooltip Bubble

    private func tooltipText(for item: ToolbarItem) -> String {
        switch item {
        case .tool(let tool):
            switch tool {
            case .rectangle: return "矩形 (R)"
            case .oval: return "椭圆 (O)"
            case .line: return "直线 (L)"
            case .arrow: return "箭头 (A)"
            case .pen: return "画笔 (P)"
            case .mosaic: return "马赛克 (M)"
            case .text: return "文字 (T)"
            case .step: return "步骤序号 (S)"
            }
        case .pin: return "贴图 (F)"
        case .undo: return "撤销 (⌘Z)"
        case .redo: return "下一步 (⇧⌘Z)"
        case .ocr: return "文字识别 (O)"
        case .cancel: return "退出 (Esc)"
        case .scrolling: return "滚动长截图"
        case .save: return "保存 (⌘S)"
        case .copy: return "完成/复制 (↵)"
        }
    }

    private func drawTooltipBubble(text: String, above targetRect: NSRect) {
        let font = NSFont.systemFont(ofSize: 11, weight: .semibold)
        let attrs: [NSAttributedString.Key: Any] = [
            .font: font,
            .foregroundColor: NSColor.white
        ]
        let textSize = (text as NSString).size(withAttributes: attrs)
        let bubbleW = textSize.width + 16
        let bubbleH: CGFloat = 22

        var bubbleX = targetRect.midX - bubbleW / 2
        bubbleX = max(6, min(bounds.width - bubbleW - 6, bubbleX))
        var bubbleY = targetRect.maxY + 8
        var arrowPointsDown = true
        if bubbleY + bubbleH > bounds.height - 10 {
            bubbleY = targetRect.minY - bubbleH - 8
            arrowPointsDown = false
        }

        let bubbleRect = NSRect(x: bubbleX, y: bubbleY, width: bubbleW, height: bubbleH)

        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.32)
        shadow.shadowBlurRadius = 4
        shadow.shadowOffset = NSSize(width: 0, height: -1)
        shadow.set()

        NSColor(white: 0.12, alpha: 0.94).setFill()
        let bg = NSBezierPath(roundedRect: bubbleRect, xRadius: 5, yRadius: 5)

        let arrow = NSBezierPath()
        let arrowMidX = targetRect.midX
        if arrowPointsDown {
            arrow.move(to: NSPoint(x: arrowMidX - 4, y: bubbleRect.minY))
            arrow.line(to: NSPoint(x: arrowMidX, y: bubbleRect.minY - 4))
            arrow.line(to: NSPoint(x: arrowMidX + 4, y: bubbleRect.minY))
        } else {
            arrow.move(to: NSPoint(x: arrowMidX - 4, y: bubbleRect.maxY))
            arrow.line(to: NSPoint(x: arrowMidX, y: bubbleRect.maxY + 4))
            arrow.line(to: NSPoint(x: arrowMidX + 4, y: bubbleRect.maxY))
        }
        arrow.close()

        bg.append(arrow)
        bg.fill()
        NSGraphicsContext.restoreGraphicsState()

        let textPt = NSPoint(x: bubbleRect.minX + 8, y: bubbleRect.minY + (bubbleH - textSize.height) / 2)
        (text as NSString).draw(at: textPt, withAttributes: attrs)
    }

    // MARK: - Sub-Toolbar (Child Tools)

    static let subToolbarPaletteColors: [NSColor] = [
        NSColor(white: 1.0, alpha: 1.0),                               // 白色
        NSColor(srgbRed: 0.12, green: 0.12, blue: 0.13, alpha: 1.0), // 黑色
        NSColor(srgbRed: 1.0, green: 0.23, blue: 0.19, alpha: 1.0),  // 红色 #FF3B30
        NSColor(srgbRed: 1.0, green: 0.58, blue: 0.0, alpha: 1.0),   // 橙色 #FF9500
        NSColor(srgbRed: 1.0, green: 0.80, blue: 0.0, alpha: 1.0),   // 黄色 #FFCC00
        NSColor(srgbRed: 0.20, green: 0.78, blue: 0.35, alpha: 1.0),  // 绿色 #34C759
        NSColor(srgbRed: 0.0, green: 0.48, blue: 1.0, alpha: 1.0),    // 蓝色 #007AFF
        NSColor(srgbRed: 0.69, green: 0.32, blue: 0.87, alpha: 1.0)   // 紫色 #AF52DE
    ]

    private func closeColorPickerIfNeeded() {
        if NSColorPanel.sharedColorPanelExists {
            let cp = NSColorPanel.shared
            if cp.isVisible {
                cp.orderOut(nil)
            }
        }
    }

    private func subToolbarRect() -> NSRect {
        guard isSubToolbarVisible && !isScrollingMode, let tool = subToolbarTool else { return .zero }
        let tb = bottomToolbarRect()
        let w: CGFloat = (tool == .mosaic) ? 180 : 520
        let h: CGFloat = 38
        let x = max(10, min(bounds.width - w - 10, tb.midX - w / 2))
        var y = tb.minY - h - 8
        if y < 8 {
            y = tb.maxY + 8
        }
        return NSRect(x: x, y: y, width: w, height: h)
    }

    private func drawSubToolbar() {
        let r = subToolbarRect()
        guard r.width > 0, r.height > 0, let tool = subToolbarTool else { return }

        // Drop shadow
        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.18)
        shadow.shadowBlurRadius = 8
        shadow.shadowOffset = NSSize(width: 0, height: -2)
        shadow.set()

        NSColor.white.setFill()
        let bg = NSBezierPath(roundedRect: r, xRadius: 10, yRadius: 10)
        bg.fill()
        NSGraphicsContext.restoreGraphicsState()

        NSColor(white: 0.88, alpha: 1.0).setStroke()
        let border = NSBezierPath(roundedRect: r, xRadius: 10, yRadius: 10)
        border.lineWidth = 1.0
        border.stroke()

        let midY = r.midY

        // 1. 马赛克工具：独立精简居中展示
        if tool == .mosaic {
            drawSegmented(items: ["毛玻璃", "马赛克"], selectedIndex: mosaicIsBlur ? 0 : 1, in: NSRect(x: r.minX + 20, y: midY - 11, width: 140, height: 22))
            return
        }

        // 2. 左侧工具专属选项区（Left Zone: X = minX + 10 ~ minX + 128）
        switch tool {
        case .rectangle:
            drawCheckbox(in: NSRect(x: r.minX + 12, y: midY - 9, width: 52, height: 18), isChecked: isRectFilled, title: "实心")
            drawCheckbox(in: NSRect(x: r.minX + 68, y: midY - 9, width: 52, height: 18), isChecked: isRectRounded, title: "圆角")
        case .oval:
            drawCheckbox(in: NSRect(x: r.minX + 12, y: midY - 9, width: 52, height: 18), isChecked: isOvalFilled, title: "实心")
        case .line:
            break // 干净对齐，无多余字样
        case .arrow:
            drawCheckbox(in: NSRect(x: r.minX + 12, y: midY - 9, width: 80, height: 18), isChecked: isArrowCurved, title: "曲线箭头")
        case .pen:
            drawSegmented(items: ["单一颜色", "荧光笔"], selectedIndex: penIsHighlighter ? 1 : 0, in: NSRect(x: r.minX + 10, y: midY - 11, width: 116, height: 22))
        case .step:
            drawSegmented(items: ["描边", "实心"], selectedIndex: stepIsFilled ? 1 : 0, in: NSRect(x: r.minX + 12, y: midY - 11, width: 80, height: 22))
        case .text:
            drawSegmented(items: ["无", "描边", "背景"], selectedIndex: textStyle, in: NSRect(x: r.minX + 10, y: midY - 11, width: 94, height: 22))
            if textStyle != 0 {
                let tagRect = NSRect(x: r.minX + 108, y: midY - 10, width: 22, height: 20)
                let tagTitle = textIsSelectingSecondary ? ((textStyle == 1) ? "边" : "底") : "字"
                let tagColor = textIsSelectingSecondary ? textSecondaryColor : currentDrawColor
                drawMiniColorTag(title: tagTitle, color: tagColor, in: tagRect)
            }
        case .mosaic:
            break
        }

        // 3. 中间统一调色板（Middle Zone: X = minX + 136，宽约 175px）
        let dotStartX = r.minX + 136
        let colors = Self.subToolbarPaletteColors
        let activeColor = (tool == .text && textIsSelectingSecondary) ? textSecondaryColor : currentDrawColor

        for (i, c) in colors.enumerated() {
            let dotRect = NSRect(x: dotStartX + CGFloat(i) * 18.5, y: midY - 7.5, width: 15, height: 15)
            let isSelected = c.isEqual(activeColor)
            drawColorDot(color: c, in: dotRect, isSelected: isSelected)
        }
        let rainbowRect = NSRect(x: dotStartX + CGFloat(colors.count) * 18.5, y: midY - 7.5, width: 15, height: 15)
        drawRainbowDot(in: rainbowRect)

        // 4. 右侧统一滑块调节区（Right Zone: X = minX + 316 ~ maxX - 10）
        // 4.1 粗细 / 字号滑块（X = minX + 316, 宽 96）
        if tool != .step {
            let sizeSliderX = r.minX + 316
            let sizeLabel: String
            let sizeRatio: CGFloat
            let sizeValStr: String

            if tool == .text {
                sizeLabel = "字号"
                sizeRatio = max(0, min(1, (currentSize - 12) / 60.0))
                sizeValStr = "\(Int(currentSize))"
            } else if tool == .rectangle {
                sizeLabel = isRectFilled ? "实心" : "粗细"
                sizeRatio = max(0, min(1, (currentSize - 1) / 29.0))
                sizeValStr = isRectFilled ? "--" : "\(Int(currentSize))"
            } else {
                sizeLabel = "粗细"
                sizeRatio = max(0, min(1, (currentSize - 1) / 29.0))
                sizeValStr = "\(Int(currentSize))"
            }

            drawSlider(
                label: sizeLabel,
                valueStr: sizeValStr,
                ratio: sizeRatio,
                in: NSRect(x: sizeSliderX, y: midY - 10, width: 96, height: 20)
            )
        }

        // 4.2 不透明度滑块（X = minX + 416, 宽 94）
        let opacityRatio = max(0, min(1, (currentOpacity - 0.1) / 0.9))
        drawSlider(
            label: "不透明度",
            valueStr: "\(Int(round(currentOpacity * 100)))",
            ratio: opacityRatio,
            in: NSRect(x: r.minX + 416, y: midY - 10, width: 94, height: 20)
        )
    }

    private func drawMiniColorTag(title: String, color: NSColor, in rect: NSRect) {
        NSColor(srgbRed: 0.12, green: 0.53, blue: 0.98, alpha: 0.15).setFill()
        let bg = NSBezierPath(roundedRect: rect, xRadius: 4, yRadius: 4)
        bg.fill()
        NSColor(srgbRed: 0.12, green: 0.53, blue: 0.98, alpha: 0.8).setStroke()
        bg.lineWidth = 1.0
        bg.stroke()

        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 10, weight: .bold),
            .foregroundColor: NSColor(srgbRed: 0.12, green: 0.53, blue: 0.98, alpha: 1.0)
        ]
        let sz = (title as NSString).size(withAttributes: attrs)
        (title as NSString).draw(at: NSPoint(x: rect.midX - sz.width / 2, y: rect.midY - sz.height / 2), withAttributes: attrs)
    }

    private func drawCheckbox(in rect: NSRect, isChecked: Bool, title: String) {
        let boxRect = NSRect(x: rect.minX, y: rect.midY - 6.5, width: 13, height: 13)
        if isChecked {
            NSColor(srgbRed: 0.12, green: 0.53, blue: 0.98, alpha: 1.0).setFill()
            let box = NSBezierPath(roundedRect: boxRect, xRadius: 3, yRadius: 3)
            box.fill()
            let checkmark = NSBezierPath()
            checkmark.move(to: NSPoint(x: boxRect.minX + 3, y: boxRect.midY))
            checkmark.line(to: NSPoint(x: boxRect.minX + 5.5, y: boxRect.minY + 3))
            checkmark.line(to: NSPoint(x: boxRect.maxX - 3, y: boxRect.maxY - 3.5))
            checkmark.lineWidth = 1.6
            checkmark.lineCapStyle = .round
            checkmark.lineJoinStyle = .round
            NSColor.white.setStroke()
            checkmark.stroke()
        } else {
            NSColor.white.setFill()
            let box = NSBezierPath(roundedRect: boxRect, xRadius: 3, yRadius: 3)
            box.fill()
            NSColor(white: 0.72, alpha: 1.0).setStroke()
            box.lineWidth = 1.2
            box.stroke()
        }

        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 11, weight: .medium),
            .foregroundColor: NSColor(white: 0.25, alpha: 1.0)
        ]
        let sz = (title as NSString).size(withAttributes: attrs)
        (title as NSString).draw(at: NSPoint(x: boxRect.maxX + 4, y: rect.midY - sz.height / 2), withAttributes: attrs)
    }

    private func drawSegmented(items: [String], selectedIndex: Int, in rect: NSRect) {
        NSColor(white: 0.93, alpha: 1.0).setFill()
        let bg = NSBezierPath(roundedRect: rect, xRadius: 5, yRadius: 5)
        bg.fill()

        let count = CGFloat(items.count)
        guard count > 0 else { return }
        let itemW = rect.width / count

        for (i, title) in items.enumerated() {
            let itemRect = NSRect(x: rect.minX + CGFloat(i) * itemW, y: rect.minY, width: itemW, height: rect.height)
            let isSelected = (i == selectedIndex)

            if isSelected {
                NSGraphicsContext.saveGraphicsState()
                let shadow = NSShadow()
                shadow.shadowColor = NSColor.black.withAlphaComponent(0.12)
                shadow.shadowBlurRadius = 2
                shadow.shadowOffset = NSSize(width: 0, height: -1)
                shadow.set()

                NSColor.white.setFill()
                let pill = NSBezierPath(roundedRect: itemRect.insetBy(dx: 1.5, dy: 1.5), xRadius: 4, yRadius: 4)
                pill.fill()
                NSGraphicsContext.restoreGraphicsState()
            }

            let font = NSFont.systemFont(ofSize: 10.5, weight: isSelected ? .bold : .medium)
            let color = isSelected ? NSColor(srgbRed: 0.12, green: 0.53, blue: 0.98, alpha: 1.0) : NSColor(white: 0.45, alpha: 1.0)
            let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color]
            let sz = (title as NSString).size(withAttributes: attrs)
            (title as NSString).draw(at: NSPoint(x: itemRect.midX - sz.width / 2, y: itemRect.midY - sz.height / 2), withAttributes: attrs)
        }
    }

    private func drawBadge(title: String, in rect: NSRect) {
        NSColor(white: 0.93, alpha: 1.0).setFill()
        let bg = NSBezierPath(roundedRect: rect, xRadius: 4, yRadius: 4)
        bg.fill()

        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 10, weight: .medium),
            .foregroundColor: NSColor(white: 0.35, alpha: 1.0)
        ]
        let sz = (title as NSString).size(withAttributes: attrs)
        (title as NSString).draw(at: NSPoint(x: rect.midX - sz.width / 2, y: rect.midY - sz.height / 2), withAttributes: attrs)
    }

    private func drawColorDot(color: NSColor, in rect: NSRect, isSelected: Bool) {
        color.setFill()
        let dot = NSBezierPath(ovalIn: rect)
        dot.fill()

        if color == .white || color.isEqual(NSColor.white) {
            NSColor(white: 0.8, alpha: 1.0).setStroke()
            dot.lineWidth = 1.0
            dot.stroke()
        }

        if isSelected {
            NSColor(srgbRed: 0.12, green: 0.53, blue: 0.98, alpha: 1.0).setStroke()
            let ring = NSBezierPath(ovalIn: rect.insetBy(dx: -2.5, dy: -2.5))
            ring.lineWidth = 1.8
            ring.stroke()
        }
    }

    private func drawRainbowDot(in rect: NSRect) {
        let r = rect
        let mid = NSPoint(x: r.midX, y: r.midY)
        let rad = r.width / 2

        NSGraphicsContext.saveGraphicsState()
        let clip = NSBezierPath(ovalIn: r)
        clip.addClip()

        NSColor(srgbRed: 1.0, green: 0.23, blue: 0.19, alpha: 1.0).setFill()
        NSRect(x: r.minX, y: mid.y, width: rad, height: rad).fill()

        NSColor(srgbRed: 1.0, green: 0.80, blue: 0.0, alpha: 1.0).setFill()
        NSRect(x: mid.x, y: mid.y, width: rad, height: rad).fill()

        NSColor(srgbRed: 0.20, green: 0.78, blue: 0.35, alpha: 1.0).setFill()
        NSRect(x: r.minX, y: r.minY, width: rad, height: rad).fill()

        NSColor(srgbRed: 0.0, green: 0.48, blue: 1.0, alpha: 1.0).setFill()
        NSRect(x: mid.x, y: r.minY, width: rad, height: rad).fill()

        NSGraphicsContext.restoreGraphicsState()

        NSColor(white: 0.8, alpha: 1.0).setStroke()
        let ring = NSBezierPath(ovalIn: r)
        ring.lineWidth = 1.0
        ring.stroke()
    }

    private func drawSlider(label: String, valueStr: String, ratio: CGFloat, in rect: NSRect) {
        let labelAttrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 10, weight: .medium),
            .foregroundColor: NSColor(white: 0.4, alpha: 1.0)
        ]
        let lSize = (label as NSString).size(withAttributes: labelAttrs)
        (label as NSString).draw(at: NSPoint(x: rect.minX, y: rect.midY - lSize.height / 2), withAttributes: labelAttrs)

        let trackX = rect.minX + lSize.width + 5
        let trackW: CGFloat = 36
        let trackRect = NSRect(x: trackX, y: rect.midY - 1.5, width: trackW, height: 3)

        NSColor(white: 0.88, alpha: 1.0).setFill()
        NSBezierPath(roundedRect: trackRect, xRadius: 1.5, yRadius: 1.5).fill()

        let filledW = max(0, min(trackW, ratio * trackW))
        let filledRect = NSRect(x: trackX, y: rect.midY - 1.5, width: filledW, height: 3)
        NSColor(srgbRed: 0.12, green: 0.53, blue: 0.98, alpha: 1.0).setFill()
        NSBezierPath(roundedRect: filledRect, xRadius: 1.5, yRadius: 1.5).fill()

        let knobX = trackX + filledW
        let knobRect = NSRect(x: knobX - 4.5, y: rect.midY - 4.5, width: 9, height: 9)
        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.2)
        shadow.shadowBlurRadius = 2
        shadow.shadowOffset = NSSize(width: 0, height: -1)
        shadow.set()
        NSColor.white.setFill()
        NSBezierPath(ovalIn: knobRect).fill()
        NSGraphicsContext.restoreGraphicsState()

        NSColor(white: 0.7, alpha: 1.0).setStroke()
        let knobStroke = NSBezierPath(ovalIn: knobRect)
        knobStroke.lineWidth = 0.8
        knobStroke.stroke()

        let valAttrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 9.5, weight: .semibold),
            .foregroundColor: NSColor(white: 0.35, alpha: 1.0)
        ]
        let vSize = (valueStr as NSString).size(withAttributes: valAttrs)
        (valueStr as NSString).draw(at: NSPoint(x: trackX + trackW + 3, y: rect.midY - vSize.height / 2), withAttributes: valAttrs)
    }

    private func handleSubToolbarClick(at p: NSPoint) {
        let r = subToolbarRect()
        guard r.contains(p), let tool = subToolbarTool else { return }

        let midY = r.midY

        // 1. 马赛克工具
        if tool == .mosaic {
            let segRect = NSRect(x: r.minX + 20, y: midY - 11, width: 140, height: 22)
            if segRect.contains(p) {
                mosaicIsBlur = (p.x < segRect.midX)
                return
            }
            return
        }

        // 2. 左侧专有选项点击
        switch tool {
        case .rectangle:
            let r1 = NSRect(x: r.minX + 12, y: midY - 9, width: 52, height: 18)
            let r2 = NSRect(x: r.minX + 68, y: midY - 9, width: 52, height: 18)
            if r1.contains(p) { isRectFilled.toggle(); return }
            if r2.contains(p) { isRectRounded.toggle(); return }
        case .oval:
            let r1 = NSRect(x: r.minX + 12, y: midY - 9, width: 52, height: 18)
            if r1.contains(p) { isOvalFilled.toggle(); return }
        case .arrow:
            let r1 = NSRect(x: r.minX + 12, y: midY - 9, width: 80, height: 18)
            if r1.contains(p) { isArrowCurved.toggle(); return }
        case .pen:
            let segRect = NSRect(x: r.minX + 10, y: midY - 11, width: 116, height: 22)
            if segRect.contains(p) {
                penIsHighlighter = (p.x > segRect.midX)
                return
            }
        case .step:
            let segRect = NSRect(x: r.minX + 12, y: midY - 11, width: 80, height: 22)
            if segRect.contains(p) {
                stepIsFilled = (p.x > segRect.midX)
                return
            }
        case .text:
            let segRect = NSRect(x: r.minX + 10, y: midY - 11, width: 94, height: 22)
            if segRect.contains(p) {
                let idx = Int((p.x - segRect.minX) / (segRect.width / 3))
                textStyle = max(0, min(2, idx))
                return
            }
            if textStyle != 0 {
                let tagRect = NSRect(x: r.minX + 108, y: midY - 10, width: 22, height: 20)
                if tagRect.contains(p) {
                    textIsSelectingSecondary.toggle()
                    return
                }
            }
        case .line, .mosaic:
            break
        }

        // 3. 中间统一调色板点击 (X = minX + 136)
        let dotStartX = r.minX + 136
        let colors = Self.subToolbarPaletteColors
        for (i, c) in colors.enumerated() {
            let dotRect = NSRect(x: dotStartX + CGFloat(i) * 18.5, y: midY - 8, width: 16, height: 16)
            if dotRect.contains(p) {
                if tool == .text && textIsSelectingSecondary {
                    textSecondaryColor = c
                } else {
                    currentDrawColor = c
                }
                return
            }
        }
        let rainbowRect = NSRect(x: dotStartX + CGFloat(colors.count) * 18.5, y: midY - 8, width: 16, height: 16)
        if rainbowRect.contains(p) {
            openColorPicker()
            return
        }

        // 4. 右侧双滑块点击
        if tool != .step {
            let sizeSliderRect = NSRect(x: r.minX + 316, y: midY - 10, width: 96, height: 20)
            if sizeSliderRect.contains(p) {
                isDraggingSizeSlider = true
                updateSubToolbarSize(at: p.x)
                return
            }
        }

        let opacitySliderRect = NSRect(x: r.minX + 416, y: midY - 10, width: 94, height: 20)
        if opacitySliderRect.contains(p) {
            isDraggingOpacitySlider = true
            updateSubToolbarOpacity(at: p.x)
            return
        }
    }

    private func updateSubToolbarSize(at x: CGFloat) {
        guard let tool = subToolbarTool, tool != .mosaic, tool != .step else { return }
        let r = subToolbarRect()
        let trackMinX = r.minX + 316 + 28
        let trackW: CGFloat = 36
        let ratio = max(0, min(1, (x - trackMinX) / trackW))
        if tool == .text {
            currentSize = round(12 + ratio * 60)
        } else {
            currentSize = max(1, round(1 + ratio * 29))
        }
        needsDisplay = true
    }

    private func updateSubToolbarOpacity(at x: CGFloat) {
        guard let tool = subToolbarTool, tool != .mosaic else { return }
        let r = subToolbarRect()
        let trackMinX = r.minX + 416 + 40
        let trackW: CGFloat = 36
        let ratio = max(0, min(1, (x - trackMinX) / trackW))
        currentOpacity = max(0.1, min(1.0, 0.1 + ratio * 0.9))
        needsDisplay = true
    }

    private func openColorPicker() {
        let cp = NSColorPanel.shared
        let targetCol = (subToolbarTool == .text && textIsSelectingSecondary) ? textSecondaryColor : currentDrawColor
        cp.color = targetCol
        cp.setTarget(self)
        cp.setAction(#selector(colorPanelChanged(_:)))
        cp.orderFront(nil)
    }

    @objc private func colorPanelChanged(_ sender: NSColorPanel) {
        if subToolbarTool == .text && textIsSelectingSecondary {
            textSecondaryColor = sender.color
        } else {
            currentDrawColor = sender.color
        }
        needsDisplay = true
    }

    // MARK: - Color Calculation

    private func currentPixelColor() -> (r: Int, g: Int, b: Int, hex: String, name: String) {
        guard let pos = mousePosition, let snapshot = screenSnapshot else {
            return (255, 255, 255, "FFFFFF", "白色")
        }
        let scaleX = bounds.width > 0 ? (CGFloat(snapshot.width) / bounds.width) : 2.0
        let scaleY = bounds.height > 0 ? (CGFloat(snapshot.height) / bounds.height) : 2.0
        let pixelX = Int(pos.x * scaleX)
        let pixelY = Int((bounds.height - pos.y) * scaleY)
        guard pixelX >= 0, pixelX < snapshot.width, pixelY >= 0, pixelY < snapshot.height else {
            return (255, 255, 255, "FFFFFF", "白色")
        }

        // Fast path: direct memory buffer read (zero allocation, 0.001ms, buttery smooth 120 FPS)
        if let data = snapshotDataProvider, let ptr = CFDataGetBytePtr(data), snapshotBytesPerRow > 0 {
            let offset = pixelY * snapshotBytesPerRow + pixelX * 4
            let total = CFDataGetLength(data)
            if offset + 3 < total {
                let b = Int(ptr[offset])
                let g = Int(ptr[offset + 1])
                let r = Int(ptr[offset + 2])
                let hex = String(format: "%02X%02X%02X", r, g, b)
                let name = chineseColorName(r: r, g: g, b: b)
                return (r, g, b, hex, name)
            }
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
