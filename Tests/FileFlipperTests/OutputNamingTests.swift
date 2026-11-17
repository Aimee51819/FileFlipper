import XCTest
@testable import FileFlipper

final class OutputNamingTests: TempFolderTestCase {
    func testNextUsesSameFolderAndNewExtension() throws {
        let source = try touch("Photo.heic")
        let output = OutputNaming.next(to: source, ext: "jpg")
        XCTAssertEqual(output.lastPathComponent, "Photo.jpg")
        XCTAssertEqual(output.deletingLastPathComponent().standardizedFileURL, folder.standardizedFileURL)
    }

    func testNextNeverOverwritesExistingFiles() throws {
        let source = try touch("Photo.heic")
        _ = try touch("Photo (compressed).jpg")
        _ = try touch("Photo (compressed) 2.jpg")
        let output = OutputNaming.next(to: source, suffix: " (compressed)", ext: "jpg")
        XCTAssertEqual(output.lastPathComponent, "Photo (compressed) 3.jpg")
    }

    func testMakeFolderPicksAFreeName() throws {
        let source = try touch("Deck.pdf")
        let first = try OutputNaming.makeFolder(next: source, suffix: " pages")
        let second = try OutputNaming.makeFolder(next: source, suffix: " pages")
        XCTAssertEqual(first.lastPathComponent, "Deck pages")
        XCTAssertEqual(second.lastPathComponent, "Deck pages 2")
    }
}
