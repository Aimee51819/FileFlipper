import AppKit
import ImageIO
import UniformTypeIdentifiers

struct ImageTarget: FormatTarget {
    let title: String
    let type: UTType
    let ext: String
    var aliases: [String] = []
}

/// Image conversions and tools, built on ImageIO (the same codecs Preview uses).
enum ImageConverter {
    static let targets: [ImageTarget] = [
        ImageTarget(title: "PNG", type: .png, ext: "png"),
        ImageTarget(title: "JPG", type: .jpeg, ext: "jpg", aliases: ["jpeg", "jpe"]),
        ImageTarget(title: "HEIC", type: .heic, ext: "heic", aliases: ["heif"]),
        ImageTarget(title: "WEBP", type: .webP, ext: "webp"),
        ImageTarget(title: "TIFF", type: .tiff, ext: "tiff", aliases: ["tif"]),
        ImageTarget(title: "GIF", type: .gif, ext: "gif"),
        ImageTarget(title: "BMP", type: .bmp, ext: "bmp"),
    ]

    private static let writableTypes: Set<String> =
        Set((CGImageDestinationCopyTypeIdentifiers() as? [String]) ?? [])

    static func canWrite(_ type: UTType) -> Bool {
        writableTypes.contains(type.identifier)
    }

    // MARK: Convert

    static func convert(_ url: URL, to type: UTType, ext: String) throws -> URL {
        let source = try imageSource(url)
        // Keep every frame/page for formats that support it.
        let keepsFrames = type == .gif || type == .tiff
        let count = keepsFrames ? CGImageSourceGetCount(source) : 1
        let output = OutputNaming.next(to: url, ext: ext)
        guard let destination = CGImageDestinationCreateWithURL(output as CFURL, type.identifier as CFString, count, nil) else {
            throw ConversionError.unsupported(ext.uppercased())
        }

        let opaqueFormat = type == .jpeg || type == .bmp
        for index in 0..<count {
            var properties = (CGImageSourceCopyPropertiesAtIndex(source, index, nil) as? [CFString: Any]) ?? [:]
            properties[kCGImageDestinationLossyCompressionQuality] = 0.9
            if opaqueFormat, let image = CGImageSourceCreateImageAtIndex(source, index, nil), image.hasAlpha,
               let flattened = flatten(image) {
                CGImageDestinationAddImage(destination, flattened, properties as CFDictionary)
            } else {
                CGImageDestinationAddImageFromSource(destination, source, index, properties as CFDictionary)
            }
        }
        return try finalize(destination, output)
    }

    // MARK: Tools

    static func compress(_ url: URL) throws -> URL {
        let source = try imageSource(url)
        let sourceType = format(of: source)
        let lossy: Set<UTType> = [.jpeg, .heic, .webP]
        let target: (UTType, String)
        if let match = lossy.first(where: { sourceType.conforms(to: $0) }), canWrite(match) {
            target = (match, url.pathExtension.lowercased())
        } else if let image = CGImageSourceCreateImageAtIndex(source, 0, nil), image.hasAlpha {
            target = (.heic, "heic")   // keeps transparency
        } else {
            target = (.jpeg, "jpg")
        }

        let output = OutputNaming.next(to: url, suffix: " (compressed)", ext: target.1)
        guard let destination = CGImageDestinationCreateWithURL(output as CFURL, target.0.identifier as CFString, 1, nil) else {
            throw ConversionError.unsupported(target.1.uppercased())
        }
        let properties: [CFString: Any] = [kCGImageDestinationLossyCompressionQuality: 0.6]
        CGImageDestinationAddImageFromSource(destination, source, 0, properties as CFDictionary)
        return try FileInfo.requireSmaller(finalize(destination, output), than: url)
    }

    /// Opens a crop window; saves the selected area as a new image.
    static func crop(_ url: URL) throws -> URL {
        let source = try imageSource(url)
        let image = try orientedImage(source)
        guard let rect = CropWindow.run(image: image, fileName: url.lastPathComponent),
              let cropped = image.cropping(to: rect) else {
            throw ConversionError.cancelled
        }
        return try save(cropped, near: url, suffix: " (cropped)", like: source)
    }

    /// Re-encodes the pixels only, dropping EXIF, GPS, camera and other metadata.
    static func stripMetadata(_ url: URL) throws -> URL {
        let source = try imageSource(url)
        let image = try orientedImage(source)
        return try save(image, near: url, suffix: " (no metadata)", like: source)
    }

    static func resizeHalf(_ url: URL) throws -> URL {
        let source = try imageSource(url)
        let longest = pixelSize(source).map { max($0.width, $0.height) } ?? 0
        let image = try orientedImage(source, maxPixelSize: max(1, longest / 2))
        return try save(image, near: url, suffix: " (50%)", like: source)
    }

    static func rotate(_ url: URL) throws -> URL {
        let source = try imageSource(url)
        let image = try orientedImage(source)
        let rotated = try draw(width: image.height, height: image.width) { context in
            // 90° clockwise
            context.translateBy(x: 0, y: CGFloat(image.width))
            context.rotate(by: -.pi / 2)
            context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        }
        return try save(rotated, near: url, suffix: " (rotated)", like: source)
    }

    static func flip(_ url: URL) throws -> URL {
        let source = try imageSource(url)
        let image = try orientedImage(source)
        let flipped = try draw(width: image.width, height: image.height) { context in
            context.translateBy(x: CGFloat(image.width), y: 0)
            context.scaleBy(x: -1, y: 1)
            context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        }
        return try save(flipped, near: url, suffix: " (flipped)", like: source)
    }

    static func grayscale(_ url: URL) throws -> URL {
        let source = try imageSource(url)
        let image = try orientedImage(source)
        guard let context = CGContext(data: nil, width: image.width, height: image.height, bitsPerComponent: 8,
                                      bytesPerRow: 0, space: CGColorSpaceCreateDeviceGray(),
                                      bitmapInfo: CGImageAlphaInfo.none.rawValue) else {
            throw ConversionError.message(L("Couldn't create a grayscale canvas"))
        }
        context.setFillColor(gray: 1, alpha: 1)
        context.fill(CGRect(x: 0, y: 0, width: image.width, height: image.height))
        context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        guard let gray = context.makeImage() else { throw ConversionError.message(L("Couldn't render the image")) }
        return try save(gray, near: url, suffix: " (grayscale)", like: source)
    }

    /// See `BackgroundRemover` for how plain backgrounds and photos are handled.
    static func removeBackground(_ url: URL) throws -> URL {
        let image = try orientedImage(try imageSource(url))
        let cutout = try BackgroundRemover.removeBackground(from: image, name: url.lastPathComponent)
        let output = OutputNaming.next(to: url, suffix: " (no background)", ext: "png")
        return try write(cutout, to: output, type: .png)
    }

    // MARK: Shared helpers

    static func imageSource(_ url: URL) throws -> CGImageSource {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil), CGImageSourceGetCount(source) > 0 else {
            throw ConversionError.unreadable(url)
        }
        return source
    }

    static func write(_ image: CGImage, to output: URL, type: UTType, quality: Double = 0.9) throws -> URL {
        guard let destination = CGImageDestinationCreateWithURL(output as CFURL, type.identifier as CFString, 1, nil) else {
            throw ConversionError.unsupported(output.pathExtension.uppercased())
        }
        let properties: [CFString: Any] = [kCGImageDestinationLossyCompressionQuality: quality]
        CGImageDestinationAddImage(destination, image, properties as CFDictionary)
        return try finalize(destination, output)
    }

    /// Saves in the same format as the source when possible, otherwise PNG.
    private static func save(_ image: CGImage, near url: URL, suffix: String, like source: CGImageSource) throws -> URL {
        let sourceType = format(of: source)
        let sameFormat = canWrite(sourceType)
        let ext = sameFormat ? url.pathExtension.lowercased() : "png"
        let output = OutputNaming.next(to: url, suffix: suffix, ext: ext)
        return try write(image, to: output, type: sameFormat ? sourceType : .png)
    }

    private static func finalize(_ destination: CGImageDestination, _ output: URL) throws -> URL {
        guard CGImageDestinationFinalize(destination) else {
            try? FileManager.default.removeItem(at: output)
            throw ConversionError.writeFailed(output)
        }
        return output
    }

    private static func format(of source: CGImageSource) -> UTType {
        (CGImageSourceGetType(source) as String?).flatMap { UTType($0) } ?? .png
    }

    private static func pixelSize(_ source: CGImageSource) -> (width: Int, height: Int)? {
        guard let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int,
              let height = properties[kCGImagePropertyPixelHeight] as? Int else { return nil }
        return (width, height)
    }

    /// Decodes the first frame with its EXIF orientation applied.
    private static func orientedImage(_ source: CGImageSource, maxPixelSize: Int? = nil) throws -> CGImage {
        let longest = pixelSize(source).map { max($0.width, $0.height) } ?? 0
        let size = maxPixelSize ?? longest
        if size > 0 {
            let options: [CFString: Any] = [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceShouldCacheImmediately: true,
                kCGImageSourceThumbnailMaxPixelSize: size,
            ]
            if let image = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) {
                return image
            }
        }
        guard let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
            throw ConversionError.message(L("Couldn't decode the image"))
        }
        return image
    }

    private static func draw(width: Int, height: Int, _ body: (CGContext) -> Void) throws -> CGImage {
        guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw ConversionError.message(L("Couldn't create a canvas"))
        }
        context.interpolationQuality = .high
        body(context)
        guard let image = context.makeImage() else { throw ConversionError.message(L("Couldn't render the image")) }
        return image
    }

    /// JPEG and BMP have no transparency, so composite onto white first.
    static func flatten(_ image: CGImage) -> CGImage? {
        try? draw(width: image.width, height: image.height) { context in
            context.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
            context.fill(CGRect(x: 0, y: 0, width: image.width, height: image.height))
            context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        }
    }
}

extension CGImage {
    var hasAlpha: Bool {
        switch alphaInfo {
        case .none, .noneSkipFirst, .noneSkipLast: return false
        default: return true
        }
    }
}
