import AppKit

enum ExcelAutomation {
    static let bundleID = "com.microsoft.Excel"
    static let extensions: Set<String> = ["xlsx", "xlsm", "xls"]

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

    private static let exportQueue = DispatchQueue(label: "FileFlipper.ExcelExport")

    private static func runExport(from source: URL, to output: URL) -> Bool {
        let wasRunning = !NSRunningApplication.runningApplications(withBundleIdentifier: bundleID).isEmpty
        func quoted(_ path: String) -> String {
            "\"" + path.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"") + "\""
        }
        let quit = wasRunning ? "" : """
                if (count of workbooks) is 0 then quit saving no
        """
        let script = """
        with timeout of 300 seconds
            tell application id "\(bundleID)"
                open POSIX file \(quoted(source.path))
                set theWorkbook to active workbook
                save workbook as theWorkbook filename (POSIX file \(quoted(output.path)) as text) file format PDF file format
                close active workbook saving no
        \(quit)
            end tell
        end timeout
        """
        var error: NSDictionary?
        NSAppleScript(source: script)?.executeAndReturnError(&error)
        if let error {
            NSLog("FileFlipper: Excel export failed: %@", error)
            try? FileManager.default.removeItem(at: output)
            return false
        }
        return FileManager.default.fileExists(atPath: output.path)
    }
}
