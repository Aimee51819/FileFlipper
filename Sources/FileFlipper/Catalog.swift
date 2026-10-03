import AppKit
import UniformTypeIdentifiers

/// One bubble on the picker arc.
struct PickerItem {
    let title: String
    /// SF Symbol inside the bubble. Formats get one from `Catalog.formatSymbol(for:)`.
    var symbol: String? = nil
    /// One line shown near the pointer while the bubble is hovered.
    var detail: String? = nil
    /// Some AppKit APIs (HTML import, printing) must run on the main thread.
    var runsOnMain = false
    /// Takes the dropped files and returns the files it created.
    let action: ([URL]) throws -> [URL]
}

enum FileKind: Equatable {
    case image, pdf, document, presentation, spreadsheet, video, audio, other

    init(url: URL) {
        let type = (try? url.resourceValues(forKeys: [.contentTypeKey]))?.contentType
            ?? UTType(filenameExtension: url.pathExtension.lowercased())
        switch url.pathExtension.lowercased() {
        case "pptx": self = .presentation; return
        case "xlsx": self = .spreadsheet; return
        default: break
        }
        guard let type else { self = .other; return }

        if type.conforms(to: .pdf) {
            self = .pdf
        } else if type.conforms(to: .image) {
            self = .image
        } else if type.conforms(to: .movie) {
            self = .video
        } else if type.conforms(to: .audio) {
            self = .audio
        } else if DocumentConverter.readableTypes.contains(where: { type.conforms(to: $0) }) {
            self = .document
        } else {
            self = .other
        }
    }
}

/// Decides what appears on the picker for a given set of dragged files.
enum Catalog {
    static func items(for urls: [URL], tools: Bool) -> [PickerItem] {
        guard let first = urls.first else { return [] }
        let kind = FileKind(url: first)
        let sourceExt = first.pathExtension.lowercased()

        if tools {
            return toolItems(kind: kind, count: urls.filter { FileKind(url: $0) == kind }.count)
        }
        return formatItems(kind: kind, sourceExt: sourceExt)
    }

    // MARK: Formats (Shift)

    private static func formatItems(kind: FileKind, sourceExt: String) -> [PickerItem] {
        switch kind {
        case .image:
            var items = ImageConverter.targets
                .filter { !$0.matches(ext: sourceExt) && ImageConverter.canWrite($0.type) }
                .map { target in
                    PickerItem(title: target.title, action: perFile(kind) { url in
                        try [ImageConverter.convert(url, to: target.type, ext: target.ext)]
                    })
                }
            items.append(PickerItem(title: L("PDF"), action: perFile(kind) { url in
                try [PDFConverter.makePDF(from: [url], near: url)]
            }))
            return items

        case .pdf:
            var items = ["PNG", "JPG", "TIFF", "HEIC"].compactMap { name -> PickerItem? in
                guard let target = ImageConverter.targets.first(where: { $0.title == name }),
                      ImageConverter.canWrite(target.type) else { return nil }
                return PickerItem(title: name, action: perFile(kind) { url in
                    try PDFConverter.toImages(url, type: target.type, ext: target.ext)
                })
            }
            items.append(PickerItem(title: L("TXT"), action: perFile(kind) { url in
                try [PDFConverter.toText(url)]
            }))
            items.append(PickerItem(title: L("MD"), detail: L("Save as Markdown (OCR for scans)"), action: perFile(kind) { url in
                try [PDFConverter.toMarkdown(url)]
            }))
            for target in DocumentConverter.targets where target.ext == "docx" || target.ext == "rtf" {
                items.append(PickerItem(title: target.title, runsOnMain: true, action: perFile(kind) { url in
                    try [PDFConverter.toDocument(url, target: target)]
                }))
            }
            return items

        case .document:
            return DocumentConverter.targets
                .filter { !$0.matches(ext: sourceExt) }
                .map { target in
                    PickerItem(title: target.title, detail: target.ext == "md" ? L("Markdown, ready for AI") : nil,
                               runsOnMain: true, action: perFile(kind) { url in
                        try [DocumentConverter.convert(url, to: target)]
                    })
                }

        case .presentation:
            return [
                PickerItem(title: L("PDF"), detail: L("One page per slide"), runsOnMain: true, action: perFile(kind) { url in
                    try [PresentationConverter.toPDF(url)]
                }),
                PickerItem(title: L("MD"), detail: L("Slide titles and text as Markdown"), action: perFile(kind) { url in
                    try [PresentationConverter.toMarkdown(url)]
                }),
            ]

        case .spreadsheet:
            return [
                PickerItem(title: L("PDF"), detail: L("Every sheet as a table"), runsOnMain: true, action: perFile(kind) { url in
                    try [SpreadsheetConverter.toPDF(url)]
                }),
                PickerItem(title: L("MD"), detail: L("Every sheet as a Markdown table"), action: perFile(kind) { url in
                    try [SpreadsheetConverter.toMarkdown(url)]
                }),
            ]

        case .video:
            return MediaConverter.videoTargets
                .filter { !$0.matches(ext: sourceExt) }
                .map { target in
                    PickerItem(title: target.title, action: perFile(kind) { url in
                        try [MediaConverter.convertVideo(url, to: target)]
                    })
                }

        case .audio:
            return MediaConverter.audioTargets
                .filter { !$0.matches(ext: sourceExt) }
                .map { target in
                    PickerItem(title: target.title, action: perFile(kind) { url in
                        try [MediaConverter.convertAudio(url, to: target)]
                    })
                }

        case .other:
            return []
        }
    }

    // MARK: Tools (Option + Shift)

    private static func toolItems(kind: FileKind, count: Int) -> [PickerItem] {
        var items: [PickerItem] = []
        switch kind {
        case .image:
            items = [
                PickerItem(title: L("Crop"), symbol: "crop", detail: L("Crop to a selected area"), runsOnMain: true,
                          action: perFile(kind) { try [ImageConverter.crop($0)] }),
                PickerItem(title: L("Compress"), symbol: "rectangle.compress.vertical", detail: L("Make the image smaller"),
                          action: perFile(kind) { try [ImageConverter.compress($0)] }),
                PickerItem(title: L("Clean"), symbol: "location.slash", detail: L("Remove GPS and camera info"),
                          action: perFile(kind) { try [ImageConverter.stripMetadata($0)] }),
                PickerItem(title: L("50%"), symbol: "arrow.down.right.and.arrow.up.left", detail: L("Half the width and height"),
                          action: perFile(kind) { try [ImageConverter.resizeHalf($0)] }),
                PickerItem(title: L("Rotate"), symbol: "rotate.right", detail: L("Rotate 90° clockwise"),
                          action: perFile(kind) { try [ImageConverter.rotate($0)] }),
                PickerItem(title: L("Flip"), symbol: "arrow.left.and.right", detail: L("Mirror left to right"),
                          action: perFile(kind) { try [ImageConverter.flip($0)] }),
                PickerItem(title: L("B&W"), symbol: "circle.lefthalf.filled", detail: L("Black and white"),
                          action: perFile(kind) { try [ImageConverter.grayscale($0)] }),
                PickerItem(title: L("Cutout"), symbol: "scissors", detail: L("Remove the background"),
                          action: perFile(kind) { try [ImageConverter.removeBackground($0)] }),
            ]
            if count > 1 {
                items.append(PickerItem(title: L("Merge"), symbol: "square.stack", detail: L("Combine into one PDF"),
                                       action: merge(kind)))
            }

        case .pdf:
            items = [
                PickerItem(title: L("Compress"), symbol: "rectangle.compress.vertical", detail: L("Make the PDF smaller"),
                          action: perFile(kind) { try [PDFConverter.compress($0)] }),
                PickerItem(title: L("Clean"), symbol: "person.crop.circle.badge.xmark", detail: L("Remove author info"),
                          action: perFile(kind) { try [PDFConverter.stripMetadata($0)] }),
                PickerItem(title: L("Rotate"), symbol: "rotate.right", detail: L("Rotate every page 90°"),
                          action: perFile(kind) { try [PDFConverter.rotate($0)] }),
                PickerItem(title: L("Split"), symbol: "square.split.2x1", detail: L("One PDF per page"),
                          action: perFile(kind) { try PDFConverter.split($0) }),
                PickerItem(title: L("Text"), symbol: "text.viewfinder", detail: L("Extract text, OCR for scans"),
                          action: perFile(kind) { try [PDFConverter.toText($0)] }),
            ]
            if count > 1 {
                items.append(PickerItem(title: L("Merge"), symbol: "square.stack", detail: L("Combine the PDFs into one"), action: merge(kind)))
            }

        case .video:
            items = [
                PickerItem(title: L("Compress"), symbol: "rectangle.compress.vertical", detail: L("Make the video smaller"),
                          action: perFile(kind) { try [MediaConverter.compressVideo($0)] }),
                PickerItem(title: L("720p"), symbol: "arrow.down.right.and.arrow.up.left", detail: L("Convert to 720p"),
                          action: perFile(kind) { try [MediaConverter.resizeVideo720($0)] }),
                PickerItem(title: L("Mute"), symbol: "speaker.slash", detail: L("Remove the sound"),
                          action: perFile(kind) { try [MediaConverter.mute($0)] }),
                PickerItem(title: L("Audio"), symbol: "music.note", detail: L("Keep only the sound (M4A)"),
                          action: perFile(kind) { try [MediaConverter.extractAudio($0)] }),
                PickerItem(title: L("Frame"), symbol: "camera", detail: L("Save one frame as an image"),
                          action: perFile(kind) { try [MediaConverter.snapshot($0)] }),
            ]

        case .audio:
            items = [
                PickerItem(title: L("Compress"), symbol: "rectangle.compress.vertical", detail: L("Make the file smaller"),
                          action: perFile(kind) { try [MediaConverter.compressAudio($0)] }),
                PickerItem(title: L("Mono"), symbol: "speaker.wave.1", detail: L("Convert to mono"),
                          action: perFile(kind) { try [MediaConverter.mono($0)] }),
            ]

        case .document:
            items = [
                PickerItem(title: L("Plain"), symbol: "textformat", detail: L("Remove all formatting"), runsOnMain: true,
                          action: perFile(kind) { try [DocumentConverter.stripFormatting($0)] }),
            ]

        case .presentation, .spreadsheet, .other:
            items = []
        }
        return items
    }

    // MARK: Helpers

    /// Icon for a format bubble, by its label.
    static func formatSymbol(for title: String) -> String {
        switch title {
        case "PDF": return "doc.richtext"
        case "TXT": return "doc.plaintext"
        case "MD": return "text.alignleft"
        case "DOCX", "DOC", "ODT", "RTF": return "doc.text"
        case "HTML": return "chevron.left.forwardslash.chevron.right"
        case "GIF": return "photo.stack"
        case "MP4", "MOV": return "film"
        case "M4A", "WAV", "AIFF", "CAF": return "waveform"
        default: return "photo"
        }
    }

    /// Runs `body` for every dropped file of the given kind.
    private static func perFile(_ kind: FileKind, _ body: @escaping (URL) throws -> [URL]) -> ([URL]) throws -> [URL] {
        return { urls in
            var outputs: [URL] = []
            var firstError: Error?
            for url in urls where FileKind(url: url) == kind {
                do {
                    outputs += try body(url)
                } catch {
                    firstError = firstError ?? error
                }
            }
            if outputs.isEmpty, let firstError { throw firstError }
            return outputs
        }
    }

    private static func merge(_ kind: FileKind) -> ([URL]) throws -> [URL] {
        return { urls in
            let matching = urls.filter { FileKind(url: $0) == kind }
            guard let first = matching.first else { return [] }
            return try [PDFConverter.makePDF(from: matching, near: first, name: "Merged")]
        }
    }
}

/// A target format: label on the picker + file extension.
protocol FormatTarget {
    var title: String { get }
    var ext: String { get }
    var aliases: [String] { get }
}

extension FormatTarget {
    var aliases: [String] { [] }

    func matches(ext other: String) -> Bool {
        other == ext || aliases.contains(other)
    }
}
