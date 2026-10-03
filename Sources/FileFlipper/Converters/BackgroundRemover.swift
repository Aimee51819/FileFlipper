import CoreGraphics
import CoreImage
import Vision

/// The image "Remove BG" tool.
///
/// Pictures on a plain background (logos, icons, screenshots, product shots on white) are
/// handled like a magic wand: the background colour is flood-filled from the edges, and the
/// soft rim around the subject is made semi-transparent with the background colour taken
/// out of it, so no light fringe is left behind. Everything else goes to Vision's subject
/// lifting (the same feature as "Copy Subject" in Photos), which is trained on photos.
enum BackgroundRemover {
    static func removeBackground(from image: CGImage, name: String) throws -> CGImage {
        if let cutout = removeSolidBackground(image) {
            return cutout
        }
        return try liftSubject(image, name: name)
    }

    // MARK: Plain backgrounds

    /// Colour distance (0…441) up to which a pixel counts as background.
    private static let tolerance: Double = 36
    /// Pixels between `tolerance` and this distance near the background become semi-transparent.
    private static let softLimit: Double = 80
    /// How far (in pixels) the soft rim may reach into the subject.
    private static let softDepth = 6

    /// Returns `nil` when the background isn't a single plain colour.
    static func removeSolidBackground(_ image: CGImage) -> CGImage? {
        let width = image.width, height = image.height
        guard width > 2, height > 2 else { return nil }
        let count = width * height
        var pixels = [UInt8](repeating: 0, count: count * 4)
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        let bitmapInfo = CGImageAlphaInfo.premultipliedLast.rawValue

        let drawn = pixels.withUnsafeMutableBytes { buffer -> Bool in
            guard let context = CGContext(data: buffer.baseAddress, width: width, height: height,
                                          bitsPerComponent: 8, bytesPerRow: width * 4,
                                          space: space, bitmapInfo: bitmapInfo) else { return false }
            context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
            return true
        }
        guard drawn else { return nil }

        // 1. Estimate the background colour from the outermost pixels.
        var border: [Int] = []
        border.reserveCapacity(2 * (width + height))
        for x in 0..<width { border.append(x); border.append((height - 1) * width + x) }
        for y in 1..<(height - 1) { border.append(y * width); border.append(y * width + width - 1) }

        let opaqueBorder = border.filter { pixels[$0 * 4 + 3] > 250 }
        // A mostly transparent border means the background is already gone (or there is none).
        guard opaqueBorder.count * 2 > border.count else { return nil }
        func median(_ channel: Int) -> Double {
            let values = opaqueBorder.map { pixels[$0 * 4 + channel] }.sorted()
            return Double(values[values.count / 2])
        }
        let background = (median(0), median(1), median(2))

        func distance(_ index: Int) -> Double {
            let offset = index * 4
            let alpha = Double(pixels[offset + 3])
            if alpha < 8 { return 0 }   // already transparent: treat as background
            // Un-premultiply before comparing.
            let scale = 255 / alpha
            let dr = Double(pixels[offset]) * scale - background.0
            let dg = Double(pixels[offset + 1]) * scale - background.1
            let db = Double(pixels[offset + 2]) * scale - background.2
            return (dr * dr + dg * dg + db * db).squareRoot()
        }

        // 2. It has to be a *plain* background: nearly all of the border must match it.
        let matching = border.filter { distance($0) <= tolerance }.count
        guard Double(matching) >= Double(border.count) * 0.85 else { return nil }

        // 3. Flood-fill the background from the edges.
        var isBackground = [Bool](repeating: false, count: count)
        var queue: [Int] = []
        queue.reserveCapacity(count / 4)
        for index in border where !isBackground[index] && distance(index) <= tolerance {
            isBackground[index] = true
            queue.append(index)
        }
        var head = 0
        while head < queue.count {
            let index = queue[head]
            head += 1
            forEachNeighbour(of: index, width: width, height: height) { next in
                if !isBackground[next] && distance(next) <= tolerance {
                    isBackground[next] = true
                    queue.append(next)
                }
            }
        }
        guard head > 0, head < count else { return nil }   // nothing found, or nothing left

        // 4. Soft rim: walk a few pixels into the subject from the background edge.
        var alpha = [Double](repeating: 1, count: count)
        var depth = [Int](repeating: -1, count: count)
        var rim: [Int] = []
        for index in queue {
            alpha[index] = 0
            forEachNeighbour(of: index, width: width, height: height) { next in
                if !isBackground[next] && depth[next] < 0 {
                    depth[next] = 1
                    rim.append(next)
                }
            }
        }
        head = 0
        while head < rim.count {
            let index = rim[head]
            head += 1
            let d = distance(index)
            guard d < softLimit else { continue }   // solid subject: stop here
            alpha[index] = min(alpha[index], max(0, (d - tolerance) / (softLimit - tolerance)))
            guard depth[index] < softDepth else { continue }
            forEachNeighbour(of: index, width: width, height: height) { next in
                if !isBackground[next] && depth[next] < 0 {
                    depth[next] = depth[index] + 1
                    rim.append(next)
                }
            }
        }

        // 5. Apply: remove the background colour that bled into semi-transparent pixels.
        for index in 0..<count where alpha[index] < 1 {
            let offset = index * 4
            let a = alpha[index]
            let original = Double(pixels[offset + 3]) / 255
            let newAlpha = a * original
            // Premultiplied colour of the subject = observed colour − (1 − a) × background.
            let channels = [background.0, background.1, background.2]
            for channel in 0..<3 {
                let observed = original > 0 ? Double(pixels[offset + channel]) / original : 0
                let subject = observed - (1 - a) * channels[channel]
                // Premultiplied values can never exceed the alpha.
                pixels[offset + channel] = UInt8(max(0, min(newAlpha * 255, subject * original)))
            }
            pixels[offset + 3] = UInt8(max(0, min(255, newAlpha * 255)))
        }

        return pixels.withUnsafeMutableBytes { buffer -> CGImage? in
            CGContext(data: buffer.baseAddress, width: width, height: height, bitsPerComponent: 8,
                      bytesPerRow: width * 4, space: space, bitmapInfo: bitmapInfo)?.makeImage()
        }
    }

    private static func forEachNeighbour(of index: Int, width: Int, height: Int, _ body: (Int) -> Void) {
        let x = index % width
        if x > 0 { body(index - 1) }
        if x < width - 1 { body(index + 1) }
        if index >= width { body(index - width) }
        if index < (height - 1) * width { body(index + width) }
    }

    // MARK: Photos

    private static func liftSubject(_ image: CGImage, name: String) throws -> CGImage {
        let request = VNGenerateForegroundInstanceMaskRequest()
        let handler = VNImageRequestHandler(cgImage: image)
        try handler.perform([request])
        guard let result = request.results?.first, !result.allInstances.isEmpty else {
            throw ConversionError.message("No subject found in \(name)")
        }
        let buffer = try result.generateMaskedImage(ofInstances: result.allInstances, from: handler,
                                                    croppedToInstancesExtent: false)
        let ciImage = CIImage(cvPixelBuffer: buffer)
        guard let cutout = CIContext().createCGImage(ciImage, from: ciImage.extent) else {
            throw ConversionError.message("Couldn't render the cutout")
        }
        return cutout
    }
}
