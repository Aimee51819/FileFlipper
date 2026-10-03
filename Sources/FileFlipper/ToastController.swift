import AppKit

/// A small HUD at the bottom of the screen that reports progress and results.
final class ToastController {
    private var panel: NSPanel?
    private let iconView = NSImageView()
    private let label = NSTextField(labelWithString: "")
    private var token = 0

    func show(_ text: String, symbol: String, duration: TimeInterval? = 2.5) {
        token += 1
        let panel = self.panel ?? makePanel()

        iconView.image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil)?
            .withSymbolConfiguration(NSImage.SymbolConfiguration(pointSize: 15, weight: .semibold))
        label.stringValue = text
        label.sizeToFit()

        let width = min(max(label.frame.width + 64, 180), 520)
        let height: CGFloat = 44
        let screen = NSScreen.screens.first { NSMouseInRect(NSEvent.mouseLocation, $0.frame, false) } ?? NSScreen.main
        let visible = screen?.visibleFrame ?? NSRect(x: 0, y: 0, width: 800, height: 600)
        panel.setFrame(NSRect(x: visible.midX - width / 2, y: visible.minY + 80, width: width, height: height),
                       display: true)
        layout(width: width, height: height)

        if !panel.isVisible {
            panel.alphaValue = 0
            panel.orderFrontRegardless()
        }
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.15
            panel.animator().alphaValue = 1
        }

        guard let duration else { return }
        let current = token
        DispatchQueue.main.asyncAfter(deadline: .now() + duration) { [weak self] in
            guard let self, self.token == current else { return }
            NSAnimationContext.runAnimationGroup({ context in
                context.duration = 0.25
                panel.animator().alphaValue = 0
            }, completionHandler: {
                if self.token == current { panel.orderOut(nil) }
            })
        }
    }

    private func layout(width: CGFloat, height: CGFloat) {
        iconView.frame = NSRect(x: 16, y: (height - 20) / 2, width: 20, height: 20)
        let labelHeight = label.frame.height
        label.frame = NSRect(x: 44, y: (height - labelHeight) / 2, width: width - 60, height: labelHeight)
    }

    private func makePanel() -> NSPanel {
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 240, height: 44),
                            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.level = .statusBar
        panel.hidesOnDeactivate = false
        panel.ignoresMouseEvents = true
        panel.isReleasedWhenClosed = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]

        let background = NSVisualEffectView()
        background.material = .hudWindow
        background.blendingMode = .behindWindow
        background.state = .active
        background.wantsLayer = true
        background.layer?.cornerRadius = 12
        background.layer?.masksToBounds = true

        iconView.contentTintColor = NSColor(calibratedRed: 0.98, green: 0.45, blue: 0.15, alpha: 1)
        label.font = NSFont.systemFont(ofSize: 13, weight: .medium)
        label.textColor = .labelColor
        label.lineBreakMode = .byTruncatingMiddle
        background.addSubview(iconView)
        background.addSubview(label)
        panel.contentView = background

        self.panel = panel
        return panel
    }
}
