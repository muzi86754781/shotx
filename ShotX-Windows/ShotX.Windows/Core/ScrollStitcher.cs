using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ShotX.Windows.Core;

/// <summary>
/// 长截图核心拼接引擎（全重叠区多行全局校验 + 底部增量拼接 + 悬浮弹窗自愈擦除）
/// 针对重复列表/表格防重影、鼠标悬浮弹窗防截入、高分屏 ClearType 抗锯齿与平滑滚动深度优化
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

            int lastW = _lastStitchedBitmap.Width;
            int lastH = _lastStitchedBitmap.Height;
            int freshW = freshBitmap.Width;
            int freshH = freshBitmap.Height;

            if (lastW != freshW || lastH != freshH || lastW < 24 || lastH < 36)
            {
                return (false, false, 0);
            }

            var match = FindScrollOffsetAndRegions(_lastStitchedBitmap, freshBitmap);
            int dy = match.Dy;
            if (dy < 4)
            {
                return (false, false, 0);
            }

            int topFixedH = match.TopFixedH;
            int bottomFixedH = match.BottomFixedH;
            int seamY = Math.Max(topFixedH + dy + 4, Math.Min(lastH - 4, lastH - bottomFixedH));

            int oldLongH = _stitchedLongBitmap.Height;
            int keepOldH = Math.Max(1, oldLongH - (lastH - seamY));
            int freshStartY = Math.Max(0, seamY - dy);
            int copyFreshH = freshH - freshStartY;
            int newTotalH = keepOldH + copyFreshH;
            int actualAddH = newTotalH - oldLongH;

            if (actualAddH < 2)
            {
                return (false, false, 0);
            }
            if (newTotalH > MaxHeightLimit)
            {
                return (false, true, 0);
            }

            var newLong = new Bitmap(lastW, newTotalH, PixelFormat.Format32bppArgb);

            using (var g = Graphics.FromImage(newLong))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;

                // 1. 保留历史长图直到底部接缝行 seamY (对应长图坐标 keepOldH)
                g.DrawImage(
                    _stitchedLongBitmap,
                    new Rectangle(0, 0, lastW, keepOldH),
                    new Rectangle(0, 0, lastW, keepOldH),
                    GraphicsUnit.Pixel
                );

                // 2. 从新帧严格对齐的接缝行 freshStartY = seamY - dy 追加新增底部内容，不复制画面中部的鼠标悬浮窗
                g.DrawImage(
                    freshBitmap,
                    new Rectangle(0, keepOldH, freshW, copyFreshH),
                    new Rectangle(0, freshStartY, freshW, copyFreshH),
                    GraphicsUnit.Pixel
                );
            }

            // 3. 悬浮弹窗/高亮自愈擦除：若上一帧某区域被静止悬浮弹窗或气泡遮挡，而在新帧中该页面内容已向上滚出遮挡区，则用新帧干净像素修复长图对应位置
            HealStationaryPopupsInOverlap(newLong, _lastStitchedBitmap, freshBitmap, dy, topFixedH, seamY, oldLongH);

            _stitchedLongBitmap.Dispose();
            _stitchedLongBitmap = newLong;

            _lastStitchedBitmap.Dispose();
            _lastStitchedBitmap = new Bitmap(freshBitmap);

            SliceCount++;
            LastAddedHeight = actualAddH;
            return (true, false, actualAddH);
        }
        finally
        {
            _isStitching = false;
        }
    }

    private readonly struct ScrollMatchResult
    {
        public readonly int Dy;
        public readonly int TopFixedH;
        public readonly int BottomFixedH;

        public ScrollMatchResult(int dy, int topFixedH, int bottomFixedH)
        {
            Dy = dy;
            TopFixedH = topFixedH;
            BottomFixedH = bottomFixedH;
        }
    }

    /// <summary>
    /// 在重叠区域内检测上一帧中静止不动的悬浮窗/气泡，并用新帧中已滚出遮挡区的干净内容进行像素级自愈替换
    /// </summary>
    private static void HealStationaryPopupsInOverlap(
        Bitmap newLong,
        Bitmap lastBmp,
        Bitmap freshBmp,
        int dy,
        int topFixedH,
        int activeBottom,
        int oldLongH)
    {
        int w = lastBmp.Width;
        int h = lastBmp.Height;
        int overlapStart = Math.Max(topFixedH, 4);
        int overlapEnd = activeBottom - dy;
        if (overlapEnd - overlapStart < 12) return;

        var rectFrame = new Rectangle(0, 0, w, h);
        var rectLong = new Rectangle(0, 0, w, newLong.Height);

        BitmapData? dataLong = null;
        BitmapData? dataLast = null;
        BitmapData? dataFresh = null;

        try
        {
            dataLong = newLong.LockBits(rectLong, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            dataLast = lastBmp.LockBits(rectFrame, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            dataFresh = freshBmp.LockBits(rectFrame, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            int strideLong = dataLong.Stride;
            int strideFrame = dataLast.Stride;

            unsafe
            {
                byte* ptrLong = (byte*)dataLong.Scan0;
                byte* ptrLast = (byte*)dataLast.Scan0;
                byte* ptrFresh = (byte*)dataFresh.Scan0;

                // 以 8x4 小块为单位检测：
                // 条件 1: 在同一屏幕位置 oldY = y + dy 处，last[oldY] 与 fresh[oldY] 几乎完全一致 (diffStationary < 6.0)，说明屏幕该位置存在不随页面滚动的静止物（如鼠标悬浮弹窗）
                // 条件 2: 页面从 oldY 滚到 y 后，fresh[y] 与 last[oldY] 差异明显 (diffContent > 18.0)，说明真实页面内容已从弹窗下方滚出
                // 条件 3: 新位置 y 处在当前帧并非另一个静止弹窗 (diffAtNewPos > 12.0)
                int blockW = 8;
                int blockH = 4;
                int longOffsetBase = oldLongH - h;

                for (int y = overlapStart; y + blockH <= overlapEnd; y += blockH)
                {
                    int oldY = y + dy;
                    int longY = longOffsetBase + oldY;
                    if (longY < 0 || longY + blockH > oldLongH) continue;

                    for (int x = 4; x + blockW <= w - 4; x += blockW)
                    {
                        int sumStationaryAtOld = 0;
                        int sumMismatch = 0;
                        int sumStationaryAtNew = 0;
                        int sumNextScrollMatch = 0;
                        bool canCheckNextScroll = (oldY + dy + blockH <= h);
                        int count = 0;

                        for (int by = 0; by < blockH; by += 2)
                        {
                            byte* rowLastOld = ptrLast + (oldY + by) * strideFrame;
                            byte* rowFreshOld = ptrFresh + (oldY + by) * strideFrame;
                            byte* rowFreshNew = ptrFresh + (y + by) * strideFrame;
                            byte* rowLastNew = ptrLast + (y + by) * strideFrame;
                            byte* rowLastNext = canCheckNextScroll ? (ptrLast + (oldY + dy + by) * strideFrame) : null;

                            for (int bx = 0; bx < blockW; bx += 2)
                            {
                                int off = (x + bx) * 4;
                                int dStatOld = Math.Abs(rowLastOld[off] - rowFreshOld[off])
                                             + Math.Abs(rowLastOld[off + 1] - rowFreshOld[off + 1])
                                             + Math.Abs(rowLastOld[off + 2] - rowFreshOld[off + 2]);

                                int dMismatch = Math.Abs(rowLastOld[off] - rowFreshNew[off])
                                              + Math.Abs(rowLastOld[off + 1] - rowFreshNew[off + 1])
                                              + Math.Abs(rowLastOld[off + 2] - rowFreshNew[off + 2]);

                                int dStatNew = Math.Abs(rowLastNew[off] - rowFreshNew[off])
                                             + Math.Abs(rowLastNew[off + 1] - rowFreshNew[off + 1])
                                             + Math.Abs(rowLastNew[off + 2] - rowFreshNew[off + 2]);

                                if (canCheckNextScroll && rowLastNext != null)
                                {
                                    sumNextScrollMatch += Math.Abs(rowFreshOld[off] - rowLastNext[off])
                                                        + Math.Abs(rowFreshOld[off + 1] - rowLastNext[off + 1])
                                                        + Math.Abs(rowFreshOld[off + 2] - rowLastNext[off + 2]);
                                }

                                sumStationaryAtOld += dStatOld;
                                sumMismatch += dMismatch;
                                sumStationaryAtNew += dStatNew;
                                count++;
                            }
                        }

                        double avgStatOld = sumStationaryAtOld / (double)(count * 3);
                        double avgMismatch = sumMismatch / (double)(count * 3);
                        double avgStatNew = sumStationaryAtNew / (double)(count * 3);
                        double avgNextScroll = canCheckNextScroll ? (sumNextScrollMatch / (double)(count * 3)) : 999.0;

                        // 特征 1：常驻型悬浮弹窗（如划词工具栏、悬浮按钮），在屏幕 oldY 处静止不动 (avgStatOld < 8.0)，而滚到 y 处的真实页面内容不同 (avgMismatch > 13.0)
                        bool isStationaryPopupAtOld = (avgStatOld < 8.0 && avgMismatch > 13.0);

                        // 特征 2：瞬态型鼠标悬浮气泡（滚动后自动消失），导致上一帧 oldY 处与新帧 y 处内容不一致 (avgMismatch > 14.0)，但新帧在 oldY 处已正常接续下一段滚动内容 (avgNextScroll < 9.0) 或新位置并非静止弹窗 (avgStatNew > 10.0)
                        bool isTransientTooltipAtOld = (avgMismatch > 14.0 && avgStatNew > 8.0 && (avgNextScroll < 9.0 || avgStatNew > 14.0));

                        if (isStationaryPopupAtOld || isTransientTooltipAtOld)
                        {
                            // 用已滚出弹窗遮挡区的干净像素 fresh[y..y+blockH] 修复 newLong[longY..longY+blockH]
                            for (int by = 0; by < blockH; by++)
                            {
                                byte* dstRow = ptrLong + (longY + by) * strideLong;
                                byte* srcRow = ptrFresh + (y + by) * strideFrame;
                                int startByte = x * 4;
                                int byteCount = blockW * 4;
                                for (int b = 0; b < byteCount; b++)
                                {
                                    dstRow[startByte + b] = srcRow[startByte + b];
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }
        finally
        {
            if (dataLong != null) try { newLong.UnlockBits(dataLong); } catch { }
            if (dataLast != null) try { lastBmp.UnlockBits(dataLast); } catch { }
            if (dataFresh != null) try { freshBmp.UnlockBits(dataFresh); } catch { }
        }
    }

    private static ScrollMatchResult FindScrollOffsetAndRegions(Bitmap last, Bitmap fresh)
    {
        int w = last.Width;
        int h = last.Height;

        var rect = new Rectangle(0, 0, w, h);
        BitmapData? dataLast = null;
        BitmapData? dataFresh = null;

        try
        {
            dataLast = last.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            dataFresh = fresh.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            int stride1 = dataLast.Stride;
            int stride2 = dataFresh.Stride;
            byte[] buf1 = new byte[stride1 * h];
            byte[] buf2 = new byte[stride2 * h];

            System.Runtime.InteropServices.Marshal.Copy(dataLast.Scan0, buf1, 0, buf1.Length);
            System.Runtime.InteropServices.Marshal.Copy(dataFresh.Scan0, buf2, 0, buf2.Length);

            return ComputeScrollMatchFromBuffers(buf1, stride1, buf2, stride2, w, h);
        }
        finally
        {
            if (dataLast != null) try { last.UnlockBits(dataLast); } catch { }
            if (dataFresh != null) try { fresh.UnlockBits(dataFresh); } catch { }
        }
    }

    private static ScrollMatchResult ComputeScrollMatchFromBuffers(
        byte[] buf1, int stride1,
        byte[] buf2, int stride2,
        int w, int h)
    {
        int marginX = Math.Max(8, (int)(w * 0.05));
        int usableW = w - marginX * 2;
        if (usableW < 16 || h < 40) return new ScrollMatchResult(0, 0, 0);

        // 1. 密集水平采样列 (最高 96 列)
        int numSampleCols = Math.Min(96, usableW);
        int[] allCols = new int[numSampleCols];
        for (int i = 0; i < numSampleCols; i++)
        {
            allCols[i] = marginX + (int)((long)i * (usableW - 1) / Math.Max(1, numSampleCols - 1));
        }

        int checkTop = Math.Max(4, (int)(h * 0.12));
        int checkBot = Math.Min(h - 4, (int)(h * 0.88));
        int checkRows = Math.Max(1, (checkBot - checkTop) / 2);

        // 2. 筛选发生变化且含垂直边缘特征的活跃列（过滤固定侧边栏与空白列）
        List<int> activeColList = new(numSampleCols);
        for (int ci = 0; ci < numSampleCols; ci++)
        {
            int xOff = allCols[ci] * 4;
            long diffSum = 0;
            long gradSum = 0;
            for (int y = checkTop; y < checkBot; y += 2)
            {
                int idx1 = y * stride1 + xOff;
                int idx2 = y * stride2 + xOff;
                diffSum += Math.Abs(buf1[idx1] - buf2[idx2])
                         + Math.Abs(buf1[idx1 + 1] - buf2[idx2 + 1])
                         + Math.Abs(buf1[idx1 + 2] - buf2[idx2 + 2]);

                int idxNext = (y + 2) * stride1 + xOff;
                gradSum += Math.Abs(buf1[idx1] - buf1[idxNext])
                         + Math.Abs(buf1[idx1 + 1] - buf1[idxNext + 1])
                         + Math.Abs(buf1[idx1 + 2] - buf1[idxNext + 2]);
            }

            double avgDiff = diffSum / (double)(checkRows * 3);
            double avgGrad = gradSum / (double)(checkRows * 3);
            if (avgDiff > 1.2 && avgGrad > 0.8)
            {
                activeColList.Add(allCols[ci]);
            }
        }

        // 若几乎没有活跃变化列，说明页面完全静止
        if (activeColList.Count == 0)
        {
            return new ScrollMatchResult(0, 0, 0);
        }

        int[] sampleCols = activeColList.Count >= 12 ? activeColList.ToArray() : allCols;
        int colCount = sampleCols.Length;
        int[] colByteOffsets = new int[colCount];
        for (int i = 0; i < colCount; i++) colByteOffsets[i] = sampleCols[i] * 4;

        // 3. 计算每一行的垂直边缘梯度能量与零位移内点率 (Inlier Ratio at dy = 0)
        // 关键防重影机制：当页面未滚动、仅出现鼠标悬浮弹窗或文字高亮时，虽然局部像素变化很大，但全图大多数特征点在 dy=0 处依然完全重合！
        double[] rowVertEnergy = new double[h];
        double[] rowZeroDiff = new double[h];
        int totalFeaturePoints = 0;
        int unchangedFeaturePointsAtZero = 0;

        for (int y = 2; y < h - 2; y++)
        {
            int rowPrev = (y - 2) * stride1;
            int rowCurr1 = y * stride1;
            int rowNext = (y + 2) * stride1;
            int rowCurr2 = y * stride2;

            long vGradSum = 0;
            long fDiffSum = 0;

            for (int ci = 0; ci < colCount; ci++)
            {
                int xOff = colByteOffsets[ci];
                int iPrev = rowPrev + xOff;
                int iCurr1 = rowCurr1 + xOff;
                int iNext = rowNext + xOff;
                int iCurr2 = rowCurr2 + xOff;

                int vg = Math.Abs(buf1[iNext] - buf1[iPrev])
                       + Math.Abs(buf1[iNext + 1] - buf1[iPrev + 1])
                       + Math.Abs(buf1[iNext + 2] - buf1[iPrev + 2]);

                if (ci + 1 < colCount)
                {
                    int xOff2 = colByteOffsets[ci + 1];
                    vg += Math.Abs(buf1[iCurr1] - buf1[rowCurr1 + xOff2])
                        + Math.Abs(buf1[iCurr1 + 1] - buf1[rowCurr1 + xOff2 + 1])
                        + Math.Abs(buf1[iCurr1 + 2] - buf1[rowCurr1 + xOff2 + 2]);
                }

                int fd = Math.Abs(buf1[iCurr1] - buf2[iCurr2])
                       + Math.Abs(buf1[iCurr1 + 1] - buf2[iCurr2 + 1])
                       + Math.Abs(buf1[iCurr1 + 2] - buf2[iCurr2 + 2]);

                vGradSum += vg;
                fDiffSum += fd;

                if (vg >= 36 && y >= checkTop && y < checkBot)
                {
                    totalFeaturePoints++;
                    if (fd <= 12) // 单通道平均误差 <= 4.0，判定该边缘特征点未发生位移
                    {
                        unchangedFeaturePointsAtZero++;
                    }
                }
            }

            rowVertEnergy[y] = vGradSum / (double)(colCount * 3);
            rowZeroDiff[y] = fDiffSum / (double)(colCount * 3);
        }

        // 若全图 62% 以上的边缘特征点在 dy=0 处完全未动，说明页面根本没有滚动（仅仅是弹出了悬浮气泡或选中了文字），直接返回 0！
        if (totalFeaturePoints >= 25)
        {
            double zeroInlierRatio = unchangedFeaturePointsAtZero / (double)totalFeaturePoints;
            if (zeroInlierRatio >= 0.62)
            {
                return new ScrollMatchResult(0, 0, 0);
            }
        }

        // 4. 识别顶部与底部静止固定栏 (Sticky Header / Sticky Footer)
        int maxTopScan = (int)(h * 0.22);
        int topFixedH = Math.Max(6, (int)(h * 0.05));
        for (int y = 4; y < maxTopScan; y++)
        {
            if (rowZeroDiff[y] < 1.8)
            {
                topFixedH = y + 1;
            }
            else if (y > topFixedH + 6)
            {
                break;
            }
        }

        int maxBotScan = (int)(h * 0.22);
        int bottomFixedH = 0;
        for (int y = h - 3; y >= h - maxBotScan; y--)
        {
            if (rowZeroDiff[y] < 1.8)
            {
                bottomFixedH = h - y;
            }
            else if ((h - y) > bottomFixedH + 6)
            {
                break;
            }
        }

        int activeTop = Math.Max(8, topFixedH);
        int activeBot = Math.Min(h - 8, h - Math.Max(6, bottomFixedH));
        int activeSpan = activeBot - activeTop;
        if (activeSpan < 36)
        {
            return new ScrollMatchResult(0, 0, 0);
        }

        // 5. 提取整个有效区内的高梯度特征点集合 (用于全重叠区多行全局匹配，彻底杜绝重复表格行误匹配)
        // 每个特征点记录 (y, xOff, b, g, r)
        List<(int Y, int XOff)> featurePoints = new(1600);
        int rowStep = Math.Max(1, activeSpan / 90);
        for (int y = activeTop + 2; y < activeBot - 2; y += rowStep)
        {
            if (rowVertEnergy[y] < 2.2) continue;
            int rPrev = (y - 1) * stride1;
            int rCurr = y * stride1;
            int rNext = (y + 1) * stride1;

            for (int ci = 0; ci < colCount; ci++)
            {
                int xOff = colByteOffsets[ci];
                int iPrev = rPrev + xOff;
                int iCurr = rCurr + xOff;
                int iNext = rNext + xOff;

                int vg = Math.Abs(buf1[iNext] - buf1[iPrev])
                       + Math.Abs(buf1[iNext + 1] - buf1[iPrev + 1])
                       + Math.Abs(buf1[iNext + 2] - buf1[iPrev + 2]);

                int hg = 0;
                if (ci + 1 < colCount)
                {
                    int xOffNext = colByteOffsets[ci + 1];
                    hg = Math.Abs(buf1[iCurr] - buf1[rCurr + xOffNext])
                       + Math.Abs(buf1[iCurr + 1] - buf1[rCurr + xOffNext + 1])
                       + Math.Abs(buf1[iCurr + 2] - buf1[rCurr + xOffNext + 2]);
                }

                // 同时具有垂直或水平边缘特征的文字/控件笔画点（如订单号、客户编号、手机号）
                if (vg + hg >= 42)
                {
                    featurePoints.Add((y, xOff));
                }
            }
        }

        // 若特征点过多，均匀下采样至 ~900 个点，兼顾微秒级速度与全屏多行覆盖率
        if (featurePoints.Count > 900)
        {
            var sub = new List<(int Y, int XOff)>(900);
            double step = featurePoints.Count / 900.0;
            for (int i = 0; i < 900; i++)
            {
                sub.Add(featurePoints[(int)(i * step)]);
            }
            featurePoints = sub;
        }

        int maxDy = Math.Min((int)(h * 0.78), activeSpan - 20);
        if (maxDy < 4) return new ScrollMatchResult(0, 0, 0);

        // 6. 对每一个候选滚动位移 dy ∈ [4, maxDy] 进行全重叠区多行特征评估
        // 对于候选 dy，所有满足 y_last - dy >= activeTop 的特征点都应在 fresh 的 y_fresh = y_last - dy 处精确重合！
        // 这跨越了整个画面的所有表格行（4~8 行），绝不会因为单行内的“【双11特惠】...”相同而认错行！
        double bestScore = double.MaxValue;
        int bestDy = 0;
        double bestInlierRatio = 0;

        // 计算 dy = 0 时的基准误差
        double zeroScore = EvaluateDyAcrossOverlap(buf1, stride1, buf2, stride2, colByteOffsets, featurePoints, activeTop, activeBot, 0, out _);
        if (zeroScore < 2.2)
        {
            return new ScrollMatchResult(0, 0, 0);
        }

        for (int dy = 4; dy <= maxDy; dy++)
        {
            double score = EvaluateDyAcrossOverlap(buf1, stride1, buf2, stride2, colByteOffsets, featurePoints, activeTop, activeBot, dy, out double inlierRatio);

            if (score < bestScore)
            {
                bestScore = score;
                bestDy = dy;
                bestInlierRatio = inlierRatio;
            }
        }

        // 7. 严格校验最佳匹配位移：
        // - 综合误差必须显著低于零位移误差 (bestScore < zeroScore * 0.65)
        // - 并且特征点内点重合率需达标 (bestInlierRatio >= 0.48 或 bestScore <= 10.5)
        if (bestDy >= 4 &&
            bestScore <= 18.0 &&
            bestScore < zeroScore * 0.65 &&
            (bestInlierRatio >= 0.45 || bestScore <= 9.5))
        {
            return new ScrollMatchResult(bestDy, topFixedH, bottomFixedH);
        }

        return new ScrollMatchResult(0, 0, 0);
    }

    /// <summary>
    /// 在整个重叠区域 [activeTop + dy, activeBot) 上评估候选滚动量 dy 的多行匹配度（具备抗悬浮弹窗干扰的截尾均值能力）
    /// </summary>
    private static double EvaluateDyAcrossOverlap(
        byte[] buf1, int stride1,
        byte[] buf2, int stride2,
        int[] colByteOffsets,
        List<(int Y, int XOff)> featurePoints,
        int activeTop,
        int activeBot,
        int dy,
        out double inlierRatio)
    {
        inlierRatio = 0;
        int minLastY = activeTop + dy;
        if (activeBot - minLastY < 20) return double.MaxValue;

        long featErrSum = 0;
        int featCount = 0;
        int inliers = 0;

        int nFeat = featurePoints.Count;
        for (int i = 0; i < nFeat; i++)
        {
            var pt = featurePoints[i];
            if (pt.Y < minLastY || pt.Y >= activeBot) continue;

            int freshY = pt.Y - dy;
            int idx1 = pt.Y * stride1 + pt.XOff;
            int idx2 = freshY * stride2 + pt.XOff;

            int diff = Math.Abs(buf1[idx1] - buf2[idx2])
                     + Math.Abs(buf1[idx1 + 1] - buf2[idx2 + 1])
                     + Math.Abs(buf1[idx1 + 2] - buf2[idx2 + 2]);

            // 截断单点最大异常值 (Huber / Truncated L1)，防止局部鼠标悬浮弹窗拉高真实 dy 的误差
            int clampedDiff = diff > 90 ? 90 : diff;
            featErrSum += clampedDiff;
            featCount++;

            if (diff <= 18) // 单通道误差 <= 6.0 视为匹配内点
            {
                inliers++;
            }
        }

        // 同时在重叠区均匀采样 ~16 行全宽像素，验证背景与整体行结构一致性
        int overlapH = activeBot - minLastY;
        int sampleRows = Math.Min(16, Math.Max(4, overlapH / 12));
        int colStep = Math.Max(1, colByteOffsets.Length / 24);
        long gridErrSum = 0;
        int gridCount = 0;

        for (int r = 0; r < sampleRows; r++)
        {
            int lastY = minLastY + (r * (overlapH - 1)) / Math.Max(1, sampleRows - 1);
            int freshY = lastY - dy;
            int rOff1 = lastY * stride1;
            int rOff2 = freshY * stride2;

            for (int ci = 0; ci < colByteOffsets.Length; ci += colStep)
            {
                int xOff = colByteOffsets[ci];
                int idx1 = rOff1 + xOff;
                int idx2 = rOff2 + xOff;

                int diff = Math.Abs(buf1[idx1] - buf2[idx2])
                         + Math.Abs(buf1[idx1 + 1] - buf2[idx2 + 1])
                         + Math.Abs(buf1[idx1 + 2] - buf2[idx2 + 2]);

                gridErrSum += (diff > 90 ? 90 : diff);
                gridCount++;
            }
        }

        double gridErr = gridCount > 0 ? gridErrSum / (double)(gridCount * 3) : 999.0;
        if (featCount < 12)
        {
            inlierRatio = gridErr < 5.0 ? 0.8 : 0.0;
            return gridErr;
        }

        double featErr = featErrSum / (double)(featCount * 3);
        inlierRatio = inliers / (double)featCount;

        // 综合特征点截断误差、网格误差与内点奖励
        return featErr * 0.72 + gridErr * 0.28 * (1.15 - 0.35 * inlierRatio);
    }
}
