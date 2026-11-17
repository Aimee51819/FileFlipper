import AppKit
import XCTest

/// A fresh temporary folder per test, removed afterwards.
class TempFolderTestCase: XCTestCase {
    var folder: URL!

    override func setUpWithError() throws {
        folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("FileFlipperTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: folder)
    }

    /// Writes a small solid-colour PNG and returns its URL.
    func makePNG(named name: String = "Photo", width: Int = 64, height: Int = 48) throws -> URL {
        let context = try XCTUnwrap(CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
                                              bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(),
                                              bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.setFillColor(CGColor(red: 0.2, green: 0.6, blue: 0.3, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        let image = try XCTUnwrap(context.makeImage())
        let url = folder.appendingPathComponent(name).appendingPathExtension("png")
        let rep = NSBitmapImageRep(cgImage: image)
        try XCTUnwrap(rep.representation(using: .png, properties: [:])).write(to: url)
        return url
    }

    func touch(_ name: String) throws -> URL {
        let url = folder.appendingPathComponent(name)
        try Data().write(to: url)
        return url
    }
}
