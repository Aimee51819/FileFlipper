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

    static func read(_ url: URL) throws -> NSAttributedString {
        do {
            return try NSAttributedString(url: url, options: [:], documentAttributes: nil)
        } catch {
            throw ConversionError.unreadable(url)
        }
    }

    static func convert(_ url: URL, to target: DocumentTarget) throws -> URL {
        // Word files keep their headings and lists only when read from the XML directly.
        if target.ext == "md", url.pathExtension.lowercased() == "docx" {
            let markdown = try WordMarkdown.markdown(url)
            let output = OutputNaming.next(to: url, ext: "md")
            try markdown.write(to: output, atomically: true, encoding: .utf8)
            return output
        }
        let text = try read(url)
        let output = OutputNaming.next(to: url, ext: target.ext)
        try write(text, to: output, target: target)
        return output
    }

    static func stripFormatting(_ url: URL) throws -> URL {
        let text = try read(url)
        let output = OutputNaming.next(to: url, ext: "txt")
        try text.string.write(to: output, atomically: true, encoding: .utf8)
        return output
    }

    static func write(_ text: NSAttributedString, to output: URL, target: DocumentTarget) throws {
        if target.ext == "md" {
            try MarkdownWriter.markdown(from: text).write(to: output, atomically: true, encoding: .utf8)
            return
        }
        guard let documentType = target.documentType else {
            try printPDF(text, to: output)
            return
        }
        if documentType == .plain {
            try text.string.write(to: output, atomically: true, encoding: .utf8)
            return
        }
        let data = try text.data(from: NSRange(location: 0, length: text.length),
                                 documentAttributes: [.documentType: documentType])
        try data.write(to: output, options: .atomic)
    }

    /// Lays the text out on US Letter pages with 1" margins and "prints" it to a PDF file.
    private static func printPDF(_ text: NSAttributedString, to output: URL) throws {
        let paper = NSSize(width: 612, height: 792)
        let margin: CGFloat = 72

        let printInfo = NSPrintInfo()
        printInfo.paperSize = paper
        printInfo.topMargin = margin
        printInfo.bottomMargin = margin
        printInfo.leftMargin = margin
        printInfo.rightMargin = margin
        printInfo.horizontalPagination = .fit
        printInfo.verticalPagination = .automatic
        printInfo.isVerticallyCentered = false
        printInfo.isHorizontallyCentered = false
        printInfo.jobDisposition = .save
        printInfo.dictionary()[NSPrintInfo.AttributeKey.jobSavingURL.rawValue] = output

        let contentWidth = paper.width - margin * 2
        let textView = NSTextView(frame: NSRect(x: 0, y: 0, width: contentWidth, height: paper.height - margin * 2))
        textView.isVerticallyResizable = true
        textView.isHorizontallyResizable = false
        textView.textContainer?.widthTracksTextView = true
        textView.textContainer?.containerSize = NSSize(width: contentWidth, height: .greatestFiniteMagnitude)
        textView.textStorage?.setAttributedString(text)
        if let container = textView.textContainer {
            textView.layoutManager?.ensureLayout(for: container)
        }
        textView.sizeToFit()

        let operation = NSPrintOperation(view: textView, printInfo: printInfo)
        operation.showsPrintPanel = false
        operation.showsProgressPanel = false
        guard operation.run(), FileManager.default.fileExists(atPath: output.path) else {
            throw ConversionError.writeFailed(output)
        }
    }
}
