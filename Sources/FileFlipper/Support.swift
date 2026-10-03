import Foundation

enum ConversionError: LocalizedError {
    case unreadable(URL)
    case writeFailed(URL)
    case unsupported(String)
    case message(String)
    /// The user backed out (e.g. closed the crop window). Not shown as an error.
    case cancelled

    var errorDescription: String? {
        switch self {
        case .cancelled: return "Cancelled"
        case .unreadable(let url): return "Couldn't read \(url.lastPathComponent)"
        case .writeFailed(let url): return "Couldn't write \(url.lastPathComponent)"
        case .unsupported(let what): return "\(what) isn't supported on this Mac"
        case .message(let text): return text
        }
    }
}

/// Converted copies are saved next to the original, never overwriting anything.
enum OutputNaming {
    /// `Photo.heic` + suffix " (compressed)" + ext "jpg" -> `Photo (compressed).jpg`, then `Photo (compressed) 2.jpg`, ...
    static func next(to url: URL, suffix: String = "", ext: String) -> URL {
        let directory = url.deletingLastPathComponent()
        let base = url.deletingPathExtension().lastPathComponent + suffix
        return unique(in: directory, base: base, ext: ext)
    }

    static func unique(in directory: URL, base: String, ext: String) -> URL {
        func candidate(_ name: String) -> URL {
            directory.appendingPathComponent(name).appendingPathExtension(ext)
        }
        var url = candidate(base)
        var counter = 2
        while FileManager.default.fileExists(atPath: url.path) {
            url = candidate("\(base) \(counter)")
            counter += 1
        }
        return url
    }

    /// Creates a new folder next to `url` (used when one file produces many outputs).
    static func makeFolder(next url: URL, suffix: String) throws -> URL {
        let directory = url.deletingLastPathComponent()
        let base = url.deletingPathExtension().lastPathComponent + suffix
        var folder = directory.appendingPathComponent(base, isDirectory: true)
        var counter = 2
        while FileManager.default.fileExists(atPath: folder.path) {
            folder = directory.appendingPathComponent("\(base) \(counter)", isDirectory: true)
            counter += 1
        }
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false)
        return folder
    }
}

enum FileInfo {
    static func bytes(of url: URL) -> Int {
        (try? url.resourceValues(forKeys: [.fileSizeKey]))?.fileSize ?? 0
    }

    static func sizeString(of url: URL) -> String {
        ByteCountFormatter.string(fromByteCount: Int64(bytes(of: url)), countStyle: .file)
    }

    /// For "Compress" tools: keeps `output` only if it really is smaller than `original`.
    static func requireSmaller(_ output: URL, than original: URL) throws -> URL {
        guard bytes(of: output) >= bytes(of: original) else { return output }
        try? FileManager.default.removeItem(at: output)
        throw ConversionError.message("\(original.lastPathComponent) is already as small as it gets")
    }
}
