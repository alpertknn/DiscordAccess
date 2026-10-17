import AppKit

@main
struct RenderIcons {
    static func main() throws {
        guard CommandLine.arguments.count == 3 else { fatalError("Usage: render-icons <iconset folder> <output.icns>") }
        let root = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        var entries = Data()
        func bigEndian(_ value: UInt32) -> Data {
            var number = value.bigEndian
            return withUnsafeBytes(of: &number) { Data($0) }
        }
        let types = [16: "icp4", 32: "icp5", 64: "icp6", 128: "ic07", 256: "ic08", 512: "ic09", 1024: "ic10"]
        var included = Set<Int>()
        for points in [16, 32, 128, 256, 512] {
            for scale in [1, 2] {
                let pixels = points * scale
                let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels,
                    bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
                NSGraphicsContext.saveGraphicsState()
                NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
                BrandIcon.drawAppIcon(size: CGFloat(pixels))
                NSGraphicsContext.restoreGraphicsState()
                let suffix = scale == 2 ? "@2x" : ""
                let png = bitmap.representation(using: .png, properties: [:])!
                try png.write(to: root.appendingPathComponent("icon_\(points)x\(points)\(suffix).png"))
                if included.insert(pixels).inserted, let type = types[pixels] {
                    entries.append(Data(type.utf8))
                    entries.append(bigEndian(UInt32(png.count + 8)))
                    entries.append(png)
                }
            }
        }
        var icns = Data("icns".utf8)
        icns.append(bigEndian(UInt32(entries.count + 8)))
        icns.append(entries)
        try icns.write(to: URL(fileURLWithPath: CommandLine.arguments[2]))
    }
}
