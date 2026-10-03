import PDFKit
import Vision

/// Reads text from PDF pages that have no text layer (scanned or photographed pages),
/// using Vision's on-device text recognition — the same engine as Live Text.
enum TextRecognizer {
    /// Languages to try, in order of preference, limited to what this Mac supports.
    private static let preferredLanguages = [
        "zh-Hans", "zh-Hant", "en-US", "ja-JP", "ko-KR",
        "fr-FR", "de-DE", "es-ES", "it-IT", "pt-BR", "ru-RU",
    ]

    /// A page counts as having text if PDFKit finds more than a stray header or page number.
    static func hasTextLayer(_ page: PDFPage) -> Bool {
        let characters = (page.string ?? "").filter { !$0.isWhitespace }
        return characters.count >= 20
    }

    /// OCR one page. Returns an empty string if nothing is readable.
    static func recognize(_ page: PDFPage) -> String {
        // 3x (216 dpi) gives Vision enough detail for small print.
        guard let image = PDFConverter.render(page, scale: 3) else { return "" }

        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = true
        request.automaticallyDetectsLanguage = true
        let supported = (try? request.supportedRecognitionLanguages()) ?? []
        let languages = preferredLanguages.filter { supported.contains($0) }
        if !languages.isEmpty {
            request.recognitionLanguages = languages
        }

        do {
            try VNImageRequestHandler(cgImage: image).perform([request])
        } catch {
            return ""
        }
        let lines = (request.results ?? []).compactMap { observation -> (box: CGRect, text: String)? in
            guard let text = observation.topCandidates(1).first?.string else { return nil }
            return (observation.boundingBox, text)
        }
        return joinIntoLines(lines)
    }

    /// Vision returns pieces of text; put pieces on the same line side by side, top to bottom.
    private static func joinIntoLines(_ pieces: [(box: CGRect, text: String)]) -> String {
        // Vision's coordinates start at the bottom-left, so a larger y is higher on the page.
        let sorted = pieces.sorted { $0.box.midY > $1.box.midY }
        var rows: [[(box: CGRect, text: String)]] = []
        for piece in sorted {
            if let last = rows.last?.first,
               abs(last.box.midY - piece.box.midY) < min(last.box.height, piece.box.height) / 2 {
                rows[rows.count - 1].append(piece)
            } else {
                rows.append([piece])
            }
        }
        return rows.map { row in
            row.sorted { $0.box.minX < $1.box.minX }.map(\.text).joined(separator: " ")
        }.joined(separator: "\n")
    }
}
