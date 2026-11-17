// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "FileFlipper",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "FileFlipper",
            path: "Sources/FileFlipper"
        ),
        // Regression tests for the conversion logic. Run with `swift test` (CI runs them on every push).
        .testTarget(
            name: "FileFlipperTests",
            dependencies: ["FileFlipper"],
            path: "Tests/FileFlipperTests"
        ),
    ]
)
