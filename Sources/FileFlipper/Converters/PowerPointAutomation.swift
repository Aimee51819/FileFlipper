import AppKit

enum PowerPointAutomation {
    static let bundleID = "com.microsoft.Powerpoint"
    static let extensions: Set<String> = ["pptx", "pptm", "ppt", "ppsx", "pps"]

    static var isInstalled: Bool {
        NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) != nil
    }

    static var isEnabled: Bool { WordAutomation.isEnabled }

    static func canExport(_ source: URL) -> Bool {
        isEnabled && extensions.contains(source.pathExtension.lowercased()) && isInstalled
    }

    static func exportPDF(from source: URL, to output: URL) -> Bool {
        exportQueue.sync { runExport(from: source, to: output) }
    }

    private static let exportQueue = DispatchQueue(label: "FileFlipper.PowerPointExport")

    private static func runExport(from source: URL, to output: URL) -> Bool {
        let wasRunning = !NSRunningApplication.runningApplications(withBundleIdentifier: bundleID).isEmpty
        func quoted(_ path: String) -> String {
            "\"" + path.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"") + "\""
        }
        let quit = wasRunning ? "" : """
                if (count of presentations) is 0 then quit
        """
        let script = """
        with timeout of 300 seconds
            tell application id "\(bundleID)"
                open POSIX file \(quoted(source.path))
                set thePresentation to active presentation
                save thePresentation in POSIX file \(quoted(output.path)) as save as PDF
                close thePresentation saving no
        \(quit)
            end tell
        end timeout
        """
        var error: NSDictionary?
        NSAppleScript(source: script)?.executeAndReturnError(&error)
        if let error {
            NSLog("FileFlipper: PowerPoint export failed: %@", error)
            try? FileManager.default.removeItem(at: output)
            return false
        }
        return FileManager.default.fileExists(atPath: output.path)
    }
}
