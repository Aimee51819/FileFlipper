// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "FileFlipper",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "FileFlipper",
            path: "Sources/FileFlipper"
        )
    ]
)
