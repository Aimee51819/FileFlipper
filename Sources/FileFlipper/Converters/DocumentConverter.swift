import AppKit
import UniformTypeIdentifiers

struct DocumentTarget: FormatTarget {
    let title: String
    let ext: String
    /// `nil` means PDF, which is produced by printing instead of an export.
    let documentType: NSAttributedString.DocumentType?
    var aliases: [String] = []
}

/// Text document conversions, using the same Cocoa text system as TextEdit.
/// Must run on the main thread (HTML import and printing require it).
enum DocumentConverter {
    static let targets: [DocumentTarget] = [
        DocumentTarget(title: "DOCX", ext: "docx", documentType: .officeOpenXML),
        DocumentTarget(title: "PDF", ext: "pdf", documentType: nil),
        DocumentTarget(title: "RTF", ext: "rtf", documentType: .rtf),
        DocumentTarget(title: "MD", ext: "md", documentType: .plain, aliases: ["markdown"]),
        DocumentTarget(title: "TXT", ext: "txt", documentType: .plain, aliases: ["text"]),
        DocumentTarget(title: "HTML", ext: "html", documentType: .html, aliases: ["htm"]),
        DocumentTarget(title: "ODT", ext: "odt", documentType: .openDocument),
        DocumentTarget(title: "DOC", ext: "doc", documentType: .docFormat),
    ]

    static let readableTypes: [UTType] = ([
        UTType.rtf, UTType.rtfd, UTType.flatRTFD, UTType.plainText, UTType.html,
        UTType("org.openxmlformats.wordprocessingml.document"),
        UTType("com.microsoft.word.doc"),
        UTType("org.oasis-open.opendocument.text"),
    ] as [UTType?]).compactMap { $0 }

    /// Reads a document together with its page setup (when the format has one).
    static func readDocument(_ url: URL) throws -> (text: NSAttributedString, page: WordReader.PageSetup?) {
        // Word files: our own reader keeps styles, lists, tables, pictures and the page setup.
        if url.pathExtension.lowercased() == "docx", let document = try? WordReader.read(url) {
            return (document.text, document.page)
        }
        var attributes: NSDictionary?
        let text: NSAttributedString
        do {
            text = try NSAttributedString(url: url, options: [:], documentAttributes: &attributes)
        } catch {
            throw ConversionError.unreadable(url)
        }
        // RTF and some other formats carry their own paper size and margins.
        var page: WordReader.PageSetup?
        if let attributes = attributes as? [NSAttributedString.DocumentAttributeKey: Any],
           let size = attributes[.paperSize] as? NSSize, size.width > 0, size.height > 0 {
            var setup = WordReader.PageSetup()
            setup.size = size
            func margin(_ key: NSAttributedString.DocumentAttributeKey) -> CGFloat {
                (attributes[key] as? NSNumber).map { CGFloat($0.doubleValue) } ?? 72
            }
            setup.margins = NSEdgeInsets(top: margin(.topMargin), left: margin(.leftMargin),
                                         bottom: margin(.bottomMargin), right: margin(.rightMargin))
            page = setup
        }
        return (text, page)
    }

    static func read(_ url: URL) throws -> NSAttributedString {
        try readDocument(url).text
    }

    static func convert(_ url: URL, to target: DocumentTarget) throws -> URL {
        // Word files keep their headings and lists only when read from the XML directly.
        if target.ext == "md", url.pathExtension.lowercased() == "docx" {
            let markdown = try WordMarkdown.markdown(url)
            let output = OutputNaming.next(to: url, ext: "md")
            try markdown.write(to: output, atomically: true, encoding: .utf8)
            return output
        }
        let document = try readDocument(url)
        let output = OutputNaming.next(to: url, ext: target.ext)
        try write(document.text, to: output, target: target, page: document.page)
        return output
    }

    static func stripFormatting(_ url: URL) throws -> URL {
        let text = try read(url)
        let output = OutputNaming.next(to: url, ext: "txt")
        try text.string.write(to: output, atomically: true, encoding: .utf8)
        return output
    }

    static func write(_ text: NSAttributedString, to output: URL, target: DocumentTarget,
                      page: WordReader.PageSetup? = nil) throws {
        if target.ext == "md" {
            try MarkdownWriter.markdown(from: text).write(to: output, atomically: true, encoding: .utf8)
            return
        }
        guard let documentType = target.documentType else {
            try renderPDF(text, page: page ?? defaultPage, to: output)
            return
        }
        if documentType == .plain {
            try text.string.write(to: output, atomically: true, encoding: .utf8)
            return
        }
        var attributes: [NSAttributedString.DocumentAttributeKey: Any] = [.documentType: documentType]
        if let page {
            attributes[.paperSize] = page.size
            attributes[.topMargin] = page.margins.top
            attributes[.bottomMargin] = page.margins.bottom
            attributes[.leftMargin] = page.margins.left
            attributes[.rightMargin] = page.margins.right
        }
        let data = try text.data(from: NSRange(location: 0, length: text.length), documentAttributes: attributes)
        try data.write(to: output, options: .atomic)
    }

    /// Letter in North America, A4 elsewhere, with 1" (2.54 cm) margins — Word's defaults.
    static var defaultPage: WordReader.PageSetup {
        var page = WordReader.PageSetup()
        let letterRegions: Set<String> = ["US", "CA", "MX", "PH", "CL", "CO", "VE", "PR"]
        if !letterRegions.contains(Locale.current.region?.identifier ?? "US") {
            page.size = NSSize(width: 595.28, height: 841.89)
        }
        return page
    }

    /// Lays the text out page by page (page breaks honoured) and draws each page into a PDF.
    static func renderPDF(_ text: NSAttributedString, page: WordReader.PageSetup, to output: URL) throws {
        let storage = NSTextStorage(attributedString: text)
        let layout = NSLayoutManager()
        layout.allowsNonContiguousLayout = false
        layout.usesFontLeading = true
        storage.addLayoutManager(layout)

        let contentSize = NSSize(width: page.size.width - page.margins.left - page.margins.right,
                                 height: page.size.height - page.margins.top - page.margins.bottom)
        guard contentSize.width > 20, contentSize.height > 20 else { throw ConversionError.writeFailed(output) }
        var containers: [NSTextContainer] = []
        repeat {
            let container = NSTextContainer(size: contentSize)
            container.lineFragmentPadding = 0
            layout.addTextContainer(container)
            containers.append(container)
            _ = layout.glyphRange(for: container)   // lays out this page
        } while NSMaxRange(layout.glyphRange(for: containers.last!)) < layout.numberOfGlyphs && containers.count < 5000
        // A trailing page break can leave an empty last page.
        if containers.count > 1, layout.glyphRange(for: containers.last!).length == 0 { containers.removeLast() }

        var mediaBox = CGRect(origin: .zero, size: page.size)
        guard let context = CGContext(output as CFURL, mediaBox: &mediaBox, nil) else { throw ConversionError.writeFailed(output) }
        for container in containers {
            context.beginPage(mediaBox: &mediaBox)
            context.saveGState()
            context.translateBy(x: 0, y: page.size.height)
            context.scaleBy(x: 1, y: -1)
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: true)
            let range = layout.glyphRange(for: container)
            let origin = NSPoint(x: page.margins.left, y: page.margins.top)
            layout.drawBackground(forGlyphRange: range, at: origin)
            layout.drawGlyphs(forGlyphRange: range, at: origin)
            NSGraphicsContext.restoreGraphicsState()
            context.restoreGState()
            context.endPage()
        }
        context.closePDF()
    }
}
