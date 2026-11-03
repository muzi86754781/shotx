import Cocoa

/// 物理像素与逻辑点精准换算与对齐工具：解决跨 DPI 屏幕、Retina 高低分屏时的 1px 亚像素模糊与边缘采样漂移
enum PixelAlignment {
    /// 将逻辑点 rect 按照目标屏幕的 backingScaleFactor 像素对齐
    static func alignedRect(_ rect: CGRect, scale: CGFloat) -> CGRect {
        guard scale > 0 else { return rect.integral }
        let x = (rect.origin.x * scale).rounded() / scale
        let y = (rect.origin.y * scale).rounded() / scale
        let w = (rect.width * scale).rounded() / scale
        let h = (rect.height * scale).rounded() / scale
        return CGRect(x: x, y: y, width: max(0, w), height: max(0, h))
    }

    /// 按照屏幕像素比例对齐点
    static func alignedPoint(_ point: CGPoint, scale: CGFloat) -> CGPoint {
        guard scale > 0 else { return point }
        return CGPoint(
            x: (point.x * scale).rounded() / scale,
            y: (point.y * scale).rounded() / scale
        )
    }

    /// 按照屏幕像素比例对齐尺寸
    static func alignedSize(_ size: CGSize, scale: CGFloat) -> CGSize {
        guard scale > 0 else { return size }
        return CGSize(
            width: (size.width * scale).rounded() / scale,
            height: (size.height * scale).rounded() / scale
        )
    }

    /// 查找与目标矩形重合面积最大的屏幕，获取真实物理缩放比
    static func primaryScreen(for rect: CGRect) -> NSScreen {
        var bestScreen: NSScreen = NSScreen.main ?? NSScreen.screens.first ?? NSScreen()
        var maxArea: CGFloat = 0

        for screen in NSScreen.screens {
            let intersection = screen.frame.intersection(rect)
            if !intersection.isNull && !intersection.isEmpty {
                let area = intersection.width * intersection.height
                if area > maxArea {
                    maxArea = area
                    bestScreen = screen
                }
            }
        }
        return bestScreen
    }

    /// 获取最佳显示器的 backingScaleFactor
    static func scale(for rect: CGRect) -> CGFloat {
        return primaryScreen(for: rect).backingScaleFactor
    }
}
