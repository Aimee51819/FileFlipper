import AppKit
import OSLog
import ServiceManagement

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private enum Keys {
        static let enabled = "enabled"
        static let shownWelcome = "shownWelcome"
    }

    private var statusItem: NSStatusItem!
    private let monitor = DragMonitor()
    private let picker = PickerController()
    private let toast = ToastController()
    private let log = Logger(subsystem: "com.aimeesun.fileflipper", category: "actions")

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Two running copies (e.g. one in Applications and one in a build folder) would each show a picker.
        let others = NSRunningApplication.runningApplications(withBundleIdentifier: Bundle.main.bundleIdentifier ?? "")
            .filter { $0.processIdentifier != ProcessInfo.processInfo.processIdentifier }
        if !others.isEmpty {
            NSApp.activate(ignoringOtherApps: true)
            let alert = NSAlert()
            alert.messageText = L("FileFlipper is already running")
            alert.informativeText = L("Look for the ◎ icon in the menu bar at the top-right of your screen.")
            alert.runModal()
            NSApp.terminate(nil)
            return
        }

        UserDefaults.standard.register(defaults: [Keys.enabled: true])

        setUpStatusItem()

        monitor.isEnabled = UserDefaults.standard.bool(forKey: Keys.enabled)
        monitor.onShow = { [weak self] point, urls, tools in
            self?.picker.show(at: point, urls: urls, tools: tools)
        }
        monitor.onModeChange = { [weak self] tools in
            self?.picker.setToolsMode(tools)
        }
        monitor.onEnd = { [weak self] in
            self?.picker.hide(afterDelay: 0.35)
        }
        picker.onPick = { [weak self] item, urls in
            self?.run(item, on: urls)
        }
        monitor.start()

        if !UserDefaults.standard.bool(forKey: Keys.shownWelcome) {
            UserDefaults.standard.set(true, forKey: Keys.shownWelcome)
            showHelp()
        }
    }

    // MARK: Running actions

    private func run(_ item: PickerItem, on urls: [URL]) {
        // Sandbox: we need permission to write into the folders holding these files.
        guard let endAccess = FolderAccess.shared.beginAccess(to: urls.map { $0.deletingLastPathComponent() }) else {
            log.error("No folder access for \(urls.first?.deletingLastPathComponent().path ?? "?", privacy: .public)")
            toast.show(L("FileFlipper needs folder access to save the converted file"), symbol: "lock.fill", duration: 4)
            return
        }

        let what = urls.count == 1 ? urls[0].lastPathComponent : L("%@ files", String(urls.count))
        toast.show(L("%@: %@…", item.title, what), symbol: "hourglass", duration: nil)

        let work = { () -> Result<[URL], Error> in
            Result { try item.action(urls) }
        }
        if item.runsOnMain {
            DispatchQueue.main.async { [weak self] in
                let result = work()
                endAccess()
                self?.finish(result, item: item)
            }
        } else {
            DispatchQueue.global(qos: .userInitiated).async {
                let result = work()
                DispatchQueue.main.async { [weak self] in
                    endAccess()
                    self?.finish(result, item: item)
                }
            }
        }
    }

    private func finish(_ result: Result<[URL], Error>, item: PickerItem) {
        switch result {
        case .success(let outputs) where outputs.count == 1:
            toast.show(L("Saved %@", outputs[0].lastPathComponent), symbol: "checkmark.circle.fill")
            NSSound(named: "Pop")?.play()
        case .success(let outputs) where outputs.count > 1:
            toast.show(L("Saved %@ files", String(outputs.count)), symbol: "checkmark.circle.fill")
            NSSound(named: "Pop")?.play()
        case .success:
            toast.show(L("%@: nothing to do", item.title), symbol: "exclamationmark.circle")
        case .failure(ConversionError.cancelled):
            toast.show(L("Cancelled"), symbol: "xmark.circle", duration: 1.2)
        case .failure(let error):
            log.error("\(item.title, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
            toast.show(error.localizedDescription, symbol: "xmark.octagon.fill", duration: 4)
            NSSound.beep()
        }
    }

    // MARK: Menu bar

    private func setUpStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let button = statusItem.button {
            button.image = NSImage(systemSymbolName: "circle.circle", accessibilityDescription: "FileFlipper")
            button.image?.isTemplate = true
        }
        let menu = NSMenu()
        menu.delegate = self
        statusItem.menu = menu
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()

        let enabled = NSMenuItem(title: L("Enabled"), action: #selector(toggleEnabled), keyEquivalent: "")
        enabled.target = self
        enabled.state = monitor.isEnabled ? .on : .off
        menu.addItem(enabled)

        let login = NSMenuItem(title: L("Launch at Login"), action: #selector(toggleLaunchAtLogin), keyEquivalent: "")
        login.target = self
        login.state = SMAppService.mainApp.status == .enabled ? .on : .off
        menu.addItem(login)

        if FolderAccess.isSandboxed {
            menu.addItem(.separator())
            let folders = FolderAccess.shared.grantedFolders
            let header = NSMenuItem(title: folders.isEmpty ? L("No Folder Access Yet") : L("Can Save In:"),
                                    action: nil, keyEquivalent: "")
            header.isEnabled = false
            menu.addItem(header)
            for folder in folders {
                let entry = NSMenuItem(title: "  " + folder.path, action: nil, keyEquivalent: "")
                entry.isEnabled = false
                menu.addItem(entry)
            }
            let grant = NSMenuItem(title: L("Allow Home Folder…"), action: #selector(grantHome), keyEquivalent: "")
            grant.target = self
            menu.addItem(grant)
            if !folders.isEmpty {
                let reset = NSMenuItem(title: L("Reset Folder Access"), action: #selector(resetFolders), keyEquivalent: "")
                reset.target = self
                menu.addItem(reset)
            }
        }

        menu.addItem(.separator())

        let help = NSMenuItem(title: L("How to Use…"), action: #selector(showHelp), keyEquivalent: "")
        help.target = self
        menu.addItem(help)

        let about = NSMenuItem(title: L("About FileFlipper"), action: #selector(showAbout), keyEquivalent: "")
        about.target = self
        menu.addItem(about)

        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: L("Quit FileFlipper"), action: #selector(NSApplication.terminate(_:)),
                                keyEquivalent: "q"))
    }

    @objc private func toggleEnabled() {
        monitor.isEnabled.toggle()
        UserDefaults.standard.set(monitor.isEnabled, forKey: Keys.enabled)
    }

    @objc private func toggleLaunchAtLogin() {
        do {
            if SMAppService.mainApp.status == .enabled {
                try SMAppService.mainApp.unregister()
            } else {
                try SMAppService.mainApp.register()
            }
        } catch {
            toast.show(L("Launch at Login: %@", error.localizedDescription), symbol: "xmark.octagon.fill", duration: 4)
        }
    }

    @objc private func grantHome() {
        FolderAccess.shared.requestHomeAccess()
    }

    @objc private func resetFolders() {
        FolderAccess.shared.resetAll()
    }

    @objc private func showAbout() {
        NSApp.activate(ignoringOtherApps: true)
        NSApp.orderFrontStandardAboutPanel(options: [
            .credits: NSAttributedString(string: L("Convert files right in Finder.\nOpen source under the MIT License.")),
        ])
    }

    @objc private func showHelp() {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = L("How to use FileFlipper")
        var paragraphs = [
            L("Convert: start dragging a file in Finder, then hold Shift. Round buttons appear in an arc above the pointer. Drop the file on a format and the converted copy is saved next to the original."),
            L("Tools: hold Option + Shift while dragging to see tools for that file type (crop, compress, cut out, rotate, merge, split…)."),
            L("Markdown: pick MD to turn Word, PDF, PowerPoint or Excel into Markdown, ready for AI."),
            L("Changed your mind? Let go below the arc and nothing happens."),
            L("Everything happens locally on your Mac. FileFlipper lives in the menu bar."),
        ]
        if FolderAccess.isSandboxed {
            paragraphs.append(L("The first time you convert a file in a folder, macOS asks you to allow FileFlipper to save there. Choose “Allow Home Folder” to grant access once for all your folders."))
        }
        alert.informativeText = paragraphs.joined(separator: "\n\n")
        alert.addButton(withTitle: L("Got It"))
        if FolderAccess.isSandboxed && FolderAccess.shared.grantedFolders.isEmpty {
            alert.addButton(withTitle: L("Allow Home Folder…"))
        }
        if alert.runModal() == .alertSecondButtonReturn {
            FolderAccess.shared.requestHomeAccess()
        }
    }
}
