using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ShotX.Windows.Core;

/// <summary>
/// 长截图核心拼接引擎（线程安全 + 历史长图全局视口重定位 + 双向步进追踪 + 重复表格行抗误匹配 + 悬浮弹窗自愈擦除）
/// 针对鼠标滑轮上下往返、快速滚动、固定表头/底栏及高度相似列表行深度优化。
/// </summary>
public sealed class ScrollStitcher
{
    private readonly object _syncLock = new();

    private Bitmap? _stitchedLongBitmap;
    private Bitmap? _lastStitchedBitmap; // 底部水位线帧：对应当前长图最底端 [LH - FH, LH)
    private Bitmap? _prevLiveBitmap;     // 上一实时采样帧：用于计算帧间运动方向与动态固定区

    private int _estimatedViewportTop;   // 当前视口在长图中的估计顶部 Y（到达底部时等于 LH - FH）
    private int _knownTopFixedH;         // 最近一次有效滚动观测到的顶部固定栏高度
    private int _knownBottomFixedH;      // 最近一次有效滚动观测到的底部固定栏高度
    private int[]? _knownActiveCols;     // 最近一次有效滚动观测到的活跃内容列（剔除静止侧栏与滚动条）

    public const int MaxHeightLimit = 32000;
    private const int MinStitchDy = 6;

    public Bitmap? StitchedLongBitmap
    {
        get
        {
            lock (_syncLock)
            {
                return _stitchedLongBitmap;
            }
        }
    }

    public bool HasImage
    {
        get
        {
            lock (_syncLock)
            {
                return _stitchedLongBitmap != null;
            }
        }
    }

    public int CurrentHeight
    {
        get
        {
            lock (_syncLock)
            {
                return _stitchedLongBitmap?.Height ?? 0;
            }
        }
    }

    public int SliceCount { get; private set; }
    public int LastAddedHeight { get; private set; }

    /// <summary>
    /// 返回当前已拼接长图的独立线程安全副本，供 UI 预览绘制与最终导出使用，避免与后台拼接线程竞争位图锁。
    /// </summary>
    public Bitmap? CreateStitchedSnapshot()
    {
        lock (_syncLock)
        {
            return _stitchedLongBitmap != null ? new Bitmap(_stitchedLongBitmap) : null;
        }
    }

    public void Reset()
    {
        lock (_syncLock)
        {
            _stitchedLongBitmap?.Dispose();
            _lastStitchedBitmap?.Dispose();
            _prevLiveBitmap?.Dispose();
            _stitchedLongBitmap = null;
            _lastStitchedBitmap = null;
            _prevLiveBitmap = null;
            _estimatedViewportTop = 0;
            _knownTopFixedH = 0;
            _knownBottomFixedH = 0;
            _knownActiveCols = null;
            SliceCount = 0;
            LastAddedHeight = 0;
        }
    }

    public void Initialize(Bitmap initialBitmap)
    {
        lock (_syncLock)
        {
            _stitchedLongBitmap?.Dispose();
            _lastStitchedBitmap?.Dispose();
            _prevLiveBitmap?.Dispose();
            _stitchedLongBitmap = new Bitmap(initialBitmap);
            _lastStitchedBitmap = new Bitmap(initialBitmap);
            _prevLiveBitmap = new Bitmap(initialBitmap);
            _estimatedViewportTop = 0;
            _knownTopFixedH = 0;
            _knownBottomFixedH = 0;
            _knownActiveCols = null;
            SliceCount = 1;
            LastAddedHeight = 0;
        }
    }

    /// <summary>
    /// 快速判断两帧屏幕截图之间是否发生实质性像素变化（用于区分窗口是否真正响应了滚轮）。
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
                int yStep = Math.Max(4, h / 36);
                int xStep = Math.Max(4, w / 36);
                for (int y = h / 10; y < h * 9 / 10; y += yStep)
                {
                    byte* ra = pa + y * sa;
                    byte* rb = pb + y * sb;
                    for (int x = w / 12; x < w * 11 / 12; x += xStep)
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
            return (diffSum / (double)(samples * 3)) > 0.55;
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
        lock (_syncLock)
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

            // 0. 相对上一实时帧完全静止则直接跳过
            if (_prevLiveBitmap != null && !HasSignificantChange(_prevLiveBitmap, freshBitmap))
            {
                return (false, false, 0);
            }

            int bottomBaseY = _stitchedLongBitmap.Height - freshH;

            // 1. 计算相对上一实时帧的相邻步进位移与运动方向（更新固定区高度与活跃列信息）
            BidirectionalMatchResult stepMatch = default;
            bool hasStepMatch = false;
            if (_prevLiveBitmap != null)
            {
                stepMatch = FindBidirectionalMatch(_prevLiveBitmap, freshBitmap);
                hasStepMatch = stepMatch.Direction != 0;

                if (hasStepMatch)
                {
                    _knownTopFixedH = Math.Min(stepMatch.TopFixedH, (int)(freshH * 0.24));
                    _knownBottomFixedH = Math.Min(stepMatch.BottomFixedH, (int)(freshH * 0.24));
                    if (stepMatch.ActiveColByteOffsets != null && stepMatch.ActiveColByteOffsets.Length >= 12)
                    {
                        _knownActiveCols = stepMatch.ActiveColByteOffsets;
                    }
                    _estimatedViewportTop = Math.Max(0, _estimatedViewportTop + stepMatch.SignedDy);
                }
            }

            // 若这一帧明显是用户主动【向上回滚】，直接跟踪位置并绝对禁止向底部拼接
            if (hasStepMatch && stepMatch.Direction < 0 && stepMatch.SignedDy <= -4 &&
                stepMatch.BestUpScore <= 6.5 && stepMatch.BestUpInlier >= 0.72)
            {
                // 若恰好回滚到距离底部一屏之内，用底部水位线校准精确 Y
                var upCheck = FindBidirectionalMatch(_lastStitchedBitmap, freshBitmap);
                if (upCheck.ZeroInlierRatio >= 0.68 || upCheck.ZeroScore < 2.2)
                {
                    _estimatedViewportTop = bottomBaseY;
                }
                else if (upCheck.Direction < 0 && upCheck.SignedDy <= -4)
                {
                    _estimatedViewportTop = Math.Max(0, bottomBaseY + upCheck.SignedDy);
                }

                ReplacePrevLiveBitmap(freshBitmap);
                return (false, false, 0);
            }

            // 2. 与底部水位线帧 (_lastStitchedBitmap) 执行双向精确匹配
            var wmMatch = FindBidirectionalMatch(_lastStitchedBitmap, freshBitmap);

            // 2a. 画面与底部水位线重合（回滚到历史最底端），重新对齐水位线
            if (wmMatch.ZeroInlierRatio >= 0.68 || wmMatch.ZeroScore < 2.2)
            {
                _estimatedViewportTop = bottomBaseY;
                ReplacePrevLiveBitmap(freshBitmap);
                return (false, false, 0);
            }

            // 2b. 当前画面明显位于底部水位线之上（处于已截取历史区域），只同步视口位置绝不拼接
            if (wmMatch.Direction < 0 && wmMatch.SignedDy <= -4)
            {
                _estimatedViewportTop = Math.Max(0, bottomBaseY + wmMatch.SignedDy);
                ReplacePrevLiveBitmap(freshBitmap);
                return (false, false, 0);
            }

            // 2c. 在做向下拼接前，首先在已有长图历史中检查当前视口是否其实仍处于历史区间 [0 .. bottomBaseY]
            int skipTop = Math.Max(_knownTopFixedH, Math.Max(wmMatch.TopFixedH, (int)(freshH * 0.08)));
            int skipBot = Math.Max(_knownBottomFixedH, Math.Max(wmMatch.BottomFixedH, (int)(freshH * 0.08)));
            if (_stitchedLongBitmap.Height > freshH + 12)
            {
                int foundY = TryRelocalizeInLongBitmap(
                    _stitchedLongBitmap,
                    freshBitmap,
                    skipTop,
                    skipBot,
                    _knownActiveCols ?? wmMatch.ActiveColByteOffsets,
                    out double relocErr,
                    out double relocInlier,
                    out double relocHardMismatch);

                if (foundY >= 0)
                {
                    bool relocIsSolid = relocErr <= 6.0 && relocInlier >= 0.78 && relocHardMismatch <= 0.06;
                    bool betterThanDown = wmMatch.Direction <= 0 || relocErr <= wmMatch.BestDownScore + 1.2;
                    if (relocIsSolid && betterThanDown)
                    {
                        _estimatedViewportTop = foundY;
                        ReplacePrevLiveBitmap(freshBitmap);
                        return (false, false, 0);
                    }
                }
            }

            // 2d. 校验向下扩展候选是否满足严格重叠置信度（杜绝重复表格行/错位强拼）
            bool allowAppend = wmMatch.Direction > 0 && wmMatch.SignedDy >= MinStitchDy;
            if (allowAppend)
            {
                bool qualityOk = wmMatch.BestDownScore <= 8.5 &&
                                 wmMatch.BestDownInlier >= 0.70 &&
                                 wmMatch.BestDownHardMismatch <= 0.09;

                // 若先前回滚到距底部很远的位置且尚未重新确认靠近底线，则要求极高匹配一致度
                if (_estimatedViewportTop < bottomBaseY - (int)(freshH * 0.35))
                {
                    qualityOk = wmMatch.BestDownScore <= 5.0 &&
                                wmMatch.BestDownInlier >= 0.80 &&
                                wmMatch.BestDownHardMismatch <= 0.05;
                }

                if (!qualityOk)
                {
                    allowAppend = false;
                }
            }

            if (!allowAppend)
            {
                // 无论是否匹配成功，永远把 _prevLiveBitmap 前进到当前帧，防止状态机锁死在过时帧上
                ReplacePrevLiveBitmap(freshBitmap);
                return (false, false, 0);
            }

            // 3. 确认向下有效扩展 dy 像素，按接缝精确对齐拼接到底部
            int dy = wmMatch.SignedDy;
            int topFixedH = Math.Min(wmMatch.TopFixedH, (int)(lastH * 0.24));
            int bottomFixedH = Math.Min(wmMatch.BottomFixedH, (int)(lastH * 0.24));
            int seamY = Math.Max(topFixedH + dy + 4, Math.Min(lastH - 4, lastH - bottomFixedH));

            int oldLongH = _stitchedLongBitmap.Height;
            int keepOldH = Math.Max(1, oldLongH - (lastH - seamY));
            int freshStartY = Math.Max(0, seamY - dy);
            int copyFreshH = freshH - freshStartY;
            int newTotalH = keepOldH + copyFreshH;
            int actualAddH = newTotalH - oldLongH;

            if (actualAddH < 2)
            {
                ReplacePrevLiveBitmap(freshBitmap);
                return (false, false, 0);
            }
            if (newTotalH > MaxHeightLimit)
            {
                ReplacePrevLiveBitmap(freshBitmap);
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

            // 4. 仅在极高置信度匹配下执行静止悬浮弹窗像素自愈，避免误伤普通表格文字
            if (wmMatch.BestDownScore <= 6.0 && wmMatch.BestDownInlier >= 0.78)
            {
                HealStationaryPopupsInOverlap(newLong, _lastStitchedBitmap, freshBitmap, dy, topFixedH, seamY, oldLongH);
            }

            _stitchedLongBitmap.Dispose();
            _stitchedLongBitmap = newLong;

            _lastStitchedBitmap.Dispose();
            _lastStitchedBitmap = new Bitmap(freshBitmap);

            ReplacePrevLiveBitmap(freshBitmap);
            _estimatedViewportTop = newLong.Height - freshH;
            _knownTopFixedH = topFixedH;
            _knownBottomFixedH = bottomFixedH;
            if (wmMatch.ActiveColByteOffsets != null && wmMatch.ActiveColByteOffsets.Length >= 12)
            {
                _knownActiveCols = wmMatch.ActiveColByteOffsets;
            }

            SliceCount++;
            LastAddedHeight = actualAddH;
            return (true, false, actualAddH);
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
        public readonly double BestDownHardMismatch;
        public readonly double BestUpScore;
        public readonly double BestUpInlier;
        public readonly int[]? ActiveColByteOffsets;

        public BidirectionalMatchResult(
            int direction,
            int signedDy,
            int topFixedH,
            int bottomFixedH,
            double zeroScore,
            double zeroInlierRatio,
            double bestDownScore,
            double bestDownInlier,
            double bestDownHardMismatch,
            double bestUpScore,
            double bestUpInlier,
            int[]? activeColByteOffsets)
        {
            Direction = direction;
            SignedDy = signedDy;
            TopFixedH = topFixedH;
            BottomFixedH = bottomFixedH;
            ZeroScore = zeroScore;
            ZeroInlierRatio = zeroInlierRatio;
            BestDownScore = bestDownScore;
            BestDownInlier = bestDownInlier;
            BestDownHardMismatch = bestDownHardMismatch;
            BestUpScore = bestUpScore;
            BestUpInlier = bestUpInlier;
            ActiveColByteOffsets = activeColByteOffsets;
        }
    }

    /// <summary>
    /// 在历史长图中重定位当前视口（严格跳过顶部/底部固定栏，并只采样活跃列，防止固定表头干扰定位）。
    /// </summary>
    private static int TryRelocalizeInLongBitmap(
        Bitmap longBmp,
        Bitmap freshBmp,
        int skipTopH,
        int skipBottomH,
        int[]? preferredColByteOffsets,
        out double outBestErr,
        out double outBestInlier,
        out double outHardMismatch)
    {
        outBestErr = double.MaxValue;
        outBestInlier = 0;
        outHardMismatch = 1.0;

        int lw = longBmp.Width;
        int lh = longBmp.Height;
        int fw = freshBmp.Width;
        int fh = freshBmp.Height;
        if (lw != fw || lh <= fh + 12 || fw < 32 || fh < 48) return -1;

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

            int[] xOffs;
            if (preferredColByteOffsets != null && preferredColByteOffsets.Length >= 12)
            {
                xOffs = preferredColByteOffsets;
            }
            else
            {
                int marginX = Math.Max(8, (int)(fw * 0.06));
                int usableW = fw - marginX * 2;
                int cols = Math.Min(72, usableW);
                xOffs = new int[cols];
                for (int i = 0; i < cols; i++)
                {
                    xOffs[i] = (marginX + (int)((long)i * (usableW - 1) / Math.Max(1, cols - 1))) * 4;
                }
            }

            int yMin = Math.Max(skipTopH + 4, Math.Max(8, (int)(fh * 0.14)));
            int yMax = Math.Min(fh - skipBottomH - 4, Math.Min(fh - 8, (int)(fh * 0.86)));
            if (yMax - yMin < 24)
            {
                yMin = Math.Max(8, (int)(fh * 0.22));
                yMax = Math.Min(fh - 8, (int)(fh * 0.78));
            }
            if (yMax - yMin < 16) return -1;

            List<(int Y, int XOff)> pts = new(320);
            int yStep = Math.Max(1, (yMax - yMin) / 64);

            for (int y = yMin; y < yMax; y += yStep)
            {
                int rP = (y - 1) * sFresh;
                int rN = (y + 1) * sFresh;
                for (int ci = 0; ci < xOffs.Length; ci++)
                {
                    int xOff = xOffs[ci];
                    int vg = Math.Abs(bufFresh[rN + xOff] - bufFresh[rP + xOff])
                           + Math.Abs(bufFresh[rN + xOff + 1] - bufFresh[rP + xOff + 1])
                           + Math.Abs(bufFresh[rN + xOff + 2] - bufFresh[rP + xOff + 2]);
                    if (vg >= 42)
                    {
                        pts.Add((y, xOff));
                    }
                }
            }

            if (pts.Count < 24) return -1;
            if (pts.Count > 240)
            {
                var sub = new List<(int Y, int XOff)>(240);
                double step = pts.Count / 240.0;
                for (int i = 0; i < 240; i++) sub.Add(pts[(int)(i * step)]);
                pts = sub;
            }

            int maxBaseY = lh - fh;
            int bestY = -1;
            double bestErr = double.MaxValue;
            double bestInlier = 0;
            double bestHard = 1.0;

            // 粗搜索 (步长 5px)
            for (int baseY = 0; baseY <= maxBaseY; baseY += 5)
            {
                double err = EvalRelocalizeOffset(bufLong, sLong, bufFresh, sFresh, pts, baseY, out double inl, out double hard);
                if (err < bestErr)
                {
                    bestErr = err;
                    bestY = baseY;
                    bestInlier = inl;
                    bestHard = hard;
                }
            }

            if (bestY < 0) return -1;

            // 精细搜索 ([bestY - 5, bestY + 5])
            int refineStart = Math.Max(0, bestY - 5);
            int refineEnd = Math.Min(maxBaseY, bestY + 5);
            for (int baseY = refineStart; baseY <= refineEnd; baseY++)
            {
                double err = EvalRelocalizeOffset(bufLong, sLong, bufFresh, sFresh, pts, baseY, out double inl, out double hard);
                if (err < bestErr)
                {
                    bestErr = err;
                    bestY = baseY;
                    bestInlier = inl;
                    bestHard = hard;
                }
            }

            outBestErr = bestErr;
            outBestInlier = bestInlier;
            outHardMismatch = bestHard;

            if (bestErr <= 7.0 && bestInlier >= 0.74 && bestHard <= 0.08)
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
        out double inlierRatio,
        out double hardMismatchRatio)
    {
        long errSum = 0;
        int inliers = 0;
        int hardMismatches = 0;
        int n = pts.Count;
        for (int i = 0; i < n; i++)
        {
            var pt = pts[i];
            int idxL = (baseY + pt.Y) * sLong + pt.XOff;
            int idxF = pt.Y * sFresh + pt.XOff;

            int diff = Math.Abs(bufLong[idxL] - bufFresh[idxF])
                     + Math.Abs(bufLong[idxL + 1] - bufFresh[idxF + 1])
                     + Math.Abs(bufLong[idxL + 2] - bufFresh[idxF + 2]);

            errSum += (diff > 105 ? 105 : diff);
            if (diff <= 21) inliers++;
            if (diff >= 60) hardMismatches++;
        }

        inlierRatio = n > 0 ? inliers / (double)n : 0.0;
        hardMismatchRatio = n > 0 ? hardMismatches / (double)n : 1.0;
        return n > 0 ? errSum / (double)(n * 3) : 999.0;
    }

    private static void HealStationaryPopupsInOverlap(
        Bitmap newLong,
        Bitmap lastBmp,
        Bitmap freshBmp,
        int dy,
        int topFixedH,
        int seamY,
        int oldLongH)
    {
        int w = lastBmp.Width;
        int h = lastBmp.Height;
        int activeTop = Math.Max(6, topFixedH);
        int activeBottom = Math.Min(h - 4, seamY);

        int overlapStart = activeTop;
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

                        bool isStationaryPopupAtOld = (avgStatOld < 5.0 && avgMismatch > 24.0 && avgStatNew > 12.0);
                        bool isTransientTooltipAtOld = (canCheckNextScroll && avgNextScroll < 6.0 && avgMismatch > 24.0 && avgStatNew > 12.0);

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
            return new BidirectionalMatchResult(0, 0, 0, 0, 999, 0, 999, 0, 1, 999, 0, null);

        // 1. 高密度水平采样列 (最高 140 列，确保短数字列与日期/手机号列均有充足样本)
        int numSampleCols = Math.Min(140, usableW);
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
            if (avgDiff > 1.0 && avgGrad > 0.6)
            {
                activeColList.Add(allCols[ci]);
            }
        }

        if (activeColList.Count == 0)
        {
            return new BidirectionalMatchResult(0, 0, 0, 0, 0, 1.0, 999, 0, 1, 999, 0, null);
        }

        int[] sampleCols = activeColList.Count >= 14 ? activeColList.ToArray() : allCols;
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
        if (totalFeaturePoints >= 25 && zeroInlierRatioGlobal >= 0.66)
        {
            return new BidirectionalMatchResult(0, 0, 0, 0, 1.0, zeroInlierRatioGlobal, 999, 0, 1, 999, 0, colByteOffsets);
        }

        // 4. 识别顶部与底部静止固定栏
        int maxTopScan = (int)(h * 0.24);
        int topFixedH = Math.Max(4, (int)(h * 0.03));
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

        int maxBotScan = (int)(h * 0.24);
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

        int activeTop = Math.Max(6, topFixedH);
        int activeBot = Math.Min(h - 6, h - Math.Max(4, bottomFixedH));
        int activeSpan = activeBot - activeTop;
        if (activeSpan < 32)
        {
            return new BidirectionalMatchResult(0, 0, topFixedH, bottomFixedH, 999, zeroInlierRatioGlobal, 999, 0, 1, 999, 0, colByteOffsets);
        }

        // 5. 高密度提取特征点（提升细小数字与唯一字段命中率）
        List<(int Y, int XOff)> featurePoints = ExtractFeaturePoints(buf1, stride1, colByteOffsets, rowVertEnergy, activeTop, activeBot, activeSpan);
        List<(int Y, int XOff)> featurePointsFresh = ExtractFeaturePoints(buf2, stride2, colByteOffsets, rowVertEnergy, activeTop, activeBot, activeSpan);

        // 支持最高接近 88% 视口高度的单步大位移匹配，应对鼠标滑轮高速连拨
        int maxDy = Math.Min((int)(h * 0.88), activeSpan - 14);
        if (maxDy < 4)
        {
            return new BidirectionalMatchResult(0, 0, topFixedH, bottomFixedH, 999, zeroInlierRatioGlobal, 999, 0, 1, 999, 0, colByteOffsets);
        }

        double zeroScore = EvaluateDownDy(buf1, stride1, buf2, stride2, colByteOffsets, featurePoints, activeTop, activeBot, 0, out double zeroFeatInlier, out _, out _);
        if (zeroScore < 2.2 || zeroFeatInlier >= 0.68)
        {
            return new BidirectionalMatchResult(0, 0, topFixedH, bottomFixedH, zeroScore, Math.Max(zeroInlierRatioGlobal, zeroFeatInlier), 999, 0, 1, 999, 0, colByteOffsets);
        }

        // 6. 双向搜索：
        // (A) 向下滚动候选 dy > 0：last[y] == fresh[y - dy]
        int bestDownDy = 0;
        double bestDownScore = double.MaxValue;
        double bestDownInlier = 0;
        double bestDownHardMismatch = 1.0;
        int bestDownFeatCount = 0;

        for (int dy = 4; dy <= maxDy; dy++)
        {
            double score = EvaluateDownDy(buf1, stride1, buf2, stride2, colByteOffsets, featurePoints, activeTop, activeBot, dy, out double inlier, out int featCnt, out double hardMismatch);
            if (score < bestDownScore)
            {
                bestDownScore = score;
                bestDownDy = dy;
                bestDownInlier = inlier;
                bestDownFeatCount = featCnt;
                bestDownHardMismatch = hardMismatch;
            }
        }

        // (B) 向上滚动候选 upDy > 0（fresh[y] == last[y - upDy]）
        int bestUpDy = 0;
        double bestUpScore = double.MaxValue;
        double bestUpInlier = 0;
        double bestUpHardMismatch = 1.0;
        int bestUpFeatCount = 0;

        for (int upDy = 4; upDy <= maxDy; upDy++)
        {
            double score = EvaluateDownDy(buf2, stride2, buf1, stride1, colByteOffsets, featurePointsFresh, activeTop, activeBot, upDy, out double inlier, out int featCnt, out double hardMismatch);
            if (score < bestUpScore)
            {
                bestUpScore = score;
                bestUpDy = upDy;
                bestUpInlier = inlier;
                bestUpFeatCount = featCnt;
                bestUpHardMismatch = hardMismatch;
            }
        }

        bool downCandidateValid = bestDownDy >= 4 &&
                                  bestDownScore <= 11.5 &&
                                  bestDownScore < zeroScore * 0.60 &&
                                  bestDownHardMismatch <= 0.12 &&
                                  (bestDownFeatCount >= 32 ? (bestDownInlier >= 0.64 || bestDownScore <= 6.5)
                                                           : (bestDownInlier >= 0.70 && bestDownScore <= 8.0));

        bool upCandidateValid = bestUpDy >= 4 &&
                                bestUpScore <= 12.0 &&
                                bestUpScore < zeroScore * 0.62 &&
                                bestUpHardMismatch <= 0.12 &&
                                (bestUpFeatCount >= 32 ? (bestUpInlier >= 0.62 || bestUpScore <= 7.0)
                                                       : (bestUpInlier >= 0.68 && bestUpScore <= 8.5));

        // 7. 双向胜者裁决：当向上滚动得分优于或接近向下滚动（且有效）时，优先判定为向上回滚，杜绝错拼
        if (upCandidateValid && (!downCandidateValid || bestUpScore <= bestDownScore * 1.08))
        {
            return new BidirectionalMatchResult(
                -1,
                -bestUpDy,
                topFixedH,
                bottomFixedH,
                zeroScore,
                Math.Max(zeroInlierRatioGlobal, zeroFeatInlier),
                bestDownScore,
                bestDownInlier,
                bestDownHardMismatch,
                bestUpScore,
                bestUpInlier,
                colByteOffsets);
        }

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
                bestDownInlier,
                bestDownHardMismatch,
                bestUpScore,
                bestUpInlier,
                colByteOffsets);
        }

        return new BidirectionalMatchResult(
            0,
            0,
            topFixedH,
            bottomFixedH,
            zeroScore,
            Math.Max(zeroInlierRatioGlobal, zeroFeatInlier),
            bestDownScore,
            bestDownInlier,
            bestDownHardMismatch,
            bestUpScore,
            bestUpInlier,
            colByteOffsets);
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
        List<(int Y, int XOff)> pts = new(2000);
        int colCount = colByteOffsets.Length;
        int rowStep = Math.Max(1, activeSpan / 160);

        for (int y = activeTop + 2; y < activeBot - 2; y += rowStep)
        {
            if (rowVertEnergy[y] < 1.6) continue;
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

                if (vg + hg >= 38)
                {
                    pts.Add((y, xOff));
                }
            }
        }

        if (pts.Count > 1100)
        {
            var sub = new List<(int Y, int XOff)>(1100);
            double step = pts.Count / 1100.0;
            for (int i = 0; i < 1100; i++)
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
        out double inlierRatio,
        out int featCount,
        out double hardMismatchRatio)
    {
        inlierRatio = 0;
        featCount = 0;
        hardMismatchRatio = 1.0;

        int minLastY = activeTop + dy;
        if (activeBot - minLastY < 14) return double.MaxValue;

        long featErrSum = 0;
        int inliers = 0;
        int hardMismatches = 0;

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

            featErrSum += (diff > 105 ? 105 : diff);
            featCount++;

            if (diff <= 21) inliers++;
            if (diff >= 60) hardMismatches++;
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
            hardMismatchRatio = gridErr < 5.0 ? 0.0 : 1.0;
            return gridErr;
        }

        double featErr = featErrSum / (double)(featCount * 3);
        inlierRatio = inliers / (double)featCount;
        hardMismatchRatio = hardMismatches / (double)featCount;

        // 对强失配比例施加显式惩罚，彻底区分“真正匹配（仅轻微锯齿）”与“重复表格行数字错位（局部强失配）”
        return featErr * 0.72 + gridErr * 0.28 * (1.20 - 0.40 * inlierRatio) + hardMismatchRatio * 28.0;
    }
}
