import AppKit

/// A small window for the image "Crop" tool: shows the picture, lets the user drag out
/// an area (optionally locked to an aspect ratio) and returns that area in image pixels.
enum CropWindow {
    private static let aspects: [(title: String, ratio: CGFloat?)] = [
        ("Free", nil), ("1:1", 1), ("4:3", 4.0 / 3), ("3:2", 3.0 / 2), ("16:9", 16.0 / 9), ("9:16", 9.0 / 16),
    ]

    /// Runs modally. Returns the crop rectangle (top-left origin, pixels) or `nil` if cancelled.
    static func run(image: CGImage, fileName: String) -> CGRect? {
        // Layout: a rounded dark canvas with the picture, and a control row underneath,
        // with the same margin all around so nothing sits against the window edge.
        let margin: CGFloat = 20
        let rowHeight: CGFloat = 32
        let rowGap: CGFloat = 16
        let canvasPadding: CGFloat = 18

        let maxPicture = NSSize(width: 760, height: 520)
        let scale = min(maxPicture.width / CGFloat(image.width), maxPicture.height / CGFloat(image.height), 1)
        let canvasSize = NSSize(width: max(CGFloat(image.width) * scale + canvasPadding * 2, 700),
                                height: max(CGFloat(image.height) * scale + canvasPadding * 2, 240))
        let contentSize = NSSize(width: canvasSize.width + margin * 2,
                                 height: canvasSize.height + rowHeight + rowGap + margin * 2)

        let content = NSView(frame: NSRect(origin: .zero, size: contentSize))
        let cropView = CropView(image: image,
                                frame: NSRect(x: margin, y: margin + rowHeight + rowGap,
                                              width: canvasSize.width, height: canvasSize.height),
                                padding: canvasPadding)
        content.addSubview(cropView)

        let handler = Handler(cropView: cropView)

        // Aspect ratio choices, left-aligned.
        var x = margin
        for (index, aspect) in aspects.enumerated() {
            let button = PillButton(title: aspect.title, style: .choice, height: 28)
            button.target = handler
            button.action = #selector(Handler.aspectChanged(_:))
            button.tag = index
            button.isOn = index == 0
            button.setFrameOrigin(NSPoint(x: x, y: margin + (rowHeight - 28) / 2))
            content.addSubview(button)
            handler.aspectButtons.append((button, aspect.ratio))
            x = button.frame.maxX + 6
        }

        let sizeLabel = NSTextField(labelWithString: "")
        sizeLabel.textColor = Palette.brown.withAlphaComponent(0.6)
        sizeLabel.font = .monospacedDigitSystemFont(ofSize: 12, weight: .medium)
        sizeLabel.frame = NSRect(x: x + 10, y: margin + (rowHeight - 16) / 2, width: 130, height: 16)
        content.addSubview(sizeLabel)
        handler.sizeLabel = sizeLabel

        // Actions, right-aligned.
        let save = PillButton(title: "Crop", symbol: "crop", style: .primary, height: rowHeight)
        save.target = handler
        save.action = #selector(Handler.crop(_:))
        save.keyEquivalent = "\r"
        save.setFrameOrigin(NSPoint(x: contentSize.width - margin - save.frame.width, y: margin))
        content.addSubview(save)

        let cancel = PillButton(title: "Cancel", style: .secondary, height: rowHeight)
        cancel.target = handler
        cancel.action = #selector(Handler.cancel(_:))
        cancel.keyEquivalent = "\u{1b}"
        cancel.setFrameOrigin(NSPoint(x: save.frame.minX - 10 - cancel.frame.width, y: margin))
        content.addSubview(cancel)

        let window = NSWindow(contentRect: content.frame, styleMask: [.titled, .closable],
                              backing: .buffered, defer: false)
        window.title = "Crop “\(fileName)”"
        window.titlebarAppearsTransparent = true
        window.backgroundColor = Palette.cream
        window.appearance = NSAppearance(named: .aqua)
        window.contentView = content
        window.isReleasedWhenClosed = false
        window.level = .floating
        window.delegate = handler
        window.center()

        cropView.onChange = { [weak handler] in handler?.updateSizeLabel() }
        handler.updateSizeLabel()

        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
        let response = withExtendedLifetime(handler) { NSApp.runModal(for: window) }
        window.orderOut(nil)
        return response == .OK ? cropView.cropRectInPixels : nil
    }

    private final class Handler: NSObject, NSWindowDelegate {
        let cropView: CropView
        var aspectButtons: [(button: PillButton, ratio: CGFloat?)] = []
        var sizeLabel: NSTextField?

        init(cropView: CropView) {
            self.cropView = cropView
        }

        @objc func aspectChanged(_ sender: PillButton) {
            for entry in aspectButtons {
                entry.button.isOn = entry.button === sender
            }
            cropView.aspect = aspectButtons[sender.tag].ratio
        }

        @objc func crop(_ sender: Any?) {
            NSApp.stopModal(withCode: .OK)
        }

        @objc func cancel(_ sender: Any?) {
            NSApp.stopModal(withCode: .cancel)
        }

        func windowShouldClose(_ sender: NSWindow) -> Bool {
            NSApp.stopModal(withCode: .cancel)
            return true
        }

        func updateSizeLabel() {
            let rect = cropView.cropRectInPixels
            sizeLabel?.stringValue = "\(Int(rect.width)) × \(Int(rect.height)) px"
        }
    }
}

/// A capsule button in FileFlipper's colours: orange for the main action, translucent peach otherwise.
private final class PillButton: NSButton {
    enum Style { case primary, secondary, choice }

    var isOn = false {
        didSet { needsDisplay = true }
    }

    private let style: Style
    private let symbolName: String?
    private var isHovering = false {
        didSet { needsDisplay = true }
    }

    init(title: String, symbol: String? = nil, style: Style, height: CGFloat) {
        self.style = style
        self.symbolName = symbol
        super.init(frame: .zero)
        self.title = title
        isBordered = false
        setButtonType(.momentaryChange)
        let textWidth = (title as NSString).size(withAttributes: [.font: font(for: style)]).width
        let iconWidth: CGFloat = symbol == nil ? 0 : 20
        let sidePadding: CGFloat = style == .choice ? 12 : 18
        setFrameSize(NSSize(width: ceil(textWidth + iconWidth + sidePadding * 2), height: height))
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect],
                                       owner: self, userInfo: nil))
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override func mouseEntered(with event: NSEvent) { isHovering = true }
    override func mouseExited(with event: NSEvent) { isHovering = false }

    private func font(for style: Style) -> NSFont {
        .systemFont(ofSize: style == .choice ? 12 : 13, weight: style == .primary ? .bold : .semibold)
    }

    private var colors: (fill: NSColor, text: NSColor) {
        let pressed = isHighlighted
        switch style {
        case .primary:
            return (Palette.orange.withAlphaComponent(pressed ? 0.75 : (isHovering ? 0.9 : 1)), .white)
        case .secondary:
            return (Palette.peach.withAlphaComponent(pressed ? 0.95 : (isHovering ? 0.8 : 0.6)), Palette.brown)
        case .choice where isOn:
            return (Palette.orange.withAlphaComponent(0.88), .white)
        case .choice:
            return (Palette.peach.withAlphaComponent(pressed ? 0.85 : (isHovering ? 0.7 : 0.45)),
                    Palette.brown.withAlphaComponent(0.85))
        }
    }

    override func draw(_ dirtyRect: NSRect) {
        let (fill, text) = colors
        let radius = bounds.height / 2
        fill.setFill()
        NSBezierPath(roundedRect: bounds, xRadius: radius, yRadius: radius).fill()

        let label = NSAttributedString(string: title, attributes: [.font: font(for: style), .foregroundColor: text])
        let labelSize = label.size()
        var symbol: NSImage?
        if let symbolName {
            symbol = NSImage(systemSymbolName: symbolName, accessibilityDescription: nil)?
                .withSymbolConfiguration(NSImage.SymbolConfiguration(pointSize: 12, weight: .bold)
                    .applying(NSImage.SymbolConfiguration(paletteColors: [text])))
        }
        let iconWidth: CGFloat = symbol == nil ? 0 : 20
        var x = (bounds.width - labelSize.width - iconWidth) / 2
        if let symbol {
            let size = symbol.size
            symbol.draw(in: NSRect(x: x, y: (bounds.height - size.height) / 2, width: size.width, height: size.height))
            x += iconWidth
        }
        label.draw(at: NSPoint(x: x, y: (bounds.height - labelSize.height) / 2))
    }
}

/// Draws the image with a draggable selection: drag inside to move it, drag a corner to
/// resize it, drag anywhere else on the picture to start a new selection.
private final class CropView: NSView {
    var aspect: CGFloat? {
        didSet { fitSelectionToAspect() }
    }
    var onChange: (() -> Void)?

    private let image: CGImage
    private let padding: CGFloat
    private var selection: NSRect = .zero

    private enum Drag {
        case none
        case move(start: NSPoint, original: NSRect)
        case resize(anchor: NSPoint)
    }
    private var drag: Drag = .none
    private let handleSize: CGFloat = 10

    init(image: CGImage, frame: NSRect, padding: CGFloat) {
        self.image = image
        self.padding = padding
        super.init(frame: frame)
        selection = imageRect
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    /// Where the picture is drawn inside the view (aspect-fit).
    private var imageRect: NSRect {
        let area = bounds.insetBy(dx: padding, dy: padding)
        let scale = min(area.width / CGFloat(image.width), area.height / CGFloat(image.height))
        let width = CGFloat(image.width) * scale
        let height = CGFloat(image.height) * scale
        return NSRect(x: area.midX - width / 2, y: area.midY - height / 2, width: width, height: height)
    }

    /// The selection in image pixels, with a top-left origin as `CGImage.cropping(to:)` expects.
    var cropRectInPixels: CGRect {
        let frame = imageRect
        let scale = CGFloat(image.width) / frame.width
        let rect = CGRect(x: ((selection.minX - frame.minX) * scale).rounded(),
                          y: ((frame.maxY - selection.maxY) * scale).rounded(),
                          width: (selection.width * scale).rounded(),
                          height: (selection.height * scale).rounded())
        return rect.intersection(CGRect(x: 0, y: 0, width: image.width, height: image.height))
    }

    // MARK: Drawing

    override func draw(_ dirtyRect: NSRect) {
        Palette.canvas.setFill()
        NSBezierPath(roundedRect: bounds, xRadius: 14, yRadius: 14).fill()

        let frame = imageRect
        NSGraphicsContext.current?.cgContext.draw(image, in: frame)

        // Dim everything outside the selection.
        let shade = NSBezierPath(rect: frame)
        shade.append(NSBezierPath(rect: selection))
        shade.windingRule = .evenOdd
        Palette.canvas.withAlphaComponent(0.7).setFill()
        shade.fill()

        // Rule-of-thirds guides.
        let guides = NSBezierPath()
        for step in 1...2 {
            let x = selection.minX + selection.width * CGFloat(step) / 3
            let y = selection.minY + selection.height * CGFloat(step) / 3
            guides.move(to: NSPoint(x: x, y: selection.minY))
            guides.line(to: NSPoint(x: x, y: selection.maxY))
            guides.move(to: NSPoint(x: selection.minX, y: y))
            guides.line(to: NSPoint(x: selection.maxX, y: y))
        }
        NSColor.white.withAlphaComponent(0.35).setStroke()
        guides.lineWidth = 1
        guides.stroke()

        let border = NSBezierPath(rect: selection)
        NSColor.white.withAlphaComponent(0.85).setStroke()
        border.lineWidth = 1
        border.stroke()

        // L-shaped corner grips, drawn just inside the selection.
        let arm = min(20, selection.width / 3, selection.height / 3)
        let grips = NSBezierPath()
        grips.lineWidth = 4
        grips.lineCapStyle = .round
        grips.lineJoinStyle = .round
        for corner in corners {
            let dx: CGFloat = corner.x == selection.minX ? 1 : -1
            let dy: CGFloat = corner.y == selection.minY ? 1 : -1
            let tip = NSPoint(x: corner.x + dx * 1.5, y: corner.y + dy * 1.5)
            grips.move(to: NSPoint(x: tip.x + dx * arm, y: tip.y))
            grips.line(to: tip)
            grips.line(to: NSPoint(x: tip.x, y: tip.y + dy * arm))
        }
        NSGraphicsContext.current?.saveGraphicsState()
        let glow = NSShadow()
        glow.shadowColor = NSColor.black.withAlphaComponent(0.35)
        glow.shadowBlurRadius = 3
        glow.set()
        NSColor.white.setStroke()
        grips.stroke()
        NSGraphicsContext.current?.restoreGraphicsState()
    }

    private var corners: [NSPoint] {
        [NSPoint(x: selection.minX, y: selection.minY), NSPoint(x: selection.maxX, y: selection.minY),
         NSPoint(x: selection.minX, y: selection.maxY), NSPoint(x: selection.maxX, y: selection.maxY)]
    }

    // MARK: Mouse

    override func mouseDown(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        if let corner = corners.first(where: { hypot($0.x - point.x, $0.y - point.y) <= handleSize * 1.5 }) {
            // Resize from the opposite corner.
            let anchor = NSPoint(x: corner.x == selection.minX ? selection.maxX : selection.minX,
                                 y: corner.y == selection.minY ? selection.maxY : selection.minY)
            drag = .resize(anchor: anchor)
        } else if selection.contains(point) {
            drag = .move(start: point, original: selection)
        } else if imageRect.contains(point) {
            drag = .resize(anchor: point)
        } else {
            drag = .none
        }
    }

    override func mouseDragged(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        let frame = imageRect
        switch drag {
        case .none:
            return
        case .move(let start, let original):
            var moved = original.offsetBy(dx: point.x - start.x, dy: point.y - start.y)
            moved.origin.x = min(max(moved.minX, frame.minX), frame.maxX - moved.width)
            moved.origin.y = min(max(moved.minY, frame.minY), frame.maxY - moved.height)
            selection = moved
        case .resize(let anchor):
            let clamped = NSPoint(x: min(max(point.x, frame.minX), frame.maxX),
                                  y: min(max(point.y, frame.minY), frame.maxY))
            selection = rect(from: anchor, to: clamped)
        }
        needsDisplay = true
        onChange?()
    }

    override func mouseUp(with event: NSEvent) {
        // A click without a real drag would leave a tiny selection; fall back to the whole picture.
        if selection.width < 8 || selection.height < 8 {
            selection = imageRect
            fitSelectionToAspect()
        }
        drag = .none
        needsDisplay = true
        onChange?()
    }

    override func resetCursorRects() {
        addCursorRect(imageRect, cursor: .crosshair)
    }

    // MARK: Geometry

    private func rect(from anchor: NSPoint, to point: NSPoint) -> NSRect {
        let dx = point.x - anchor.x
        let dy = point.y - anchor.y
        var width = abs(dx)
        var height = abs(dy)
        if let aspect {
            // Shrink whichever side is too long; the point is already inside the picture, so this still fits.
            if height > 0, width / height > aspect {
                width = height * aspect
            } else {
                height = width / aspect
            }
        }
        return NSRect(x: dx >= 0 ? anchor.x : anchor.x - width,
                      y: dy >= 0 ? anchor.y : anchor.y - height,
                      width: width, height: height)
    }

    /// The largest selection with the chosen aspect ratio, centered on the picture.
    private func fitSelectionToAspect() {
        let frame = imageRect
        guard let aspect else {
            selection = frame
            needsDisplay = true
            onChange?()
            return
        }
        var width = frame.width
        var height = width / aspect
        if height > frame.height {
            height = frame.height
            width = height * aspect
        }
        selection = NSRect(x: frame.midX - width / 2, y: frame.midY - height / 2, width: width, height: height)
        needsDisplay = true
        onChange?()
    }
}
