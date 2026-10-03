import AppKit

/// Watches for file drags happening anywhere on the system (typically from Finder)
/// and reports when the user holds Shift (formats) or Option+Shift (tools) mid-drag.
///
/// It polls global state instead of installing event taps, so it needs no
/// Accessibility or Input Monitoring permission:
///  - `NSEvent.pressedMouseButtons` tells us whether the mouse is held down
///  - the system drag pasteboard's `changeCount` changes when a new drag starts
///  - `NSEvent.modifierFlags` gives the currently held modifier keys
final class DragMonitor {
    var isEnabled = true

    /// Called when the picker should appear: cursor location (screen coords), dragged files, tools mode.
    var onShow: ((NSPoint, [URL], Bool) -> Void)?
    /// Called when the user switches between Shift and Option+Shift while the picker is visible.
    var onModeChange: ((Bool) -> Void)?
    /// Called when the mouse button is released after the picker was shown.
    var onEnd: (() -> Void)?

    private let dragPasteboard = NSPasteboard(name: .drag)
    private var timer: Timer?
    private var mouseWasDown = false
    private var changeCountAtMouseDown = 0
    private var pickerShown = false
    private var toolsMode = false

    func start() {
        changeCountAtMouseDown = dragPasteboard.changeCount
        let timer = Timer(timeInterval: 1.0 / 30.0, repeats: true) { [weak self] _ in
            self?.tick()
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    func stop() {
        timer?.invalidate()
        timer = nil
    }

    private func tick() {
        let mouseDown = NSEvent.pressedMouseButtons & 1 != 0

        // Keep the baseline up to date while the button is up, so any change seen while it's
        // held means a drag started during this press (even if we polled a bit late).
        if !mouseDown {
            changeCountAtMouseDown = dragPasteboard.changeCount
        }
        if !mouseDown && mouseWasDown && pickerShown {
            pickerShown = false
            onEnd?()
        }
        mouseWasDown = mouseDown

        guard mouseDown, isEnabled else { return }

        let flags = NSEvent.modifierFlags.intersection(.deviceIndependentFlagsMask)
        let shift = flags.contains(.shift)
        let option = flags.contains(.option)

        if !pickerShown {
            // A new drag has started since the mouse went down, and Shift is held.
            guard shift, dragPasteboard.changeCount != changeCountAtMouseDown else { return }
            let urls = draggedFileURLs()
            guard !urls.isEmpty else { return }
            pickerShown = true
            toolsMode = option
            onShow?(NSEvent.mouseLocation, urls, option)
        } else if shift && option != toolsMode {
            toolsMode = option
            onModeChange?(option)
        }
    }

    private func draggedFileURLs() -> [URL] {
        let objects = dragPasteboard.readObjects(
            forClasses: [NSURL.self],
            options: [.urlReadingFileURLsOnly: true]
        )
        return (objects as? [URL]) ?? []
    }
}
