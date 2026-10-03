import AVFoundation
import ImageIO
import UniformTypeIdentifiers

struct MediaTarget: FormatTarget {
    let title: String
    let ext: String
    var aliases: [String] = []
}

/// Video and audio conversions, built on AVFoundation.
/// These run on a background queue, so waiting synchronously for exports is fine.
enum MediaConverter {
    static let videoTargets: [MediaTarget] = [
        MediaTarget(title: "MP4", ext: "mp4"),
        MediaTarget(title: "MOV", ext: "mov", aliases: ["qt"]),
        MediaTarget(title: "GIF", ext: "gif"),
        MediaTarget(title: "M4A", ext: "m4a"),
    ]

    static let audioTargets: [MediaTarget] = [
        MediaTarget(title: "M4A", ext: "m4a"),
        MediaTarget(title: "WAV", ext: "wav", aliases: ["wave"]),
        MediaTarget(title: "AIFF", ext: "aiff", aliases: ["aif"]),
        MediaTarget(title: "CAF", ext: "caf"),
    ]

    // MARK: Convert

    static func convertVideo(_ url: URL, to target: MediaTarget) throws -> URL {
        switch target.ext {
        case "gif": return try toGIF(url)
        case "m4a": return try extractAudio(url, suffix: "")
        case "mov": return try export(url, preset: AVAssetExportPresetHighestQuality, fileType: .mov, ext: "mov")
        default: return try export(url, preset: AVAssetExportPresetHighestQuality, fileType: .mp4, ext: "mp4")
        }
    }

    static func convertAudio(_ url: URL, to target: MediaTarget) throws -> URL {
        if target.ext == "m4a" {
            return try export(url, preset: AVAssetExportPresetAppleM4A, fileType: .m4a, ext: "m4a")
        }
        return try writePCM(url, ext: target.ext, bigEndian: target.ext == "aiff")
    }

    // MARK: Video tools

    static func compressVideo(_ url: URL) throws -> URL {
        try FileInfo.requireSmaller(
            export(url, preset: AVAssetExportPresetMediumQuality, fileType: .mp4, ext: "mp4", suffix: " (compressed)"),
            than: url)
    }

    static func resizeVideo720(_ url: URL) throws -> URL {
        try export(url, preset: AVAssetExportPreset1280x720, fileType: .mp4, ext: "mp4", suffix: " (720p)")
    }

    static func extractAudio(_ url: URL) throws -> URL {
        try extractAudio(url, suffix: " (audio)")
    }

    private static func extractAudio(_ url: URL, suffix: String) throws -> URL {
        let asset = AVURLAsset(url: url)
        guard !asset.tracks(withMediaType: .audio).isEmpty else {
            throw ConversionError.message(L("%@ has no audio", url.lastPathComponent))
        }
        return try export(url, preset: AVAssetExportPresetAppleM4A, fileType: .m4a, ext: "m4a", suffix: suffix)
    }

    static func mute(_ url: URL) throws -> URL {
        let asset = AVURLAsset(url: url)
        let composition = AVMutableComposition()
        guard let videoTrack = asset.tracks(withMediaType: .video).first,
              let track = composition.addMutableTrack(withMediaType: .video,
                                                      preferredTrackID: kCMPersistentTrackID_Invalid) else {
            throw ConversionError.message(L("%@ has no video", url.lastPathComponent))
        }
        try track.insertTimeRange(CMTimeRange(start: .zero, duration: asset.duration), of: videoTrack, at: .zero)
        track.preferredTransform = videoTrack.preferredTransform

        let isMP4 = ["mp4", "m4v"].contains(url.pathExtension.lowercased())
        return try export(url, asset: composition, preset: AVAssetExportPresetPassthrough,
                          fileType: isMP4 ? .mp4 : .mov, ext: isMP4 ? "mp4" : "mov", suffix: " (muted)")
    }

    static func snapshot(_ url: URL) throws -> URL {
        let asset = AVURLAsset(url: url)
        let generator = AVAssetImageGenerator(asset: asset)
        generator.appliesPreferredTrackTransform = true
        let seconds = CMTimeGetSeconds(asset.duration)
        let time = CMTime(seconds: seconds.isFinite ? min(1, seconds / 2) : 0, preferredTimescale: 600)
        let image = try generator.copyCGImage(at: time, actualTime: nil)
        let output = OutputNaming.next(to: url, suffix: " (snapshot)", ext: "png")
        return try ImageConverter.write(image, to: output, type: .png)
    }

    /// Up to the first 15 seconds at 10 fps, max 480 px.
    static func toGIF(_ url: URL) throws -> URL {
        let asset = AVURLAsset(url: url)
        let duration = min(CMTimeGetSeconds(asset.duration), 15)
        guard duration.isFinite, duration > 0 else { throw ConversionError.unreadable(url) }

        let generator = AVAssetImageGenerator(asset: asset)
        generator.appliesPreferredTrackTransform = true
        generator.maximumSize = CGSize(width: 480, height: 480)
        generator.requestedTimeToleranceBefore = .zero
        generator.requestedTimeToleranceAfter = .zero

        let fps = 10.0
        var frames: [CGImage] = []
        for index in 0..<max(1, Int(duration * fps)) {
            let time = CMTime(seconds: Double(index) / fps, preferredTimescale: 600)
            if let frame = try? generator.copyCGImage(at: time, actualTime: nil) {
                frames.append(frame)
            }
        }
        guard !frames.isEmpty else { throw ConversionError.message(L("Couldn't read frames from %@", url.lastPathComponent)) }

        let output = OutputNaming.next(to: url, ext: "gif")
        guard let destination = CGImageDestinationCreateWithURL(output as CFURL, UTType.gif.identifier as CFString,
                                                                 frames.count, nil) else {
            throw ConversionError.unsupported("GIF")
        }
        let fileProperties = [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFLoopCount: 0]] as CFDictionary
        let frameProperties = [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFDelayTime: 1 / fps]] as CFDictionary
        CGImageDestinationSetProperties(destination, fileProperties)
        for frame in frames {
            CGImageDestinationAddImage(destination, frame, frameProperties)
        }
        guard CGImageDestinationFinalize(destination) else { throw ConversionError.writeFailed(output) }
        return output
    }

    // MARK: Audio tools

    static func compressAudio(_ url: URL) throws -> URL {
        try FileInfo.requireSmaller(
            export(url, preset: AVAssetExportPresetAppleM4A, fileType: .m4a, ext: "m4a", suffix: " (compressed)"),
            than: url)
    }

    static func mono(_ url: URL) throws -> URL {
        let input = try AVAudioFile(forReading: url)
        let inputFormat = input.processingFormat
        guard let monoFormat = AVAudioFormat(standardFormatWithSampleRate: inputFormat.sampleRate, channels: 1),
              let converter = AVAudioConverter(from: inputFormat, to: monoFormat) else {
            throw ConversionError.unsupported(L("Mono conversion"))
        }
        converter.downmix = true

        let output = OutputNaming.next(to: url, suffix: " (mono)", ext: "wav")
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: inputFormat.sampleRate,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsBigEndianKey: false,
        ]
        let file = try AVAudioFile(forWriting: output, settings: settings,
                                   commonFormat: monoFormat.commonFormat, interleaved: monoFormat.isInterleaved)

        let capacity: AVAudioFrameCount = 32_768
        guard let inBuffer = AVAudioPCMBuffer(pcmFormat: inputFormat, frameCapacity: capacity),
              let outBuffer = AVAudioPCMBuffer(pcmFormat: monoFormat, frameCapacity: capacity) else {
            throw ConversionError.message(L("Couldn't allocate audio buffers"))
        }
        while input.framePosition < input.length {
            try input.read(into: inBuffer)
            if inBuffer.frameLength == 0 { break }
            try converter.convert(to: outBuffer, from: inBuffer)
            try file.write(from: outBuffer)
        }
        return output
    }

    // MARK: Helpers

    /// Decodes any readable audio file and writes 16-bit PCM (WAV, AIFF or CAF, picked by extension).
    private static func writePCM(_ url: URL, ext: String, bigEndian: Bool) throws -> URL {
        let input = try AVAudioFile(forReading: url)
        let format = input.processingFormat
        let output = OutputNaming.next(to: url, ext: ext)
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: format.sampleRate,
            AVNumberOfChannelsKey: format.channelCount,
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsBigEndianKey: bigEndian,
        ]
        let file = try AVAudioFile(forWriting: output, settings: settings,
                                   commonFormat: format.commonFormat, interleaved: format.isInterleaved)
        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 32_768) else {
            throw ConversionError.message(L("Couldn't allocate an audio buffer"))
        }
        while input.framePosition < input.length {
            try input.read(into: buffer)
            if buffer.frameLength == 0 { break }
            try file.write(from: buffer)
        }
        return output
    }

    private static func export(_ url: URL, asset: AVAsset? = nil, preset: String, fileType: AVFileType,
                               ext: String, suffix: String = "") throws -> URL {
        let asset = asset ?? AVURLAsset(url: url)
        guard let session = AVAssetExportSession(asset: asset, presetName: preset),
              session.supportedFileTypes.contains(fileType) else {
            throw ConversionError.unsupported("\(ext.uppercased()) export for \(url.lastPathComponent)")
        }
        let output = OutputNaming.next(to: url, suffix: suffix, ext: ext)
        session.outputURL = output
        session.outputFileType = fileType
        session.shouldOptimizeForNetworkUse = true

        let done = DispatchSemaphore(value: 0)
        session.exportAsynchronously { done.signal() }
        done.wait()

        guard session.status == .completed else {
            try? FileManager.default.removeItem(at: output)
            throw ConversionError.message(session.error?.localizedDescription ?? L("Export failed"))
        }
        return output
    }
}
