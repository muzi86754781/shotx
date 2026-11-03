import Cocoa
import CoreGraphics

/// 长截图核心拼接引擎：负责滚动位图的对比度采样、位移检测与垂直无缝拼接
final class ScrollStitcher {
    private(set) var stitchedLongCG: CGImage?
    private(set) var stitchedLongImage: NSImage?
    private(set) var lastStitchedCG: CGImage?
    private(set) var isStitching: Bool = false

    /// 最大高度限制（32000px），避免超出 CoreGraphics / GPU 位图显存极限
    static let maxHeightLimit: Int = 32000

    func reset() {
        lastStitchedCG = nil
        stitchedLongCG = nil
        stitchedLongImage = nil
        isStitching = false
    }

    func initialize(with initialCG: CGImage, initialSize: NSSize) {
        lastStitchedCG = initialCG
        stitchedLongCG = initialCG
        stitchedLongImage = NSImage(cgImage: initialCG, size: initialSize)
        isStitching = false
    }

    func appendSlice(freshCG: CGImage, selectionSize: NSSize) -> (didStitch: Bool, reachedLimit: Bool) {
        if isStitching { return (false, false) }
        isStitching = true
        defer { isStitching = false }

        guard let baseCG = lastStitchedCG, let currentLong = stitchedLongCG else {
            initialize(with: freshCG, initialSize: selectionSize)
            return (true, false)
        }

        if currentLong.height >= Self.maxHeightLimit {
            return (false, true)
        }

        let (hasMovement, S) = detectScrollMovement(baseCG: baseCG, freshCG: freshCG)
        guard hasMovement, S >= 3 else { return (false, false) }

        // 新增露出的底部条带为 freshCG 的 [height - S, height]
        let sliceRect = CGRect(x: 0, y: freshCG.height - S, width: freshCG.width, height: S)
        guard let sliceCG = freshCG.cropping(to: sliceRect) else { return (false, false) }

        let newH = currentLong.height + S
        let cs = CGColorSpaceCreateDeviceRGB()
        guard let ctx = CGContext(
            data: nil,
            width: currentLong.width,
            height: newH,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: cs,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return (false, false) }

        // CGContext 坐标系（原点在左下角）：
        // 旧内容上移至 y = S，新条带绘制在底部 y = 0
        ctx.draw(currentLong, in: CGRect(x: 0, y: S, width: currentLong.width, height: currentLong.height))
        ctx.draw(sliceCG, in: CGRect(x: 0, y: 0, width: sliceCG.width, height: S))
        guard let newCompositeCG = ctx.makeImage() else { return (false, false) }

        self.stitchedLongCG = newCompositeCG
        self.lastStitchedCG = freshCG

        let scale = CGFloat(newH) / CGFloat(freshCG.height)
        let newPointSize = NSSize(width: selectionSize.width, height: selectionSize.height * scale)
        self.stitchedLongImage = NSImage(cgImage: newCompositeCG, size: newPointSize)

        return (true, false)
    }

    private func detectScrollMovement(baseCG: CGImage, freshCG: CGImage) -> (hasMovement: Bool, deltaY: Int) {
        let w = baseCG.width
        let h = baseCG.height
        guard w == freshCG.width, h == freshCG.height, h > 60, w > 40 else { return (false, 0) }

        guard let bufA = getGrayscalePixelBuffer(cg: baseCG),
              let bufB = getGrayscalePixelBuffer(cg: freshCG) else { return (false, 0) }

        // 1. 横向宽幅 32 列采样：避开左右边距（各保留 16px 边距，杜绝垂直滚动条干扰）
        let marginX = min(20, w / 10)
        let sampleWidth = w - 2 * marginX
        guard sampleWidth > 32 else { return (false, 0) }
        var sampleCols: [Int] = []
        let colStep = sampleWidth / 32
        for i in 0..<32 {
            sampleCols.append(marginX + i * colStep)
        }

        // 2. 锚定带（Anchor）：选取 baseCG 底部上方一段具有明确特征的内容条带
        let anchorH = min(56, h / 3)
        let marginBottom = 8
        var anchorTop = h - marginBottom - anchorH

        // 寻找下半区对比度最强的文本行作为锚点
        var bestVar = 0.0
        var bestTop = anchorTop
        let searchMinTop = max(10, h / 2)
        for candTop in stride(from: anchorTop, through: searchMinTop, by: -10) {
            var sum = 0.0
            var sumSq = 0.0
            var cnt = 0
            for r in stride(from: 0, to: anchorH, by: 4) {
                let row = (candTop + r) * w
                for c in sampleCols {
                    let val = Double(bufA[row + c])
                    sum += val
                    sumSq += val * val
                    cnt += 1
                }
            }
            if cnt > 0 {
                let mean = sum / Double(cnt)
                let variance = (sumSq / Double(cnt)) - (mean * mean)
                if variance > bestVar {
                    bestVar = variance
                    bestTop = candTop
                }
            }
        }

        // 纯平坦区域（纯白或纯色背景）方差极小，不作盲目误匹配
        if bestVar < 2.0 {
            return (false, 0)
        }
        anchorTop = bestTop

        // 3. 快速零位移检测（静止时直接返回，彻底消除静态重复行）
        var zeroDiff = 0.0
        var zeroCount = 0
        for r in stride(from: 0, to: anchorH, by: 2) {
            let rowA = (anchorTop + r) * w
            for c in sampleCols {
                zeroDiff += abs(Double(bufA[rowA + c]) - Double(bufB[rowA + c]))
                zeroCount += 1
            }
        }
        if zeroCount > 0 && (zeroDiff / Double(zeroCount)) < 1.2 {
            return (false, 0)
        }

        // 4. 在 freshCG 中向上搜索该 Anchor 的匹配位置
        let maxS = min(Int(CGFloat(h) * 0.45), anchorTop)
        guard maxS >= 3 else { return (false, 0) }

        var bestS = 0
        var bestErr = Double.greatestFiniteMagnitude

        for S in 3...maxS {
            let y = anchorTop - S
            guard y >= 0 else { break }

            var diff = 0.0
            var count = 0
            for r in stride(from: 0, to: anchorH, by: 2) {
                let rowA = (anchorTop + r) * w
                let rowB = (y + r) * w
                for c in sampleCols {
                    diff += abs(Double(bufA[rowA + c]) - Double(bufB[rowB + c]))
                    count += 1
                }
            }
            if count > 0 {
                let err = diff / Double(count)
                if err < bestErr {
                    bestErr = err
                    bestS = S
                }
            }
        }

        // 5. 严格像素级吻合判定：平均误差小于 3.5 级灰度且位移 >= 3px
        if bestErr < 3.5 && bestS >= 3 {
            return (true, bestS)
        }
        return (false, 0)
    }

    private func getGrayscalePixelBuffer(cg: CGImage) -> [UInt8]? {
        let w = cg.width
        let h = cg.height
        var buffer = [UInt8](repeating: 0, count: w * h)
        let colorSpace = CGColorSpaceCreateDeviceGray()
        guard let ctx = CGContext(
            data: &buffer,
            width: w,
            height: h,
            bitsPerComponent: 8,
            bytesPerRow: w,
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.none.rawValue
        ) else { return nil }
        ctx.interpolationQuality = .none
        ctx.draw(cg, in: CGRect(x: 0, y: 0, width: w, height: h))
        return buffer
    }
}
