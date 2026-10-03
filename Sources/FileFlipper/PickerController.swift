import AppKit

/// Owns the transparent floating panel that hosts the bubble arc.
final class PickerController {
    static let size: CGFloat = 420
    /// How much room the arc needs above (or below) the pointer.
    private static let arcReach: CGFloat = 200

    var onPick: ((PickerItem, [URL]) -> Void)?

    private var panel: NSPanel?
    private var arcView: BubbleArcView?
    private var urls: [URL] = []
    private var hideToken = 0

    func show(at point: NSPoint, urls: [URL], tools: Bool) {
        hideToken += 1
        self.urls = urls

        let panel = self.panel ?? makePanel()
        let size = Self.size
        var frame = NSRect(x: point.x - size / 2, y: point.y - size / 2, width: size, height: size)
        var upward = true
        if let screen = NSScreen.screens.first(where: { NSMouseInRect(point, $0.frame, false) }) ?? NSScreen.main {
            let visible = screen.visibleFrame
            // Near the top of the screen, open the arc downward instead.
            upward = point.y + Self.arcReach <= visible.maxY
            // Near the sides, slide the panel back on screen. Only the half with the arc matters,
            // so the empty half may hang off the top or bottom edge.
            let bounds = visible.insetBy(dx: -12, dy: -12)
            frame.origin.x = min(max(frame.minX, bounds.minX), bounds.maxX - size)
            if upward {
                frame.origin.y = min(frame.minY, bounds.maxY - size)
            } else {
                frame.origin.y = max(frame.minY, bounds.minY)
            }
        }
        panel.setFrame(frame, display: false)
        arcView?.opensUpward = upward
        setToolsMode(tools)

        panel.alphaValue = 0
        panel.orderFrontRegardless()
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.12
            panel.animator().alphaValue = 1
        }
    }

    func setToolsMode(_ tools: Bool) {
        arcView?.configure(items: Catalog.items(for: urls, tools: tools), urls: urls, tools: tools)
    }

    /// Hides after a short delay so a drop that lands at the same moment
    /// the mouse is released still gets delivered to the picker.
    func hide(afterDelay delay: TimeInterval = 0) {
        let token = hideToken
        DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
            guard let self, token == self.hideToken, let panel = self.panel else { return }
            NSAnimationContext.runAnimationGroup({ context in
                context.duration = 0.12
                panel.animator().alphaValue = 0
            }, completionHandler: {
                guard token == self.hideToken else { return }
                panel.orderOut(nil)
            })
        }
    }

    private func makePanel() -> NSPanel {
        let size = Self.size
        let panel = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: size, height: size),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.level = .popUpMenu
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]

        let view = BubbleArcView(frame: NSRect(x: 0, y: 0, width: size, height: size))
        view.onDrop = { [weak self] item, urls in
            guard let self else { return }
            self.hideToken += 1
            self.hide()
            self.onPick?(item, urls)
        }
        panel.contentView = view

        self.panel = panel
        self.arcView = view
        return panel
    }
}
