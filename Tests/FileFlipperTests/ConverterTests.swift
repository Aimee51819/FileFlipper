import PDFKit
import XCTest
@testable import FileFlipper

final class ConverterTests: TempFolderTestCase {
    func testPNGToJPEGKeepsTheOriginalAndSize() throws {
        let png = try makePNG(width: 64, height: 48)
        let jpg = try ImageConverter.convert(png, to: .jpeg, ext: "jpg")
        XCTAssertTrue(FileManager.default.fileExists(atPath: png.path), "The original must stay untouched")
        XCTAssertEqual(jpg.pathExtension, "jpg")
        let image = try XCTUnwrap(NSImage(contentsOf: jpg)?.representations.first)
        XCTAssertEqual(image.pixelsWide, 64)
        XCTAssertEqual(image.pixelsHigh, 48)
    }

    func testImagesBecomeAOnePagePerImagePDF() throws {
        let first = try makePNG(named: "A")
        let second = try makePNG(named: "B")
        let pdf = try PDFConverter.makePDF(from: [first, second], near: first, name: "Merged")
        let document = try XCTUnwrap(PDFDocument(url: pdf))
        XCTAssertEqual(document.pageCount, 2)
    }

    func testCSVReadsRowsAndBecomesAMarkdownTable() throws {
        let csv = folder.appendingPathComponent("Sales.csv")
        try "Name,Amount\nApples,3\n\"Pears, green\",12\n".write(to: csv, atomically: true, encoding: .utf8)

        let sheets = try SpreadsheetConverter.read(csv)
        XCTAssertEqual(sheets.count, 1)
        XCTAssertEqual(sheets[0].rows.first, ["Name", "Amount"])
        XCTAssertEqual(sheets[0].rows.last, ["Pears, green", "12"])

        let markdown = try String(contentsOf: SpreadsheetConverter.toMarkdown(csv), encoding: .utf8)
        XCTAssertTrue(markdown.contains("| Name | Amount |"), markdown)
        XCTAssertTrue(markdown.contains("| --- | --- |"), markdown)
        XCTAssertTrue(markdown.contains("| Pears, green | 12 |"), markdown)
    }

    func testMarkdownTablePadsShortRows() {
        let table = MarkdownWriter.table([["a", "b", "c"], ["1"]])
        XCTAssertEqual(table, "| a | b | c |\n| --- | --- | --- |\n| 1 |  |  |")
        XCTAssertEqual(MarkdownWriter.table([]), "")
    }

    func testMarkdownEscapesSpecialCharacters() {
        XCTAssertEqual(MarkdownWriter.escape("a*b_c`d\\e"), "a\\*b\\_c\\`d\\\\e")
    }

    func testPlainPagesBecomeParagraphsSeparatedByRules() {
        let markdown = MarkdownWriter.markdown(fromPlainPages: ["Line one\nline two\n\nNext", "Page two"])
        XCTAssertEqual(markdown, "Line one line two\n\nNext\n\n---\n\nPage two\n")
    }
}
