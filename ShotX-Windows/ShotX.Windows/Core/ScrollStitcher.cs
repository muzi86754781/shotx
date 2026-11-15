using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ShotX.Windows.Core;

/// <summary>
/// 长截图核心拼接引擎（双向滚动水位追踪 + 全重叠区多行全局校验 + 历史长图重定位 + 悬浮弹窗自愈擦除）
/// 针对鼠标来回上下滑动防错乱重影、重复列表/表格防跨行误匹配、悬浮弹窗防截入及高分屏 DPI 抗锯齿深度优化
/// </summary>
public sealed class ScrollStitcher
{
    private Bitmap? _stitchedLongBitmap;
    private Bitmap? _lastStitchedBitmap; // 底部水位线帧：对应当前已拼接长图的最底端一屏
    private Bitmap? _prevLiveBitmap;     // 上一帧实时预览画面：用于双向位移追踪
    private int _offsetFromWatermark;    // 当前画面顶部相对于底部水位线帧的垂直偏移（<= 0 表示处于已截取区域上方）
    private bool _isStitching;

    public Bitmap? StitchedLongBitmap => _stitchedLongBitmap;
    public bool HasImage => _stitchedLongBitmap != null;
    public int CurrentHeight => _stitchedLongBitmap?.Height ?? 0;
    public int SliceCount { get; private set; }
    public int LastAddedHeight { get; private set; }

    public const int MaxHeightLimit = 32000;
    private const int MinStitchDy = 6;

    public void Reset()
    {
        _stitchedLongBitmap?.Dispose();
        _lastStitchedBitmap?.Dispose();
        _prevLiveBitmap?.Dispose();
        _stitchedLongBitmap = null;
        _lastStitchedBitmap = null;
        _prevLiveBitmap = null;
        _offsetFromWatermark = 0;
        _isStitching = false;
        SliceCount = 0;
        LastAddedHeight = 0;
    }

    public void Initialize(Bitmap initialBitmap)
    {
        Reset();
        _stitchedLongBitmap = new Bitmap(initialBitmap);
        _lastStitchedBitmap = new Bitmap(initialBitmap);
        _prevLiveBitmap = new Bitmap(initialBitmap);
        _offsetFromWatermark = 0;
        _isStitching = false;
        SliceCount = 1;
        LastAddedHeight = 0;
    }

    /// <summary>
    /// 快速判断两帧屏幕截图之间是否发生实质性像素变化（用于区分窗口是否真正响应了滚轮）
    /// </summary>
    public static bool HasSignificantChange(Bitmap a, Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height || a.Width < 16 || a.Height < 16)
            return false;

        int w = a.Width;
        int h = a.Height;
        var rect = new Rectangle(0, 0, w, h);
        BitmapData? da = null;
        BitmapData? db = null;
        try
        {
            da = a.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            db = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int sa = da.Stride;
            int sb = db.Stride;
            long diffSum = 0;
            int samples = 0;

            unsafe
            {
                byte* pa = (byte*)da.Scan0;
                byte* pb = (byte*)db.Scan0;
                int yStep = Math.Max(4, h / 32);
                int xStep = Math.Max(4, w / 32);
                for (int y = h / 8; y < h * 7 / 8; y += yStep)
                {
                    byte* ra = pa + y * sa;
                    byte* rb = pb + y * sb;
                    for (int x = w / 10; x < w * 9 / 10; x += xStep)
                    {
                        int off = x * 4;
                        diffSum += Math.Abs(ra[off] - rb[off])
                                 + Math.Abs(ra[off + 1] - rb[off + 1])
                                 + Math.Abs(ra[off + 2] - rb[off + 2]);
                        samples++;
                    }
                }
            }

            if (samples == 0) return false;
            return (diffSum / (double)(samples * 3)) > 0.65;
        }
        catch
        {
            return true;
        }
        finally
        {
            if (da != null) try { a.UnlockBits(da); } catch { }
            if (db != null) try { b.UnlockBits(db); } catch { }
        }
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

            if (lastW != freshW || lastH != freshH || lastW < 24 || lastH < 40)
            {
                return (false, false, 0);
            }

            // 0. 若相对上一实时帧完全静止，直接跳过，避免无意义的匹配运算与重影风险
            if (_prevLiveBitmap != null && !HasSignificantChange(_prevLiveBitmap, freshBitmap))
            {
                return (false, false, 0);
            }

            // 1. 优先用实时前后帧追踪用户的双向滚动（上滑为负，下滑为正）
            if (_prevLiveBitmap != null && _offsetFromWatermark < -12)
            {
                var liveMatch = FindBidirectionalMatch(_prevLiveBitmap, freshBitmap);
                if (liveMatch.ZeroInlierRatio >= 0.68 || liveMatch.ZeroScore < 2.2)
                {
                    return (false, false, 0);
                }

                // 若当前已非常接近或重新跨过水位线，转由直接对齐水位线；否则继续精确追踪水位线之上的位移
                if (liveMatch.Direction < 0 && liveMatch.SignedDy <= -4)
                {
                    _offsetFromWatermark += liveMatch.SignedDy; // 更负（继续向上滑）
                    ReplacePrevLiveBitmap(freshBitmap);
                    return (false, false, 0);
                }

                if (liveMatch.Direction > 0 && liveMatch.SignedDy >= 4)
                {
                    int projectedOffset = _offsetFromWatermark + liveMatch.SignedDy;
                    if (projectedOffset < -12)
                    {
                        _offsetFromWatermark = projectedOffset;
                        ReplacePrevLiveBitmap(freshBitmap);
                        return (false, false, 0);
                    }
                }
            }

            // 2. 与底部水位线帧 (_lastStitchedBitmap) 执行双向精确匹配
            var wmMatch = FindBidirectionalMatch(_lastStitchedBitmap, freshBitmap);

            // 2a. 画面与底部水位线重合（用户回滚到了已截取最底端），重新对齐水位线并等待向下突破
            if (wmMatch.ZeroInlierRatio >= 0.68 || wmMatch.ZeroScore < 2.2)
            {
                _offsetFromWatermark = 0;
                ReplacePrevLiveBitmap(freshBitmap);
                return (false, false, 0);
            }

            // 2b. 检测到相比底部水位线明显是【向上滚动】（负位移占优），更新水位偏移，绝不误触发向下拼接！
            if (wmMatch.Direction < 0 && wmMatch.SignedDy <= -4)
            {
                _offsetFromWatermark = wmMatch.SignedDy;
                ReplacePrevLiveBitmap(freshBitmap);
                return (false, false, 0);
            }

            // 2c. 如果之前处于水位线之上，要求跨过水位线的向下匹配具备极高可信度（防跨行重复误匹配）
            bool wasAboveWatermark = _offsetFromWatermark < -16;
            bool allowAppend = wmMatch.Direction > 0 && wmMatch.SignedDy >= MinStitchDy;
            if (allowAppend && wasAboveWatermark)
            {
                // 若先前向上滚了很多，必须确保这次向下匹配具备极高的内点一致率，确认真的越过了历史底线
                if (wmMatch.BestDownInlier < 0.72 || wmMatch.BestDownScore > 7.5)
                {
                    allowAppend = false;
                }
            }

            if (!allowAppend)
            {
                // 未能与水位线匹配（例如用户用鼠标快速上下大幅度拖动）：更新实时帧追踪或尝试在历史长图中重定位
                if (_prevLiveBitmap != null)
                {
                    var liveStep = FindBidirectionalMatch(_prevLiveBitmap, freshBitmap);
                    if (liveStep.Direction != 0 && Math.Abs(liveStep.SignedDy) >= 4)
                    {
                        _offsetFromWatermark = Math.Min(0, _offsetFromWatermark + liveStep.SignedDy);
                        ReplacePrevLiveBitmap(freshBitmap);
                        return (false, false, 0);
                    }
                }

                // 在历史长图中重定位当前视口位置，避免长距离上下跳滚后把旧内容当新内容重复拼接
                if (_stitchedLongBitmap.Height > freshH + 16)
                {
                    int foundY = TryRelocalizeInLongBitmap(_stitchedLongBitmap, freshBitmap);
                    if (foundY >= 0)
                    {
                        _offsetFromWatermark = foundY - (_stitchedLongBitmap.Height - freshH);
                        ReplacePrevLiveBitmap(freshBitmap);
                        return (false, false, 0);
                    }
                }

                return (false, false, 0);
            }

            // 3. 确认向下有效扩展 dy 像素，按接缝精确对齐拼接到底部
            int dy = wmMatch.SignedDy;
            int topFixedH = wmMatch.TopFixedH;
            int bottomFixedH = wmMatch.BottomFixedH;
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

                // 保留历史长图直到底部接缝行 seamY
                g.DrawImage(
                    _stitchedLongBitmap,
                    new Rectangle(0, 0, lastW, keepOldH),
                    new Rectangle(0, 0, lastW, keepOldH),
                    GraphicsUnit.Pixel
                );

                // 从新帧严格对齐的接缝行 freshStartY = seamY - dy 追加新增底部内容
                g.DrawImage(
                    freshBitmap,
                    new Rectangle(0, keepOldH, freshW, copyFreshH),
                    new Rectangle(0, freshStartY, freshW, copyFreshH),
                    GraphicsUnit.Pixel
                );
            }

            // 4. 悬浮弹窗自愈擦除：仅当同一屏幕位置在前两帧静止不动（说明存在悬浮遮挡物），而新位置滚出干净内容时才回填修复
            HealStationaryPopupsInOverlap(newLong, _lastStitchedBitmap, freshBitmap, dy, topFixedH, seamY, oldLongH);

            _stitchedLongBitmap.Dispose();
            _stitchedLongBitmap = newLong;

            _lastStitchedBitmap.Dispose();
            _lastStitchedBitmap = new Bitmap(freshBitmap);

            ReplacePrevLiveBitmap(freshBitmap);
            _offsetFromWatermark = 0;

            SliceCount++;
            LastAddedHeight = actualAddH;
            return (true, false, actualAddH);
        }
        finally
        {
            _isStitching = false;
        }
    }

    private void ReplacePrevLiveBitmap(Bitmap freshBitmap)
    {
        _prevLiveBitmap?.Dispose();
        _prevLiveBitmap = new Bitmap(freshBitmap);
    }

    private readonly struct BidirectionalMatchResult
    {
        public readonly int Direction; // +1 = 向下滚（扩展新内容）, -1 = 向上滚（回看历史）, 0 = 未匹配
        public readonly int SignedDy;
        public readonly int TopFixedH;
        public readonly int BottomFixedH;
        public readonly double ZeroScore;
        public readonly double ZeroInlierRatio;
        public readonly double BestDownScore;
        public readonly double BestDownInlier;

        public BidirectionalMatchResult(
            int direction,
            int signedDy,
            int topFixedH,
            int bottomFixedH,
            double zeroScore,
            double zeroInlierRatio,
            double bestDownScore,
            double bestDownInlier)
        {
            Direction = direction;
            SignedDy = signedDy;
            TopFixedH = topFixedH;
            BottomFixedH = bottomFixedH;
            ZeroScore = zeroScore;
            ZeroInlierRatio = zeroInlierRatio;
            BestDownScore = bestDownScore;
            BestDownInlier = bestDownInlier;
        }
    }

    /// <summary>
    /// 在历史长图中重定位当前视口（用于鼠标大幅度/高速上下回拉后的自校准）
    /// </summary>
    private static int TryRelocalizeInLongBitmap(Bitmap longBmp, Bitmap freshBmp)
    {
        int lw = longBmp.Width;
        int lh = longBmp.Height;
        int fw = freshBmp.Width;
        int fh = freshBmp.Height;
        if (lw != fw || lh <= fh + 16 || fw < 32 || fh < 48) return -1;

        BitmapData? dl = null;
        BitmapData? df = null;
        try
        {
            dl = longBmp.LockBits(new Rectangle(0, 0, lw, lh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            df = freshBmp.LockBits(new Rectangle(0, 0, fw, fh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            int sLong = dl.Stride;
            int sFresh = df.Stride;
            byte[] bufLong = new byte[sLong * lh];
            byte[] bufFresh = new byte[sFresh * fh];
            System.Runtime.InteropServices.Marshal.Copy(dl.Scan0, bufLong, 0, bufLong.Length);
            System.Runtime.InteropServices.Marshal.Copy(df.Scan0, bufFresh, 0, bufFresh.Length);

            // 从 freshBmp 中部提取 ~180 个高对比度特征点
            int marginX = Math.Max(8, (int)(fw * 0.06));
            int usableW = fw - marginX * 2;
            int cols = Math.Min(64, usableW);
            int[] xOffs = new int[cols];
            for (int i = 0; i < cols; i++)
            {
                xOffs[i] = (marginX + (int)((long)i * (usableW - 1) / Math.Max(1, cols - 1))) * 4;
            }

            List<(int Y, int XOff)> pts = new(240);
            int yMin = Math.Max(8, (int)(fh * 0.14));
            int yMax = Math.Min(fh - 8, (int)(fh * 0.86));
            int yStep = Math.Max(2, (yMax - yMin) / 40);

            for (int y = yMin; y < yMax; y += yStep)
            {
                int rP = (y - 1) * sFresh;
                int rC = y * sFresh;
                int rN = (y + 1) * sFresh;
                for (int ci = 0; ci < cols; ci++)
                {
                    int xOff = xOffs[ci];
                    int vg = Math.Abs(bufFresh[rN + xOff] - bufFresh[rP + xOff])
                           + Math.Abs(bufFresh[rN + xOff + 1] - bufFresh[rP + xOff + 1])
                           + Math.Abs(bufFresh[rN + xOff + 2] - bufFresh[rP + xOff + 2]);
                    if (vg >= 45)
                    {
                        pts.Add((y, xOff));
                    }
                }
            }

            if (pts.Count < 24) return -1;
            if (pts.Count > 180)
            {
                var sub = new List<(int Y, int XOff)>(180);
                double step = pts.Count / 180.0;
                for (int i = 0; i < 180; i++) sub.Add(pts[(int)(i * step)]);
                pts = sub;
            }

            int maxBaseY = lh - fh - 4;
            int bestY = -1;
            double bestErr = double.MaxValue;
            double bestInlier = 0;

            // 粗搜索 (步长 6px)
            for (int baseY = 0; baseY <= maxBaseY; baseY += 6)
            {
                double err = EvalRelocalizeOffset(bufLong, sLong, bufFresh, sFresh, pts, baseY, out double inl);
                if (err < bestErr)
                {
                    bestErr = err;
                    bestY = baseY;
                    bestInlier = inl;
                }
            }

            if (bestY < 0) return -1;

            // 精细搜索 ([bestY - 5, bestY + 5])
            int refineStart = Math.Max(0, bestY - 5);
            int refineEnd = Math.Min(maxBaseY, bestY + 5);
            for (int baseY = refineStart; baseY <= refineEnd; baseY++)
            {
                double err = EvalRelocalizeOffset(bufLong, sLong, bufFresh, sFresh, pts, baseY, out double inl);
                if (err < bestErr)
                {
                    bestErr = err;
                    bestY = baseY;
                    bestInlier = inl;
                }
            }

            if (bestErr <= 7.5 && bestInlier >= 0.72)
            {
                return bestY;
            }

            return -1;
        }
        catch
        {
            return -1;
        }
        finally
        {
            if (dl != null) try { longBmp.UnlockBits(dl); } catch { }
            if (df != null) try { freshBmp.UnlockBits(df); } catch { }
        }
    }

    private static double EvalRelocalizeOffset(
        byte[] bufLong, int sLong,
        byte[] bufFresh, int sFresh,
        List<(int Y, int XOff)> pts,
        int baseY,
        out double inlierRatio)
    {
        long errSum = 0;
        int inliers = 0;
        int n = pts.Count;
        for (int i = 0; i < n; i++)
        {
            var pt = pts[i];
            int idxL = (baseY + pt.Y) * sLong + pt.XOff;
            int idxF = pt.Y * sFresh + pt.XOff;
            int diff = Math.Abs(bufLong[idxL] - bufFresh[idxF])
                     + Math.Abs(bufLong[idxL + 1] - bufFresh[idxF + 1])
                     + Math.Abs(bufLong[idxL + 2] - bufFresh[idxF + 2]);
            errSum += (diff > 96 ? 96 : diff);
            if (diff <= 21) inliers++;
        }
        inlierRatio = inliers / (double)n;
        return errSum / (double)(n * 3);
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

                        // 特征 1：常驻悬浮窗在屏幕 oldY 处静止不动 (avgStatOld < 6.5)，而滚到 y 处的真实内容已改变 (avgMismatch > 15.0)
                        bool isStationaryPopupAtOld = (avgStatOld < 6.5 && avgMismatch > 15.0 && avgStatNew > 9.0);

                        // 特征 2：瞬态悬浮气泡消失后，新帧在 oldY 处已正常接续下一段内容 (avgNextScroll < 7.5)，且原先对齐失配 (avgMismatch > 16.0)
                        bool isTransientTooltipAtOld = (canCheckNextScroll && avgNextScroll < 7.5 && avgMismatch > 16.0 && avgStatNew > 10.0);

                        if (isStationaryPopupAtOld || isTransientTooltipAtOld)
                        {
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

    private static BidirectionalMatchResult FindBidirectionalMatch(Bitmap last, Bitmap fresh)
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

            return ComputeBidirectionalMatchFromBuffers(buf1, stride1, buf2, stride2, w, h);
        }
        finally
        {
            if (dataLast != null) try { last.UnlockBits(dataLast); } catch { }
            if (dataFresh != null) try { fresh.UnlockBits(dataFresh); } catch { }
        }
    }

    private static BidirectionalMatchResult ComputeBidirectionalMatchFromBuffers(
        byte[] buf1, int stride1,
        byte[] buf2, int stride2,
        int w, int h)
    {
        int marginX = Math.Max(10, (int)(w * 0.055));
        int usableW = w - marginX * 2;
        if (usableW < 16 || h < 40)
            return new BidirectionalMatchResult(0, 0, 0, 0, 999, 0, 999, 0);

        // 1. 高密度水平采样列 (最高 128 列，兼顾文字数字密集区分能力，防止重复列表模板行误匹配)
        int numSampleCols = Math.Min(128, usableW);
        int[] allCols = new int[numSampleCols];
        for (int i = 0; i < numSampleCols; i++)
        {
            allCols[i] = marginX + (int)((long)i * (usableW - 1) / Math.Max(1, numSampleCols - 1));
        }

        int checkTop = Math.Max(4, (int)(h * 0.12));
        int checkBot = Math.Min(h - 4, (int)(h * 0.88));
        int checkRows = Math.Max(1, (checkBot - checkTop) / 2);

        // 2. 筛选发生变化且含垂直边缘特征的活跃列（剔除静止侧边栏、固定背景与右侧滚动条）
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
            if (avgDiff > 1.2 && avgGrad > 0.7)
            {
                activeColList.Add(allCols[ci]);
            }
        }

        if (activeColList.Count == 0)
        {
            return new BidirectionalMatchResult(0, 0, 0, 0, 0, 1.0, 999, 0);
        }

        int[] sampleCols = activeColList.Count >= 16 ? activeColList.ToArray() : allCols;
        int colCount = sampleCols.Length;
        int[] colByteOffsets = new int[colCount];
        for (int i = 0; i < colCount; i++) colByteOffsets[i] = sampleCols[i] * 4;

        // 3. 计算逐行垂直梯度能量与零位移内点率
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
                    if (fd <= 15)
                    {
                        unchangedFeaturePointsAtZero++;
                    }
                }
            }

            rowVertEnergy[y] = vGradSum / (double)(colCount * 3);
            rowZeroDiff[y] = fDiffSum / (double)(colCount * 3);
        }

        double zeroInlierRatioGlobal = totalFeaturePoints > 0
            ? unchangedFeaturePointsAtZero / (double)totalFeaturePoints
            : 0.0;
        if (totalFeaturePoints >= 25 && zeroInlierRatioGlobal >= 0.65)
        {
            return new BidirectionalMatchResult(0, 0, 0, 0, 1.0, zeroInlierRatioGlobal, 999, 0);
        }

        // 4. 识别顶部与底部静止固定栏 (Sticky Header / Sticky Footer)
        int maxTopScan = (int)(h * 0.22);
        int topFixedH = Math.Max(6, (int)(h * 0.04));
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
            return new BidirectionalMatchResult(0, 0, topFixedH, bottomFixedH, 999, zeroInlierRatioGlobal, 999, 0);
        }

        // 5. 提取整个有效区内的高梯度特征点（来自 last 帧用于向下搜索，同时也均匀覆盖各数字/唯一文本区）
        List<(int Y, int XOff)> featurePoints = ExtractFeaturePoints(buf1, stride1, colByteOffsets, rowVertEnergy, activeTop, activeBot, activeSpan);
        List<(int Y, int XOff)> featurePointsFresh = ExtractFeaturePoints(buf2, stride2, colByteOffsets, rowVertEnergy, activeTop, activeBot, activeSpan);

        int maxDy = Math.Min((int)(h * 0.78), activeSpan - 24);
        if (maxDy < 4)
        {
            return new BidirectionalMatchResult(0, 0, topFixedH, bottomFixedH, 999, zeroInlierRatioGlobal, 999, 0);
        }

        // 零位移基准误差
        double zeroScore = EvaluateDownDy(buf1, stride1, buf2, stride2, colByteOffsets, featurePoints, activeTop, activeBot, 0, out double zeroFeatInlier);
        if (zeroScore < 2.2 || zeroFeatInlier >= 0.68)
        {
            return new BidirectionalMatchResult(0, 0, topFixedH, bottomFixedH, zeroScore, Math.Max(zeroInlierRatioGlobal, zeroFeatInlier), 999, 0);
        }

        // 6. 双向搜索：
        // (A) 向下滚动候选 dy > 0：last[y] == fresh[y - dy]
        int bestDownDy = 0;
        double bestDownScore = double.MaxValue;
        double bestDownInlier = 0;
        int bestDownFeatCount = 0;

        for (int dy = 4; dy <= maxDy; dy++)
        {
            double score = EvaluateDownDy(buf1, stride1, buf2, stride2, colByteOffsets, featurePoints, activeTop, activeBot, dy, out double inlier, out int featCnt);
            if (score < bestDownScore)
            {
                bestDownScore = score;
                bestDownDy = dy;
                bestDownInlier = inlier;
                bestDownFeatCount = featCnt;
            }
        }

        // (B) 向上滚动候选 upDy > 0（等价于 fresh[y] == last[y - upDy]）
        int bestUpDy = 0;
        double bestUpScore = double.MaxValue;
        double bestUpInlier = 0;
        int bestUpFeatCount = 0;

        for (int upDy = 4; upDy <= maxDy; upDy++)
        {
            double score = EvaluateDownDy(buf2, stride2, buf1, stride1, colByteOffsets, featurePointsFresh, activeTop, activeBot, upDy, out double inlier, out int featCnt);
            if (score < bestUpScore)
            {
                bestUpScore = score;
                bestUpDy = upDy;
                bestUpInlier = inlier;
                bestUpFeatCount = featCnt;
            }
        }

        bool downCandidateValid = bestDownDy >= 4 &&
                                  bestDownScore <= 14.5 &&
                                  bestDownScore < zeroScore * 0.62 &&
                                  (bestDownFeatCount >= 40 ? (bestDownInlier >= 0.56 || bestDownScore <= 8.0)
                                                           : (bestDownInlier >= 0.65 && bestDownScore <= 10.0));

        bool upCandidateValid = bestUpDy >= 4 &&
                                bestUpScore <= 15.0 &&
                                bestUpScore < zeroScore * 0.65 &&
                                (bestUpFeatCount >= 40 ? (bestUpInlier >= 0.52 || bestUpScore <= 8.5)
                                                       : (bestUpInlier >= 0.62 && bestUpScore <= 10.5));

        // 7. 双向胜者裁决：当向上滚动得分明显优于向下滚动时，判定为【向上回滚】
        if (upCandidateValid && (!downCandidateValid || bestUpScore <= bestDownScore * 1.05))
        {
            return new BidirectionalMatchResult(
                -1,
                -bestUpDy,
                topFixedH,
                bottomFixedH,
                zeroScore,
                Math.Max(zeroInlierRatioGlobal, zeroFeatInlier),
                bestDownScore,
                bestDownInlier);
        }

        // 若向下滚动胜出，且显著优于反方向候选，额外做严格接缝校验与重复行歧义排重
        if (downCandidateValid && (!upCandidateValid || bestDownScore < bestUpScore * 0.85))
        {
            return new BidirectionalMatchResult(
                1,
                bestDownDy,
                topFixedH,
                bottomFixedH,
                zeroScore,
                Math.Max(zeroInlierRatioGlobal, zeroFeatInlier),
                bestDownScore,
                bestDownInlier);
        }

        return new BidirectionalMatchResult(
            0,
            0,
            topFixedH,
            bottomFixedH,
            zeroScore,
            Math.Max(zeroInlierRatioGlobal, zeroFeatInlier),
            bestDownScore,
            bestDownInlier);
    }

    private static List<(int Y, int XOff)> ExtractFeaturePoints(
        byte[] buf,
        int stride,
        int[] colByteOffsets,
        double[] rowVertEnergy,
        int activeTop,
        int activeBot,
        int activeSpan)
    {
        List<(int Y, int XOff)> pts = new(1600);
        int colCount = colByteOffsets.Length;
        int rowStep = Math.Max(1, activeSpan / 96);

        for (int y = activeTop + 2; y < activeBot - 2; y += rowStep)
        {
            if (rowVertEnergy[y] < 1.8) continue;
            int rPrev = (y - 1) * stride;
            int rCurr = y * stride;
            int rNext = (y + 1) * stride;

            for (int ci = 0; ci < colCount; ci++)
            {
                int xOff = colByteOffsets[ci];
                int iPrev = rPrev + xOff;
                int iCurr = rCurr + xOff;
                int iNext = rNext + xOff;

                int vg = Math.Abs(buf[iNext] - buf[iPrev])
                       + Math.Abs(buf[iNext + 1] - buf[iPrev + 1])
                       + Math.Abs(buf[iNext + 2] - buf[iPrev + 2]);

                int hg = 0;
                if (ci + 1 < colCount)
                {
                    int xOffNext = colByteOffsets[ci + 1];
                    hg = Math.Abs(buf[iCurr] - buf[rCurr + xOffNext])
                       + Math.Abs(buf[iCurr + 1] - buf[rCurr + xOffNext + 1])
                       + Math.Abs(buf[iCurr + 2] - buf[rCurr + xOffNext + 2]);
                }

                if (vg + hg >= 40)
                {
                    pts.Add((y, xOff));
                }
            }
        }

        if (pts.Count > 1000)
        {
            var sub = new List<(int Y, int XOff)>(1000);
            double step = pts.Count / 1000.0;
            for (int i = 0; i < 1000; i++)
            {
                sub.Add(pts[(int)(i * step)]);
            }
            pts = sub;
        }

        return pts;
    }

    private static double EvaluateDownDy(
        byte[] buf1, int stride1,
        byte[] buf2, int stride2,
        int[] colByteOffsets,
        List<(int Y, int XOff)> featurePoints,
        int activeTop,
        int activeBot,
        int dy,
        out double inlierRatio)
    {
        return EvaluateDownDy(buf1, stride1, buf2, stride2, colByteOffsets, featurePoints, activeTop, activeBot, dy, out inlierRatio, out _);
    }

    private static double EvaluateDownDy(
        byte[] buf1, int stride1,
        byte[] buf2, int stride2,
        int[] colByteOffsets,
        List<(int Y, int XOff)> featurePoints,
        int activeTop,
        int activeBot,
        int dy,
        out double inlierRatio,
        out int featCount)
    {
        inlierRatio = 0;
        featCount = 0;
        int minLastY = activeTop + dy;
        if (activeBot - minLastY < 20) return double.MaxValue;

        long featErrSum = 0;
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

            int clampedDiff = diff > 105 ? 105 : diff;
            featErrSum += clampedDiff;
            featCount++;

            if (diff <= 21) // 单通道平均误差 <= 7.0 视为准确内点
            {
                inliers++;
            }
        }

        int overlapH = activeBot - minLastY;
        int sampleRows = Math.Min(18, Math.Max(5, overlapH / 10));
        int colStep = Math.Max(1, colByteOffsets.Length / 28);
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

                gridErrSum += (diff > 105 ? 105 : diff);
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

        // 综合特征点截断误差、网格一致度与内点率惩罚：重复行即使骨架一致但关键数字错位会导致 featErr 升高且 inlierRatio 偏低
        return featErr * 0.75 + gridErr * 0.25 * (1.20 - 0.40 * inlierRatio);
    }
}
