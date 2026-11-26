import AppKit

/// When Microsoft Word is installed, Word documents are turned into PDFs by Word itself
/// ("Save as PDF"), so the result looks exactly like printing from Word. Without Word — or if
/// the user doesn't allow FileFlipper to control it — the built-in layout is used instead.
enum WordAutomation {
    static let bundleID = "com.microsoft.Word"
    static let extensions: Set<String> = ["docx", "docm", "doc", "dotx", "rtf", "odt"]
    static let defaultsKey = "useMicrosoftWord"

    static var isInstalled: Bool {
        NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) != nil
    }

    /// The menu bar setting (on unless the user turned it off).
    static var isEnabled: Bool {
        get { UserDefaults.standard.object(forKey: defaultsKey) as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: defaultsKey) }
    }

    static func supports(_ source: URL) -> Bool {
        extensions.contains(source.pathExtension.lowercased())
    }

    static func canExport(_ source: URL) -> Bool {
        isEnabled && supports(source) && isInstalled
    }

    /// Asks Word to save `source` as a PDF at `output`. Returns false if anything goes wrong,
    /// so the caller can fall back.
    static func exportPDF(from source: URL, to output: URL) -> Bool {
        exportQueue.sync { runExport(from: source, to: output) }
    }

    private static let exportQueue = DispatchQueue(label: "FileFlipper.WordExport")

    private static func runExport(from source: URL, to output: URL) -> Bool {
        let wasRunning = !NSRunningApplication.runningApplications(withBundleIdentifier: bundleID).isEmpty
        func quoted(_ path: String) -> String {
            "\"" + path.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"") + "\""
        }
        // Quit Word afterwards only if FileFlipper started it and no other document is open.
        let quit = wasRunning ? "" : """
                if (count of documents) is 0 then quit saving no
        """
        let script = """
        with timeout of 180 seconds
            tell application id "\(bundleID)"
                open POSIX file \(quoted(source.path))
                set theDoc to active document
                save as theDoc file name \(quoted(output.path)) file format format PDF
                close theDoc saving no
        \(quit)
            end tell
        end timeout
        """
        var error: NSDictionary?
        NSAppleScript(source: script)?.executeAndReturnError(&error)
        if let error {
            NSLog("FileFlipper: Word export failed: %@", error)
            try? FileManager.default.removeItem(at: output)
            return false
        }
        return FileManager.default.fileExists(atPath: output.path)
    }
}

/// Pages provides a layout-aware PDF export when Microsoft Word is unavailable.
/// The built-in importer remains useful as a fallback, but may lose Word objects or list markers.
enum PagesAutomation {
    static let bundleID = "com.apple.Pages"
    static let extensions: Set<String> = ["doc", "docx", "dotx", "rtf"]
    private static let defaultsKey = "useApplePages"

    static var isInstalled: Bool {
        NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) != nil
    }

    static var isEnabled: Bool {
        get { UserDefaults.standard.object(forKey: defaultsKey) as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: defaultsKey) }
    }

    static func supports(_ source: URL) -> Bool {
        extensions.contains(source.pathExtension.lowercased())
    }

    static func canExport(_ source: URL) -> Bool {
        isEnabled && isInstalled && supports(source)
    }

    static func exportPDF(from source: URL, to output: URL) -> Bool {
        exportQueue.sync { runExport(from: source, to: output) }
    }

    private static let exportQueue = DispatchQueue(label: "FileFlipper.PagesExport")

    private static func runExport(from source: URL, to output: URL) -> Bool {
        let wasRunning = !NSRunningApplication.runningApplications(withBundleIdentifier: bundleID).isEmpty
        func quoted(_ path: String) -> String {
            "\"" + path.replacingOccurrences(of: "\\", with: "\\\\")
                .replacingOccurrences(of: "\"", with: "\\\"") + "\""
        }
        // Leave Pages open if the user already had it running; otherwise close it when done.
        let quit = wasRunning ? "" : """
                if (count of documents) is 0 then quit
        """
        let script = """
        with timeout of 180 seconds
            tell application id "\(bundleID)"
                open POSIX file \(quoted(source.path))
                set theDoc to front document
                export theDoc to POSIX file \(quoted(output.path)) as PDF
                close theDoc saving no
        \(quit)
            end tell
        end timeout
        """
        var error: NSDictionary?
        NSAppleScript(source: script)?.executeAndReturnError(&error)
        if let error {
            NSLog("FileFlipper: Pages export failed: %@", error)
            try? FileManager.default.removeItem(at: output)
            return false
        }
        return FileManager.default.fileExists(atPath: output.path)
    }
}
