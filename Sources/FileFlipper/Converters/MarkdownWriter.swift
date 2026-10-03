import AppKit

/// Turns rich text (as read by the Cocoa text system from DOCX, RTF, HTML, ODT…) into Markdown:
/// headings, bold, italic, links, bulleted and numbered lists, and tables.
enum MarkdownWriter {
    static func markdown(from text: NSAttributedString) -> String {
        let string = text.string as NSString
        let bodySize = mostCommonFontSize(in: text)
        var blocks: [String] = []
        var tableRows: [[String]] = []
        var currentTable: NSTextTable?
        var currentRow = -1

        func flushTable() {
            if !tableRows.isEmpty { blocks.append(table(tableRows)) }
            tableRows = []
            currentTable = nil
            currentRow = -1
        }

        var location = 0
        while location < string.length {
            let paragraphRange = string.paragraphRange(for: NSRange(location: location, length: 0))
            location = NSMaxRange(paragraphRange)
            var contentRange = paragraphRange
            while contentRange.length > 0,
                  let scalar = UnicodeScalar(string.character(at: NSMaxRange(contentRange) - 1)),
                  CharacterSet.newlines.contains(scalar) {
                contentRange.length -= 1
            }
            let style = text.attribute(.paragraphStyle, at: paragraphRange.location, effectiveRange: nil) as? NSParagraphStyle

            // Table cells: each cell is its own paragraph carrying an NSTextTableBlock.
            if let block = style?.textBlocks.compactMap({ $0 as? NSTextTableBlock }).last {
                if currentTable !== block.table { flushTable(); currentTable = block.table }
                if block.startingRow != currentRow {
                    tableRows.append([])
                    currentRow = block.startingRow
                }
                let cell = inline(text, contentRange).replacingOccurrences(of: "|", with: "\\|")
                tableRows[tableRows.count - 1].append(cell)
                continue
            }
            flushTable()

            let content = inline(text, contentRange).trimmingCharacters(in: .whitespaces)
            guard !content.isEmpty else { continue }

            if let lists = style?.textLists, let list = lists.last {
                let indent = String(repeating: "  ", count: max(0, lists.count - 1))
                let ordered = list.markerFormat.rawValue.contains("decimal")
                    || list.markerFormat.rawValue.contains("roman")
                    || list.markerFormat.rawValue.contains("alpha")
                let plain = stripListMarker(content)
                blocks.append(indent + (ordered ? "1. " : "- ") + plain)
            } else if let marker = literalListMarker((string.substring(with: contentRange))) {
                // Some importers write the bullet or number into the text instead of a list.
                blocks.append((marker.ordered ? "1. " : "- ") + stripListMarker(content))
            } else if let level = headingLevel(text, contentRange, bodySize: bodySize) {
                let plain = text.attributedSubstring(from: contentRange).string.trimmingCharacters(in: .whitespaces)
                blocks.append(String(repeating: "#", count: level) + " " + escape(plain))
            } else {
                blocks.append(content)
            }
        }
        flushTable()

        // Consecutive list items stay together; everything else is separated by a blank line.
        var output = ""
        for (index, block) in blocks.enumerated() {
            if index > 0 {
                let previousIsList = isListItem(blocks[index - 1])
                output += previousIsList && isListItem(block) ? "\n" : "\n\n"
            }
            output += block
        }
        return output + "\n"
    }

    /// Markdown for plain paragraphs (PDF text, OCR output).
    static func markdown(fromPlainPages pages: [String]) -> String {
        pages.map { page in
            page.components(separatedBy: "\n")
                .map { $0.trimmingCharacters(in: .whitespaces) }
                .split(separator: "", omittingEmptySubsequences: true)
                .map { $0.map(escape).joined(separator: " ") }
                .joined(separator: "\n\n")
        }
        .filter { !$0.isEmpty }
        .joined(separator: "\n\n---\n\n") + "\n"
    }

    static func table(_ rows: [[String]]) -> String {
        let width = rows.map(\.count).max() ?? 0
        guard width > 0 else { return "" }
        let padded = rows.map { $0 + Array(repeating: "", count: width - $0.count) }
        var lines = ["| " + padded[0].joined(separator: " | ") + " |",
                     "|" + Array(repeating: " --- |", count: width).joined()]
        for row in padded.dropFirst() {
            lines.append("| " + row.joined(separator: " | ") + " |")
        }
        return lines.joined(separator: "\n")
    }

    static func escape(_ text: String) -> String {
        var result = ""
        for character in text {
            if "*_`\\".contains(character) { result.append("\\") }
            result.append(character)
        }
        return result
    }

    // MARK: Inline formatting

    private static func inline(_ text: NSAttributedString, _ range: NSRange) -> String {
        var result = ""
        text.enumerateAttributes(in: range, options: []) { attributes, runRange, _ in
            let raw = (text.string as NSString).substring(with: runRange)
                .replacingOccurrences(of: "\u{2028}", with: " ")
                .replacingOccurrences(of: "\t", with: " ")
            guard !raw.isEmpty else { return }
            var piece = escape(raw)
            let font = attributes[.font] as? NSFont
            let traits = font.map { NSFontManager.shared.traits(of: $0) } ?? []
            let core = piece.trimmingCharacters(in: .whitespaces)
            if !core.isEmpty {
                var wrapped = core
                if traits.contains(.italicFontMask) { wrapped = "*\(wrapped)*" }
                if traits.contains(.boldFontMask) { wrapped = "**\(wrapped)**" }
                if let link = attributes[.link] {
                    let target = (link as? URL)?.absoluteString ?? "\(link)"
                    wrapped = "[\(wrapped)](\(target))"
                }
                piece = piece.replacingOccurrences(of: core, with: wrapped)
            }
            result += piece
        }
        // Merge "**a****b**" produced by adjacent bold runs.
        return result.replacingOccurrences(of: "****", with: "")
    }

    private static func headingLevel(_ text: NSAttributedString, _ range: NSRange, bodySize: CGFloat) -> Int? {
        guard range.length > 0, range.length < 200 else { return nil }
        var largest: CGFloat = 0
        var allBold = true
        text.enumerateAttribute(.font, in: range, options: []) { value, runRange, _ in
            guard let font = value as? NSFont else { return }
            let visible = (text.string as NSString).substring(with: runRange).trimmingCharacters(in: .whitespaces)
            guard !visible.isEmpty else { return }
            largest = max(largest, font.pointSize)
            if !NSFontManager.shared.traits(of: font).contains(.boldFontMask) { allBold = false }
        }
        if largest >= bodySize * 1.6 { return 1 }
        if largest >= bodySize * 1.3 { return 2 }
        if largest >= bodySize * 1.1 && allBold { return 3 }
        return nil
    }

    private static func mostCommonFontSize(in text: NSAttributedString) -> CGFloat {
        var counts: [CGFloat: Int] = [:]
        text.enumerateAttribute(.font, in: NSRange(location: 0, length: text.length), options: []) { value, range, _ in
            if let font = value as? NSFont { counts[font.pointSize, default: 0] += range.length }
        }
        return counts.max { $0.value < $1.value }?.key ?? 12
    }

    /// The text system puts the visible marker ("•\t", "1.\t") into the string; drop it.
    private static func stripListMarker(_ line: String) -> String {
        let trimmed = line.trimmingCharacters(in: .whitespaces)
        let markers = ["•", "◦", "▪", "‣", "-", "–", "\\*", "\\-"]
        for marker in markers where trimmed.hasPrefix(marker) {
            return String(trimmed.dropFirst(marker.count)).trimmingCharacters(in: .whitespaces)
        }
        if let match = trimmed.range(of: #"^(\d+\s+|(\d+|[a-zA-Z]|[ivxlcdm]+)[.)]\s*)"#, options: .regularExpression) {
            return String(trimmed[match.upperBound...])
        }
        return trimmed
    }

    private static func literalListMarker(_ line: String) -> (ordered: Bool, Void)? {
        let trimmed = line.trimmingCharacters(in: .whitespaces)
        if trimmed.range(of: #"^[•◦▪‣–-]\t"#, options: .regularExpression) != nil { return (false, ()) }
        if trimmed.range(of: #"^\d+[.)]?\t"#, options: .regularExpression) != nil { return (true, ()) }
        return nil
    }

    private static func isListItem(_ block: String) -> Bool {
        let trimmed = block.trimmingCharacters(in: .whitespaces)
        return trimmed.hasPrefix("- ") || trimmed.hasPrefix("1. ")
    }
}
