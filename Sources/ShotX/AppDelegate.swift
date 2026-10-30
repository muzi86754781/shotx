import Cocoa
import Combine
import UniformTypeIdentifiers

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var statusItem: NSStatusItem!
    private var hotKeyManager: HotKeyManager!
    private var fullscreenHotKeyManager: HotKeyManager!
    private var windowHotKeyManager: HotKeyManager!
    private var previousAreaHotKeyManager: HotKeyManager!
    private var ocrHotKeyManager: HotKeyManager!
    private var recordScreenHotKeyManager: HotKeyManager!
    private var recordAudioHotKeyManager: HotKeyManager!
    private var pinHotKeyManager: HotKeyManager!
    private var colorPickerHotKeyManager: HotKeyManager!
    private var desktopIconsHotKeyManager: HotKeyManager!
    private var allInOneHotKeyManager: HotKeyManager!

    private var overlayController: OverlayController?
    private var popupController: PostCapturePopupController?
    private var annotationControllers: [AnnotationWindowController] = []
    private var mainWindowController: MainWindowController?
    private var countdownController: CountdownController?
    private var timerFrameOverlay: RecordingFrameOverlay?
    private var windowCaptureController: WindowCaptureController?
    private var permissionController: PermissionPromptController?
    private var shortcutCancellables = Set<AnyCancellable>()

    func applicationDidFinishLaunching(_ notification: Notification) {
        setupStatusBar()
        setupHotKey()

        // Boot Sparkle so periodic background update checks begin.
        MainActor.assumeIsolated { _ = UpdaterController.shared }

        // Show the permissions prompt on launch if Screen Recording isn't granted.
        // Slight delay so the menu bar icon appears first and gives the user
        // visual context that the app launched.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) { [weak self] in
            MainActor.assumeIsolated {
                if !Permissions.screenRecordingGranted {
                    self?.showPermissionPrompt()
                }
            }
        }
    }

    @discardableResult
    private func ensurePermissionOrPrompt() -> Bool {
        if Permissions.screenRecordingGranted { return true }
        showPermissionPrompt()
        return false
    }

    private func showPermissionPrompt() {
        MainActor.assumeIsolated {
            if permissionController == nil {
                permissionController = PermissionPromptController()
            }
            permissionController?.show()
        }
    }

    @objc private func openPermissions() {
        showPermissionPrompt()
    }

    // MARK: - Status bar

    private func setupStatusBar() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = statusItem.button {
            button.image = AppDelegate.menuBarIcon()
            button.toolTip = "ShotX"
        }

        let menu = NSMenu()
        menu.delegate = self
        statusItem.menu = menu
    }

    /// Renders the same viewfinder + X design as the app icon, monochrome,
    /// for the menu bar (template image, auto-tints to system color).
    private static func menuBarIcon() -> NSImage {
        let s: CGFloat = 18
        let img = NSImage(size: NSSize(width: s, height: s), flipped: false) { rect in
            let inset = rect.width * 0.10
            let cornerLen = rect.width * 0.28
            let lineWidth = rect.width * 0.14
            let frame = NSRect(
                x: inset, y: inset,
                width: rect.width - 2 * inset,
                height: rect.height - 2 * inset
            )

            NSColor.black.setStroke()
            let corners = NSBezierPath()
            corners.lineWidth = lineWidth
            corners.lineCapStyle = .round
            corners.lineJoinStyle = .round

            // Top-left
            corners.move(to: NSPoint(x: frame.minX, y: frame.maxY - cornerLen))
            corners.line(to: NSPoint(x: frame.minX, y: frame.maxY))
            corners.line(to: NSPoint(x: frame.minX + cornerLen, y: frame.maxY))
            // Top-right
            corners.move(to: NSPoint(x: frame.maxX - cornerLen, y: frame.maxY))
            corners.line(to: NSPoint(x: frame.maxX, y: frame.maxY))
            corners.line(to: NSPoint(x: frame.maxX, y: frame.maxY - cornerLen))
            // Bottom-right
            corners.move(to: NSPoint(x: frame.maxX, y: frame.minY + cornerLen))
            corners.line(to: NSPoint(x: frame.maxX, y: frame.minY))
            corners.line(to: NSPoint(x: frame.maxX - cornerLen, y: frame.minY))
            // Bottom-left
            corners.move(to: NSPoint(x: frame.minX + cornerLen, y: frame.minY))
            corners.line(to: NSPoint(x: frame.minX, y: frame.minY))
            corners.line(to: NSPoint(x: frame.minX, y: frame.minY + cornerLen))
            corners.stroke()

            // Center X
            let xR = rect.width * 0.07
            let xLine = rect.width * 0.16
            let cx = rect.width / 2
            let cy = rect.height / 2
            let xPath = NSBezierPath()
            xPath.lineWidth = xLine
            xPath.lineCapStyle = .round
            xPath.move(to: NSPoint(x: cx - xR, y: cy - xR))
            xPath.line(to: NSPoint(x: cx + xR, y: cy + xR))
            xPath.move(to: NSPoint(x: cx + xR, y: cy - xR))
            xPath.line(to: NSPoint(x: cx - xR, y: cy + xR))
            xPath.stroke()

            return true
        }
        img.isTemplate = true
        return img
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        guard menu === statusItem.menu else { return }
        menu.removeAllItems()
        populateStatusMenu(menu)
    }

    private func populateStatusMenu(_ menu: NSMenu) {
        menu.addItem(menuItem(
            title: "全能截图",
            symbol: "square.grid.2x2",
            action: #selector(showAllInOne),
            shortcut: ShortcutStore.shared.allInOneShortcut
        ))

        menu.addItem(.separator())

        menu.addItem(menuItem(
            title: "区域截图",
            symbol: "rectangle.dashed",
            action: #selector(captureNow),
            shortcut: ShortcutStore.shared.shortcut
        ))

        let prev = menuItem(
            title: "截取上一区域",
            symbol: "arrow.clockwise",
            action: #selector(capturePreviousAreaFromMenu),
            shortcut: ShortcutStore.shared.previousAreaShortcut
        )
        prev.toolTip = "按照上一次截图的相同位置和大小自动截图，无需重新拉框"
        prev.isEnabled = LastCaptureStore.hasPrevious
        menu.addItem(prev)

        menu.addItem(menuItem(
            title: "截取全屏",
            symbol: "display",
            action: #selector(captureFullscreenFromMenu),
            shortcut: ShortcutStore.shared.fullscreenShortcut
        ))

        menu.addItem(menuItem(
            title: "截取窗口",
            symbol: "macwindow",
            action: #selector(captureWindow),
            shortcut: ShortcutStore.shared.windowCaptureShortcut
        ))

        menu.addItem(menuItem(
            title: "提取文字 (OCR)",
            symbol: "doc.text.viewfinder",
            action: #selector(captureTextNow),
            shortcut: ShortcutStore.shared.ocrShortcut
        ))

        menu.addItem(.separator())

        let recording = MainActor.assumeIsolated { RecordingController.shared.isRecording }
        if recording {
            let stop = menuItem(
                title: "停止录制",
                symbol: "stop.fill",
                action: #selector(stopRecording),
                shortcut: ShortcutStore.shared.recordScreenShortcut
            )
            menu.addItem(stop)
        } else {
            menu.addItem(menuItem(
                title: "屏幕录制",
                symbol: "video",
                action: #selector(recordScreen),
                shortcut: ShortcutStore.shared.recordScreenShortcut
            ))
        }

        let isAudioRec = AudioRecorderController.shared.isRecording
        menu.addItem(menuItem(
            title: isAudioRec ? "停止录音" : "屏幕录音",
            symbol: isAudioRec ? "stop.circle.fill" : "mic.fill",
            action: #selector(toggleAudioRecording),
            shortcut: ShortcutStore.shared.recordAudioShortcut
        ))

        menu.addItem(.separator())

        let timerItem = NSMenuItem(title: "定时截图", action: nil, keyEquivalent: "")
        timerItem.image = NSImage(systemSymbolName: "timer", accessibilityDescription: nil)
        let timerMenu = NSMenu()
        for seconds in [3, 5, 10] {
            let item = NSMenuItem(
                title: "\(seconds) 秒",
                action: #selector(captureAfterTimer(_:)),
                keyEquivalent: ""
            )
            item.tag = seconds
            item.target = self
            timerMenu.addItem(item)
        }
        timerItem.submenu = timerMenu
        menu.addItem(timerItem)

        menu.addItem(.separator())

        let isDeskVisible = DesktopIcons.isVisible()
        let showDesktop = menuItem(
            title: isDeskVisible ? "隐藏桌面图标" : "显示桌面图标",
            symbol: isDeskVisible ? "eye.slash" : "eye",
            action: #selector(toggleDesktopIcons),
            shortcut: ShortcutStore.shared.desktopIconsShortcut
        )
        showDesktop.toolTip = "一键隐藏或恢复桌面上所有文件和图标，录屏或演示更整洁"
        menu.addItem(showDesktop)

        menu.addItem(menuItem(
            title: "拾色器…",
            symbol: "eyedropper",
            action: #selector(pickColor),
            shortcut: ShortcutStore.shared.colorPickerShortcut
        ))

        menu.addItem(menuItem(
            title: "打开图片…",
            symbol: "folder",
            action: #selector(openImageFile)
        ))
        menu.addItem(menuItem(
            title: "从剪贴板打开",
            symbol: "doc.on.clipboard",
            action: #selector(openFromClipboard),
            keyEquivalent: "v",
            keyEquivalentModifiers: [.command, .shift]
        ))

        menu.addItem(menuItem(
            title: "贴图到屏幕…",
            symbol: "pin",
            action: #selector(pinToScreen),
            shortcut: ShortcutStore.shared.pinShortcut
        ))

        menu.addItem(.separator())

        menu.addItem(menuItem(
            title: "截图历史…",
            symbol: "clock.arrow.circlepath",
            action: #selector(showHistory)
        ))

        menu.addItem(.separator())

        if Permissions.screenRecordingGranted {
            let granted = NSMenuItem(title: "权限已获取", action: nil, keyEquivalent: "")
            let config = NSImage.SymbolConfiguration(paletteColors: [NSColor.systemGreen])
            let icon = NSImage(systemSymbolName: "checkmark.circle.fill", accessibilityDescription: nil)?
                .withSymbolConfiguration(config)
            icon?.isTemplate = false
            granted.image = icon
            granted.isEnabled = false
            menu.addItem(granted)
        } else {
            menu.addItem(menuItem(
                title: "需要权限…",
                symbol: "exclamationmark.shield",
                action: #selector(openPermissions)
            ))
        }

        menu.addItem(menuItem(
            title: "检查更新…",
            symbol: "arrow.down.circle",
            action: #selector(checkForUpdates)
        ))

        menu.addItem(menuItem(
            title: "关于 ShotX",
            symbol: "info.circle",
            action: #selector(showAbout)
        ))
        menu.addItem(menuItem(
            title: "偏好设置…",
            symbol: "gearshape",
            action: #selector(showSettings),
            keyEquivalent: ",",
            keyEquivalentModifiers: .command
        ))
        menu.addItem(menuItem(
            title: "退出 ShotX",
            symbol: "power",
            action: #selector(quit),
            keyEquivalent: "q",
            keyEquivalentModifiers: .command
        ))
    }

    private func menuItem(
        title: String,
        symbol: String?,
        action: Selector,
        keyEquivalent: String = "",
        keyEquivalentModifiers: NSEvent.ModifierFlags = [],
        shortcut: Shortcut? = nil
    ) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: keyEquivalent)
        item.keyEquivalentModifierMask = keyEquivalentModifiers
        item.target = self
        if let symbol = symbol {
            item.image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil)
        }
        if let s = shortcut,
           let ke = ShortcutFormatter.menuKeyEquivalent(for: s.keyCode) {
            item.keyEquivalent = ke
            item.keyEquivalentModifierMask = ShortcutFormatter.nsModifierFlags(from: s.modifiers)
        }
        return item
    }

    // MARK: - Hot key

    private func setupHotKey() {
        hotKeyManager = HotKeyManager()
        fullscreenHotKeyManager = HotKeyManager()
        windowHotKeyManager = HotKeyManager()
        previousAreaHotKeyManager = HotKeyManager()
        ocrHotKeyManager = HotKeyManager()
        recordScreenHotKeyManager = HotKeyManager()
        recordAudioHotKeyManager = HotKeyManager()
        pinHotKeyManager = HotKeyManager()
        colorPickerHotKeyManager = HotKeyManager()
        desktopIconsHotKeyManager = HotKeyManager()
        allInOneHotKeyManager = HotKeyManager()

        applyCaptureShortcut(ShortcutStore.shared.shortcut)
        applyFullscreenShortcut(ShortcutStore.shared.fullscreenShortcut)
        applyWindowShortcut(ShortcutStore.shared.windowCaptureShortcut)
        applyPreviousAreaShortcut(ShortcutStore.shared.previousAreaShortcut)
        applyOCRShortcut(ShortcutStore.shared.ocrShortcut)
        applyRecordScreenShortcut(ShortcutStore.shared.recordScreenShortcut)
        applyRecordAudioShortcut(ShortcutStore.shared.recordAudioShortcut)
        applyPinShortcut(ShortcutStore.shared.pinShortcut)
        applyColorPickerShortcut(ShortcutStore.shared.colorPickerShortcut)
        applyDesktopIconsShortcut(ShortcutStore.shared.desktopIconsShortcut)
        applyAllInOneShortcut(ShortcutStore.shared.allInOneShortcut)

        ShortcutStore.shared.$shortcut
            .dropFirst()
            .sink { [weak self] new in self?.applyCaptureShortcut(new) }
            .store(in: &shortcutCancellables)

        ShortcutStore.shared.$fullscreenShortcut
            .dropFirst()
            .sink { [weak self] new in self?.applyFullscreenShortcut(new) }
            .store(in: &shortcutCancellables)

        ShortcutStore.shared.$windowCaptureShortcut
            .dropFirst()
            .sink { [weak self] new in self?.applyWindowShortcut(new) }
            .store(in: &shortcutCancellables)

        ShortcutStore.shared.$previousAreaShortcut
            .dropFirst()
            .sink { [weak self] new in self?.applyPreviousAreaShortcut(new) }
            .store(in: &shortcutCancellables)

        ShortcutStore.shared.$ocrShortcut
            .dropFirst()
            .sink { [weak self] new in self?.applyOCRShortcut(new) }
            .store(in: &shortcutCancellables)

        ShortcutStore.shared.$recordScreenShortcut
            .dropFirst()
            .sink { [weak self] new in self?.applyRecordScreenShortcut(new) }
            .store(in: &shortcutCancellables)

        ShortcutStore.shared.$recordAudioShortcut
            .dropFirst()
            .sink { [weak self] new in self?.applyRecordAudioShortcut(new) }
            .store(in: &shortcutCancellables)

        ShortcutStore.shared.$pinShortcut
            .dropFirst()
            .sink { [weak self] new in self?.applyPinShortcut(new) }
            .store(in: &shortcutCancellables)

        ShortcutStore.shared.$colorPickerShortcut
            .dropFirst()
            .sink { [weak self] new in self?.applyColorPickerShortcut(new) }
            .store(in: &shortcutCancellables)

        ShortcutStore.shared.$desktopIconsShortcut
            .dropFirst()
            .sink { [weak self] new in self?.applyDesktopIconsShortcut(new) }
            .store(in: &shortcutCancellables)

        ShortcutStore.shared.$allInOneShortcut
            .dropFirst()
            .sink { [weak self] new in self?.applyAllInOneShortcut(new) }
            .store(in: &shortcutCancellables)
    }

    private func applyCaptureShortcut(_ s: Shortcut) {
        hotKeyManager.register(keyCode: s.keyCode, modifiers: s.modifiers) { [weak self] in
            self?.captureNow()
        }
    }

    private func applyFullscreenShortcut(_ s: Shortcut) {
        fullscreenHotKeyManager.register(keyCode: s.keyCode, modifiers: s.modifiers) { [weak self] in
            self?.captureFullscreen(settleDelay: 0)
        }
    }

    private func applyWindowShortcut(_ s: Shortcut) {
        windowHotKeyManager.register(keyCode: s.keyCode, modifiers: s.modifiers) { [weak self] in
            self?.captureWindow()
        }
    }

    private func applyPreviousAreaShortcut(_ s: Shortcut) {
        previousAreaHotKeyManager.register(keyCode: s.keyCode, modifiers: s.modifiers) { [weak self] in
            self?.capturePreviousArea(settleDelay: 0)
        }
    }

    private func applyOCRShortcut(_ s: Shortcut) {
        ocrHotKeyManager.register(keyCode: s.keyCode, modifiers: s.modifiers) { [weak self] in
            self?.captureTextNow()
        }
    }

    private func applyRecordScreenShortcut(_ s: Shortcut) {
        recordScreenHotKeyManager.register(keyCode: s.keyCode, modifiers: s.modifiers) { [weak self] in
            self?.recordScreen()
        }
    }

    private func applyRecordAudioShortcut(_ s: Shortcut) {
        recordAudioHotKeyManager.register(keyCode: s.keyCode, modifiers: s.modifiers) { [weak self] in
            self?.toggleAudioRecording()
        }
    }

    private func applyPinShortcut(_ s: Shortcut) {
        pinHotKeyManager.register(keyCode: s.keyCode, modifiers: s.modifiers) { [weak self] in
            self?.pinToScreen()
        }
    }

    private func applyColorPickerShortcut(_ s: Shortcut) {
        colorPickerHotKeyManager.register(keyCode: s.keyCode, modifiers: s.modifiers) { [weak self] in
            self?.pickColor()
        }
    }

    private func applyDesktopIconsShortcut(_ s: Shortcut) {
        desktopIconsHotKeyManager.register(keyCode: s.keyCode, modifiers: s.modifiers) { [weak self] in
            self?.toggleDesktopIcons()
        }
    }

    private func applyAllInOneShortcut(_ s: Shortcut) {
        allInOneHotKeyManager.register(keyCode: s.keyCode, modifiers: s.modifiers) { [weak self] in
            self?.showAllInOne()
        }
    }

    // MARK: - Capture actions

    @objc func captureNow() {
        guard overlayController == nil else { return }
        guard ensurePermissionOrPrompt() else { return }
        let controller = OverlayController()
        overlayController = controller
        controller.begin { [weak self] in
            self?.overlayController = nil
        }
    }

    /// The status menu's dismissal animation outlives the action that triggered
    /// it by roughly this long. Menu windows belong to our process but never
    /// pass through `CaptureExclusions`, so unlike our own panels they can't be
    /// filtered out of the composite — a menu-invoked capture still has to wait
    /// them out. Hotkey-invoked captures pass 0 and run synchronously.
    private static let menuDismissSettle: TimeInterval = 0.15

    @objc private func captureFullscreenFromMenu() {
        captureFullscreen(settleDelay: Self.menuDismissSettle)
    }

    private func captureFullscreen(settleDelay: TimeInterval) {
        guard ensurePermissionOrPrompt() else { return }
        let mouseLoc = NSEvent.mouseLocation
        let screen = NSScreen.screens.first(where: { $0.frame.contains(mouseLoc) }) ?? NSScreen.main
        guard let screen = screen else { return }

        after(settleDelay) { [weak self] in
            guard let self = self,
                  let image = ScreenCapture.capture(rectInScreenCoords: screen.frame, screen: screen)
            else { return }
            LastCaptureStore.save(rect: screen.frame, screen: screen)
            self.handleCapturedImage(image)
            ToastController.shared.show(
                message: "全屏截图已复制到剪贴板",
                icon: "display",
                tint: .systemGreen,
                position: .center
            )
        }
    }

    @objc private func capturePreviousAreaFromMenu() {
        capturePreviousArea(settleDelay: Self.menuDismissSettle)
    }

    private func capturePreviousArea(settleDelay: TimeInterval) {
        guard ensurePermissionOrPrompt() else { return }
        guard let stored = LastCaptureStore.load() else {
            NSSound.beep()
            ToastController.shared.show(
                message: "暂无上一次截图区域，请先进行一次截图",
                icon: "exclamationmark.triangle.fill",
                tint: .systemOrange,
                position: .center
            )
            return
        }
        after(settleDelay) { [weak self] in
            guard let self = self,
                  let image = ScreenCapture.capture(rectInScreenCoords: stored.rect, screen: stored.screen)
            else { return }
            self.handleCapturedImage(image)
            ToastController.shared.show(
                message: "上一区域截图已复制到剪贴板",
                icon: "arrow.clockwise",
                tint: .systemGreen,
                position: .center
            )
        }
    }

    /// Runs on the next main-queue turn when `delay` is zero, so the caller's
    /// event finishes unwinding before we grab pixels.
    private func after(_ delay: TimeInterval, _ work: @escaping () -> Void) {
        if delay <= 0 {
            DispatchQueue.main.async(execute: work)
        } else {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: work)
        }
    }

    @objc private func captureAfterTimer(_ sender: NSMenuItem) {
        let seconds = sender.tag
        countdownController?.cancel()
        let cd = CountdownController()
        countdownController = cd
        cd.start(
            seconds: seconds,
            onCancel: { [weak self] in
                self?.countdownController = nil
            },
            onFinish: { [weak self] in
                self?.countdownController = nil
                self?.captureNow()
            }
        )
    }

    @objc private func captureWindow() {
        guard windowCaptureController == nil else { return }
        guard ensurePermissionOrPrompt() else { return }
        let controller = WindowCaptureController()
        windowCaptureController = controller
        controller.begin { [weak self] image in
            self?.windowCaptureController = nil
            guard let image = image else { return }
            self?.handleCapturedImage(image)
            ToastController.shared.show(
                message: "窗口截图已复制到剪贴板",
                icon: "macwindow",
                tint: .systemGreen,
                position: .center
            )
        }
    }

    @objc func captureTextNow() {
        guard overlayController == nil else { return }
        guard ensurePermissionOrPrompt() else { return }
        let controller = OverlayController()
        overlayController = controller
        controller.begin(isOCR: true, onOCRResult: { [weak self] ocrImage in
            self?.overlayController = nil
            guard let image = ocrImage,
                  let cgImage = OCR.cgImage(from: image) else { return }
            Task { @MainActor in
                if let text = await OCR.recognize(in: cgImage) {
                    ImageSaver.copyTextToClipboard(text)
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
        }, completion: { [weak self] in
            self?.overlayController = nil
        })
    }

    @objc private func pinToScreen() {
        captureNow()
    }

    @objc private func toggleDesktopIcons() {
        let willBeVisible = !DesktopIcons.isVisible()
        DesktopIcons.toggle()
        let msg = willBeVisible ? "桌面图标已恢复显示" : "桌面图标已隐藏"
        let icon = willBeVisible ? "eye.fill" : "eye.slash.fill"
        ToastController.shared.show(
            message: msg,
            icon: icon,
            tint: .systemBlue,
            position: .center
        )
    }

    @objc private func toggleAudioRecording() {
        AudioRecorderController.shared.toggleRecording()
    }

    @objc private func pickColor() {
        MainActor.assumeIsolated { ColorPicker.pick() }
    }

    @objc private func recordScreen() {
        guard ensurePermissionOrPrompt() else { return }
        MainActor.assumeIsolated {
            RecordingController.shared.startRecordingFlow()
        }
    }

    @objc private func stopRecording() {
        MainActor.assumeIsolated {
            RecordingController.shared.stopRecording()
        }
    }

    @objc private func showAllInOne() {
        AllInOneController.shared.show { [weak self] action in
            guard let self = self else { return }
            switch action {
            case .area:       self.captureNow()
            case .window:     self.captureWindow()
            // The All-In-One panel is registered chrome, so its dismissal is
            // filtered out of the composite — no settle delay needed.
            case .fullscreen: self.captureFullscreen(settleDelay: 0)
            case .previous:   self.capturePreviousArea(settleDelay: 0)
            case .record:     self.recordScreen()
            case .timer:
                let item = NSMenuItem()
                item.tag = 3
                self.captureAfterTimer(item)
            }
        }
    }

    @objc private func openFromClipboard() {
        let pb = NSPasteboard.general
        if let image = NSImage(pasteboard: pb) {
            openAnnotator(with: image)
        } else {
            ToastController.shared.show(
                message: "剪贴板中没有图片",
                icon: "exclamationmark.triangle.fill",
                tint: .systemOrange
            )
        }
    }

    @objc private func openImageFile() {
        let panel = NSOpenPanel()
        if #available(macOS 12.0, *) {
            panel.allowedContentTypes = [.png, .jpeg, .tiff, .heic, .bmp, .gif]
        }
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        panel.canChooseFiles = true
        panel.prompt = "打开"
        NSApp.activate(ignoringOtherApps: true)
        panel.begin { [weak self] response in
            if response == .OK, let url = panel.url, let image = NSImage(contentsOf: url) {
                self?.openAnnotator(with: image)
            }
        }
    }

    // MARK: - Shared post-capture

    private func handleCapturedImage(_ image: NSImage) {
        SoundEffect.shared.playShutter()
        ImageSaver.copyToClipboard(image)
        HistoryStore.shared.add(image)
    }

    @discardableResult
    private func showPostCapturePopup(for image: NSImage) -> PostCapturePopupController {
        MainActor.assumeIsolated {
            popupController?.dismiss()
            let controller = PostCapturePopupController()
            popupController = controller
            controller.show(image: image, onEdit: { [weak self] in
                self?.openAnnotator(with: image)
            })
            return controller
        }
    }

    func openAnnotator(with image: NSImage) {
        // Kept in a list rather than a single slot: opening a second annotator
        // used to drop the first controller on the floor while its window was
        // still on screen, and the orphan's close callback would then clear the
        // reference to the live one.
        let controller = AnnotationWindowController()
        annotationControllers.append(controller)
        controller.open(image: image) { [weak self, weak controller] in
            self?.annotationControllers.removeAll { $0 === controller }
        }
    }

    // MARK: - Windows

    @objc private func showHistory() {
        if mainWindowController == nil {
            mainWindowController = MainWindowController()
        }
        mainWindowController?.show(tab: .history)
    }

    @objc private func showSettings() {
        if mainWindowController == nil {
            mainWindowController = MainWindowController()
        }
        mainWindowController?.show(tab: .settings)
    }

    @objc private func checkForUpdates() {
        MainActor.assumeIsolated {
            UpdaterController.shared.checkForUpdates()
        }
    }

    @objc private func showAbout() {
        NSApp.activate(ignoringOtherApps: true)

        let credits = NSMutableAttributedString()
        let baseAttrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 11),
            .foregroundColor: NSColor.labelColor,
            .paragraphStyle: {
                let p = NSMutableParagraphStyle()
                p.alignment = .center
                p.lineSpacing = 2
                return p
            }()
        ]

        credits.append(NSAttributedString(
            string: "现代 macOS 菜单栏截图与录屏工具。\n\n",
            attributes: baseAttrs
        ))
        credits.append(NSAttributedString(string: "Made by ", attributes: baseAttrs))
        credits.append(NSAttributedString(
            string: "@muzi86754781",
            attributes: baseAttrs.merging([
                .link: URL(string: "https://github.com/muzi86754781")!,
                .foregroundColor: NSColor.linkColor
            ]) { _, new in new }
        ))
        credits.append(NSAttributedString(string: "\n", attributes: baseAttrs))
        credits.append(NSAttributedString(
            string: "github.com/muzi86754781/shotx",
            attributes: baseAttrs.merging([
                .link: URL(string: "https://github.com/muzi86754781/shotx")!,
                .foregroundColor: NSColor.linkColor
            ]) { _, new in new }
        ))

        // applicationName auto-populates from CFBundleName.
        // The "Version X" line uses CFBundleShortVersionString automatically.
        // Pass an empty .applicationVersion (CFBundleVersion) so the panel
        // doesn't render "Version 1.6 (1.6)" when both are the same string.
        NSApp.orderFrontStandardAboutPanel(options: [
            .applicationVersion: "",
            .credits: credits
        ])
    }

    @objc private func quit() {
        NSApp.terminate(nil)
    }
}
