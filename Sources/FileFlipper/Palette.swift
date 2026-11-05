import AppKit

/// FileFlipper's colours (deep green + mint), shared by the picker, crop window and toast.
enum Palette {
    private static func hex(_ value: UInt32, _ alpha: CGFloat = 1) -> NSColor {
        NSColor(srgbRed: CGFloat((value >> 16) & 0xFF) / 255, green: CGFloat((value >> 8) & 0xFF) / 255,
                blue: CGFloat(value & 0xFF) / 255, alpha: alpha)
    }

    /// Main action / hover colour.
    static let accent = hex(0x1F7A63)
    /// Soft mint for secondary buttons and highlights.
    static let mint = hex(0xD3EBDF)
    /// Text colour: a deep green-black.
    static let ink = hex(0x16332D)
    static let cream = hex(0xF5F9F7)
    static let canvas = hex(0x13221E)
    /// Picker bubbles.
    static let bubble = hex(0xF8FBF9, 0.98)
    static let bubbleBorder = hex(0xC6DED2)
    static let icon = hex(0x1F6B57)
}
