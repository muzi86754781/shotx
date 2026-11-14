using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ShotX.Windows.Core;

/// <summary>
/// 长截图核心拼接引擎（基于垂直梯度特征点提取 + 多锚点动态过滤静态边栏/底栏 + 锚点接缝对齐拼接）
/// 针对 Windows ClearType 次像素抗锯齿、高分屏 DPI 缩放、稀疏文本与平滑滚动进行深度优化
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

            var (hasMovement, S, anchorBottom) = DetectScrollMovement(_lastStitchedBitmap, freshBitmap);
            if (!hasMovement || S < 2)
            {
                return (false, false, 0);
            }

            int currentW = _stitchedLongBitmap.Width;
            int currentH = _stitchedLongBitmap.Height;
            int baseH = Math.Min(currentH, Math.Min(_lastStitchedBitmap.Height, freshBitmap.Height));

            // 将拼接缝对准已严格验证匹配的锚点下沿 (safeAnchorBottom)，自动覆盖并消除底部可能存在的悬浮条/固定底栏
            int safeAnchorBottom = Math.Clamp(anchorBottom, S + 6, baseH);
            int stitchSeamY = Math.Clamp(currentH - baseH + safeAnchorBottom, 1, currentH);
            int freshSeamY = Math.Clamp(safeAnchorBottom - S, 0, freshBitmap.Height - 1);
            int copyH = freshBitmap.Height - freshSeamY;
            int newH = stitchSeamY + copyH;

            if (newH <= currentH || copyH <= 0)
            {
                return (false, false, 0);
            }

            var newComposite = new Bitmap(currentW, newH, PixelFormat.Format32bppArgb);
            newComposite.SetResolution(freshBitmap.HorizontalResolution, freshBitmap.VerticalResolution);

            using (var g = Graphics.FromImage(newComposite))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.SmoothingMode = SmoothingMode.None;

                // 1. 绘制已有长图锚点缝以上的稳定部分
                var topRect = new Rectangle(0, 0, currentW, stitchSeamY);
                g.DrawImage(_stitchedLongBitmap, topRect, topRect, GraphicsUnit.Pixel);

                // 2. 从 freshBitmap 的匹配接缝处向下完整绘制到底部
                int sliceW = Math.Min(currentW, freshBitmap.Width);
                var srcRect = new Rectangle(0, freshSeamY, sliceW, copyH);
                var destRect = new Rectangle(0, stitchSeamY, sliceW, copyH);
                g.DrawImage(freshBitmap, destRect, srcRect, GraphicsUnit.Pixel);
            }

            int actualAdded = newH - currentH;

            _stitchedLongBitmap.Dispose();
            _stitchedLongBitmap = newComposite;

            _lastStitchedBitmap.Dispose();
            _lastStitchedBitmap = new Bitmap(freshBitmap);

            SliceCount++;
            LastAddedHeight = actualAdded;

            return (true, false, actualAdded);
        }
        finally
        {
            _isStitching = false;
        }
    }

    private unsafe (bool HasMovement, int DeltaY, int AnchorBottom) DetectScrollMovement(Bitmap baseBmp, Bitmap freshBmp)
    {
        int w1 = baseBmp.Width;
        int h1 = baseBmp.Height;
        int w2 = freshBmp.Width;
        int h2 = freshBmp.Height;

        if (h1 < 40 || w1 < 30 || h2 < 40 || w2 < 30) return (false, 0, 0);
        if (Math.Abs(w1 - w2) > 8 || Math.Abs(h1 - h2) > 8) return (false, 0, 0);

        int w = Math.Min(w1, w2);
        int h = Math.Min(h1, h2);

        var bufA = GetGrayscalePixelBuffer(baseBmp, w, h);
        var bufB = GetGrayscalePixelBuffer(freshBmp, w, h);
        if (bufA == null || bufB == null) return (false, 0, 0);

        // 1. 避开左右滚动条与窗口边框（保留 6% 边距），高密度列采样确保绝不漏掉细小文字笔画
        int marginX = Math.Max(8, (int)(w * 0.06));
        int sampleWidth = w - 2 * marginX;
        if (sampleWidth <= 20) return (false, 0, 0);

        int rawColCount = Math.Min(96, Math.Max(32, sampleWidth / 4));
        var rawCols = new int[rawColCount];
        for (int i = 0; i < rawColCount; i++)
        {
            rawCols[i] = marginX + (i * sampleWidth) / rawColCount;
        }

        // 2. 过滤静态侧边栏列（例如左侧固定目录树、右侧静止面板），仅保留真正发生滚动变化的活跃列
        int yCheckStart = Math.Max(4, (int)(h * 0.12));
        int yCheckEnd = Math.Min(h - 4, (int)(h * 0.88));
        var colDiffs = new double[rawColCount];
        double maxColDiff = 0.0;

        for (int i = 0; i < rawColCount; i++)
        {
            int c = rawCols[i];
            int sumD = 0;
            int cntD = 0;
            for (int y = yCheckStart; y < yCheckEnd; y += 3)
            {
                int idx = y * w + c;
                sumD += Math.Abs(bufA[idx] - bufB[idx]);
                cntD++;
            }
            double avgD = cntD > 0 ? (double)sumD / cntD : 0.0;
            colDiffs[i] = avgD;
            if (avgD > maxColDiff) maxColDiff = avgD;
        }

        // 如果全图所有列均完全静止，直接快速返回
        if (maxColDiff < 0.45)
        {
            return (false, 0, 0);
        }

        var activeColsList = new List<int>(rawColCount);
        double activeColThreshold = Math.Max(0.35, maxColDiff * 0.08);
        for (int i = 0; i < rawColCount; i++)
        {
            if (colDiffs[i] >= activeColThreshold)
            {
                activeColsList.Add(rawCols[i]);
            }
        }

        int[] sampleCols = activeColsList.Count >= 12 ? activeColsList.ToArray() : rawCols;
        int colCount = sampleCols.Length;

        // 3. 计算每一行的纵向梯度能量 (rowVertEnergy) 与帧间变化量 (rowFrameDiff)
        // 纵向梯度只对水平文字笔画、图片纹理、横线敏感，完全免疫垂直边框与分栏背景的干扰
        var rowVertEnergy = new double[h];
        var rowFrameDiff = new double[h];
        for (int y = 4; y < h - 4; y++)
        {
            int r0 = y * w;
            int r1 = (y + 1) * w;
            int r2 = (y + 2) * w;
            int vSum = 0;
            int dSum = 0;
            for (int i = 0; i < colCount; i++)
            {
                int c = sampleCols[i];
                int a0 = bufA[r0 + c];
                vSum += Math.Abs(a0 - bufA[r2 + c]) + (Math.Abs(a0 - bufA[r1 + c]) >> 1);
                dSum += Math.Abs(a0 - bufB[r0 + c]);
            }
            rowVertEnergy[y] = (double)vSum / colCount;
            rowFrameDiff[y] = (double)dSum / colCount;
        }

        int anchorH = Math.Min(42, Math.Max(20, h / 11));

        // 4. 分三段区域搜寻最佳动态特征锚点（优先底部，其次中下部，再次中上部，自动跳过固定底栏与空白区）
        int top1 = FindBestMovingAnchorTop(rowVertEnergy, rowFrameDiff, h, anchorH, (int)(h * 0.60), (int)(h * 0.90));
        int top2 = FindBestMovingAnchorTop(rowVertEnergy, rowFrameDiff, h, anchorH, (int)(h * 0.36), (int)(h * 0.62));
        int top3 = FindBestMovingAnchorTop(rowVertEnergy, rowFrameDiff, h, anchorH, (int)(h * 0.16), (int)(h * 0.38));

        int[] candidates = { top1, top2, top3 };
        for (int k = 0; k < candidates.Length; k++)
        {
            int candTop = candidates[k];
            if (candTop <= 0) continue;
            bool duplicate = false;
            for (int j = 0; j < k; j++)
            {
                if (Math.Abs(candidates[j] - candTop) < anchorH / 2)
                {
                    duplicate = true;
                    break;
                }
            }
            if (duplicate) continue;

            var match = MatchAnchor(bufA, bufB, w, h, anchorH, candTop, sampleCols);
            if (match.HasMovement && match.DeltaY >= 2)
            {
                return (true, match.DeltaY, candTop + anchorH);
            }
        }

        return (false, 0, 0);
    }

    private static int FindBestMovingAnchorTop(double[] rowVertEnergy, double[] rowFrameDiff, int h, int anchorH, int minTop, int maxTop)
    {
        maxTop = Math.Min(h - anchorH - 6, maxTop);
        minTop = Math.Max(6, minTop);
        if (maxTop <= minTop) return -1;

        double bestScore = -1.0;
        int bestTop = -1;

        double fallbackEnergy = -1.0;
        int fallbackTop = maxTop;

        for (int candTop = maxTop; candTop >= minTop; candTop -= 3)
        {
            double sumV = 0.0;
            double sumD = 0.0;
            for (int r = 0; r < anchorH; r++)
            {
                sumV += rowVertEnergy[candTop + r];
                sumD += rowFrameDiff[candTop + r];
            }
            double avgV = sumV / anchorH;
            double avgD = sumD / anchorH;

            if (avgV > fallbackEnergy)
            {
                fallbackEnergy = avgV;
                fallbackTop = candTop;
            }

            // 要求锚点既有纵向纹理特征，又在当前帧发生了实际位移变化（过滤固定不变的底栏/状态栏）
            if (avgV >= 0.15 && avgD >= 0.20)
            {
                double score = avgV * Math.Sqrt(Math.Min(avgD, 36.0));
                if (score > bestScore)
                {
                    bestScore = score;
                    bestTop = candTop;
                }
            }
        }

        if (bestTop > 0) return bestTop;
        return fallbackEnergy >= 0.12 ? fallbackTop : -1;
    }

    private static unsafe (bool HasMovement, int DeltaY) MatchAnchor(
        byte[] bufA,
        byte[] bufB,
        int w,
        int h,
        int anchorH,
        int anchorTop,
        int[] sampleCols)
    {
        int colCount = sampleCols.Length;

        // 提取锚点带内的活跃纵向边缘特征点 (r, c)，消除大面积空白背景对稀疏文字匹配误差的稀释
        var featRowOffsets = new List<int>(256);
        var featCols = new List<int>(256);

        for (int thresh = 4; thresh >= 1; thresh -= 3)
        {
            featRowOffsets.Clear();
            featCols.Clear();
            for (int r = 0; r < anchorH - 2; r++)
            {
                int row0 = (anchorTop + r) * w;
                int row2 = (anchorTop + r + 2) * w;
                for (int i = 0; i < colCount; i++)
                {
                    int c = sampleCols[i];
                    if (Math.Abs(bufA[row0 + c] - bufA[row2 + c]) >= thresh)
                    {
                        featRowOffsets.Add(r);
                        featCols.Add(c);
                    }
                }
            }
            if (featRowOffsets.Count >= 14) break;
        }

        int featCount = featRowOffsets.Count;
        // 如果连 >= 1 的纵向特征点都不足 6 个，说明该条带是纯色空白，无法作为可靠锚点
        if (featCount < 6)
        {
            return (false, 0);
        }

        // 均匀抽样特征点上限至 220 个，保证全步长扫描在 0.3ms 内完成
        int[] fRows;
        int[] fCols;
        if (featCount > 220)
        {
            fRows = new int[220];
            fCols = new int[220];
            for (int k = 0; k < 220; k++)
            {
                int idx = (k * featCount) / 220;
                fRows[k] = featRowOffsets[idx];
                fCols[k] = featCols[idx];
            }
            featCount = 220;
        }
        else
        {
            fRows = featRowOffsets.ToArray();
            fCols = featCols.ToArray();
        }

        // 计算零位移 (S = 0) 时的基准误差
        double zeroFeatDiff = 0.0;
        for (int k = 0; k < featCount; k++)
        {
            int idx = (anchorTop + fRows[k]) * w + fCols[k];
            zeroFeatDiff += Math.Abs(bufA[idx] - bufB[idx]);
        }
        double zeroFeatErr = zeroFeatDiff / featCount;

        double zeroTotDiff = 0.0;
        int zeroTotCnt = 0;
        for (int r = 0; r < anchorH; r += 2)
        {
            int rowA = (anchorTop + r) * w;
            for (int i = 0; i < colCount; i++)
            {
                int c = sampleCols[i];
                zeroTotDiff += Math.Abs(bufA[rowA + c] - bufB[rowA + c]);
                zeroTotCnt++;
            }
        }
        double zeroTotErr = zeroTotCnt > 0 ? zeroTotDiff / zeroTotCnt : 0.0;

        // 若特征点在 S = 0 处几乎无变化，说明画面静止
        if (zeroFeatErr < 1.2 && zeroTotErr < 0.5)
        {
            return (false, 0);
        }

        double zeroCombErr = zeroFeatErr * 0.72 + zeroTotErr * 0.28;

        int maxS = Math.Min((int)(h * 0.90), anchorTop);
        if (maxS < 2) return (false, 0);

        int bestS = 0;
        double bestCombErr = double.MaxValue;
        double bestFeatErr = double.MaxValue;
        double bestTotErr = double.MaxValue;

        // 全步长逐像素线性扫描 (S 从 2 到 maxS)
        for (int S = 2; S <= maxS; S++)
        {
            int y = anchorTop - S;
            if (y < 0) break;

            double fDiff = 0.0;
            for (int k = 0; k < featCount; k++)
            {
                int r = fRows[k];
                int c = fCols[k];
                fDiff += Math.Abs(bufA[(anchorTop + r) * w + c] - bufB[(y + r) * w + c]);
            }
            double fErr = fDiff / featCount;

            // 快速剪枝：如果特征点误差已经远大于当前最优解，跳过全列校验
            if (fErr * 0.72 >= bestCombErr) continue;

            double tDiff = 0.0;
            int tCnt = 0;
            for (int r = 0; r < anchorH; r += 2)
            {
                int rowA = (anchorTop + r) * w;
                int rowB = (y + r) * w;
                for (int i = 0; i < colCount; i += 2)
                {
                    int c = sampleCols[i];
                    tDiff += Math.Abs(bufA[rowA + c] - bufB[rowB + c]);
                    tCnt++;
                }
            }
            double tErr = tCnt > 0 ? tDiff / tCnt : fErr;
            double cErr = fErr * 0.72 + tErr * 0.28;

            if (cErr < bestCombErr)
            {
                bestCombErr = cErr;
                bestFeatErr = fErr;
                bestTotErr = tErr;
                bestS = S;
            }
        }

        // 判定匹配成功：
        // 1. 最佳位移点相比静止状态 (S=0) 误差显著下降 (< 68%)
        // 2. 特征点误差与整体误差在 ClearType / 平滑滚动容差范围内
        if (bestS >= 2 &&
            bestFeatErr <= 28.0 &&
            bestTotErr <= 20.0 &&
            bestCombErr < zeroCombErr * 0.68)
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
