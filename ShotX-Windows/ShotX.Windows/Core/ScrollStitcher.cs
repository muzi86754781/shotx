using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ShotX.Windows.Core;

/// <summary>
/// 长截图核心拼接引擎（基于绝对底部水位线与灰度差值匹配，杜绝回滚重叠，支持流畅顺向延展）
/// 1:1 移植自 ShotX macOS 核心算法，针对 Windows ClearType 与高分屏 DPI 进行深度优化
/// </summary>
public sealed class ScrollStitcher
{
    private Bitmap? _stitchedLongBitmap;
    private Bitmap? _lastStitchedBitmap;
    private bool _isStitching;

    public Bitmap? StitchedLongBitmap => _stitchedLongBitmap;
    public bool HasImage => _stitchedLongBitmap != null;
    public int CurrentHeight => _stitchedLongBitmap?.Height ?? 0;
    public int SliceCount { get; private set; }
    public int LastAddedHeight { get; private set; }

    public const int MaxHeightLimit = 32000;

    public void Reset()
    {
        _stitchedLongBitmap?.Dispose();
        _lastStitchedBitmap?.Dispose();
        _stitchedLongBitmap = null;
        _lastStitchedBitmap = null;
        _isStitching = false;
        SliceCount = 0;
        LastAddedHeight = 0;
    }

    public void Initialize(Bitmap initialBitmap)
    {
        Reset();
        _stitchedLongBitmap = new Bitmap(initialBitmap);
        _lastStitchedBitmap = new Bitmap(initialBitmap);
        _isStitching = false;
        SliceCount = 1;
        LastAddedHeight = 0;
    }

    public (bool DidStitch, bool ReachedLimit, int AddedHeight) AppendSlice(Bitmap freshBitmap)
    {
        if (_isStitching) return (false, false, 0);
        _isStitching = true;
        try
        {
            if (_stitchedLongBitmap == null || _lastStitchedBitmap == null)
            {
                Initialize(freshBitmap);
                return (true, false, freshBitmap.Height);
            }

            if (_stitchedLongBitmap.Height >= MaxHeightLimit)
            {
                return (false, true, 0);
            }

            var (hasMovement, S) = DetectScrollMovement(_lastStitchedBitmap, freshBitmap);
            if (!hasMovement || S < 3)
            {
                return (false, false, 0);
            }

            int currentW = _stitchedLongBitmap.Width;
            int currentH = _stitchedLongBitmap.Height;
            int newH = currentH + S;

            var newComposite = new Bitmap(currentW, newH, PixelFormat.Format32bppArgb);
            newComposite.SetResolution(freshBitmap.HorizontalResolution, freshBitmap.VerticalResolution);

            using (var g = Graphics.FromImage(newComposite))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.SmoothingMode = SmoothingMode.None;

                // 1. 绘制已有长图到上方
                g.DrawImage(_stitchedLongBitmap, 0, 0, currentW, currentH);

                // 2. 截取 freshBitmap 底部新露出的 S 高度条带，拼接到最底端
                int sliceW = Math.Min(currentW, freshBitmap.Width);
                var srcRect = new Rectangle(0, freshBitmap.Height - S, sliceW, S);
                var destRect = new Rectangle(0, currentH, sliceW, S);
                g.DrawImage(freshBitmap, destRect, srcRect, GraphicsUnit.Pixel);
            }

            _stitchedLongBitmap.Dispose();
            _stitchedLongBitmap = newComposite;

            _lastStitchedBitmap.Dispose();
            _lastStitchedBitmap = new Bitmap(freshBitmap);

            SliceCount++;
            LastAddedHeight = S;

            return (true, false, S);
        }
        finally
        {
            _isStitching = false;
        }
    }

    private unsafe (bool HasMovement, int DeltaY) DetectScrollMovement(Bitmap baseBmp, Bitmap freshBmp)
    {
        int w1 = baseBmp.Width;
        int h1 = baseBmp.Height;
        int w2 = freshBmp.Width;
        int h2 = freshBmp.Height;

        if (h1 < 50 || w1 < 40 || h2 < 50 || w2 < 40) return (false, 0);
        if (Math.Abs(w1 - w2) > 6 || Math.Abs(h1 - h2) > 6) return (false, 0);

        int w = Math.Min(w1, w2);
        int h = Math.Min(h1, h2);

        var bufA = GetGrayscalePixelBuffer(baseBmp, w, h);
        var bufB = GetGrayscalePixelBuffer(freshBmp, w, h);
        if (bufA == null || bufB == null) return (false, 0);

        // 1. 避开左右边距（各保留 8% 边距，杜绝垂直滚动条或侧边栏干扰）
        int marginX = Math.Max(12, (int)(w * 0.08));
        int sampleWidth = w - 2 * marginX;
        if (sampleWidth <= 32) return (false, 0);

        const int colCount = 40;
        var sampleCols = new int[colCount];
        int colStep = sampleWidth / colCount;
        for (int i = 0; i < colCount; i++)
        {
            sampleCols[i] = marginX + i * colStep;
        }

        // 2. 在 base 下半部分寻找对比度最强（方差最大）的锚定条带
        int anchorH = Math.Min(48, Math.Max(24, h / 10));
        int searchMinTop = Math.Max(8, (int)(h * 0.25));
        int searchMaxTop = Math.Min(h - anchorH - 6, (int)(h * 0.92));
        if (searchMaxTop <= searchMinTop) return (false, 0);

        double bestVar = 0.0;
        int bestTop = searchMaxTop;

        for (int candTop = searchMaxTop; candTop >= searchMinTop; candTop -= 4)
        {
            double sum = 0.0;
            double sumSq = 0.0;
            int cnt = 0;
            for (int r = 0; r < anchorH; r += 3)
            {
                int row = (candTop + r) * w;
                for (int i = 0; i < colCount; i++)
                {
                    double val = bufA[row + sampleCols[i]];
                    sum += val;
                    sumSq += val * val;
                    cnt++;
                }
            }

            if (cnt > 0)
            {
                double mean = sum / cnt;
                double variance = (sumSq / cnt) - (mean * mean);
                if (variance > bestVar)
                {
                    bestVar = variance;
                    bestTop = candTop;
                }
            }
        }

        // 纯平坦区域（纯白背景等无纹理内容）不作误匹配
        if (bestVar < 1.0) return (false, 0);

        // 3. 快速零位移检测（完全静止直接跳过）
        double zeroDiff = 0.0;
        int zeroCount = 0;
        for (int r = 0; r < anchorH; r += 2)
        {
            int rowA = (bestTop + r) * w;
            for (int i = 0; i < colCount; i++)
            {
                int col = sampleCols[i];
                zeroDiff += Math.Abs(bufA[rowA + col] - bufB[rowA + col]);
                zeroCount++;
            }
        }
        if (zeroCount > 0 && (zeroDiff / zeroCount) < 1.0)
        {
            return (false, 0);
        }

        // 4. 在 fresh 中向上搜索向下位移 S (3px 到 90% 视口高度)
        int maxS = Math.Min((int)(h * 0.90), bestTop);
        if (maxS < 3) return (false, 0);

        // 粗扫描：步长 2
        int coarseBestS = 0;
        double coarseBestErr = double.MaxValue;

        for (int S = 3; S <= maxS; S += 2)
        {
            int y = bestTop - S;
            if (y < 0) break;

            double diff = 0.0;
            int count = 0;
            for (int r = 0; r < anchorH; r += 3)
            {
                int rowA = (bestTop + r) * w;
                int rowB = (y + r) * w;
                for (int i = 0; i < colCount; i++)
                {
                    int col = sampleCols[i];
                    diff += Math.Abs(bufA[rowA + col] - bufB[rowB + col]);
                    count++;
                }
            }

            if (count > 0)
            {
                double err = diff / count;
                if (err < coarseBestErr)
                {
                    coarseBestErr = err;
                    coarseBestS = S;
                }
            }
        }

        if (coarseBestS < 3) return (false, 0);

        // 精细扫描：在 coarseBestS 附近 ±2px 细扫步长 1 寻找像素级对齐点
        int fineStart = Math.Max(3, coarseBestS - 2);
        int fineEnd = Math.Min(maxS, coarseBestS + 2);
        int bestS = coarseBestS;
        double bestErr = double.MaxValue;

        for (int S = fineStart; S <= fineEnd; S++)
        {
            int y = bestTop - S;
            if (y < 0) break;

            double diff = 0.0;
            int count = 0;
            for (int r = 0; r < anchorH; r += 2)
            {
                int rowA = (bestTop + r) * w;
                int rowB = (y + r) * w;
                for (int i = 0; i < colCount; i++)
                {
                    int col = sampleCols[i];
                    diff += Math.Abs(bufA[rowA + col] - bufB[rowB + col]);
                    count++;
                }
            }

            if (count > 0)
            {
                double err = diff / count;
                if (err < bestErr)
                {
                    bestErr = err;
                    bestS = S;
                }
            }
        }

        // Windows 平台兼容 ClearType 次像素反锯齿与高分屏缩放（误差阈值放宽至 14.0 灰度级）
        if (bestErr <= 14.0 && bestS >= 3)
        {
            return (true, bestS);
        }

        return (false, 0);
    }

    private static unsafe byte[]? GetGrayscalePixelBuffer(Bitmap bmp, int targetW, int targetH)
    {
        var buffer = new byte[targetW * targetH];
        var data = bmp.LockBits(new Rectangle(0, 0, Math.Min(bmp.Width, targetW), Math.Min(bmp.Height, targetH)), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* scan0 = (byte*)data.Scan0;
            int stride = data.Stride;

            for (int y = 0; y < targetH && y < bmp.Height; y++)
            {
                byte* row = scan0 + (y * stride);
                int destOffset = y * targetW;
                for (int x = 0; x < targetW && x < bmp.Width; x++)
                {
                    int b = row[x * 4];
                    int g = row[x * 4 + 1];
                    int r = row[x * 4 + 2];
                    buffer[destOffset + x] = (byte)((r * 77 + g * 150 + b * 29) >> 8);
                }
            }
            return buffer;
        }
        catch
        {
            return null;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
