import AppKit

/// Shared vector artwork for the status item and the packaged app icon.
enum BrandIcon {
    // 128 × 96 design grid; generous counters survive at menu bar sizes.
    static func monogram() -> NSBezierPath {
        let p = NSBezierPath()
        p.windingRule = .evenOdd
        p.move(to: NSPoint(x: 8, y: 16))
        p.line(to: NSPoint(x: 36, y: 16))
        p.curve(to: NSPoint(x: 72, y: 48), controlPoint1: NSPoint(x: 59, y: 16), controlPoint2: NSPoint(x: 72, y: 28))
        p.curve(to: NSPoint(x: 36, y: 80), controlPoint1: NSPoint(x: 72, y: 68), controlPoint2: NSPoint(x: 59, y: 80))
        p.line(to: NSPoint(x: 8, y: 80)); p.close()
        p.move(to: NSPoint(x: 26, y: 32)); p.line(to: NSPoint(x: 36, y: 32))
        p.curve(to: NSPoint(x: 54, y: 48), controlPoint1: NSPoint(x: 48, y: 32), controlPoint2: NSPoint(x: 54, y: 38))
        p.curve(to: NSPoint(x: 36, y: 64), controlPoint1: NSPoint(x: 54, y: 58), controlPoint2: NSPoint(x: 48, y: 64))
        p.line(to: NSPoint(x: 26, y: 64)); p.close()
        return p
    }

    static func aShape() -> NSBezierPath {
        let p = NSBezierPath()
        p.windingRule = .evenOdd
        p.move(to: NSPoint(x: 52, y: 16)); p.line(to: NSPoint(x: 80, y: 80))
        p.line(to: NSPoint(x: 98, y: 80)); p.line(to: NSPoint(x: 126, y: 16))
        p.line(to: NSPoint(x: 106, y: 16)); p.line(to: NSPoint(x: 101, y: 29))
        p.line(to: NSPoint(x: 77, y: 29)); p.line(to: NSPoint(x: 72, y: 16)); p.close()
        p.move(to: NSPoint(x: 83, y: 45)); p.line(to: NSPoint(x: 89, y: 61))
        p.line(to: NSPoint(x: 95, y: 45)); p.close()
        return p
    }

    static func drawMark(in rect: NSRect, color: NSColor) {
        NSGraphicsContext.saveGraphicsState()
        let transform = AffineTransform(translationByX: rect.minX, byY: rect.minY)
        var scaled = transform
        scaled.scale(x: rect.width / 128, y: rect.height / 96)
        color.setFill()
        for path in [monogram(), aShape()] { path.transform(using: scaled); path.fill() }
        NSGraphicsContext.restoreGraphicsState()
    }

    static let statusImage: NSImage = {
        let image = NSImage(size: NSSize(width: 22, height: 18), flipped: false) { _ in
            drawMark(in: NSRect(x: 0, y: 0.75, width: 21, height: 15.75), color: .black)
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = "DiscordAccess"
        return image
    }()

    static func drawAppIcon(size: CGFloat) {
        let tile = NSRect(x: size * 0.05, y: size * 0.05, width: size * 0.9, height: size * 0.9)
        NSColor(srgbRed: 88 / 255, green: 101 / 255, blue: 242 / 255, alpha: 1).setFill()
        NSBezierPath(roundedRect: tile, xRadius: size * 0.20, yRadius: size * 0.20).fill()
        drawMark(in: NSRect(x: size * 0.13, y: size * 0.2225, width: size * 0.72, height: size * 0.54), color: .white)
    }
}
