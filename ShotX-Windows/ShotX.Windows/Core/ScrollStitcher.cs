using System.Drawing;
using System.Drawing.Imaging;

namespace ShotX.Windows.Core;

/// <summary>
/// 长截图核心拼接引擎（基于绝对底部水位线与灰度差值匹配，杜绝回滚重叠，支持流畅顺向延展）
/// 1:1 移植自 ShotX macOS 核心算法
/// </summary>
public sealed class ScrollStitcher
{
    private Bitmap? _stitchedLongBitmap;
    private Bitmap? _lastStitchedBitmap;
    private bool _isStitching;

    public Bitmap? StitchedLongBitmap => _stitchedLongBitmap;
    public bool HasImage => _stitchedLongBitmap != null;
    public int CurrentHeight => _stitchedLongBitmap?.Height ?? 0;

    public const int MaxHeightLimit = 32000;

    public void Reset()
    {
        _stitchedLongBitmap?.Dispose();
        _lastStitchedBitmap?.Dispose();
        _stitchedLongBitmap = null;
        _lastStitchedBitmap = null;
        _isStitching = false;
    }

    public void Initialize(Bitmap initialBitmap)
    {
        Reset();
        _stitchedLongBitmap = new Bitmap(initialBitmap);
        _lastStitchedBitmap = new Bitmap(initialBitmap);
        _isStitching = false;
    }

    public (bool DidStitch, bool ReachedLimit) AppendSlice(Bitmap freshBitmap)
    {
        if (_isStitching) return (false, false);
        _isStitching = true;
        try
        {
            if (_stitchedLongBitmap == null || _lastStitchedBitmap == null)
            {
                Initialize(freshBitmap);
                return (true, false);
            }

            if (_stitchedLongBitmap.Height >= MaxHeightLimit)
            {
                return (false, true);
            }

            var (hasMovement, S) = DetectScrollMovement(_lastStitchedBitmap, freshBitmap);
            if (!hasMovement || S < 3)
            {
                return (false, false);
            }

            int currentW = _stitchedLongBitmap.Width;
            int newH = _stitchedLongBitmap.Height + S;

            var newComposite = new Bitmap(currentW, newH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(newComposite))
            {
                // 1. 绘制已有的长图到上方
                g.DrawImage(_stitchedLongBitmap, 0, 0, currentW, _stitchedLongBitmap.Height);

                // 2. 截取 freshBitmap 底部新露出的 S 高度条带，拼接到最底端
                int sliceW = Math.Min(currentW, freshBitmap.Width);
                var srcRect = new Rectangle(0, freshBitmap.Height - S, sliceW, S);
                var destRect = new Rectangle(0, _stitchedLongBitmap.Height, sliceW, S);
                g.DrawImage(freshBitmap, destRect, srcRect, GraphicsUnit.Pixel);
            }

            _stitchedLongBitmap.Dispose();
            _stitchedLongBitmap = newComposite;

            _lastStitchedBitmap.Dispose();
            _lastStitchedBitmap = new Bitmap(freshBitmap);

            return (true, false);
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

        if (h1 < 60 || w1 < 40 || h2 < 60 || w2 < 40) return (false, 0);
        if (Math.Abs(w1 - w2) > 4 || Math.Abs(h1 - h2) > 4) return (false, 0);

        int w = Math.Min(w1, w2);
        int h = Math.Min(h1, h2);

        var bufA = GetGrayscalePixelBuffer(baseBmp, w, h);
        var bufB = GetGrayscalePixelBuffer(freshBmp, w, h);
        if (bufA == null || bufB == null) return (false, 0);

        // 1. 避开左右边距采样 32 列
        int marginX = Math.Min(20, w / 10);
        int sampleWidth = w - 2 * marginX;
        if (sampleWidth <= 32) return (false, 0);

        var sampleCols = new int[32];
        int colStep = sampleWidth / 32;
        for (int i = 0; i < 32; i++)
        {
            sampleCols[i] = marginX + i * colStep;
        }

        // 2. 在 base 下半部分寻找对比度最强（方差最大）的锚定条带
        int anchorH = Math.Min(56, Math.Max(36, h / 8));
        int searchMinTop = Math.Max(10, (int)(h * 0.35));
        int searchMaxTop = Math.Min(h - anchorH - 8, (int)(h * 0.90));
        if (searchMaxTop <= searchMinTop) return (false, 0);

        double bestVar = 0.0;
        int bestTop = searchMaxTop;

        for (int candTop = searchMaxTop; candTop >= searchMinTop; candTop -= 6)
        {
            double sum = 0.0;
            double sumSq = 0.0;
            int cnt = 0;
            for (int r = 0; r < anchorH; r += 4)
            {
                int row = (candTop + r) * w;
                foreach (var c in sampleCols)
                {
                    double val = bufA[row + c];
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

        if (bestVar < 2.0) return (false, 0);

        // 3. 快速零位移检测（完全静止直接跳过）
        double zeroDiff = 0.0;
        int zeroCount = 0;
        for (int r = 0; r < anchorH; r += 2)
        {
            int rowA = (bestTop + r) * w;
            foreach (var c in sampleCols)
            {
                zeroDiff += Math.Abs(bufA[rowA + c] - bufB[rowA + c]);
                zeroCount++;
            }
        }
        if (zeroCount > 0 && (zeroDiff / zeroCount) < 1.2)
        {
            return (false, 0);
        }

        // 4. 在 fresh 向上偏移中搜索向下位移 S (3px 到 85% 视口高度)
        int maxDown = Math.Min((int)(h * 0.85), bestTop);
        if (maxDown < 3) return (false, 0);

        int bestS = 0;
        double bestErr = double.MaxValue;

        for (int S = 3; S <= maxDown; S++)
        {
            int y = bestTop - S;
            if (y < 0) break;

            double diff = 0.0;
            int count = 0;
            for (int r = 0; r < anchorH; r += 2)
            {
                int rowA = (bestTop + r) * w;
                int rowB = (y + r) * w;
                foreach (var c in sampleCols)
                {
                    diff += Math.Abs(bufA[rowA + c] - bufB[rowB + c]);
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

        if (bestErr < 4.2 && bestS >= 3)
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
                    // 快速灰度转换
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

