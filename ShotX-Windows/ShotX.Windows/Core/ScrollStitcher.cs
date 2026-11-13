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
            if (!hasMovement || S < 2)
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

        const int colCount = 36;
        var sampleCols = new int[colCount];
        int colStep = sampleWidth / colCount;
        for (int i = 0; i < colCount; i++)
        {
            sampleCols[i] = marginX + i * colStep;
        }

        int anchorH = Math.Min(52, Math.Max(28, h / 10));

        // 双锚点检测：
        // 锚点 1（优先）：baseBmp 底部高对比度区域 (0.55h ~ 0.92h)，最大位移可达视口 92%
        // 锚点 2（备用）：baseBmp 中部高对比度区域 (0.25h ~ 0.55h)
        int bestTop1 = FindBestAnchorTop(bufA, w, h, anchorH, sampleCols, (int)(h * 0.55), (int)(h * 0.92));
        int bestTop2 = FindBestAnchorTop(bufA, w, h, anchorH, sampleCols, (int)(h * 0.25), (int)(h * 0.55));

        if (bestTop1 > 0)
        {
            var res1 = MatchAnchor(bufA, bufB, w, h, anchorH, bestTop1, sampleCols);
            if (res1.HasMovement) return res1;
        }

        if (bestTop2 > 0 && bestTop2 != bestTop1)
        {
            var res2 = MatchAnchor(bufA, bufB, w, h, anchorH, bestTop2, sampleCols);
            if (res2.HasMovement) return res2;
        }

        return (false, 0);
    }

    private static unsafe int FindBestAnchorTop(byte[] bufA, int w, int h, int anchorH, int[] sampleCols, int minTop, int maxTop)
    {
        maxTop = Math.Min(h - anchorH - 6, maxTop);
        minTop = Math.Max(6, minTop);
        if (maxTop <= minTop) return -1;

        double bestVar = 0.0;
        int bestTop = -1;

        for (int candTop = maxTop; candTop >= minTop; candTop -= 4)
        {
            double sum = 0.0;
            double sumSq = 0.0;
            int cnt = 0;
            for (int r = 0; r < anchorH; r += 3)
            {
                int row = (candTop + r) * w;
                for (int i = 0; i < sampleCols.Length; i++)
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

        return (bestVar >= 1.5) ? bestTop : -1;
    }

    private static unsafe (bool HasMovement, int DeltaY) MatchAnchor(byte[] bufA, byte[] bufB, int w, int h, int anchorH, int anchorTop, int[] sampleCols)
    {
        // 快速零位移检测（完全静止直接跳过）
        double zeroDiff = 0.0;
        int zeroCount = 0;
        for (int r = 0; r < anchorH; r += 2)
        {
            int rowA = (anchorTop + r) * w;
            for (int i = 0; i < sampleCols.Length; i++)
            {
                zeroDiff += Math.Abs(bufA[rowA + sampleCols[i]] - bufB[rowA + sampleCols[i]]);
                zeroCount++;
            }
        }
        if (zeroCount > 0 && (zeroDiff / zeroCount) < 1.0)
        {
            return (false, 0);
        }

        int maxS = Math.Min((int)(h * 0.92), anchorTop);
        if (maxS < 2) return (false, 0);

        int bestS = 0;
        double bestErr = double.MaxValue;

        // 全步长逐像素线性扫描 (S 从 2 到 maxS)，绝不漏掉单像素文字笔画与次像素对齐点
        for (int S = 2; S <= maxS; S++)
        {
            int y = anchorTop - S;
            if (y < 0) break;

            double diff = 0.0;
            int count = 0;
            for (int r = 0; r < anchorH; r += 2)
            {
                int rowA = (anchorTop + r) * w;
                int rowB = (y + r) * w;
                for (int i = 0; i < sampleCols.Length; i++)
                {
                    diff += Math.Abs(bufA[rowA + sampleCols[i]] - bufB[rowB + sampleCols[i]]);
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

        // 阈值宽容匹配 Windows ClearType 与高分屏缩放 (<= 18.0 灰度级)
        if (bestErr <= 18.0 && bestS >= 2)
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
