import Cocoa

final class OverlayWindow: NSWindow {
    private let selectionView: SelectionView
    private let targetScreen: NSScreen

    init(
        screen: NSScreen,
        onSelect: @escaping (CGRect, NSScreen) -> Void,
        onCancel: @escaping () -> Void
    ) {
        self.targetScreen = screen
        self.selectionView = SelectionView(frame: NSRect(origin: .zero, size: screen.frame.size))

        super.init(
            contentRect: screen.frame,
            styleMask: [.borderless],
            backing: .buffered,
            defer: false
        )
        setFrame(screen.frame, display: false)

        // Paired with the explicit close() in OverlayController: programmatic
        // NSWindows default to releasing themselves on close, which is an
        // over-release under ARC.
        isReleasedWhenClosed = false
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        // Use floating level so system permission dialogs and alerts remain in front
        level = .floating
        ignoresMouseEvents = false
        isMovable = false
        acceptsMouseMovedEvents = true
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]

        selectionView.onSelect = { [weak self] rectInView in
            guard let self = self else { return }
            // Convert view-local AppKit coords → global AppKit coords (origin at primary screen bottom-left).
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
        // If a system modal alert or another application steals focus,
        // ensure cursor is restored so the user is never stuck with a hidden cursor.
        NSCursor.unhide()
    }
}
