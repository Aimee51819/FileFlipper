import AppKit

/// Reads a Word (.docx) file into rich text the way Word lays it out: styles (fonts, sizes,
/// colours, spacing, indents), headings, numbered and bulleted lists, tables, pictures, links,
/// page breaks, and the page size and margins. The Cocoa text system's own DOCX import ignores
/// paragraph styles and page setup, so documents came out as plain 12 pt text on Letter paper.
enum WordReader {
    struct PageSetup {
        var size = NSSize(width: 612, height: 792)
        var margins = NSEdgeInsets(top: 72, left: 72, bottom: 72, right: 72)
        var contentWidth: CGFloat { size.width - margins.left - margins.right }
    }

    static func read(_ url: URL) throws -> (text: NSAttributedString, page: PageSetup) {
        let archive: ZipArchive
        do { archive = try ZipArchive(url: url) } catch { throw ConversionError.unreadable(url) }
        guard let document = OfficePackage.xml(archive, "word/document.xml"),
              let body = document.child("body") else { throw ConversionError.unreadable(url) }
        let builder = Builder(archive: archive)
        if let section = body.child("sectPr") { builder.readPageSetup(section) }
        builder.blocks(in: body, cell: nil)
        builder.finish()
        return (builder.output, builder.page)
    }

    // MARK: - Properties

    /// Run (character) formatting; `nil` means "not set at this level".
    struct RunProps {
        var font: String?
        var eastAsiaFont: String?
        var size: CGFloat?
        var bold: Bool?
        var italic: Bool?
        var underline: Bool?
        var strike: Bool?
        var color: NSColor??
        var highlight: NSColor??
        var caps: Bool?
        var vertAlign: String?

        mutating func apply(_ other: RunProps) {
            if let v = other.font { font = v }
            if let v = other.eastAsiaFont { eastAsiaFont = v }
            if let v = other.size { size = v }
            if let v = other.bold { bold = v }
            if let v = other.italic { italic = v }
            if let v = other.underline { underline = v }
            if let v = other.strike { strike = v }
            if let v = other.color { color = v }
            if let v = other.highlight { highlight = v }
            if let v = other.caps { caps = v }
            if let v = other.vertAlign { vertAlign = v }
        }
    }

    /// Paragraph formatting, in points.
    struct ParaProps {
        var alignment: NSTextAlignment?
        var before: CGFloat?
        var after: CGFloat?
        var line: (value: CGFloat, rule: String)?
        var left: CGFloat?
        var right: CGFloat?
        var firstLine: CGFloat?     // positive = first-line indent, negative = hanging
        var numID: String?
        var level: Int?
        var pageBreakBefore: Bool?
        var contextualSpacing: Bool?

        mutating func apply(_ other: ParaProps) {
            if let v = other.alignment { alignment = v }
            if let v = other.before { before = v }
            if let v = other.after { after = v }
            if let v = other.line { line = v }
            if let v = other.left { left = v }
            if let v = other.right { right = v }
            if let v = other.firstLine { firstLine = v }
            if let v = other.numID { numID = v }
            if let v = other.level { level = v }
            if let v = other.pageBreakBefore { pageBreakBefore = v }
            if let v = other.contextualSpacing { contextualSpacing = v }
        }
    }

    struct Style {
        var basedOn: String?
        var para = ParaProps()
        var run = RunProps()
        var name = ""
    }

    struct Level {
        var format = "bullet"
        var text = "•"
        var start = 1
        var para = ParaProps()
        var run = RunProps()
    }

    // MARK: - Parsing helpers

    static func twips(_ value: String?) -> CGFloat? {
        value.flatMap(Double.init).map { CGFloat($0) / 20 }
    }

    static func isOn(_ element: XMLElement?) -> Bool? {
        guard let element else { return nil }
        guard let value = element.attr("val") else { return true }
        return !["0", "false", "off", "none"].contains(value.lowercased())
    }

    static func runProps(_ rPr: XMLElement?, theme: [String: String]) -> RunProps {
        var props = RunProps()
        guard let rPr else { return props }
        if let fonts = rPr.child("rFonts") {
            if let name = fonts.attr("ascii") ?? fonts.attr("hAnsi") { props.font = name }
            else if let ref = fonts.attr("asciiTheme") ?? fonts.attr("hAnsiTheme") { props.font = theme[ref] }
            if let name = fonts.attr("eastAsia") { props.eastAsiaFont = name }
            else if let ref = fonts.attr("eastAsiaTheme") { props.eastAsiaFont = theme[ref] }
        }
        if let size = (rPr.child("sz") ?? rPr.child("szCs"))?.attr("val").flatMap(Double.init) {
            props.size = CGFloat(size) / 2
        }
        props.bold = isOn(rPr.child("b"))
        props.italic = isOn(rPr.child("i"))
        if let u = rPr.child("u") { props.underline = (u.attr("val") ?? "single") != "none" }
        props.strike = isOn(rPr.child("strike"))
        props.caps = isOn(rPr.child("caps"))
        if let color = rPr.child("color")?.attr("val") {
            props.color = .some(color == "auto" ? nil : PresentationConverter.hex(color))
        }
        if let highlight = rPr.child("highlight")?.attr("val") {
            props.highlight = .some(highlightColor(highlight))
        } else if let fill = rPr.child("shd")?.attr("fill"), fill != "auto" {
            props.highlight = .some(PresentationConverter.hex(fill))
        }
        if let vert = rPr.child("vertAlign")?.attr("val") { props.vertAlign = vert }
        return props
    }

    static func paraProps(_ pPr: XMLElement?) -> ParaProps {
        var props = ParaProps()
        guard let pPr else { return props }
        switch pPr.child("jc")?.attr("val") {
        case "center": props.alignment = .center
        case "right", "end": props.alignment = .right
        case "both", "distribute", "lowKashida", "mediumKashida", "highKashida": props.alignment = .justified
        case "left", "start": props.alignment = .left
        default: break
        }
        if let spacing = pPr.child("spacing") {
            props.before = twips(spacing.attr("before"))
            props.after = twips(spacing.attr("after"))
            // "Auto" spacing (used by documents pasted from the web) is 14 pt in Word.
            if let auto = spacing.attr("beforeAutospacing"), auto == "1" || auto == "true" { props.before = 14 }
            if let auto = spacing.attr("afterAutospacing"), auto == "1" || auto == "true" { props.after = 14 }
            if let line = spacing.attr("line").flatMap(Double.init) {
                props.line = (CGFloat(line), spacing.attr("lineRule") ?? "auto")
            }
        }
        if let ind = pPr.child("ind") {
            props.left = twips(ind.attr("left") ?? ind.attr("start"))
            props.right = twips(ind.attr("right") ?? ind.attr("end"))
            if let hanging = twips(ind.attr("hanging")) { props.firstLine = -hanging }
            else if let first = twips(ind.attr("firstLine")) { props.firstLine = first }
        }
        if let num = pPr.child("numPr") {
            props.numID = num.child("numId")?.attr("val")
            props.level = num.child("ilvl")?.attr("val").flatMap(Int.init)
        }
        props.pageBreakBefore = isOn(pPr.child("pageBreakBefore"))
        props.contextualSpacing = isOn(pPr.child("contextualSpacing"))
        return props
    }

    static func highlightColor(_ name: String) -> NSColor? {
        let colors: [String: String] = [
            "yellow": "FFFF00", "green": "00FF00", "cyan": "00FFFF", "magenta": "FF00FF", "blue": "0000FF",
            "red": "FF0000", "darkBlue": "000080", "darkCyan": "008080", "darkGreen": "008000",
            "darkMagenta": "800080", "darkRed": "800000", "darkYellow": "808000", "darkGray": "808080",
            "lightGray": "C0C0C0", "black": "000000", "white": "FFFFFF",
        ]
        return colors[name].flatMap(PresentationConverter.hex)
    }

    /// Fonts Word documents use that macOS doesn't ship, mapped to the closest system font.
    static func systemFont(for name: String?, size: CGFloat) -> NSFont? {
        guard let name, !name.isEmpty else { return nil }
        if let font = NSFont(name: name, size: size) { return font }
        let lower = name.lowercased()
        let fallbacks: [(keys: [String], font: String)] = [
            (["宋体", "simsun", "新宋体", "nsimsun", "songti", "宋"], "Songti SC"),
            (["仿宋", "fangsong"], "STFangsong"),
            (["楷体", "kaiti", "楷"], "Kaiti SC"),
            (["微软雅黑", "microsoft yahei", "等线", "dengxian", "黑体", "simhei", "pingfang", "雅黑"], "PingFang SC"),
            (["mincho", "明朝", "ms gothic", "meiryo", "yu gothic"], "Hiragino Sans"),
            (["malgun", "batang", "gulim", "맑은"], "Apple SD Gothic Neo"),
            (["cambria", "georgia", "book antiqua", "garamond", "constantia"], "Georgia"),
            (["times", "minion"], "Times New Roman"),
            (["consolas", "courier", "lucida console", "menlo", "cascadia"], "Menlo"),
            (["calibri", "aptos", "segoe", "arial", "helvetica", "verdana", "tahoma", "candara", "corbel", "franklin"], "Helvetica Neue"),
        ]
        for entry in fallbacks where entry.keys.contains(where: { lower.contains($0) }) {
            if let font = NSFont(name: entry.font, size: size) { return font }
        }
        return nil
    }

    // MARK: - Builder

    private final class Builder {
        let archive: ZipArchive
        let output = NSMutableAttributedString()
        var page = PageSetup()

        private var styles: [String: Style] = [:]
        private var defaultParagraphStyle: String?
        private var defaults = Style()
        private var theme: [String: String] = [:]
        private var numberingLevels: [String: [Int: Level]] = [:]     // numId → levels
        private var startOverrides: [String: [Int: Int]] = [:]
        private var counters: [String: [Int: Int]] = [:]
        private let links: [String: (target: String, type: String)]

        /// For "contextual spacing": the previous paragraph's style and range.
        private var previous: (style: String?, range: NSRange, contextual: Bool)?

        init(archive: ZipArchive) {
            self.archive = archive
            links = OfficePackage.relationships(archive, for: "word/document.xml")
            readTheme()
            readStyles()
            readNumbering()
        }

        // MARK: Setup

        private func readTheme() {
            guard let theme = OfficePackage.xml(archive, "word/theme/theme1.xml"),
                  let fonts = theme.descendant("fontScheme") else { return }
            func face(_ group: String, _ script: String) -> String? {
                let value = fonts.child(group)?.child(script)?.attr("typeface")
                return value?.isEmpty == false ? value : nil
            }
            self.theme["minorHAnsi"] = face("minorFont", "latin")
            self.theme["minorAscii"] = face("minorFont", "latin")
            self.theme["majorHAnsi"] = face("majorFont", "latin")
            self.theme["majorAscii"] = face("majorFont", "latin")
            self.theme["minorEastAsia"] = face("minorFont", "ea")
            self.theme["majorEastAsia"] = face("majorFont", "ea")
        }

        private func readStyles() {
            guard let root = OfficePackage.xml(archive, "word/styles.xml") else { return }
            if let docDefaults = root.child("docDefaults") {
                defaults.run = WordReader.runProps(docDefaults.child("rPrDefault")?.child("rPr"), theme: theme)
                defaults.para = WordReader.paraProps(docDefaults.child("pPrDefault")?.child("pPr"))
            }
            for element in root.children("style") {
                guard let id = element.attr("styleId") else { continue }
                var style = Style()
                style.name = (element.child("name")?.attr("val") ?? id).lowercased()
                style.basedOn = element.child("basedOn")?.attr("val")
                style.para = WordReader.paraProps(element.child("pPr"))
                style.run = WordReader.runProps(element.child("rPr"), theme: theme)
                styles[id] = style
                if element.attr("type") == "paragraph", element.attr("default") == "1" { defaultParagraphStyle = id }
            }
        }

        private func readNumbering() {
            guard let root = OfficePackage.xml(archive, "word/numbering.xml") else { return }
            var abstract: [String: [Int: Level]] = [:]
            for element in root.children("abstractNum") {
                guard let id = element.attr("abstractNumId") else { continue }
                var levels: [Int: Level] = [:]
                for lvl in element.children("lvl") {
                    var level = Level()
                    let index = lvl.attr("ilvl").flatMap(Int.init) ?? 0
                    level.format = lvl.child("numFmt")?.attr("val") ?? "bullet"
                    level.text = lvl.child("lvlText")?.attr("val") ?? ""
                    level.start = lvl.child("start")?.attr("val").flatMap(Int.init) ?? 1
                    level.para = WordReader.paraProps(lvl.child("pPr"))
                    level.run = WordReader.runProps(lvl.child("rPr"), theme: theme)
                    levels[index] = level
                }
                abstract[id] = levels
            }
            for element in root.children("num") {
                guard let id = element.attr("numId"), let abstractID = element.child("abstractNumId")?.attr("val") else { continue }
                numberingLevels[id] = abstract[abstractID]
                for override in element.children("lvlOverride") {
                    if let level = override.attr("ilvl").flatMap(Int.init),
                       let start = override.child("startOverride")?.attr("val").flatMap(Int.init) {
                        startOverrides[id, default: [:]][level] = start
                    }
                }
            }
        }

        func readPageSetup(_ section: XMLElement) {
            if let size = section.child("pgSz"),
               let width = WordReader.twips(size.attr("w")), let height = WordReader.twips(size.attr("h")) {
                page.size = NSSize(width: width, height: height)
            }
            if let margin = section.child("pgMar") {
                page.margins = NSEdgeInsets(top: abs(WordReader.twips(margin.attr("top")) ?? 72),
                                            left: WordReader.twips(margin.attr("left")) ?? 72,
                                            bottom: abs(WordReader.twips(margin.attr("bottom")) ?? 72),
                                            right: WordReader.twips(margin.attr("right")) ?? 72)
            }
        }

        // MARK: Style resolution

        private func styleChain(_ id: String?) -> [Style] {
            var chain: [Style] = []
            var current = id
            var guardCount = 0
            while let key = current, let style = styles[key], guardCount < 20 {
                chain.insert(style, at: 0)
                current = style.basedOn
                guardCount += 1
            }
            return chain
        }

        // MARK: Blocks

        func blocks(in container: XMLElement, cell: NSTextTableBlock?) {
            for case let element as XMLElement in container.children ?? [] {
                switch element.localName {
                case "p": paragraph(element, cell: cell)
                case "tbl": table(element, outer: cell)
                case "sdt", "customXml", "smartTag":
                    if let content = element.child("sdtContent") ?? (element.localName == "sdt" ? nil : element) {
                        blocks(in: content, cell: cell)
                    }
                default: continue
                }
            }
        }

        func finish() {
            // Drop a trailing paragraph break so no blank last page is produced.
            while output.length > 0, (output.string as NSString).character(at: output.length - 1) == 10 {
                output.deleteCharacters(in: NSRange(location: output.length - 1, length: 1))
            }
        }

        private func paragraph(_ p: XMLElement, cell: NSTextTableBlock?) {
            let pPr = p.child("pPr")
            let styleID = pPr?.child("pStyle")?.attr("val") ?? defaultParagraphStyle
            let chain = styleChain(styleID)

            var para = defaults.para
            var baseRun = defaults.run
            for style in chain { para.apply(style.para); baseRun.apply(style.run) }
            let direct = WordReader.paraProps(pPr)
            para.apply(direct)

            // Numbering: the level's indents sit between the style and the paragraph's own.
            var marker: (text: String, run: RunProps)?
            if let numID = para.numID, numID != "0", let levels = numberingLevels[numID] {
                let levelIndex = para.level ?? 0
                if let level = levels[levelIndex] {
                    var merged = defaults.para
                    for style in chain { merged.apply(style.para) }
                    merged.apply(level.para)
                    merged.apply(direct)
                    para = merged
                    marker = (markerText(numID: numID, level: levelIndex, levels: levels), level.run)
                }
            }

            if para.pageBreakBefore == true, output.length > 0, cell == nil { output.append(NSAttributedString(string: "\u{0C}")) }

            let style = NSMutableParagraphStyle()
            style.alignment = para.alignment ?? .left
            let left = para.left ?? 0
            let first = para.firstLine ?? 0
            style.headIndent = left
            style.firstLineHeadIndent = max(0, left + first)
            style.tailIndent = -(para.right ?? 0)
            style.paragraphSpacingBefore = para.before ?? 0
            style.paragraphSpacing = para.after ?? 0
            if let line = para.line {
                switch line.rule {
                case "exact":
                    style.minimumLineHeight = line.value / 20
                    style.maximumLineHeight = line.value / 20
                case "atLeast":
                    style.minimumLineHeight = line.value / 20
                default:
                    style.lineHeightMultiple = line.value / 240
                }
            }
            style.defaultTabInterval = 36
            if marker != nil { style.tabStops = [NSTextTab(textAlignment: .left, location: left)] } else { style.tabStops = [] }
            if let cell { style.textBlocks = [cell] }

            // Contextual spacing: no gap between paragraphs of the same style.
            if para.contextualSpacing == true, let previous, previous.style == styleID {
                style.paragraphSpacingBefore = 0
                if previous.contextual, previous.range.length > 0,
                   let prevStyle = (output.attribute(.paragraphStyle, at: previous.range.location, effectiveRange: nil) as? NSParagraphStyle)?
                    .mutableCopy() as? NSMutableParagraphStyle {
                    prevStyle.paragraphSpacing = 0
                    output.addAttribute(.paragraphStyle, value: prevStyle, range: previous.range)
                }
            }

            let start = output.length
            if let marker {
                var run = baseRun
                run.apply(marker.run)
                output.append(NSAttributedString(string: marker.text + "\t", attributes: attributes(run, paragraph: style)))
            }
            runs(in: p, base: baseRun, paragraph: style, link: nil)

            // The paragraph mark carries the size of an empty line.
            var mark = baseRun
            mark.apply(WordReader.runProps(pPr?.child("rPr"), theme: theme))
            output.append(NSAttributedString(string: "\n", attributes: attributes(mark, paragraph: style)))
            let range = NSRange(location: start, length: output.length - start)
            output.addAttribute(.paragraphStyle, value: style, range: range)
            previous = (styleID, range, para.contextualSpacing == true)
        }

        private func markerText(numID: String, level: Int, levels: [Int: Level]) -> String {
            var values = counters[numID] ?? [:]
            let start = startOverrides[numID]?[level] ?? levels[level]?.start ?? 1
            values[level] = (values[level].map { $0 + 1 }) ?? start
            for deeper in values.keys where deeper > level { values[deeper] = nil }
            counters[numID] = values

            guard let current = levels[level] else { return "•" }
            if current.format == "bullet" {
                return WordReader.bullet(current.text)
            }
            if current.format == "none" { return "" }
            var text = current.text
            for index in 0...level {
                let value = values[index] ?? (startOverrides[numID]?[index] ?? levels[index]?.start ?? 1)
                text = text.replacingOccurrences(of: "%\(index + 1)", with: WordReader.format(value, levels[index]?.format ?? "decimal"))
            }
            return text
        }

        // MARK: Runs

        private func runs(in container: XMLElement, base: RunProps, paragraph: NSParagraphStyle, link: URL?) {
            for case let node as XMLElement in container.children ?? [] {
                switch node.localName {
                case "r":
                    var run = base
                    if let rStyle = node.child("rPr")?.child("rStyle")?.attr("val") {
                        for style in styleChain(rStyle) { run.apply(style.run) }
                    }
                    run.apply(WordReader.runProps(node.child("rPr"), theme: theme))
                    var attrs = attributes(run, paragraph: paragraph)
                    if let link { attrs[.link] = link }
                    for case let part as XMLElement in node.children ?? [] {
                        switch part.localName {
                        case "t":
                            var text = part.stringValue ?? ""
                            if run.caps == true { text = text.uppercased() }
                            appendText(text, run: run, attributes: attrs)
                        case "tab": output.append(NSAttributedString(string: "\t", attributes: attrs))
                        case "br":
                            output.append(NSAttributedString(string: part.attr("type") == "page" ? "\u{0C}" : "\u{2028}", attributes: attrs))
                        case "cr": output.append(NSAttributedString(string: "\u{2028}", attributes: attrs))
                        case "noBreakHyphen": output.append(NSAttributedString(string: "\u{2011}", attributes: attrs))
                        case "sym":
                            if let code = part.attr("char").flatMap({ UInt32($0, radix: 16) }).flatMap(UnicodeScalar.init) {
                                output.append(NSAttributedString(string: WordReader.bullet(String(code)), attributes: attrs))
                            }
                        case "drawing", "pict", "object":
                            if let image = picture(part) { output.append(image) }
                        default: break
                        }
                    }
                case "hyperlink":
                    let target = node.relationshipID.flatMap { links[$0]?.target }.flatMap(URL.init(string:))
                    runs(in: node, base: base, paragraph: paragraph, link: target ?? link)
                case "ins", "smartTag", "fldSimple", "customXml", "bdo", "dir":
                    runs(in: node, base: base, paragraph: paragraph, link: link)
                case "sdt":
                    if let content = node.child("sdtContent") { runs(in: content, base: base, paragraph: paragraph, link: link) }
                default:
                    continue   // deleted text, bookmarks, comments, proofing marks…
                }
            }
        }

        /// Uses the East Asian font for Chinese, Japanese and Korean characters.
        private func appendText(_ text: String, run: RunProps, attributes: [NSAttributedString.Key: Any]) {
            guard !text.isEmpty else { return }
            guard let eastAsia = run.eastAsiaFont, eastAsia != run.font,
                  let cjkFont = WordReader.systemFont(for: eastAsia, size: (attributes[.font] as? NSFont)?.pointSize ?? 12) else {
                output.append(NSAttributedString(string: text, attributes: attributes))
                return
            }
            var segment = ""
            var segmentIsCJK = false
            func flush() {
                guard !segment.isEmpty else { return }
                var attrs = attributes
                if segmentIsCJK { attrs[.font] = WordReader.styled(cjkFont, run) }
                output.append(NSAttributedString(string: segment, attributes: attrs))
                segment = ""
            }
            for character in text {
                let cjk = character.unicodeScalars.contains { WordReader.isCJK($0) }
                if cjk != segmentIsCJK { flush(); segmentIsCJK = cjk }
                segment.append(character)
            }
            flush()
        }

        func attributes(_ run: RunProps, paragraph: NSParagraphStyle) -> [NSAttributedString.Key: Any] {
            var size = run.size ?? 10
            if run.vertAlign == "superscript" || run.vertAlign == "subscript" { size *= 0.65 }
            let base = WordReader.systemFont(for: run.font, size: size) ?? NSFont.systemFont(ofSize: size)
            var attrs: [NSAttributedString.Key: Any] = [
                .font: WordReader.styled(base, run),
                .paragraphStyle: paragraph,
                .foregroundColor: (run.color ?? nil) ?? NSColor.black,
            ]
            if run.underline == true { attrs[.underlineStyle] = NSUnderlineStyle.single.rawValue }
            if run.strike == true { attrs[.strikethroughStyle] = NSUnderlineStyle.single.rawValue }
            if let highlight = run.highlight ?? nil { attrs[.backgroundColor] = highlight }
            if run.vertAlign == "superscript" { attrs[.baselineOffset] = size * 0.5 }
            if run.vertAlign == "subscript" { attrs[.baselineOffset] = -size * 0.25 }
            return attrs
        }

        // MARK: Pictures

        private func picture(_ element: XMLElement) -> NSAttributedString? {
            let blipID = element.descendant("blip")?.attr("embed") ?? element.descendant("imagedata")?.relationshipID
            guard let id = blipID, let path = links[id]?.target,
                  let data = archive.data(at: path), let image = NSImage(data: data) else { return nil }
            var size = image.size
            if let extent = element.descendant("extent"),
               let cx = extent.attr("cx").flatMap(Double.init), let cy = extent.attr("cy").flatMap(Double.init), cx > 0, cy > 0 {
                size = NSSize(width: cx / 12_700, height: cy / 12_700)
            }
            let maxWidth = page.contentWidth
            if size.width > maxWidth { size = NSSize(width: maxWidth, height: size.height * maxWidth / size.width) }
            let attachment = NSTextAttachment()
            attachment.image = image
            attachment.bounds = CGRect(origin: .zero, size: size)
            return NSAttributedString(attachment: attachment)
        }

        // MARK: Tables

        private func table(_ tbl: XMLElement, outer: NSTextTableBlock?) {
            let columns = (tbl.child("tblGrid")?.children("gridCol") ?? []).compactMap { WordReader.twips($0.attr("w")) }
            let tblPr = tbl.child("tblPr")
            let styleName = tblPr?.child("tblStyle")?.attr("val").flatMap { styles[$0]?.name } ?? ""
            let tableBorders = tblPr?.child("tblBorders")
            let styleBorders = tblPr?.child("tblStyle")?.attr("val").flatMap { id in
                OfficePackage.xml(archive, "word/styles.xml")?.children("style")
                    .first { $0.attr("styleId") == id }?.child("tblPr")?.child("tblBorders")
            }
            func hasBorders(_ borders: XMLElement?) -> Bool {
                guard let borders else { return false }
                return (borders.children ?? []).compactMap { $0 as? XMLElement }.contains {
                    let value = $0.attr("val") ?? "none"
                    return value != "none" && value != "nil"
                }
            }
            let bordered = hasBorders(tableBorders) || hasBorders(styleBorders) || styleName.contains("grid")

            let table = NSTextTable()
            table.numberOfColumns = max(1, columns.count)
            table.layoutAlgorithm = .fixedLayoutAlgorithm
            table.collapsesBorders = true
            table.hidesEmptyCells = false

            for (rowIndex, tr) in tbl.children("tr").enumerated() {
                var column = 0
                for tc in tr.children("tc") {
                    let tcPr = tc.child("tcPr")
                    let span = tcPr?.child("gridSpan")?.attr("val").flatMap(Int.init) ?? 1
                    let block = NSTextTableBlock(table: table, startingRow: rowIndex, rowSpan: 1,
                                                 startingColumn: min(column, table.numberOfColumns - 1),
                                                 columnSpan: max(1, min(span, table.numberOfColumns - column)))
                    let width = (column..<min(column + span, columns.count)).reduce(CGFloat(0)) { $0 + columns[$1] }
                    if width > 0 { block.setValue(width, type: .absoluteValueType, for: .width) }
                    block.setWidth(5.4, type: .absoluteValueType, for: .padding, edge: .minX)
                    block.setWidth(5.4, type: .absoluteValueType, for: .padding, edge: .maxX)
                    block.setWidth(1, type: .absoluteValueType, for: .padding, edge: .minY)
                    block.setWidth(1, type: .absoluteValueType, for: .padding, edge: .maxY)
                    if bordered {
                        block.setWidth(0.5, type: .absoluteValueType, for: .border)
                        block.setBorderColor(.black)
                    }
                    if let fill = tcPr?.child("shd")?.attr("fill"), fill != "auto", let color = PresentationConverter.hex(fill) {
                        block.backgroundColor = color
                    }
                    if let vAlign = tcPr?.child("vAlign")?.attr("val") {
                        block.verticalAlignment = vAlign == "center" ? .middleAlignment : vAlign == "bottom" ? .bottomAlignment : .topAlignment
                    }
                    let before = output.length
                    previous = nil
                    blocks(in: tc, cell: block)
                    if output.length == before {
                        let style = NSMutableParagraphStyle(); style.textBlocks = [block]
                        output.append(NSAttributedString(string: "\n", attributes: [.paragraphStyle: style, .font: NSFont.systemFont(ofSize: 10)]))
                    }
                    column += span
                }
            }
            previous = nil
            _ = outer
        }
    }

    // MARK: - Small helpers

    static func styled(_ font: NSFont, _ run: RunProps) -> NSFont {
        var result = font
        if run.bold == true { result = NSFontManager.shared.convert(result, toHaveTrait: .boldFontMask) }
        if run.italic == true { result = NSFontManager.shared.convert(result, toHaveTrait: .italicFontMask) }
        return result
    }

    static func isCJK(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x2E80...0x9FFF, 0xAC00...0xD7AF, 0xF900...0xFAFF, 0xFF00...0xFFEF, 0x20000...0x2FA1F: return true
        default: return false
        }
    }

    /// Word stores bullets as private-use characters from the Symbol and Wingdings fonts.
    static func bullet(_ text: String) -> String {
        switch text {
        case "\u{F0B7}", "\u{F06C}", "\u{F09F}", "": return "•"
        case "\u{F0A7}", "\u{F06E}", "\u{F0FC}": return "▪"
        case "o": return "◦"
        case "\u{F0D8}": return "➢"
        case "\u{F076}": return "❖"
        default: return text
        }
    }

    static func format(_ value: Int, _ format: String) -> String {
        switch format {
        case "lowerLetter": return letters(value).lowercased()
        case "upperLetter": return letters(value)
        case "lowerRoman": return roman(value).lowercased()
        case "upperRoman": return roman(value)
        case "decimalZero": return value < 10 ? "0\(value)" : "\(value)"
        case "chineseCounting", "chineseCountingThousand", "ideographTraditional", "chineseLegalSimplified",
             "ideographDigital", "japaneseCounting", "taiwaneseCounting":
            return chinese(value)
        case "decimalEnclosedCircle", "decimalEnclosedCircleChinese":
            return value >= 1 && value <= 20 ? String(UnicodeScalar(0x2460 + value - 1)!) : "\(value)"
        default: return "\(value)"
        }
    }

    private static func letters(_ value: Int) -> String {
        guard value > 0 else { return "\(value)" }
        let letter = String(UnicodeScalar(65 + (value - 1) % 26)!)
        return String(repeating: letter, count: (value - 1) / 26 + 1)
    }

    private static func roman(_ value: Int) -> String {
        guard value > 0, value < 4000 else { return "\(value)" }
        let table: [(Int, String)] = [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
                                       (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")]
        var number = value, result = ""
        for (amount, symbol) in table { while number >= amount { result += symbol; number -= amount } }
        return result
    }

    private static func chinese(_ value: Int) -> String {
        let digits = ["零", "一", "二", "三", "四", "五", "六", "七", "八", "九"]
        guard value > 0, value < 100 else { return "\(value)" }
        if value < 10 { return digits[value] }
        let tens = value / 10, ones = value % 10
        return (tens == 1 ? "" : digits[tens]) + "十" + (ones == 0 ? "" : digits[ones])
    }
}
