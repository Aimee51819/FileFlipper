import Foundation

/// Word (.docx) → Markdown, read straight from the document XML so that heading styles,
/// bulleted and numbered lists, bold, italic, links and tables survive. (The Cocoa text
/// system's DOCX import drops paragraph styles, so headings and lists would be lost.)
enum WordMarkdown {
    static func markdown(_ url: URL) throws -> String {
        let archive: ZipArchive
        do { archive = try ZipArchive(url: url) } catch { throw ConversionError.unreadable(url) }
        guard let document = OfficePackage.xml(archive, "word/document.xml"),
              let body = document.child("body") else { throw ConversionError.unreadable(url) }
        let context = Context(archive: archive)
        var blocks: [String] = []
        context.blocks(in: body, into: &blocks)

        var output = ""
        for (index, block) in blocks.enumerated() {
            if index > 0 { output += isListItem(blocks[index - 1]) && isListItem(block) ? "\n" : "\n\n" }
            output += block
        }
        return output + "\n"
    }

    private static func isListItem(_ block: String) -> Bool {
        let trimmed = block.trimmingCharacters(in: .whitespaces)
        return trimmed.hasPrefix("- ") || trimmed.range(of: #"^\d+\. "#, options: .regularExpression) != nil
    }

    private struct Style {
        var name = ""
        var basedOn: String?
        var numID: String?
        var level: Int?
        var outlineLevel: Int?
        var bold = false
        var italic = false
    }

    private final class Context {
        let links: [String: (target: String, type: String)]
        var styles: [String: Style] = [:]
        /// numId → (level → is ordered list)
        var numbering: [String: [Int: Bool]] = [:]
        /// Running counters for numbered lists, per numId and level.
        var counters: [String: [Int: Int]] = [:]

        init(archive: ZipArchive) {
            links = OfficePackage.relationships(archive, for: "word/document.xml")
            if let root = OfficePackage.xml(archive, "word/styles.xml") {
                for element in root.children("style") {
                    guard let id = element.attr("styleId") else { continue }
                    var style = Style()
                    style.name = (element.child("name")?.attr("val") ?? id).lowercased()
                    style.basedOn = element.child("basedOn")?.attr("val")
                    let pPr = element.child("pPr")
                    style.numID = pPr?.child("numPr")?.child("numId")?.attr("val")
                    style.level = pPr?.child("numPr")?.child("ilvl")?.attr("val").flatMap(Int.init)
                    style.outlineLevel = pPr?.child("outlineLvl")?.attr("val").flatMap(Int.init)
                    style.bold = Context.isOn(element.child("rPr")?.child("b"))
                    style.italic = Context.isOn(element.child("rPr")?.child("i"))
                    styles[id] = style
                }
            }
            if let root = OfficePackage.xml(archive, "word/numbering.xml") {
                var abstract: [String: [Int: Bool]] = [:]
                for element in root.children("abstractNum") {
                    guard let id = element.attr("abstractNumId") else { continue }
                    var levels: [Int: Bool] = [:]
                    for level in element.children("lvl") {
                        let index = level.attr("ilvl").flatMap(Int.init) ?? 0
                        let format = level.child("numFmt")?.attr("val") ?? "bullet"
                        levels[index] = format != "bullet" && format != "none"
                    }
                    abstract[id] = levels
                }
                for element in root.children("num") {
                    guard let id = element.attr("numId"), let abstractID = element.child("abstractNumId")?.attr("val") else { continue }
                    numbering[id] = abstract[abstractID]
                }
            }
        }

        static func isOn(_ element: XMLElement?) -> Bool {
            guard let element else { return false }
            let value = element.attr("val")
            return value == nil || !["0", "false", "off"].contains(value!)
        }

        /// Follows `basedOn` so inherited heading levels and lists are found.
        func resolvedStyle(_ id: String?) -> Style {
            var result = Style()
            var chain: [Style] = []
            var current = id
            var guardCount = 0
            while let key = current, let style = styles[key], guardCount < 10 {
                chain.append(style); current = style.basedOn; guardCount += 1
            }
            result.name = chain.first?.name ?? ""
            for style in chain.reversed() {
                if style.numID != nil { result.numID = style.numID }
                if style.level != nil { result.level = style.level }
                if style.outlineLevel != nil { result.outlineLevel = style.outlineLevel }
                result.bold = result.bold || style.bold
                result.italic = result.italic || style.italic
            }
            return result
        }

        func blocks(in container: XMLElement, into blocks: inout [String]) {
            for case let element as XMLElement in container.children ?? [] {
                switch element.localName {
                case "p":
                    if let block = paragraph(element) { blocks.append(block) }
                case "tbl":
                    let rows = element.children("tr").map { row in
                        row.children("tc").map { cell in
                            cell.children("p").map { inline($0, style: Style()) }.joined(separator: " ")
                                .replacingOccurrences(of: "|", with: "\\|")
                                .trimmingCharacters(in: .whitespaces)
                        }
                    }
                    if !rows.isEmpty { blocks.append(MarkdownWriter.table(rows)) }
                case "sdt":
                    if let content = element.child("sdtContent") { self.blocks(in: content, into: &blocks) }
                default:
                    continue
                }
            }
        }

        private func paragraph(_ p: XMLElement) -> String? {
            let pPr = p.child("pPr")
            let style = resolvedStyle(pPr?.child("pStyle")?.attr("val"))
            let text = inline(p, style: style).trimmingCharacters(in: .whitespaces)
            guard !text.isEmpty else { return nil }

            // Headings: Title, "heading N", or an outline level.
            var heading: Int?
            if style.name == "title" { heading = 1 }
            else if style.name.hasPrefix("heading "), let n = Int(style.name.dropFirst(8)) { heading = n }
            else if let outline = pPr?.child("outlineLvl")?.attr("val").flatMap(Int.init) ?? style.outlineLevel, outline < 9 {
                heading = outline + 1
            }
            if let heading {
                let plain = plainText(p)
                return String(repeating: "#", count: min(6, max(1, heading))) + " " + MarkdownWriter.escape(plain)
            }

            // Lists: numbering on the paragraph, or from its style.
            let numID = pPr?.child("numPr")?.child("numId")?.attr("val") ?? style.numID
            if let numID, numID != "0" {
                let styleLevel = style.name.split(separator: " ").last.flatMap { Int($0) }.map { max(0, $0 - 1) }
                let level = pPr?.child("numPr")?.child("ilvl")?.attr("val").flatMap(Int.init) ?? style.level ?? styleLevel ?? 0
                let ordered = numbering[numID]?[level] ?? style.name.contains("number")
                let indent = String(repeating: "   ", count: level)
                if ordered {
                    var levels = counters[numID] ?? [:]
                    levels[level, default: 0] += 1
                    for deeper in levels.keys where deeper > level { levels[deeper] = 0 }
                    counters[numID] = levels
                    return indent + "\(levels[level]!). " + text
                }
                return indent + "- " + text
            }
            // "List Bullet 2" and friends: the trailing number is the nesting level.
            let styleLevel = style.name.split(separator: " ").last.flatMap { Int($0) }.map { max(0, $0 - 1) } ?? 0
            let styleIndent = String(repeating: "   ", count: styleLevel)
            if style.name.contains("list bullet") { return styleIndent + "- " + text }
            if style.name.contains("list number") { return styleIndent + "1. " + text }
            if style.name == "quote" || style.name == "intense quote" { return "> " + text }
            return text
        }

        private func plainText(_ p: XMLElement) -> String {
            p.descendants("t").map { $0.stringValue ?? "" }.joined().trimmingCharacters(in: .whitespaces)
        }

        /// Runs → Markdown, merging neighbouring runs with the same formatting.
        func inline(_ p: XMLElement, style: Style) -> String {
            struct Piece { var text: String; var bold: Bool; var italic: Bool; var link: String? }
            var pieces: [Piece] = []
            func collect(_ container: XMLElement, link: String?) {
                for case let node as XMLElement in container.children ?? [] {
                    switch node.localName {
                    case "r":
                        let rPr = node.child("rPr")
                        let bold = rPr?.child("b").map { Context.isOn($0) } ?? style.bold
                        let italic = rPr?.child("i").map { Context.isOn($0) } ?? style.italic
                        var text = ""
                        for case let part as XMLElement in node.children ?? [] {
                            switch part.localName {
                            case "t": text += part.stringValue ?? ""
                            case "tab": text += " "
                            case "br", "cr": text += " "
                            default: break
                            }
                        }
                        if !text.isEmpty { pieces.append(Piece(text: text, bold: bold, italic: italic, link: link)) }
                    case "hyperlink":
                        let target = node.relationshipID.flatMap { links[$0]?.target }
                            ?? node.attr("anchor").map { "#" + $0 }
                        collect(node, link: target)
                    case "smartTag", "ins", "fldSimple":
                        collect(node, link: link)
                    default:
                        continue
                    }
                }
            }
            collect(p, link: nil)

            var merged: [Piece] = []
            for piece in pieces {
                if var last = merged.last, last.bold == piece.bold, last.italic == piece.italic, last.link == piece.link {
                    last.text += piece.text
                    merged[merged.count - 1] = last
                } else {
                    merged.append(piece)
                }
            }
            return merged.map { piece -> String in
                let escaped = MarkdownWriter.escape(piece.text)
                let core = escaped.trimmingCharacters(in: .whitespaces)
                guard !core.isEmpty else { return escaped }
                var wrapped = core
                if piece.italic { wrapped = "*\(wrapped)*" }
                if piece.bold { wrapped = "**\(wrapped)**" }
                if let link = piece.link { wrapped = "[\(wrapped)](\(link))" }
                return escaped.replacingOccurrences(of: core, with: wrapped)
            }.joined()
        }
    }
}
