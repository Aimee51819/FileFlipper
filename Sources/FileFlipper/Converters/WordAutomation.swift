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

    static func canExport(_ source: URL) -> Bool {
        isEnabled && extensions.contains(source.pathExtension.lowercased()) && isInstalled
    }

    /// Asks Word to save `source` as a PDF at `output`. Returns false if anything goes wrong,
    /// so the caller can fall back. Must run on the main thread (NSAppleScript).
    static func exportPDF(from source: URL, to output: URL) -> Bool {
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
