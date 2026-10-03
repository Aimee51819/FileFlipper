import AppKit
import PDFKit
import UniformTypeIdentifiers

/// PDF conversions and tools, built on PDFKit.
enum PDFConverter {
    static func open(_ url: URL) throws -> PDFDocument {
        guard let document = PDFDocument(url: url) else { throw ConversionError.unreadable(url) }
        if document.isLocked {
            throw ConversionError.message("\(url.lastPathComponent) is password protected")
        }
        return document
    }

    // MARK: Convert

    /// One image per page. A single page is saved next to the PDF; several pages go into
    /// a folder as "Page 001.png", "Page 002.png", ... (TIFF keeps all pages in one file).
    static func toImages(_ url: URL, type: UTType, ext: String) throws -> [URL] {
        let document = try open(url)
        guard document.pageCount > 0 else { throw ConversionError.message("\(url.lastPathComponent) has no pages") }

        if document.pageCount == 1 || type == .tiff {
            let output = OutputNaming.next(to: url, ext: ext)
            guard let destination = CGImageDestinationCreateWithURL(output as CFURL, type.identifier as CFString,
                                                                     document.pageCount, nil) else {
                throw ConversionError.unsupported(ext.uppercased())
            }
            for index in 0..<document.pageCount {
                guard let page = document.page(at: index), let image = render(page) else {
                    throw ConversionError.message("Couldn't render page \(index + 1)")
                }
                CGImageDestinationAddImage(destination, image,
                                           [kCGImageDestinationLossyCompressionQuality: 0.9] as CFDictionary)
            }
            guard CGImageDestinationFinalize(destination) else { throw ConversionError.writeFailed(output) }
            return [output]
        }

        let folder = try OutputNaming.makeFolder(next: url, suffix: " (\(ext.uppercased()))")
        var outputs: [URL] = []
        for index in 0..<document.pageCount {
            guard let page = document.page(at: index), let image = render(page) else { continue }
            let name = String(format: "Page %03d", index + 1)
            let output = folder.appendingPathComponent(name).appendingPathExtension(ext)
            outputs.append(try ImageConverter.write(image, to: output, type: type))
        }
        return outputs.isEmpty ? [] : [folder]
    }

    /// Text of every page. Pages without a text layer (scans, photos) are read with OCR.
    static func toText(_ url: URL) throws -> URL {
        let document = try open(url)
        var pages: [String] = []
        for index in 0..<document.pageCount {
            guard let page = document.page(at: index) else { continue }
            let text = TextRecognizer.hasTextLayer(page) ? (page.string ?? "") : TextRecognizer.recognize(page)
            pages.append(text.trimmingCharacters(in: .whitespacesAndNewlines))
        }
        let text = pages.joined(separator: "\n\n")
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ConversionError.message("No text found in \(url.lastPathComponent)")
        }
        let output = OutputNaming.next(to: url, ext: "txt")
        try text.write(to: output, atomically: true, encoding: .utf8)
        return output
    }

    /// Markdown from the page text (OCR for scanned pages), one section per page.
    static func toMarkdown(_ url: URL) throws -> URL {
        let document = try open(url)
        var pages: [String] = []
        for index in 0..<document.pageCount {
            guard let page = document.page(at: index) else { continue }
            pages.append(TextRecognizer.hasTextLayer(page) ? (page.string ?? "") : TextRecognizer.recognize(page))
        }
        guard pages.contains(where: { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }) else {
            throw ConversionError.message("No text found in \(url.lastPathComponent)")
        }
        let output = OutputNaming.next(to: url, ext: "md")
        try MarkdownWriter.markdown(fromPlainPages: pages).write(to: output, atomically: true, encoding: .utf8)
        return output
    }

    static func toDocument(_ url: URL, target: DocumentTarget) throws -> URL {
        let document = try open(url)
        let combined = NSMutableAttributedString()
        for index in 0..<document.pageCount {
            guard let page = document.page(at: index) else { continue }
            // Keep the original formatting when there is a text layer; otherwise use OCR as plain text.
            let pageText: NSAttributedString
            if TextRecognizer.hasTextLayer(page), let attributed = page.attributedString {
                pageText = attributed
            } else {
                pageText = NSAttributedString(string: TextRecognizer.recognize(page), attributes: [
                    .font: NSFont.systemFont(ofSize: 12),
                ])
            }
            guard pageText.length > 0 else { continue }
            if combined.length > 0 {
                combined.append(NSAttributedString(string: "\n\u{0C}"))  // page break
            }
            combined.append(pageText)
        }
        guard combined.length > 0 else {
            throw ConversionError.message("No text found in \(url.lastPathComponent)")
        }
        let output = OutputNaming.next(to: url, ext: target.ext)
        try DocumentConverter.write(combined, to: output, target: target)
        return output
    }

    /// Builds a PDF from images and/or PDFs, in the given order.
    static func makePDF(from urls: [URL], near anchor: URL, name: String? = nil) throws -> URL {
        let result = PDFDocument()
        for url in urls {
            if FileKind(url: url) == .pdf {
                let document = try open(url)
                for index in 0..<document.pageCount {
                    if let page = document.page(at: index)?.copy() as? PDFPage {
                        result.insert(page, at: result.pageCount)
                    }
                }
            } else if let image = NSImage(contentsOf: url), let page = PDFPage(image: image) {
                result.insert(page, at: result.pageCount)
            } else {
                throw ConversionError.unreadable(url)
            }
        }
        guard result.pageCount > 0 else { throw ConversionError.message("Nothing to put in the PDF") }

        let output: URL
        if let name {
            output = OutputNaming.unique(in: anchor.deletingLastPathComponent(), base: name, ext: "pdf")
        } else {
            output = OutputNaming.next(to: anchor, ext: "pdf")
        }
        guard result.write(to: output) else { throw ConversionError.writeFailed(output) }
        return output
    }

    // MARK: Tools

    static func compress(_ url: URL) throws -> URL {
        let document = try open(url)
        let output = OutputNaming.next(to: url, suffix: " (compressed)", ext: "pdf")
        let options: [PDFDocumentWriteOption: Any] = [
            .saveImagesAsJPEGOption: true,
            .optimizeImagesForScreenOption: true,
        ]
        guard document.write(to: output, withOptions: options) else { throw ConversionError.writeFailed(output) }
        return try FileInfo.requireSmaller(output, than: url)
    }

    static func stripMetadata(_ url: URL) throws -> URL {
        let document = try open(url)
        document.documentAttributes = [:]
        let output = OutputNaming.next(to: url, suffix: " (no metadata)", ext: "pdf")
        guard document.write(to: output) else { throw ConversionError.writeFailed(output) }
        return output
    }

    static func rotate(_ url: URL) throws -> URL {
        let document = try open(url)
        for index in 0..<document.pageCount {
            if let page = document.page(at: index) {
                page.rotation = (page.rotation + 90) % 360
            }
        }
        let output = OutputNaming.next(to: url, suffix: " (rotated)", ext: "pdf")
        guard document.write(to: output) else { throw ConversionError.writeFailed(output) }
        return output
    }

    /// One PDF per page, in a folder next to the original.
    static func split(_ url: URL) throws -> [URL] {
        let document = try open(url)
        guard document.pageCount > 1 else {
            throw ConversionError.message("\(url.lastPathComponent) only has one page")
        }
        let folder = try OutputNaming.makeFolder(next: url, suffix: " (pages)")
        for index in 0..<document.pageCount {
            guard let page = document.page(at: index)?.copy() as? PDFPage else { continue }
            let single = PDFDocument()
            single.insert(page, at: 0)
            let output = folder.appendingPathComponent(String(format: "Page %03d", index + 1))
                .appendingPathExtension("pdf")
            guard single.write(to: output) else { throw ConversionError.writeFailed(output) }
        }
        return [folder]
    }

    // MARK: Rendering

    /// Renders a page at 2x (144 dpi) on a white background.
    static func render(_ page: PDFPage, scale: CGFloat = 2) -> CGImage? {
        let box = page.bounds(for: .mediaBox)
        let sideways = page.rotation % 180 != 0
        let width = Int(((sideways ? box.height : box.width) * scale).rounded())
        let height = Int(((sideways ? box.width : box.height) * scale).rounded())
        guard width > 0, height > 0,
              let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        context.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        context.interpolationQuality = .high
        context.scaleBy(x: scale, y: scale)
        page.draw(with: .mediaBox, to: context)  // applies the page's rotation
        return context.makeImage()
    }
}
