import XCTest
@testable import FileFlipper

final class CatalogTests: TempFolderTestCase {
    func testFileKindByExtension() {
        let expected: [String: FileKind] = [
            "png": .image, "JPG": .image, "heic": .image,
            "pdf": .pdf,
            "docx": .document, "rtf": .document, "txt": .document, "html": .document,
            "pptx": .presentation,
            "xlsx": .spreadsheet, "csv": .spreadsheet, "tsv": .spreadsheet,
            "mp4": .video, "mov": .video,
            "m4a": .audio, "wav": .audio,
            "unknownext": .other,
        ]
        for (ext, kind) in expected {
            let url = folder.appendingPathComponent("file").appendingPathExtension(ext)
            XCTAssertEqual(FileKind(url: url), kind, "Wrong kind for .\(ext)")
        }
    }

    func testNoFilesMeansNoBubbles() {
        XCTAssertTrue(Catalog.items(for: [], tools: false).isEmpty)
        XCTAssertTrue(Catalog.items(for: [], tools: true).isEmpty)
    }

    func testImageFormatsExcludeTheSourceFormat() throws {
        let png = try makePNG()
        let titles = Catalog.items(for: [png], tools: false).map(\.title)
        XCTAssertFalse(titles.contains("PNG"), "A PNG should not offer PNG: \(titles)")
        XCTAssertTrue(titles.contains("JPG"), "Expected JPG in \(titles)")
        XCTAssertTrue(titles.contains("PDF"), "Expected PDF in \(titles)")
    }

    func testEveryKindWithFormatsOffersSomething() {
        for ext in ["png", "pdf", "docx", "pptx", "xlsx", "csv", "mp4", "m4a"] {
            let url = folder.appendingPathComponent("file").appendingPathExtension(ext)
            XCTAssertFalse(Catalog.items(for: [url], tools: false).isEmpty, "No formats offered for .\(ext)")
        }
    }

    func testToolsModeOffersToolsForImagesAndPDFs() {
        for ext in ["png", "pdf", "mp4"] {
            let url = folder.appendingPathComponent("file").appendingPathExtension(ext)
            XCTAssertFalse(Catalog.items(for: [url], tools: true).isEmpty, "No tools offered for .\(ext)")
        }
    }

    func testFormatTargetAliases() {
        let jpg = ImageConverter.targets.first { $0.title == "JPG" }!
        XCTAssertTrue(jpg.matches(ext: "jpg"))
        XCTAssertTrue(jpg.matches(ext: "jpeg"))
        XCTAssertFalse(jpg.matches(ext: "png"))
    }

    func testFormatSymbols() {
        XCTAssertEqual(Catalog.formatSymbol(for: "PDF"), "doc.richtext")
        XCTAssertEqual(Catalog.formatSymbol(for: "MP4"), "film")
        XCTAssertEqual(Catalog.formatSymbol(for: "WAV"), "waveform")
        XCTAssertEqual(Catalog.formatSymbol(for: "JPG"), "photo")
    }
}
