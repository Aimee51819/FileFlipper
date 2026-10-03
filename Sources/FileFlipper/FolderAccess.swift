import AppKit

/// The Mac App Store requires the App Sandbox. A sandboxed app may read files that are
/// dropped on it, but may not create new files next to them unless the user has granted
/// access to that folder. FileFlipper asks once per folder (the user can pick their whole
/// Home folder to never be asked again) and remembers the answer as a security-scoped bookmark.
///
/// Outside the sandbox (e.g. `swift run`) every call is a no-op.
final class FolderAccess {
    static let shared = FolderAccess()

    static var isSandboxed: Bool {
        ProcessInfo.processInfo.environment["APP_SANDBOX_CONTAINER_ID"] != nil
    }

    /// The user's real home folder (`NSHomeDirectory()` points into the sandbox container).
    static var realHome: URL {
        if let entry = getpwuid(getuid()), let dir = entry.pointee.pw_dir {
            return URL(fileURLWithPath: String(cString: dir), isDirectory: true)
        }
        return FileManager.default.homeDirectoryForCurrentUser
    }

    private let defaultsKey = "folderBookmarks"
    private(set) var grantedFolders: [URL] = []

    private init() {
        load()
    }

    /// Makes sure the app may write into every folder in `directories`, asking the user
    /// when needed. Returns a closure that ends the access, or `nil` if the user declined.
    func beginAccess(to directories: [URL]) -> (() -> Void)? {
        guard Self.isSandboxed else { return {} }

        var active: [URL] = []
        let stopAll = { active.forEach { $0.stopAccessingSecurityScopedResource() } }
        for directory in Set(directories.map { $0.standardizedFileURL }) {
            guard let grant = covering(directory) ?? requestAccess(to: directory) else {
                stopAll()
                return nil
            }
            if grant.startAccessingSecurityScopedResource() {
                active.append(grant)
            }
        }
        return stopAll
    }

    /// Asks for access to the Home folder, which covers almost everything a user drags.
    func requestHomeAccess() {
        _ = requestAccess(to: Self.realHome)
    }

    func resetAll() {
        grantedFolders = []
        UserDefaults.standard.removeObject(forKey: defaultsKey)
    }

    // MARK: Private

    private func covering(_ directory: URL) -> URL? {
        let path = directory.path
        return grantedFolders.first { grant in
            let root = grant.standardizedFileURL.path
            return path == root || root == "/" || path.hasPrefix(root + "/")
        }
    }

    private func requestAccess(to directory: URL) -> URL? {
        NSApp.activate(ignoringOtherApps: true)
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.canCreateDirectories = false
        panel.directoryURL = directory
        panel.prompt = L("Grant Access")
        panel.message = L("FileFlipper needs your permission to save converted files in “%@”. Tip: choose your Home folder to allow every folder inside it at once.", directory.lastPathComponent)
        guard panel.runModal() == .OK, let chosen = panel.url else { return nil }

        do {
            let bookmark = try chosen.bookmarkData(options: .withSecurityScope,
                                                   includingResourceValuesForKeys: nil, relativeTo: nil)
            var bookmarks = UserDefaults.standard.array(forKey: defaultsKey) as? [Data] ?? []
            bookmarks.append(bookmark)
            UserDefaults.standard.set(bookmarks, forKey: defaultsKey)
            if let resolved = resolve(bookmark) {
                grantedFolders.append(resolved)
            }
        } catch {
            NSLog("FileFlipper: couldn't save folder permission: \(error)")
        }
        return covering(directory)
    }

    private func load() {
        let bookmarks = UserDefaults.standard.array(forKey: defaultsKey) as? [Data] ?? []
        grantedFolders = bookmarks.compactMap(resolve)
    }

    private func resolve(_ bookmark: Data) -> URL? {
        var stale = false
        return try? URL(resolvingBookmarkData: bookmark, options: .withSecurityScope,
                        relativeTo: nil, bookmarkDataIsStale: &stale)
    }
}
