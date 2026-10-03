import AppKit

/// The picker: round icon bubbles on an arc above the pointer (below it near the top of the
/// screen). Every bubble is the same distance from the pointer, and each one owns the whole
/// direction it sits in, so a short flick toward a bubble is enough to land on it.
/// The view is a drag destination: the user drops the dragged file(s) onto a bubble.
final class BubbleArcView: NSView {
    var onDrop: ((PickerItem, [URL]) -> Void)?

    /// Arc above the pointer (`true`) or below it.
    var opensUpward = true {
        didSet { needsDisplay = true }
    }

    private var items: [PickerItem] = []
    private var urls: [URL] = []
    private var isTools = false
    private var hovered: Int? {
        didSet {
            guard hovered != oldValue else { return }
            needsDisplay = true
            if hovered != nil {
                NSHapticFeedbackManager.defaultPerformer.perform(.alignment, performanceTime: .now)
            }
        }
    }

    /// The pointer sits at the centre of the view.
    private var center: NSPoint { NSPoint(x: bounds.midX, y: bounds.midY) }

    // Geometry (points).
    private let baseRadius: CGFloat = 150
    private let maxSpan: CGFloat = 200            // degrees the arc may cover
    private let cancelRadius: CGFloat = 70        // drops closer to the pointer than this do nothing
    private let hoverScale: CGFloat = 1.14
    private var diameter: CGFloat { items.count > 7 ? 56 : 60 }

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        registerForDraggedTypes([.fileURL])
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    func configure(items: [PickerItem], urls: [URL], tools: Bool) {
        self.items = items
        self.urls = urls
        self.isTools = tools
        hovered = nil
        needsDisplay = true
    }

    // MARK: - Layout

    /// Radius of the arc and the angle between neighbouring bubbles (degrees).
    private var layout: (radius: CGFloat, step: CGFloat) {
        let count = items.count
        guard count > 1 else { return (baseRadius, 0) }
        let spacing = diameter + 8
        var radius = baseRadius
        // Tightest step that keeps bubbles apart, but spread a few bubbles out a little.
        let tightest = 2 * asin(spacing / (2 * radius)) * 180 / .pi
        var step = max(tightest, min(48, 150 / CGFloat(count - 1)))
        if step * CGFloat(count - 1) > maxSpan {
            step = maxSpan / CGFloat(count - 1)
            radius = spacing / (2 * sin(step / 2 * .pi / 180))
        }
        return (radius, step)
    }

    /// Direction of bubble `index` in degrees, ordered left to right.
    private func angle(of index: Int) -> CGFloat {
        let (_, step) = layout
        let span = step * CGFloat(items.count - 1)
        return opensUpward ? 90 + span / 2 - CGFloat(index) * step
                           : -90 - span / 2 + CGFloat(index) * step
    }

    private func position(of index: Int) -> NSPoint {
        let radians = angle(of: index) * .pi / 180
        let radius = layout.radius
        return NSPoint(x: center.x + cos(radians) * radius, y: center.y + sin(radians) * radius)
    }

    /// Whichever bubble lies in the pointer's direction, once it has moved far enough.
    private func itemIndex(at point: NSPoint) -> Int? {
        guard !items.isEmpty else { return nil }
        let dx = point.x - center.x
        let dy = point.y - center.y
        let distance = hypot(dx, dy)
        guard distance >= cancelRadius, distance <= layout.radius + diameter * 1.5 else { return nil }
        let direction = atan2(dy, dx) * 180 / .pi
        func difference(_ index: Int) -> CGFloat {
            var delta = (direction - angle(of: index)).truncatingRemainder(dividingBy: 360)
            if delta > 180 { delta -= 360 }
            if delta < -180 { delta += 360 }
            return abs(delta)
        }
        let nearest = items.indices.min { difference($0) < difference($1) }!
        let tolerance = max(layout.step / 2, 30)
        return difference(nearest) <= tolerance ? nearest : nil
    }

    // MARK: - Drawing

    override func draw(_ dirtyRect: NSRect) {
        for index in items.indices where index != hovered {
            drawBubble(index)
        }
        if let hovered {
            drawBubble(hovered)
        }
        drawCaption()
    }

    private func drawBubble(_ index: Int) {
        let item = items[index]
        let isHovered = hovered == index
        let size = diameter * (isHovered ? hoverScale : 1)
        let middle = position(of: index)
        let rect = NSRect(x: middle.x - size / 2, y: middle.y - size / 2, width: size, height: size)
        let circle = NSBezierPath(ovalIn: rect)

        NSGraphicsContext.current?.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(isHovered ? 0.28 : 0.18)
        shadow.shadowBlurRadius = isHovered ? 12 : 8
        shadow.shadowOffset = NSSize(width: 0, height: -3)
        shadow.set()
        (isHovered ? Palette.orange : Palette.bubble).setFill()
        circle.fill()
        NSGraphicsContext.current?.restoreGraphicsState()

        if !isHovered {
            Palette.bubbleBorder.setStroke()
            circle.lineWidth = 1
            circle.stroke()
        }

        let iconColor = isHovered ? NSColor.white : Palette.icon
        let textColor = isHovered ? NSColor.white : Palette.brown
        let scale = isHovered ? hoverScale : 1

        // Label: shrink the font for longer words so it always fits inside the bubble.
        var fontSize: CGFloat = 10.5 * scale
        var title = NSAttributedString(string: item.title, attributes: [
            .font: NSFont.systemFont(ofSize: fontSize, weight: .semibold), .foregroundColor: textColor,
        ])
        while title.size().width > size - 12, fontSize > 7.5 {
            fontSize -= 0.5
            title = NSAttributedString(string: item.title, attributes: [
                .font: NSFont.systemFont(ofSize: fontSize, weight: .semibold), .foregroundColor: textColor,
            ])
        }
        let titleSize = title.size()

        let symbolName = item.symbol ?? Catalog.formatSymbol(for: item.title)
        let symbol = (NSImage(systemSymbolName: symbolName, accessibilityDescription: item.title)
            ?? NSImage(systemSymbolName: "doc", accessibilityDescription: item.title))?
            .withSymbolConfiguration(NSImage.SymbolConfiguration(pointSize: 17 * scale, weight: .semibold)
                .applying(NSImage.SymbolConfiguration(paletteColors: [iconColor])))
        let symbolSize = symbol?.size ?? .zero
        let total = symbolSize.height + 2 + titleSize.height
        let top = middle.y + total / 2
        symbol?.draw(in: NSRect(x: middle.x - symbolSize.width / 2, y: top - symbolSize.height,
                                width: symbolSize.width, height: symbolSize.height))
        title.draw(at: NSPoint(x: middle.x - titleSize.width / 2, y: top - total))
    }

    /// A small pill between the pointer and the arc: the file name, or what the hovered bubble does.
    private func drawCaption() {
        let text: String
        let accent: String
        if let hovered, items.indices.contains(hovered) {
            text = items[hovered].detail ?? L("Save as %@", items[hovered].title)
            accent = ""
        } else if items.isEmpty {
            text = isTools ? L("No tools for this file type") : L("No formats for this file type")
            accent = ""
        } else if let first = urls.first {
            text = urls.count == 1 ? first.lastPathComponent : L("%@ files", String(urls.count))
            accent = isTools ? L("TOOLS") : L("CONVERT")
        } else {
            return
        }

        let paragraph = NSMutableParagraphStyle()
        paragraph.lineBreakMode = .byTruncatingMiddle
        let label = NSMutableAttributedString(string: text, attributes: [
            .font: NSFont.systemFont(ofSize: 11.5, weight: .medium),
            .foregroundColor: Palette.brown,
            .paragraphStyle: paragraph,
        ])
        if !accent.isEmpty {
            label.append(NSAttributedString(string: "  " + accent, attributes: [
                .font: NSFont.systemFont(ofSize: 9, weight: .heavy),
                .foregroundColor: Palette.orange,
                .kern: 0.8,
            ]))
        }
        let labelSize = label.size()
        // Narrow enough to fit between the two outermost bubbles.
        let width = min(labelSize.width + 24, 200)
        let height: CGFloat = 24
        let offset: CGFloat = items.isEmpty ? 0 : 50
        let y = center.y + (opensUpward ? offset : -offset) - height / 2
        let pill = NSRect(x: center.x - width / 2, y: y, width: width, height: height)

        NSGraphicsContext.current?.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.15)
        shadow.shadowBlurRadius = 6
        shadow.shadowOffset = NSSize(width: 0, height: -2)
        shadow.set()
        Palette.cream.setFill()
        NSBezierPath(roundedRect: pill, xRadius: height / 2, yRadius: height / 2).fill()
        NSGraphicsContext.current?.restoreGraphicsState()

        label.draw(with: NSRect(x: pill.minX + 12, y: pill.midY - labelSize.height / 2 + 1,
                                width: width - 24, height: labelSize.height),
                   options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine])
    }

    // MARK: - Drag destination

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        track(sender)
    }

    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation {
        track(sender)
    }

    override func draggingExited(_ sender: NSDraggingInfo?) {
        hovered = nil
    }

    override func prepareForDragOperation(_ sender: NSDraggingInfo) -> Bool {
        hovered != nil
    }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        guard let index = hovered, items.indices.contains(index) else { return false }
        let item = items[index]
        let dropped = (sender.draggingPasteboard.readObjects(
            forClasses: [NSURL.self],
            options: [.urlReadingFileURLsOnly: true]
        ) as? [URL]) ?? []
        let targets = dropped.isEmpty ? urls : dropped
        hovered = nil
        // Let the drag session finish before doing any work.
        DispatchQueue.main.async { [weak self] in
            self?.onDrop?(item, targets)
        }
        return true
    }

    private func track(_ sender: NSDraggingInfo) -> NSDragOperation {
        hovered = itemIndex(at: convert(sender.draggingLocation, from: nil))
        guard hovered != nil else { return [] }
        let mask = sender.draggingSourceOperationMask
        if mask.contains(.copy) { return .copy }
        if mask.contains(.generic) { return .generic }
        return mask.isEmpty ? [] : .copy
    }
}
