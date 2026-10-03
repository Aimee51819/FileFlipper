import Compression
import Foundation

/// Minimal read-only ZIP reader, enough for Office files (.pptx, .xlsx are ZIP packages).
/// Supports stored and deflated entries.
struct ZipArchive {
    private struct Entry {
        let method: UInt16
        let compressedSize: Int
        let size: Int
        let localHeaderOffset: Int
    }

    private let data: Data
    private var entries: [String: Entry] = [:]

    init(url: URL) throws {
        data = try Data(contentsOf: url, options: .mappedIfSafe)
        try readCentralDirectory()
    }

    var paths: [String] { Array(entries.keys) }

    func contains(_ path: String) -> Bool { entries[path] != nil }

    /// Contents of `path` (e.g. "ppt/slides/slide1.xml"), or `nil` if missing.
    func data(at path: String) -> Data? {
        guard let entry = entries[path] else { return nil }
        let header = entry.localHeaderOffset
        guard uint32(at: header) == 0x0403_4b50 else { return nil }
        let start = header + 30 + Int(uint16(at: header + 26)) + Int(uint16(at: header + 28))
        guard start + entry.compressedSize <= data.count else { return nil }
        let compressed = data.subdata(in: start..<(start + entry.compressedSize))

        switch entry.method {
        case 0:
            return compressed
        case 8:
            guard entry.size > 0 else { return Data() }
            var output = Data(count: entry.size)
            let written = output.withUnsafeMutableBytes { out in
                compressed.withUnsafeBytes { input in
                    compression_decode_buffer(out.bindMemory(to: UInt8.self).baseAddress!, entry.size,
                                              input.bindMemory(to: UInt8.self).baseAddress!, compressed.count,
                                              nil, COMPRESSION_ZLIB)
                }
            }
            return written == entry.size ? output : nil
        default:
            return nil
        }
    }

    // MARK: Parsing

    private mutating func readCentralDirectory() throws {
        // The end-of-central-directory record sits in the last 64 KB + 22 bytes.
        let minimum = 22
        guard data.count >= minimum else { throw ConversionError.message(L("Not a valid Office file")) }
        var end = -1
        var position = data.count - minimum
        let lowest = max(0, data.count - 65_557)
        while position >= lowest {
            if uint32(at: position) == 0x0605_4b50 { end = position; break }
            position -= 1
        }
        guard end >= 0 else { throw ConversionError.message(L("Not a valid Office file")) }

        let count = Int(uint16(at: end + 10))
        var offset = Int(uint32(at: end + 16))
        for _ in 0..<count {
            guard offset + 46 <= data.count, uint32(at: offset) == 0x0201_4b50 else { break }
            let method = uint16(at: offset + 10)
            let compressedSize = Int(uint32(at: offset + 20))
            let size = Int(uint32(at: offset + 24))
            let nameLength = Int(uint16(at: offset + 28))
            let extraLength = Int(uint16(at: offset + 30))
            let commentLength = Int(uint16(at: offset + 32))
            let localHeader = Int(uint32(at: offset + 42))
            let nameData = data.subdata(in: (offset + 46)..<(offset + 46 + nameLength))
            if let name = String(data: nameData, encoding: .utf8) {
                entries[name] = Entry(method: method, compressedSize: compressedSize, size: size,
                                      localHeaderOffset: localHeader)
            }
            offset += 46 + nameLength + extraLength + commentLength
        }
        guard !entries.isEmpty else { throw ConversionError.message(L("Not a valid Office file")) }
    }

    private func uint16(at offset: Int) -> UInt16 {
        guard offset + 2 <= data.count else { return 0 }
        return UInt16(data[data.startIndex + offset]) | UInt16(data[data.startIndex + offset + 1]) << 8
    }

    private func uint32(at offset: Int) -> UInt32 {
        guard offset + 4 <= data.count else { return 0 }
        var value: UInt32 = 0
        for index in 0..<4 {
            value |= UInt32(data[data.startIndex + offset + index]) << (8 * index)
        }
        return value
    }
}

// MARK: - XML helpers shared by the Office converters

extension XMLElement {
    /// Direct children with this local name (namespace prefix ignored).
    func children(_ name: String) -> [XMLElement] {
        (children ?? []).compactMap { $0 as? XMLElement }.filter { $0.localName == name }
    }

    func child(_ name: String) -> XMLElement? {
        (children ?? []).lazy.compactMap { $0 as? XMLElement }.first { $0.localName == name }
    }

    /// First descendant with this local name, depth first.
    func descendant(_ name: String) -> XMLElement? {
        for case let element as XMLElement in children ?? [] {
            if element.localName == name { return element }
            if let found = element.descendant(name) { return found }
        }
        return nil
    }

    func descendants(_ name: String) -> [XMLElement] {
        var found: [XMLElement] = []
        for case let element as XMLElement in children ?? [] {
            if element.localName == name { found.append(element) }
            found += element.descendants(name)
        }
        return found
    }

    /// Attribute by local name (so "r:embed" is just "embed").
    func attr(_ name: String) -> String? {
        attributes?.first { $0.localName == name }?.stringValue
    }

    /// The relationship id ("r:id"), which can share its local name with a plain "id" attribute.
    var relationshipID: String? {
        attributes?.first { $0.localName == "id" && ($0.prefix == "r" || ($0.uri ?? "").contains("relationships")) }?.stringValue
    }
}

enum OfficePackage {
    static func xml(_ archive: ZipArchive, _ path: String) -> XMLElement? {
        guard let data = archive.data(at: path),
              let document = try? XMLDocument(data: data, options: []) else { return nil }
        return document.rootElement()
    }

    /// Relationship id → absolute package path, for the part at `path`.
    static func relationships(_ archive: ZipArchive, for path: String) -> [String: (target: String, type: String)] {
        let directory = (path as NSString).deletingLastPathComponent
        let relsPath = directory + "/_rels/" + (path as NSString).lastPathComponent + ".rels"
        guard let root = xml(archive, relsPath) else { return [:] }
        var result: [String: (String, String)] = [:]
        for relationship in root.children("Relationship") {
            guard let id = relationship.attr("Id"), let target = relationship.attr("Target") else { continue }
            // External targets (web links) are kept as written.
            let external = relationship.attr("TargetMode") == "External"
            result[id] = (external ? target : resolve(target, from: directory), relationship.attr("Type") ?? "")
        }
        return result
    }

    /// Resolves "../media/image1.png" relative to a directory inside the package.
    static func resolve(_ target: String, from directory: String) -> String {
        if target.hasPrefix("/") { return String(target.dropFirst()) }
        var parts = directory.split(separator: "/").map(String.init)
        for piece in target.split(separator: "/") {
            if piece == ".." { _ = parts.popLast() } else if piece != "." { parts.append(String(piece)) }
        }
        return parts.joined(separator: "/")
    }
}
