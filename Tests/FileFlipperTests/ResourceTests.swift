import XCTest

/// Guards the files the app bundle is built from (they are not compiled, so typos only show up at runtime).
final class ResourceTests: XCTestCase {
    private let root = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()

    private func strings(_ language: String, _ file: String = "Localizable") throws -> [String: String] {
        let url = root.appendingPathComponent("Resources/Localization/\(language).lproj/\(file).strings")
        let dictionary = try XCTUnwrap(NSDictionary(contentsOf: url) as? [String: String], "\(url.path) does not parse")
        return dictionary
    }

    func testLocalizationFilesParse() throws {
        // English keys are the English text, so en.lproj may be empty; it still has to parse.
        _ = try strings("en")
        _ = try strings("en", "InfoPlist")
        XCTAssertFalse(try strings("zh-Hans").isEmpty)
        XCTAssertFalse(try strings("zh-Hans", "InfoPlist").isEmpty)
    }

    func testChineseTranslationsKeepPlaceholders() throws {
        for (key, value) in try strings("zh-Hans") {
            let count = { (text: String) in text.components(separatedBy: "%@").count }
            XCTAssertEqual(count(key), count(value), "Placeholder count differs for \"\(key)\"")
        }
    }

    func testInfoPlistAndEntitlements() throws {
        let info = try XCTUnwrap(NSDictionary(contentsOf: root.appendingPathComponent("Resources/Info.plist")))
        XCTAssertEqual(info["LSUIElement"] as? Bool, true, "FileFlipper is a menu bar app")
        let entitlements = try XCTUnwrap(NSDictionary(contentsOf: root.appendingPathComponent("Resources/FileFlipper.entitlements")))
        XCTAssertEqual(entitlements["com.apple.security.app-sandbox"] as? Bool, true, "The App Store build must be sandboxed")
    }
}
