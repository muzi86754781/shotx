import Cocoa

final class OverlayController {
    private var windows: [OverlayWindow] = []
    private var completion: ((NSImage?, CGRect?, NSScreen?) -> Void)?
    private var onFinished: (() -> Void)?
    private var onOCRResult: ((NSImage?) -> Void)?
    private var finished = false
    private var cursorHidden = false

    func begin(
        isOCR: Bool = false,
        onOCRResult: ((NSImage?) -> Void)? = nil,
        completion: (() -> Void)? = nil
    ) {
        self.onFinished = completion
        self.onOCRResult = onOCRResult
        startOverlay(isOCR: isOCR)
    }

    func begin(legacyCompletion: @escaping (NSImage?, CGRect?, NSScreen?) -> Void) {
        self.completion = legacyCompletion
        startOverlay(isOCR: false)
    }

    private func startOverlay(isOCR: Bool) {
        NSCursor.hide()
        cursorHidden = true

        // 1. Capture snapshot of each screen before displaying overlay windows
        var snapshots: [NSScreen: CGImage] = [:]
        for screen in NSScreen.screens {
            if let img = ScreenCapture.capture(rectInScreenCoords: screen.frame, screen: screen) {
                if let cg = img.cgImage(forProposedRect: nil, context: nil, hints: nil) {
                    snapshots[screen] = cg
                }
            }
        }

        // 2. Open OverlayWindow on each display
        for screen in NSScreen.screens {
            let window = OverlayWindow(
                screen: screen,
                snapshot: snapshots[screen],
                isOCR: isOCR,
                onOCRResult: { [weak self] img in
                    self?.onOCRResult?(img)
                },
                onActive: { [weak self] activeWindow in
                    self?.dismissOtherWindows(except: activeWindow)
                },
                onFinish: { [weak self] in
                    self?.finish()
                },
                onCancel: { [weak self] in
                    self?.finish()
                }
            )
            windows.append(window)
            window.orderFrontRegardless()
            CaptureExclusions.register(window)
        }

        NSApp.activate(ignoringOtherApps: true)
        let mouseLoc = NSEvent.mouseLocation
        let keyWindow = windows.first(where: { $0.frame.contains(mouseLoc) }) ?? windows.first
        keyWindow?.makeKey()
    }

    private func dismissOtherWindows(except activeWindow: OverlayWindow) {
        for w in windows where w !== activeWindow {
            CaptureExclusions.unregister(w)
            w.orderOut(nil)
            w.contentView = nil
            w.close()
        }
        windows = [activeWindow]
    }

    private func finish() {
        guard !finished else { return }
        finished = true
        closeWindows()
        onFinished?()
        completion?(nil, nil, nil)
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
