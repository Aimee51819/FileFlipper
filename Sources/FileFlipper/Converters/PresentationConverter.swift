import AppKit

/// PowerPoint (.pptx) → PDF or Markdown.
///
/// macOS has no public API that renders PowerPoint files, so this reads the slide XML itself and
/// draws what it finds: text boxes and placeholders (with the master's default sizes, colours,
/// alignment and bullets), pictures, filled shapes, tables and backgrounds, including the
/// decorations on the slide layout and master. Charts, SmartArt and animations are not drawn.
enum PresentationConverter {
    // MARK: Model

    struct Run {
        var text: String
        var size: CGFloat
        var bold = false
        var italic = false
        var underline = false
        var color: NSColor
        var typeface: String?
    }

    struct Paragraph {
        var runs: [Run] = []
        var alignment: NSTextAlignment = .left
        var level = 0
        var bullet: String?
        var emptySize: CGFloat = 18
    }

    struct TextBox {
        var paragraphs: [Paragraph]
        var anchor = "t"
        var insets = NSEdgeInsets(top: 3.6, left: 7.2, bottom: 3.6, right: 7.2)
        var isTitle = false
        var plainText: String { paragraphs.map { $0.runs.map(\.text).joined() }.joined(separator: "\n") }
    }

    struct TableCell { var text: TextBox; var fill: NSColor? }

    enum Item {
        case shape(rect: CGRect, rotation: CGFloat, geometry: String, fill: NSColor?, line: NSColor?, lineWidth: CGFloat, text: TextBox?)
        case picture(rect: CGRect, rotation: CGFloat, image: NSImage)
        case table(rect: CGRect, columns: [CGFloat], rows: [(height: CGFloat, cells: [TableCell])])
    }

    struct Slide {
        var background: NSColor = .white
        var backgroundImage: NSImage?
        var items: [Item] = []
    }

    struct Deck {
        var size: CGSize
        var slides: [Slide]
    }

    // MARK: Convert

    static func toPDF(_ url: URL) throws -> URL {
        let deck = try read(url)
        let output = OutputNaming.next(to: url, ext: "pdf")
        var mediaBox = CGRect(origin: .zero, size: deck.size)
        guard let context = CGContext(output as CFURL, mediaBox: &mediaBox, nil) else { throw ConversionError.writeFailed(output) }
        for slide in deck.slides {
            context.beginPage(mediaBox: &mediaBox)
            context.translateBy(x: 0, y: deck.size.height)
            context.scaleBy(x: 1, y: -1)
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: true)
            draw(slide, size: deck.size, in: context)
            NSGraphicsContext.restoreGraphicsState()
            context.endPage()
        }
        context.closePDF()
        return output
    }

    static func toMarkdown(_ url: URL) throws -> URL {
        let deck = try read(url)
        var parts: [String] = []
        for (index, slide) in deck.slides.enumerated() {
            var title: String?
            var lines: [String] = []
            // Reading order: top to bottom, then left to right.
            let ordered = slide.items.sorted { a, b in
                let ra = rect(of: a), rb = rect(of: b)
                return abs(ra.minY - rb.minY) > 8 ? ra.minY < rb.minY : ra.minX < rb.minX
            }
            for item in ordered {
                switch item {
                case .shape(_, _, _, _, _, _, let text?):
                    if text.isTitle, title == nil {
                        title = text.plainText.replacingOccurrences(of: "\n", with: " ")
                        continue
                    }
                    var block: [String] = []
                    for paragraph in text.paragraphs {
                        let content = paragraph.runs.map(\.text).joined().trimmingCharacters(in: .whitespaces)
                        guard !content.isEmpty else { continue }
                        if paragraph.bullet != nil {
                            block.append(String(repeating: "  ", count: paragraph.level) + "- " + MarkdownWriter.escape(content))
                        } else {
                            block.append(MarkdownWriter.escape(content))
                        }
                    }
                    if !block.isEmpty { lines.append(block.joined(separator: "\n")) }
                case .table(_, _, let rows):
                    let grid = rows.map { $0.cells.map { MarkdownWriter.escape($0.text.plainText)
                        .replacingOccurrences(of: "\n", with: " ").replacingOccurrences(of: "|", with: "\\|") } }
                    if !grid.isEmpty { lines.append(MarkdownWriter.table(grid)) }
                default:
                    break
                }
            }
            let heading = "## Slide \(index + 1)" + (title.map { ": " + MarkdownWriter.escape($0) } ?? "")
            parts.append(([heading] + lines).joined(separator: "\n\n"))
        }
        let output = OutputNaming.next(to: url, ext: "md")
        try (parts.joined(separator: "\n\n") + "\n").write(to: output, atomically: true, encoding: .utf8)
        return output
    }

    private static func rect(of item: Item) -> CGRect {
        switch item {
        case .shape(let rect, _, _, _, _, _, _), .picture(let rect, _, _), .table(let rect, _, _): return rect
        }
    }

    // MARK: Reading

    private static let emu: CGFloat = 12_700   // EMU per point

    /// Per-part context: where relationships resolve and which theme/master defaults apply.
    private struct Part {
        let path: String
        let relationships: [String: (target: String, type: String)]
    }

    private struct Defaults {
        var theme: [String: NSColor] = [:]
        var titleSize: CGFloat = 44
        var bodySizes: [CGFloat] = [28, 24, 20, 20, 20, 20, 20, 20, 20]
        var otherSize: CGFloat = 18
        var titleAlignment: NSTextAlignment = .left
    }

    static func read(_ url: URL) throws -> Deck {
        let archive: ZipArchive
        do { archive = try ZipArchive(url: url) } catch { throw ConversionError.unreadable(url) }
        guard let presentation = OfficePackage.xml(archive, "ppt/presentation.xml") else { throw ConversionError.unreadable(url) }
        let presentationRels = OfficePackage.relationships(archive, for: "ppt/presentation.xml")
        var size = CGSize(width: 960, height: 540)
        if let sldSz = presentation.child("sldSz"),
           let cx = sldSz.attr("cx").flatMap(Double.init), let cy = sldSz.attr("cy").flatMap(Double.init) {
            size = CGSize(width: CGFloat(cx) / emu, height: CGFloat(cy) / emu)
        }

        var slides: [Slide] = []
        for reference in presentation.child("sldIdLst")?.children("sldId") ?? [] {
            guard let id = reference.relationshipID, let path = presentationRels[id]?.target,
                  let root = OfficePackage.xml(archive, path) else { continue }
            if root.attr("show") == "0" { continue }
            slides.append(readSlide(root, path: path, archive: archive))
        }
        guard !slides.isEmpty else { throw ConversionError.message(L("%@ has no slides", url.lastPathComponent)) }
        return Deck(size: size, slides: slides)
    }

    private static func readSlide(_ root: XMLElement, path: String, archive: ZipArchive) -> Slide {
        let slidePart = Part(path: path, relationships: OfficePackage.relationships(archive, for: path))
        let layoutPath = slidePart.relationships.values.first { $0.type.hasSuffix("/slideLayout") }?.target
        let layoutRoot = layoutPath.flatMap { OfficePackage.xml(archive, $0) }
        let layoutPart = layoutPath.map { Part(path: $0, relationships: OfficePackage.relationships(archive, for: $0)) }
        let masterPath = layoutPart?.relationships.values.first { $0.type.hasSuffix("/slideMaster") }?.target
        let masterRoot = masterPath.flatMap { OfficePackage.xml(archive, $0) }
        let masterPart = masterPath.map { Part(path: $0, relationships: OfficePackage.relationships(archive, for: $0)) }

        var defaults = Defaults()
        if let themePath = masterPart?.relationships.values.first(where: { $0.type.hasSuffix("/theme") })?.target,
           let theme = OfficePackage.xml(archive, themePath) {
            defaults.theme = themeColors(theme)
        }
        if let styles = masterRoot?.child("txStyles") {
            if let title = styles.child("titleStyle")?.child("lvl1pPr") {
                if let size = title.child("defRPr")?.attr("sz").flatMap(Double.init) { defaults.titleSize = CGFloat(size) / 100 }
                defaults.titleAlignment = alignment(title.attr("algn")) ?? .left
            }
            if let body = styles.child("bodyStyle") {
                for level in 1...9 {
                    if let size = body.child("lvl\(level)pPr")?.child("defRPr")?.attr("sz").flatMap(Double.init) {
                        defaults.bodySizes[level - 1] = CGFloat(size) / 100
                    }
                }
            }
            if let size = styles.child("otherStyle")?.child("lvl1pPr")?.child("defRPr")?.attr("sz").flatMap(Double.init) {
                defaults.otherSize = CGFloat(size) / 100
            }
        }

        let reader = TreeReader(archive: archive, defaults: defaults,
                                layoutTree: layoutRoot?.child("cSld")?.child("spTree"),
                                masterTree: masterRoot?.child("cSld")?.child("spTree"))
        var slide = Slide()
        // Background: slide, then layout, then master.
        for (element, part) in [(root, slidePart), (layoutRoot, layoutPart), (masterRoot, masterPart)] {
            guard let element, let part, let bg = element.child("cSld")?.child("bg") else { continue }
            if let props = bg.child("bgPr") {
                if let color = props.child("solidFill").flatMap({ reader.color($0) }) { slide.background = color; break }
                if let image = props.child("blipFill").flatMap({ reader.image($0, part: part) }) { slide.backgroundImage = image; break }
            }
            if let ref = bg.child("bgRef"), let color = reader.color(ref) { slide.background = color; break }
        }
        // Decorations from the master and layout (not their placeholders), then the slide itself.
        let showMaster = root.attr("showMasterSp") != "0" && layoutRoot?.attr("showMasterSp") != "0"
        if showMaster, let tree = masterRoot?.child("cSld")?.child("spTree"), let part = masterPart {
            slide.items += reader.items(in: tree, part: part, skipPlaceholders: true)
        }
        if root.attr("showMasterSp") != "0", let tree = layoutRoot?.child("cSld")?.child("spTree"), let part = layoutPart {
            slide.items += reader.items(in: tree, part: part, skipPlaceholders: true)
        }
        if let tree = root.child("cSld")?.child("spTree") {
            slide.items += reader.items(in: tree, part: slidePart, skipPlaceholders: false)
        }
        return slide
    }

    private static func themeColors(_ theme: XMLElement) -> [String: NSColor] {
        var colors: [String: NSColor] = [:]
        guard let scheme = theme.descendant("clrScheme") else { return colors }
        for case let entry as XMLElement in scheme.children ?? [] {
            guard let name = entry.localName else { continue }
            if let rgb = entry.child("srgbClr")?.attr("val") { colors[name] = hex(rgb) }
            else if let rgb = entry.child("sysClr")?.attr("lastClr") { colors[name] = hex(rgb) }
        }
        colors["tx1"] = colors["dk1"]; colors["bg1"] = colors["lt1"]
        colors["tx2"] = colors["dk2"]; colors["bg2"] = colors["lt2"]
        return colors
    }

    static func hex(_ value: String) -> NSColor? {
        guard value.count == 6, let number = Int(value, radix: 16) else { return nil }
        return NSColor(srgbRed: CGFloat((number >> 16) & 0xFF) / 255, green: CGFloat((number >> 8) & 0xFF) / 255,
                       blue: CGFloat(number & 0xFF) / 255, alpha: 1)
    }

    private static func alignment(_ value: String?) -> NSTextAlignment? {
        switch value {
        case "ctr": return .center
        case "r": return .right
        case "just", "dist": return .justified
        case "l": return .left
        default: return nil
        }
    }

    /// Walks a shape tree. Holds what's needed to resolve placeholders, colours and images.
    private struct TreeReader {
        let archive: ZipArchive
        let defaults: Defaults
        let layoutTree: XMLElement?
        let masterTree: XMLElement?

        struct Transform {
            var dx: CGFloat = 0, dy: CGFloat = 0, sx: CGFloat = 1, sy: CGFloat = 1
            func apply(_ r: CGRect) -> CGRect {
                CGRect(x: dx + r.minX * sx, y: dy + r.minY * sy, width: r.width * sx, height: r.height * sy)
            }
        }

        func items(in tree: XMLElement, part: Part, skipPlaceholders: Bool, transform: Transform = Transform()) -> [Item] {
            var result: [Item] = []
            for case let element as XMLElement in tree.children ?? [] {
                switch element.localName {
                case "sp":
                    let placeholder = element.child("nvSpPr")?.child("nvPr")?.child("ph")
                    if placeholder != nil && skipPlaceholders { continue }
                    if let item = shape(element, placeholder: placeholder, part: part, transform: transform) { result.append(item) }
                case "pic":
                    if let item = picture(element, part: part, transform: transform) { result.append(item) }
                case "graphicFrame":
                    if let item = table(element, transform: transform) { result.append(item) }
                case "grpSp":
                    guard let xfrm = element.child("grpSpPr")?.child("xfrm") else {
                        result += items(in: element, part: part, skipPlaceholders: skipPlaceholders, transform: transform)
                        continue
                    }
                    let outer = frame(xfrm) ?? .zero
                    let childOffset = point(xfrm.child("chOff"), "x", "y") ?? outer.origin
                    let childSize = size(xfrm.child("chExt")) ?? outer.size
                    var inner = Transform()
                    inner.sx = childSize.width > 0 ? outer.width / childSize.width : 1
                    inner.sy = childSize.height > 0 ? outer.height / childSize.height : 1
                    inner.dx = outer.minX - childOffset.x * inner.sx
                    inner.dy = outer.minY - childOffset.y * inner.sy
                    // Compose: parent ∘ inner.
                    let composed = Transform(dx: transform.dx + inner.dx * transform.sx, dy: transform.dy + inner.dy * transform.sy,
                                             sx: transform.sx * inner.sx, sy: transform.sy * inner.sy)
                    result += items(in: element, part: part, skipPlaceholders: skipPlaceholders, transform: composed)
                case "AlternateContent":
                    if let fallback = element.child("Fallback") {
                        result += items(in: fallback, part: part, skipPlaceholders: skipPlaceholders, transform: transform)
                    }
                default:
                    continue
                }
            }
            return result
        }

        // MARK: Shapes and text

        private func shape(_ element: XMLElement, placeholder: XMLElement?, part: Part, transform: Transform) -> Item? {
            let props = element.child("spPr")
            let inherited = placeholder.flatMap(inheritedPlaceholders)
            guard let xfrm = props?.child("xfrm") ?? inherited?.compactMap({ $0.child("spPr")?.child("xfrm") }).first,
                  let local = frame(xfrm) else { return nil }
            let rect = transform.apply(local)
            let rotation = CGFloat(xfrm.attr("rot").flatMap(Double.init) ?? 0) / 60_000

            // A picture used as the shape's fill.
            if let blip = props?.child("blipFill"), let image = image(blip, part: part) {
                return .picture(rect: rect, rotation: rotation, image: image)
            }

            let geometry = props?.child("prstGeom")?.attr("prst") ?? (placeholder == nil ? "rect" : "none")
            var fill: NSColor?
            var line: NSColor?
            var lineWidth: CGFloat = 1
            if let props {
                if props.child("noFill") == nil { fill = props.child("solidFill").flatMap(color) }
                if let ln = props.child("ln") {
                    if ln.child("noFill") == nil { line = ln.child("solidFill").flatMap(color) }
                    if let w = ln.attr("w").flatMap(Double.init) { lineWidth = CGFloat(w) / emu }
                }
                // Shapes drawn in PowerPoint take their colours from the theme style.
                if let style = element.child("style"), placeholder == nil {
                    if fill == nil, props.child("noFill") == nil, props.child("solidFill") == nil,
                       let ref = style.child("fillRef"), ref.attr("idx") != "0" { fill = color(ref) }
                    if line == nil, props.child("ln")?.child("noFill") == nil,
                       let ref = style.child("lnRef"), ref.attr("idx") != "0" { line = color(ref) }
                }
            }

            let text = element.child("txBody").map { textBox($0, placeholder: placeholder, inherited: inherited ?? [],
                                                             defaultColor: element.child("style")?.child("fontRef").flatMap(color)) }
            let hasText = !(text?.plainText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ?? true)
            if fill == nil, line == nil, !hasText { return nil }
            return .shape(rect: rect, rotation: rotation, geometry: geometry, fill: fill, line: line,
                          lineWidth: lineWidth, text: hasText ? text : nil)
        }

        private func placeholderKind(_ placeholder: XMLElement) -> String {
            placeholder.attr("type") ?? "body"
        }

        /// Layout, then master placeholders this one inherits from.
        private func inheritedPlaceholders(_ placeholder: XMLElement) -> [XMLElement] {
            let type = placeholderKind(placeholder)
            let index = placeholder.attr("idx")
            func find(in tree: XMLElement?, matchIndex: Bool) -> XMLElement? {
                tree?.descendants("sp").first { sp in
                    guard let ph = sp.child("nvSpPr")?.child("nvPr")?.child("ph") else { return false }
                    if matchIndex, let index, ph.attr("idx") == index { return true }
                    let kind = placeholderKind(ph)
                    return kind == type || (type == "ctrTitle" && kind == "title") || (type == "subTitle" && kind == "body")
                }
            }
            return [find(in: layoutTree, matchIndex: true), find(in: masterTree, matchIndex: false)].compactMap { $0 }
        }

        private func textBox(_ body: XMLElement, placeholder: XMLElement?, inherited: [XMLElement], defaultColor: NSColor?) -> TextBox {
            let kind = placeholder.map(placeholderKind)
            let isTitle = kind == "title" || kind == "ctrTitle"
            let isBody = kind == "body" || kind == "obj"
            let bodyPr = body.child("bodyPr")
            let inheritedBodyPr = inherited.compactMap { $0.child("txBody")?.child("bodyPr") }
            func bodyAttr(_ name: String) -> String? {
                bodyPr?.attr(name) ?? inheritedBodyPr.lazy.compactMap { $0.attr(name) }.first
            }
            var box = TextBox(paragraphs: [])
            box.isTitle = isTitle
            box.anchor = bodyAttr("anchor") ?? (isTitle ? "ctr" : "t")
            func inset(_ name: String, _ fallback: CGFloat) -> CGFloat {
                bodyAttr(name).flatMap(Double.init).map { CGFloat($0) / emu } ?? fallback
            }
            box.insets = NSEdgeInsets(top: inset("tIns", 3.6), left: inset("lIns", 7.2),
                                      bottom: inset("bIns", 3.6), right: inset("rIns", 7.2))
            let fontScale = CGFloat(bodyPr?.child("normAutofit")?.attr("fontScale").flatMap(Double.init) ?? 100_000) / 100_000

            // Level styles: the shape's own list style, then the inherited placeholders'.
            let listStyles = ([body.child("lstStyle")] + inherited.map { $0.child("txBody")?.child("lstStyle") }).compactMap { $0 }
            func levelProps(_ level: Int) -> [XMLElement] {
                listStyles.compactMap { $0.child("lvl\(level + 1)pPr") }
            }
            let textColor = defaultColor ?? defaults.theme["tx1"] ?? .black

            for p in body.children("p") {
                var paragraph = Paragraph()
                let pPr = p.child("pPr")
                paragraph.level = pPr?.attr("lvl").flatMap(Int.init) ?? 0
                let levelStyles = levelProps(paragraph.level)
                paragraph.alignment = alignment(pPr?.attr("algn") ?? levelStyles.lazy.compactMap { $0.attr("algn") }.first)
                    ?? (isTitle ? defaults.titleAlignment : .left)

                let baseSize: CGFloat = {
                    if let sz = levelStyles.lazy.compactMap({ $0.child("defRPr")?.attr("sz") }).first.flatMap(Double.init) {
                        return CGFloat(sz) / 100
                    }
                    if isTitle { return defaults.titleSize }
                    if isBody { return defaults.bodySizes[min(paragraph.level, 8)] }
                    if kind == "subTitle" { return defaults.bodySizes[0] * 0.8 }
                    return defaults.otherSize
                }()

                // Bullets: explicit on the paragraph, else from the list style, else body placeholders get "•".
                let bulletSources = [pPr].compactMap { $0 } + levelStyles
                if bulletSources.contains(where: { $0.child("buNone") != nil }) && pPr?.child("buChar") == nil && pPr?.child("buAutoNum") == nil {
                    paragraph.bullet = nil
                } else if let char = bulletSources.lazy.compactMap({ $0.child("buChar")?.attr("char") }).first {
                    paragraph.bullet = char
                } else if bulletSources.contains(where: { $0.child("buAutoNum") != nil }) {
                    paragraph.bullet = "#"
                } else if isBody {
                    paragraph.bullet = "•"
                }

                for case let node as XMLElement in p.children ?? [] {
                    switch node.localName {
                    case "r", "fld":
                        let rPr = node.child("rPr")
                        let size = (rPr?.attr("sz").flatMap(Double.init).map { CGFloat($0) / 100 } ?? baseSize) * fontScale
                        var run = Run(text: node.child("t")?.stringValue ?? "", size: size, color: textColor)
                        run.bold = rPr?.attr("b") == "1" || (rPr?.attr("b") == nil && levelStyles.contains { $0.child("defRPr")?.attr("b") == "1" })
                        run.italic = rPr?.attr("i") == "1"
                        run.underline = (rPr?.attr("u")).map { $0 != "none" } ?? false
                        if let fill = rPr?.child("solidFill"), let c = color(fill) { run.color = c }
                        else if let c = levelStyles.lazy.compactMap({ $0.child("defRPr")?.child("solidFill") }).first.flatMap(color) { run.color = c }
                        run.typeface = rPr?.child("latin")?.attr("typeface") ?? rPr?.child("ea")?.attr("typeface")
                        paragraph.runs.append(run)
                    case "br":
                        paragraph.runs.append(Run(text: "\n", size: baseSize * fontScale, color: textColor))
                    case "endParaRPr":
                        paragraph.emptySize = (node.attr("sz").flatMap(Double.init).map { CGFloat($0) / 100 } ?? baseSize) * fontScale
                    default:
                        break
                    }
                }
                if paragraph.runs.allSatisfy({ $0.text.trimmingCharacters(in: .whitespaces).isEmpty }) { paragraph.bullet = nil }
                box.paragraphs.append(paragraph)
            }
            return box
        }

        // MARK: Pictures and tables

        private func picture(_ element: XMLElement, part: Part, transform: Transform) -> Item? {
            guard let xfrm = element.child("spPr")?.child("xfrm"), let local = frame(xfrm),
                  let blip = element.child("blipFill"), let image = image(blip, part: part) else { return nil }
            let rotation = CGFloat(xfrm.attr("rot").flatMap(Double.init) ?? 0) / 60_000
            return .picture(rect: transform.apply(local), rotation: rotation, image: image)
        }

        func image(_ blipFill: XMLElement, part: Part) -> NSImage? {
            guard let id = blipFill.child("blip")?.attr("embed"), let path = part.relationships[id]?.target,
                  let data = archive.data(at: path) else { return nil }
            return NSImage(data: data)
        }

        private func table(_ element: XMLElement, transform: Transform) -> Item? {
            guard let xfrm = element.child("xfrm"), let local = frame(xfrm),
                  let tbl = element.descendant("tbl") else { return nil }
            let columns = (tbl.child("tblGrid")?.children("gridCol") ?? []).map {
                CGFloat($0.attr("w").flatMap(Double.init) ?? 0) / emu * transform.sx
            }
            // PowerPoint's default table style: accent-coloured header row with white bold text,
            // then banded rows in light tints of the same colour.
            let tblPr = tbl.child("tblPr")
            let styled = tblPr?.child("tableStyleId") != nil || tblPr?.attr("firstRow") == "1"
            let accent = defaults.theme["accent1"] ?? NSColor(srgbRed: 0.27, green: 0.45, blue: 0.77, alpha: 1)
            var rows: [(height: CGFloat, cells: [TableCell])] = []
            for (rowIndex, tr) in tbl.children("tr").enumerated() {
                let height = CGFloat(tr.attr("h").flatMap(Double.init) ?? 0) / emu * transform.sy
                let isHeader = styled && rowIndex == 0 && tblPr?.attr("firstRow") != "0"
                let banded = styled && tblPr?.attr("bandRow") != "0"
                let cells = tr.children("tc").map { tc -> TableCell in
                    var box = tc.child("txBody").map { textBox($0, placeholder: nil, inherited: [], defaultColor: nil) }
                        ?? TextBox(paragraphs: [])
                    box.anchor = tc.child("tcPr")?.attr("anchor") ?? "t"
                    var fill = tc.child("tcPr")?.child("solidFill").flatMap(color)
                    if fill == nil, styled {
                        if isHeader {
                            fill = accent
                            for p in box.paragraphs.indices {
                                for r in box.paragraphs[p].runs.indices {
                                    box.paragraphs[p].runs[r].bold = true
                                    box.paragraphs[p].runs[r].color = .white
                                }
                            }
                        } else if banded {
                            fill = accent.blended(withFraction: rowIndex % 2 == 1 ? 0.8 : 0.9, of: .white)
                        }
                    }
                    return TableCell(text: box, fill: fill)
                }
                rows.append((height, cells))
            }
            return .table(rect: transform.apply(local), columns: columns, rows: rows)
        }

        // MARK: Geometry and colour

        func frame(_ xfrm: XMLElement) -> CGRect? {
            guard let origin = point(xfrm.child("off"), "x", "y"), let size = size(xfrm.child("ext")) else { return nil }
            return CGRect(origin: origin, size: size)
        }

        func point(_ element: XMLElement?, _ xName: String, _ yName: String) -> CGPoint? {
            guard let element, let x = element.attr(xName).flatMap(Double.init), let y = element.attr(yName).flatMap(Double.init) else { return nil }
            return CGPoint(x: CGFloat(x) / emu, y: CGFloat(y) / emu)
        }

        func size(_ element: XMLElement?) -> CGSize? {
            guard let element, let cx = element.attr("cx").flatMap(Double.init), let cy = element.attr("cy").flatMap(Double.init) else { return nil }
            return CGSize(width: CGFloat(cx) / emu, height: CGFloat(cy) / emu)
        }

        /// Colour from an element holding srgbClr / schemeClr / sysClr / prstClr, with lumMod/lumOff/alpha.
        func color(_ holder: XMLElement) -> NSColor? {
            var base: NSColor?
            var spec: XMLElement?
            if let e = holder.child("srgbClr") { base = hex(e.attr("val") ?? ""); spec = e }
            else if let e = holder.child("schemeClr") { base = defaults.theme[e.attr("val") ?? ""]; spec = e }
            else if let e = holder.child("sysClr") { base = hex(e.attr("lastClr") ?? ""); spec = e }
            else if let e = holder.child("prstClr") {
                base = ["black": NSColor.black, "white": .white, "red": .red, "blue": .blue, "green": .green,
                        "yellow": .yellow, "gray": .gray][e.attr("val") ?? ""]
                spec = e
            }
            guard var color = base?.usingColorSpace(.sRGB) else { return nil }
            if let spec {
                var hue: CGFloat = 0, saturation: CGFloat = 0, brightness: CGFloat = 0, alpha: CGFloat = 1
                color.getHue(&hue, saturation: &saturation, brightness: &brightness, alpha: &alpha)
                let lumMod = spec.child("lumMod")?.attr("val").flatMap(Double.init).map { CGFloat($0) / 100_000 }
                let lumOff = spec.child("lumOff")?.attr("val").flatMap(Double.init).map { CGFloat($0) / 100_000 }
                let shade = spec.child("shade")?.attr("val").flatMap(Double.init).map { CGFloat($0) / 100_000 }
                let tint = spec.child("tint")?.attr("val").flatMap(Double.init).map { CGFloat($0) / 100_000 }
                if lumMod != nil || lumOff != nil {
                    // Approximate HSL luminance changes in HSB space.
                    let mod = lumMod ?? 1, off = lumOff ?? 0
                    if off > 0 { saturation *= mod }
                    brightness = brightness * mod + off
                }
                if let shade { brightness *= shade }
                if let tint { saturation *= tint; brightness = brightness + (1 - brightness) * (1 - tint) }
                if let a = spec.child("alpha")?.attr("val").flatMap(Double.init) { alpha = CGFloat(a) / 100_000 }
                color = NSColor(hue: hue, saturation: min(max(saturation, 0), 1), brightness: min(max(brightness, 0), 1), alpha: alpha)
            }
            return color
        }
    }

    // MARK: Drawing

    private static func draw(_ slide: Slide, size: CGSize, in context: CGContext) {
        slide.background.setFill()
        CGRect(origin: .zero, size: size).fill()
        slide.backgroundImage?.draw(in: CGRect(origin: .zero, size: size), from: .zero, operation: .sourceOver,
                                    fraction: 1, respectFlipped: true, hints: nil)
        for item in slide.items {
            switch item {
            case .shape(let rect, let rotation, let geometry, let fill, let line, let lineWidth, let text):
                rotated(rect, by: rotation, in: context) {
                    let path: NSBezierPath
                    switch geometry {
                    case "ellipse": path = NSBezierPath(ovalIn: rect)
                    case "roundRect":
                        let r = min(rect.width, rect.height) * 0.1667
                        path = NSBezierPath(roundedRect: rect, xRadius: r, yRadius: r)
                    default: path = NSBezierPath(rect: rect)
                    }
                    if let fill { fill.setFill(); path.fill() }
                    if let line { line.setStroke(); path.lineWidth = lineWidth; path.stroke() }
                    if let text { drawText(text, in: rect) }
                }
            case .picture(let rect, let rotation, let image):
                rotated(rect, by: rotation, in: context) {
                    image.draw(in: rect, from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
                }
            case .table(let rect, let columns, let rows):
                var y = rect.minY
                for row in rows {
                    var x = rect.minX
                    // Rows grow to fit their text, like PowerPoint does.
                    var height = row.height
                    for (index, cell) in row.cells.enumerated() where index < columns.count {
                        height = max(height, measuredHeight(cell.text, width: columns[index]))
                    }
                    for (index, cell) in row.cells.enumerated() where index < columns.count {
                        let cellRect = CGRect(x: x, y: y, width: columns[index], height: height)
                        if let fill = cell.fill { fill.setFill(); cellRect.fill() }
                        NSColor(white: 0.75, alpha: 1).setStroke()
                        let border = NSBezierPath(rect: cellRect); border.lineWidth = 0.75; border.stroke()
                        drawText(cell.text, in: cellRect)
                        x += columns[index]
                    }
                    y += height
                }
            }
        }
    }

    private static func rotated(_ rect: CGRect, by degrees: CGFloat, in context: CGContext, _ body: () -> Void) {
        guard degrees != 0 else { body(); return }
        context.saveGState()
        context.translateBy(x: rect.midX, y: rect.midY)
        context.rotate(by: degrees * .pi / 180)
        context.translateBy(x: -rect.midX, y: -rect.midY)
        body()
        context.restoreGState()
    }

    private static func attributed(_ box: TextBox) -> NSAttributedString {
        let result = NSMutableAttributedString()
        var number = 0
        for (index, paragraph) in box.paragraphs.enumerated() {
            let style = NSMutableParagraphStyle()
            style.alignment = paragraph.alignment
            style.lineHeightMultiple = 0.95
            style.paragraphSpacingBefore = index == 0 ? 0 : (paragraph.runs.first?.size ?? paragraph.emptySize) * 0.25
            let indent = CGFloat(paragraph.level) * 24
            let size = paragraph.runs.first?.size ?? paragraph.emptySize
            if let bullet = paragraph.bullet {
                number += 1
                let marker = bullet == "#" ? "\(number)." : bullet
                let markerWidth = size * 1.0
                style.firstLineHeadIndent = indent
                style.headIndent = indent + markerWidth
                style.tabStops = [NSTextTab(textAlignment: .left, location: indent + markerWidth)]
                let color = paragraph.runs.first?.color ?? .black
                result.append(NSAttributedString(string: marker + "\t", attributes: [
                    .font: NSFont.systemFont(ofSize: size), .foregroundColor: color, .paragraphStyle: style,
                ]))
            } else {
                style.firstLineHeadIndent = indent
                style.headIndent = indent
                number = 0
            }
            if paragraph.runs.isEmpty {
                result.append(NSAttributedString(string: " ", attributes: [.font: NSFont.systemFont(ofSize: paragraph.emptySize), .paragraphStyle: style]))
            }
            for run in paragraph.runs {
                var font = run.typeface.flatMap { NSFont(name: $0, size: run.size) } ?? NSFont.systemFont(ofSize: run.size)
                if run.bold { font = NSFontManager.shared.convert(font, toHaveTrait: .boldFontMask) }
                if run.italic { font = NSFontManager.shared.convert(font, toHaveTrait: .italicFontMask) }
                var attributes: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: run.color, .paragraphStyle: style]
                if run.underline { attributes[.underlineStyle] = NSUnderlineStyle.single.rawValue }
                result.append(NSAttributedString(string: run.text, attributes: attributes))
            }
            if index < box.paragraphs.count - 1 { result.append(NSAttributedString(string: "\n", attributes: [.paragraphStyle: style])) }
        }
        return result
    }

    private static func measuredHeight(_ box: TextBox, width: CGFloat) -> CGFloat {
        let text = attributed(box)
        let inner = max(1, width - box.insets.left - box.insets.right)
        let bounds = text.boundingRect(with: CGSize(width: inner, height: .greatestFiniteMagnitude),
                                       options: [.usesLineFragmentOrigin, .usesFontLeading])
        return ceil(bounds.height) + box.insets.top + box.insets.bottom
    }

    private static func drawText(_ box: TextBox, in rect: CGRect) {
        let text = attributed(box)
        let inner = CGRect(x: rect.minX + box.insets.left, y: rect.minY + box.insets.top,
                           width: max(1, rect.width - box.insets.left - box.insets.right),
                           height: max(1, rect.height - box.insets.top - box.insets.bottom))
        let height = ceil(text.boundingRect(with: CGSize(width: inner.width, height: .greatestFiniteMagnitude),
                                            options: [.usesLineFragmentOrigin, .usesFontLeading]).height)
        var y = inner.minY
        switch box.anchor {
        case "ctr": y = inner.midY - height / 2
        case "b": y = inner.maxY - height
        default: break
        }
        text.draw(with: CGRect(x: inner.minX, y: y, width: inner.width, height: max(height, inner.height)),
                  options: [.usesLineFragmentOrigin, .usesFontLeading])
    }
}
