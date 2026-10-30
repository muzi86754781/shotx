import Cocoa

final class OverlayWindow: NSWindow {
    let selectionView: SelectionView
    let targetScreen: NSScreen

    init(
        screen: NSScreen,
        snapshot: CGImage?,
        onActive: @escaping (OverlayWindow) -> Void,
        onFinish: @escaping () -> Void,
        onCancel: @escaping () -> Void
    ) {
        self.targetScreen = screen
        self.selectionView = SelectionView(
            frame: NSRect(origin: .zero, size: screen.frame.size),
            screen: screen,
            snapshot: snapshot
        )

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

        selectionView.onActive = { [weak self] in
            guard let self = self else { return }
            onActive(self)
        }
        selectionView.onFinish = onFinish
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
