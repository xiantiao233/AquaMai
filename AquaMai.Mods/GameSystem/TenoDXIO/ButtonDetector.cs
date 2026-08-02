using System;

namespace AquaMai.Mods.GameSystem
{
    public class ButtonDetector
    {
        private bool is_pressed = false;

        // ==================================================
        // A区状态变量
        // ==================================================

        private double a_baseline = 0.0;
        private bool a_baseline_initialized = false;

        private double a_peak = 0.0;

        private bool a_after_release = false;
        private double a_valley_raw = 0.0;
        private bool a_valley_valid = false;

        private bool a_fast_pending = false;
        private double a_pending_peak = 0.0;

        // ==================================================
        // 历史数据
        // ==================================================

        private readonly int[] history_16 = new int[16];
        private int history_idx = 0;
        private int history_count = 0;

        public void Reset()
        {
            is_pressed = false;

            a_baseline = 0.0;
            a_baseline_initialized = false;
            a_peak = 0.0;

            a_after_release = false;
            a_valley_raw = 0.0;
            a_valley_valid = false;

            a_fast_pending = false;
            a_pending_peak = 0.0;

            Array.Clear(history_16, 0, history_16.Length);
            history_idx = 0;
            history_count = 0;
        }

        private int GetHistory(int framesAgo)
        {
            if (history_count <= 0)
                return 0;

            int availableAgo = Math.Min(
                framesAgo,
                history_count - 1
            );

            int index =
                history_idx - 1 - availableAgo;

            while (index < 0)
                index += history_16.Length;

            return history_16[index];
        }

        private void PushHistory(int val)
        {
            history_16[history_idx] = val;
            history_idx =
                (history_idx + 1) % history_16.Length;

            if (history_count < history_16.Length)
                history_count++;
        }

        public bool ProcessFrame(
            int physicalChannel,
            int current_val,
            int setup_raw
        )
        {
            var zoneInfo =
                TouchStateProcessor.GetZoneInfo(physicalChannel);

            char block = zoneInfo.block;

            string logicalName =
                TouchStateProcessor.GetLogicalName(physicalChannel);

            int diff = current_val - setup_raw;

            int diff_deriv = history_count > 0
                ? current_val - GetHistory(0)
                : 0;

            bool on = false;

            if (block == 'A')
            {
                // ==========================================
                // A区：动态基线状态机
                // ==========================================

                double raw = current_val;
                int deriv = diff_deriv;

                if (!a_baseline_initialized)
                {
                    a_baseline = setup_raw;
                    a_baseline_initialized = true;

                    a_peak = 0.0;
                    a_after_release = false;

                    a_valley_raw = raw;
                    a_valley_valid = true;

                    a_fast_pending = false;
                    a_pending_peak = 0.0;
                }

                double signal = raw - a_baseline;

                int edgeOn =
                    TouchStateProcessor.Override_A[physicalChannel] != -1
                        ? TouchStateProcessor.Override_A[physicalChannel]
                        : TenoDXIO.AEdgeOn;

                int largeOn = Math.Max(
                    edgeOn + 1,
                    TenoDXIO.ALargeOn
                );

                // ==========================================
                // 已按下
                // ==========================================
                if (is_pressed)
                {
                    if (signal > a_peak)
                        a_peak = signal;

                    double peakDrop =
                        a_peak - signal;

                    double requiredDrop = Math.Max(
                        TenoDXIO.AReleaseMinDrop,
                        a_peak * TenoDXIO.AReleaseDropRatio
                    );

                    bool gestureRelease =
                        deriv <= TenoDXIO.AReleaseDeriv
                        && peakDrop >= requiredDrop
                        && signal <=
                           a_peak * TenoDXIO.AReleasePeakRatio;

                    bool cleanRelease =
                        signal <= TenoDXIO.ACleanRelease;

                    if (cleanRelease || gestureRelease)
                    {
                        on = false;

                        // 不要在这里把raw写入a_baseline。
                        a_after_release = true;
                        a_valley_raw = raw;
                        a_valley_valid = true;

                        a_peak = 0.0;
                        a_fast_pending = false;
                        a_pending_peak = 0.0;
                    }
                    else
                    {
                        on = true;
                    }
                }

                // ==========================================
                // 未按下
                // ==========================================
                else
                {
                    if (!a_valley_valid)
                    {
                        a_valley_raw = raw;
                        a_valley_valid = true;
                    }
                    else if (raw < a_valley_raw)
                    {
                        a_valley_raw = raw;
                    }

                    double riseFromValley =
                        raw - a_valley_raw;

                    bool canTrackBaseline =
                        Math.Abs(signal) <=
                            TenoDXIO.ABaselineTrackRange
                        && Math.Abs(deriv) <=
                            TenoDXIO.ABaselineQuietDeriv
                        && !a_fast_pending;

                    if (canTrackBaseline)
                    {
                        double baselineStep =
                            signal * TenoDXIO.ABaselineAlpha;

                        double maxStep = Math.Max(
                            0.0,
                            TenoDXIO.ABaselineMaxStep
                        );

                        if (baselineStep > maxStep)
                            baselineStep = maxStep;
                        else if (baselineStep < -maxStep)
                            baselineStep = -maxStep;

                        a_baseline += baselineStep;
                        signal = raw - a_baseline;
                    }

                    if (
                        a_after_release
                        && Math.Abs(signal) <=
                            TenoDXIO.ABaselineTrackRange
                        && Math.Abs(deriv) <=
                            TenoDXIO.ABaselineQuietDeriv
                    )
                    {
                        a_after_release = false;

                        a_valley_raw = raw;
                        a_valley_valid = true;
                        riseFromValley = 0.0;
                    }

                    bool fastRepress =
                        a_after_release
                        && signal >=
                            TenoDXIO.ARepressSignalMin
                        && riseFromValley >=
                            TenoDXIO.ARepressRise
                        && deriv >=
                            TenoDXIO.ARepressDeriv;

                    bool slowRepress =
                        a_after_release
                        && signal >=
                            TenoDXIO.ARepressSignalMin
                        && riseFromValley >=
                            TenoDXIO.ARepressSlowRise
                        && deriv >=
                            TenoDXIO.AEdgeMinDeriv;

                    if (fastRepress || slowRepress)
                    {
                        on = true;

                        a_peak = Math.Max(
                            signal,
                            TenoDXIO.ARepressSignalMin
                        );

                        a_after_release = false;
                        a_valley_valid = false;
                        a_fast_pending = false;
                        a_pending_peak = 0.0;
                    }
                    else if (a_after_release)
                    {
                        // 等待从释放谷底发生下一次上升。
                        on = false;

                        a_fast_pending = false;
                        a_pending_peak = 0.0;
                    }
                    else if (a_fast_pending)
                    {
                        if (signal > a_pending_peak)
                            a_pending_peak = signal;

                        if (
                            signal >= largeOn
                            && deriv >= 0
                        )
                        {
                            on = true;

                            a_peak = Math.Max(
                                signal,
                                a_pending_peak
                            );

                            a_after_release = false;
                            a_valley_valid = false;
                            a_fast_pending = false;
                            a_pending_peak = 0.0;
                        }
                        else if (
                            deriv <=
                                TenoDXIO.APendingSettleDeriv
                            && a_pending_peak >=
                                TenoDXIO.AShortTapPeak
                        )
                        {
                            on = true;

                            a_peak = Math.Max(
                                signal,
                                a_pending_peak
                            );

                            a_after_release = false;
                            a_valley_valid = false;
                            a_fast_pending = false;
                            a_pending_peak = 0.0;
                        }
                        else if (
                            signal <
                                TenoDXIO.AFastPendingCancel
                            || (
                                deriv < 0
                                && a_pending_peak <
                                    TenoDXIO.AShortTapPeak
                            )
                        )
                        {
                            on = false;

                            a_fast_pending = false;
                            a_pending_peak = 0.0;
                        }
                        else
                        {
                            on = false;
                        }
                    }
                    else
                    {
                        if (
                            signal >= largeOn
                            && deriv >= 0
                        )
                        {
                            on = true;
                        }
                        else if (
                            signal >= edgeOn
                            && deriv >=
                                TenoDXIO.AEdgeMinDeriv
                        )
                        {
                            if (
                                deriv >=
                                TenoDXIO.AFastRiseDeriv
                            )
                            {
                                a_fast_pending = true;
                                a_pending_peak = signal;
                                on = false;
                            }
                            else
                            {
                                on = true;
                            }
                        }
                        else
                        {
                            on = false;
                        }

                        if (on)
                        {
                            a_peak = Math.Max(
                                signal,
                                edgeOn
                            );

                            a_after_release = false;
                            a_valley_valid = false;
                            a_fast_pending = false;
                            a_pending_peak = 0.0;
                        }
                    }
                }
            }
            else if (block == 'C')
            {
                // C区保持原逻辑
                int c_diff =
                    TouchStateProcessor.Override_C_Diff[physicalChannel] != -1
                        ? TouchStateProcessor.Override_C_Diff[physicalChannel]
                        : TenoDXIO.BlockC_DiffThreshold;

                int c_deriv_t =
                    TouchStateProcessor.Override_C_DerivT[physicalChannel] != -1
                        ? TouchStateProcessor.Override_C_DerivT[physicalChannel]
                        : TenoDXIO.BlockC_DerivThreshold;

                int c_deriv_r =
                    TouchStateProcessor.Override_C_DerivR[physicalChannel] != -1
                        ? TouchStateProcessor.Override_C_DerivR[physicalChannel]
                        : TenoDXIO.BlockC_DerivRelease;

                int c_diff_r =
                    TouchStateProcessor.Override_C_DiffR[physicalChannel] != -1
                        ? TouchStateProcessor.Override_C_DiffR[physicalChannel]
                        : TenoDXIO.BlockC_DiffRelease;

                if (diff > c_diff || diff_deriv > c_deriv_t)
                {
                    if (
                        diff_deriv < c_deriv_r
                        && diff < c_diff * 1.5
                    )
                    {
                        on = false;
                    }
                    else
                    {
                        on = true;
                    }
                }
                else if (diff < c_diff_r)
                {
                    on = false;
                }
            }
            else
            {
                // B/D/E区保持原逻辑
                int default_diff = 15;
                int default_deriv_r = -16;

                if (block == 'B')
                {
                    default_diff =
                        TenoDXIO.BlockB_DiffThreshold;

                    default_deriv_r =
                        TenoDXIO.BlockB_DerivRelease;
                }
                else if (block == 'D')
                {
                    default_diff =
                        TenoDXIO.BlockD_DiffThreshold;

                    default_deriv_r =
                        TenoDXIO.BlockD_DerivRelease;
                }
                else if (block == 'E')
                {
                    default_diff =
                        TenoDXIO.BlockE_DiffThreshold;

                    default_deriv_r =
                        TenoDXIO.BlockE_DerivRelease;
                }

                int bde_diff =
                    TouchStateProcessor.Override_BDE_Diff[physicalChannel] != -1
                        ? TouchStateProcessor.Override_BDE_Diff[physicalChannel]
                        : default_diff;

                int bde_deriv_r =
                    TouchStateProcessor.Override_BDE_DerivR[physicalChannel] != -1
                        ? TouchStateProcessor.Override_BDE_DerivR[physicalChannel]
                        : default_deriv_r;

                int last_diff =
                    GetHistory(0) - setup_raw;

                if (diff > bde_diff * 1.5)
                {
                    on = true;
                }
                else if (
                    diff > bde_diff
                    && last_diff > bde_diff / 2
                )
                {
                    on = true;
                }
                else if (
                    is_pressed
                    && diff > bde_diff
                )
                {
                    on = true;
                }

                if (diff_deriv < bde_deriv_r)
                {
                    if (diff < bde_diff * 1.5)
                        on = false;
                }

                if (diff <= bde_diff / 2)
                    on = false;
            }

            is_pressed = on;

            PushHistory(current_val);

            TenoDXIO.WriteLog(
                physicalChannel,
                block,
                logicalName,
                current_val,
                setup_raw,
                diff,
                diff_deriv,
                on
            );

            return on;
        }
    }
}