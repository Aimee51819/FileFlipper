import AppKit

/// Excel (.xlsx) → PDF or Markdown. Reads the cell values of every sheet (text, numbers,
/// dates, percentages) and lays them out as tables; formulas show their last calculated value.
enum SpreadsheetConverter {
    struct Sheet {
        let name: String
        let rows: [[String]]
        /// Columns whose cells are mostly numbers are right-aligned.
        let numericColumns: Set<Int>
    }

    // MARK: Convert

    static func toPDF(_ url: URL) throws -> URL {
        let sheets = try read(url)
        let output = OutputNaming.next(to: url, ext: "pdf")
        try renderPDF(sheets, to: output)
        return output
    }

    static func toMarkdown(_ url: URL) throws -> URL {
        let sheets = try read(url)
        var parts: [String] = []
        for sheet in sheets {
            let rows = sheet.rows.map { $0.map { MarkdownWriter.escape($0).replacingOccurrences(of: "|", with: "\\|")
                .replacingOccurrences(of: "\n", with: " ") } }
            parts.append("## " + MarkdownWriter.escape(sheet.name) + "\n\n" + MarkdownWriter.table(rows))
        }
        let output = OutputNaming.next(to: url, ext: "md")
        try (parts.joined(separator: "\n\n") + "\n").write(to: output, atomically: true, encoding: .utf8)
        return output
    }

    // MARK: Reading

    static func read(_ url: URL) throws -> [Sheet] {
        let archive: ZipArchive
        do { archive = try ZipArchive(url: url) } catch { throw ConversionError.unreadable(url) }
        guard let workbook = OfficePackage.xml(archive, "xl/workbook.xml") else { throw ConversionError.unreadable(url) }
        let relationships = OfficePackage.relationships(archive, for: "xl/workbook.xml")
        let shared = sharedStrings(archive)
        let formats = cellFormats(archive)
        let date1904 = workbook.child("workbookPr")?.attr("date1904") == "1"

        var sheets: [Sheet] = []
        for sheet in workbook.child("sheets")?.children("sheet") ?? [] {
            if sheet.attr("state") == "hidden" || sheet.attr("state") == "veryHidden" { continue }
            guard let id = sheet.relationshipID, let path = relationships[id]?.target,
                  let root = OfficePackage.xml(archive, path) else { continue }
            let name = sheet.attr("name") ?? "Sheet"
            var grid: [Int: [Int: (String, Bool)]] = [:]
            for row in root.child("sheetData")?.children("row") ?? [] {
                if row.attr("hidden") == "1" { continue }
                for cell in row.children("c") {
                    guard let reference = cell.attr("r"), let (column, rowIndex) = position(reference) else { continue }
                    let (text, numeric) = value(of: cell, shared: shared, formats: formats, date1904: date1904)
                    if !text.isEmpty { grid[rowIndex, default: [:]][column] = (text, numeric) }
                }
            }
            guard let lastRow = grid.keys.max(), let firstRow = grid.keys.min() else { continue }
            let columns = grid.values.flatMap(\.keys)
            let firstColumn = columns.min() ?? 0, lastColumn = columns.max() ?? 0
            var rows: [[String]] = []
            var numericCounts = [Int: Int](), totalCounts = [Int: Int]()
            for r in firstRow...lastRow {
                var line: [String] = []
                for c in firstColumn...lastColumn {
                    let entry = grid[r]?[c]
                    line.append(entry?.0 ?? "")
                    if let entry, r != firstRow {
                        totalCounts[c - firstColumn, default: 0] += 1
                        if entry.1 { numericCounts[c - firstColumn, default: 0] += 1 }
                    }
                }
                rows.append(line)
            }
            let numeric = Set(totalCounts.compactMap { column, total in
                (numericCounts[column] ?? 0) * 2 > total ? column : nil
            })
            sheets.append(Sheet(name: name, rows: rows, numericColumns: numeric))
        }
        guard !sheets.isEmpty else { throw ConversionError.message(L("%@ has no data", url.lastPathComponent)) }
        return sheets
    }

    private static func sharedStrings(_ archive: ZipArchive) -> [String] {
        guard let root = OfficePackage.xml(archive, "xl/sharedStrings.xml") else { return [] }
        return root.children("si").map { item in
            if let direct = item.child("t") { return direct.stringValue ?? "" }
            return item.children("r").map { $0.child("t")?.stringValue ?? "" }.joined()
        }
    }

    private enum NumberStyle { case general, date(time: Bool), time, percent(decimals: Int), fixed(decimals: Int, grouping: Bool) }

    /// Number style for each cell style index (`s` attribute).
    private static func cellFormats(_ archive: ZipArchive) -> [NumberStyle] {
        guard let root = OfficePackage.xml(archive, "xl/styles.xml") else { return [] }
        var custom: [Int: String] = [:]
        for format in root.child("numFmts")?.children("numFmt") ?? [] {
            if let id = format.attr("numFmtId").flatMap(Int.init) { custom[id] = format.attr("formatCode") ?? "" }
        }
        return (root.child("cellXfs")?.children("xf") ?? []).map { xf in
            let id = xf.attr("numFmtId").flatMap(Int.init) ?? 0
            switch id {
            case 14...17: return .date(time: false)
            case 22: return .date(time: true)
            case 18...21, 45...47: return .time
            case 9: return .percent(decimals: 0)
            case 10: return .percent(decimals: 2)
            case 1: return .fixed(decimals: 0, grouping: false)
            case 2: return .fixed(decimals: 2, grouping: false)
            case 3: return .fixed(decimals: 0, grouping: true)
            case 4: return .fixed(decimals: 2, grouping: true)
            default: break
            }
            guard let code = custom[id] else { return .general }
            // Ignore quoted literals and colour/condition sections when sniffing the format.
            let bare = code.replacingOccurrences(of: #""[^"]*""#, with: "", options: .regularExpression)
                .replacingOccurrences(of: #"\[[^\]]*\]"#, with: "", options: .regularExpression).lowercased()
            if bare.contains("y") || bare.contains("d") || (bare.contains("m") && !bare.contains("0")) {
                return .date(time: bare.contains("h"))
            }
            if bare.contains("h") || bare.contains("s") { return .time }
            let decimals = bare.range(of: #"\.(0+)"#, options: .regularExpression).map { bare[$0].count - 1 } ?? 0
            if bare.contains("%") { return .percent(decimals: decimals) }
            if bare.contains("0") || bare.contains("#") { return .fixed(decimals: decimals, grouping: bare.contains(",")) }
            return .general
        }
    }

    private static func value(of cell: XMLElement, shared: [String], formats: [NumberStyle],
                              date1904: Bool) -> (String, Bool) {
        let raw = cell.child("v")?.stringValue ?? ""
        switch cell.attr("t") {
        case "s":
            return (Int(raw).flatMap { shared.indices.contains($0) ? shared[$0] : nil } ?? "", false)
        case "inlineStr":
            let item = cell.child("is")
            let text = item?.child("t")?.stringValue ?? item?.children("r").map { $0.child("t")?.stringValue ?? "" }.joined() ?? ""
            return (text, false)
        case "b":
            return (raw == "1" ? "TRUE" : "FALSE", false)
        case "str", "e":
            return (raw, false)
        default:
            guard let number = Double(raw) else { return (raw, false) }
            let style = cell.attr("s").flatMap(Int.init).flatMap { formats.indices.contains($0) ? formats[$0] : nil } ?? .general
            return (format(number, style, date1904: date1904), true)
        }
    }

    private static func format(_ number: Double, _ style: NumberStyle, date1904: Bool) -> String {
        switch style {
        case .date(let time):
            let base = date1904 ? 24_107.0 : 25_569.0   // days from the Excel epoch to 1970-01-01
            let date = Date(timeIntervalSince1970: (number - base) * 86_400)
            let formatter = DateFormatter()
            formatter.timeZone = TimeZone(identifier: "UTC")
            formatter.dateFormat = time ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd"
            return formatter.string(from: date)
        case .time:
            let seconds = Int((number.truncatingRemainder(dividingBy: 1) * 86_400).rounded())
            return String(format: "%02d:%02d", seconds / 3600, (seconds % 3600) / 60)
        case .percent(let decimals):
            return String(format: "%.\(decimals)f%%", number * 100)
        case .fixed(let decimals, let grouping):
            let formatter = NumberFormatter()
            formatter.numberStyle = .decimal
            formatter.usesGroupingSeparator = grouping
            formatter.minimumFractionDigits = decimals
            formatter.maximumFractionDigits = decimals
            formatter.locale = Locale(identifier: "en_US_POSIX")
            return formatter.string(from: NSNumber(value: number)) ?? "\(number)"
        case .general:
            if number == number.rounded(), abs(number) < 1e15 { return String(Int64(number)) }
            let formatter = NumberFormatter()
            formatter.numberStyle = .decimal
            formatter.usesGroupingSeparator = false
            formatter.maximumSignificantDigits = 11
            formatter.usesSignificantDigits = true
            formatter.locale = Locale(identifier: "en_US_POSIX")
            return formatter.string(from: NSNumber(value: number)) ?? "\(number)"
        }
    }

    /// "C12" → (column 2, row 11), zero-based.
    private static func position(_ reference: String) -> (Int, Int)? {
        var column = 0
        var digits = ""
        for character in reference.uppercased() {
            if let ascii = character.asciiValue, character.isLetter {
                column = column * 26 + Int(ascii - 64)
            } else if character.isNumber {
                digits.append(character)
            }
        }
        guard column > 0, let row = Int(digits), row > 0 else { return nil }
        return (column - 1, row - 1)
    }

    // MARK: PDF

    private static let bodyFont = NSFont.systemFont(ofSize: 9)
    private static let headerFont = NSFont.boldSystemFont(ofSize: 9)

    private static func renderPDF(_ sheets: [Sheet], to output: URL) throws {
        var mediaBox = CGRect(x: 0, y: 0, width: 612, height: 792)
        guard let context = CGContext(output as CFURL, mediaBox: &mediaBox, nil) else {
            throw ConversionError.writeFailed(output)
        }
        let margin: CGFloat = 36
        let rowHeight: CGFloat = 16
        let padding: CGFloat = 5

        for sheet in sheets {
            let columnCount = sheet.rows.map(\.count).max() ?? 0
            guard columnCount > 0 else { continue }
            // Natural column widths, capped so one long cell can't take over the page.
            var widths = [CGFloat](repeating: 36, count: columnCount)
            for (rowIndex, row) in sheet.rows.enumerated() {
                for (column, text) in row.enumerated() {
                    let font = rowIndex == 0 ? headerFont : bodyFont
                    let width = ceil((text as NSString).size(withAttributes: [.font: font]).width) + padding * 2 + 6
                    widths[column] = min(max(widths[column], width), 220)
                }
            }
            let total = widths.reduce(0, +)
            // Portrait if it fits, otherwise landscape; then shrink a little, then split columns.
            let landscape = total > 612 - margin * 2
            let page = landscape ? CGSize(width: 792, height: 612) : CGSize(width: 612, height: 792)
            let usable = page.width - margin * 2
            let scale = max(0.7, min(1, usable / total))
            var chunks: [Range<Int>] = []
            var start = 0, running: CGFloat = 0
            for column in 0..<columnCount {
                if running + widths[column] * scale > usable, column > start {
                    chunks.append(start..<column); start = column; running = 0
                }
                running += widths[column] * scale
            }
            chunks.append(start..<columnCount)

            let header = sheet.rows.first ?? []
            let body = Array(sheet.rows.dropFirst())
            let titleHeight: CGFloat = 26
            let rowsPerPage = max(1, Int((page.height - margin * 2 - titleHeight) / (rowHeight * scale)) - 1)

            for (chunkIndex, chunk) in chunks.enumerated() {
                var first = 0
                repeat {
                    var box = CGRect(origin: .zero, size: page)
                    context.beginPage(mediaBox: &box)
                    context.translateBy(x: 0, y: page.height)
                    context.scaleBy(x: 1, y: -1)
                    NSGraphicsContext.saveGraphicsState()
                    NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: true)

                    var title = sheet.name
                    if chunks.count > 1 { title += "  (columns \(columnName(chunk.lowerBound))–\(columnName(chunk.upperBound - 1)))" }
                    NSAttributedString(string: title, attributes: [
                        .font: NSFont.boldSystemFont(ofSize: 13), .foregroundColor: Palette.brown,
                    ]).draw(at: NSPoint(x: margin, y: margin))

                    let last = min(body.count, first + rowsPerPage)
                    var y = margin + titleHeight
                    drawRow(header, chunk: chunk, widths: widths, scale: scale, y: y, height: rowHeight,
                            isHeader: true, numeric: sheet.numericColumns, margin: margin, padding: padding)
                    y += rowHeight * scale
                    for index in first..<last {
                        drawRow(body[index], chunk: chunk, widths: widths, scale: scale, y: y, height: rowHeight,
                                isHeader: false, numeric: sheet.numericColumns, margin: margin, padding: padding,
                                striped: index % 2 == 1)
                        y += rowHeight * scale
                    }
                    NSGraphicsContext.restoreGraphicsState()
                    context.endPage()
                    first = last
                } while first < body.count
                _ = chunkIndex
            }
        }
        context.closePDF()
    }

    private static func drawRow(_ row: [String], chunk: Range<Int>, widths: [CGFloat], scale: CGFloat, y: CGFloat,
                                height: CGFloat, isHeader: Bool, numeric: Set<Int>, margin: CGFloat,
                                padding: CGFloat, striped: Bool = false) {
        var x = margin
        let h = height * scale
        for column in chunk {
            let w = widths[column] * scale
            let cell = NSRect(x: x, y: y, width: w, height: h)
            if isHeader {
                Palette.peach.withAlphaComponent(0.6).setFill(); cell.fill()
            } else if striped {
                NSColor(white: 0.97, alpha: 1).setFill(); cell.fill()
            }
            NSColor(white: 0.82, alpha: 1).setStroke()
            let border = NSBezierPath(rect: cell); border.lineWidth = 0.5; border.stroke()

            let text = column < row.count ? row[column] : ""
            if !text.isEmpty {
                let paragraph = NSMutableParagraphStyle()
                paragraph.lineBreakMode = .byTruncatingTail
                paragraph.alignment = !isHeader && numeric.contains(column) ? .right : .left
                let font = (isHeader ? headerFont : bodyFont).withSize(9 * scale)
                let attributed = NSAttributedString(string: text.replacingOccurrences(of: "\n", with: " "), attributes: [
                    .font: font, .foregroundColor: NSColor(white: 0.1, alpha: 1), .paragraphStyle: paragraph,
                ])
                let textHeight = font.ascender - font.descender
                attributed.draw(with: NSRect(x: x + padding * scale, y: y + (h - textHeight) / 2,
                                             width: w - padding * 2 * scale, height: textHeight),
                                options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine])
            }
            x += w
        }
    }

    private static func columnName(_ index: Int) -> String {
        var index = index + 1
        var name = ""
        while index > 0 {
            let remainder = (index - 1) % 26
            name = String(UnicodeScalar(65 + remainder)!) + name
            index = (index - 1) / 26
        }
        return name
    }
}
