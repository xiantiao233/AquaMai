using System;
using System.Collections.Generic;

namespace AquaMai.Mods.GameSystem
{
    public class ButtonDetector
    {
        private bool is_pressed = false;

        // A区 累积-导数双鉴算法状态变量
        private int a_max_diff = 0;
        private readonly Queue<int> a_ring = new Queue<int>();
        private int a_ring_sum = 0;
        private bool a_pending = false;
        private int a_confirm_cnt = 0;
        private bool a_observing = false;
        private int a_observe_cnt = 0;
        private int a_large_gate = 0;    // large signal gate counter

        private int[] history_16 = new int[16];
        private int history_idx = 0;
        private bool history_filled = false;

        public void Reset()
        {
            is_pressed = false;
            a_max_diff = 0;
            a_ring.Clear();
            a_ring_sum = 0;
            a_pending = false;
            a_confirm_cnt = 0;
            a_observing = false;
            a_observe_cnt = 0;
            a_large_gate = 0;
            Array.Clear(history_16, 0, 16);
            history_idx = 0;
            history_filled = false;
        }

        private int GetHistory(int framesAgo)
        {
            if (!history_filled) return history_16[0];
            int index = (history_idx - 1 - framesAgo + 16) % 16;
            return history_16[index];
        }

        private void PushHistory(int val)
        {
            history_16[history_idx] = val;
            history_idx = (history_idx + 1) % 16;
            if (history_idx == 0) history_filled = true;
        }

        public bool ProcessFrame(int physicalChannel, int current_val, int setup_raw)
        {
            var zoneInfo = TouchStateProcessor.GetZoneInfo(physicalChannel);
            char block = zoneInfo.block;
            string logicalName = TouchStateProcessor.GetLogicalName(physicalChannel);

            int diff = current_val - setup_raw;
            int diff_deriv = current_val - GetHistory(0);
            int diff_deriv_2 = current_val - GetHistory(1);
            int diff_deriv_3 = current_val - GetHistory(2);

            bool on = false;

            if (block == 'A')
            {
                // ==========================================
                // A区：累积-导数双鉴算法
                // 针对大面积自电容区块，利用累积量与当前diff的关系
                // 结合一阶导数，辨别真实边缘触摸和悬空晃动
                // ==========================================

                // 大信号通道：diff >= TriggerSensitivity 进入门控确认
                int largeDiffThresh = TouchStateProcessor.Override_A[physicalChannel] != -1 ? TouchStateProcessor.Override_A[physicalChannel] : TenoDXIO.TriggerSensitivity;

                if (diff >= largeDiffThresh)
                {
                    if (!is_pressed && a_large_gate < TenoDXIO.LargeSignalGate)
                    {
                        a_large_gate++;
                        on = false;
                    }
                    else
                    {
                        on = true;
                        a_max_diff = Math.Max(a_max_diff, diff);
                        a_observing = false;
                        a_large_gate = 0;
                    }
                }
                else if (a_large_gate > 0 && diff > 200)
                {
                    // 门控到期，diff未崩溃则确认
                    a_large_gate++;
                    if (a_large_gate > TenoDXIO.LargeSignalGate)
                    {
                        on = true;
                        a_max_diff = Math.Max(a_max_diff, diff);
                        a_large_gate = 0;
                    }
                    else
                    {
                        on = false;
                    }
                }
                else if (is_pressed)
                {
                    // --- 保持阶段：动态峰值跟踪释放 ---
                    if (diff > a_max_diff)
                        a_max_diff = diff;

                    int releaseThresh = Math.Max(TenoDXIO.ReleaseFloor, (int)(a_max_diff * TenoDXIO.ReleaseRatio));

                    if (diff < releaseThresh || diff_deriv < TenoDXIO.SharpReleaseDeriv)
                    {
                        on = false;
                        a_max_diff = 0;
                        a_ring.Clear();
                        a_ring_sum = 0;
                        a_pending = false;
                        a_observing = false;
                    }
                    else
                    {
                        on = true;
                    }
                }
                else if (a_pending)
                {
                    // --- 确认阶段：含崩溃观察子阶段 ---
                    a_confirm_cnt++;
                    if (diff > a_max_diff)
                        a_max_diff = diff;

                    if (a_observing)
                    {
                        // 崩溃观察中 (优先级高于超时)
                        a_observe_cnt++;
                        if (diff_deriv < TenoDXIO.CrashDerivThreshold && diff < TenoDXIO.CrashDiffThreshold)
                        {
                            // 检测到导数崩溃：悬空误触，取消
                            a_pending = false;
                            a_observing = false;
                            a_max_diff = 0;
                            a_ring.Clear();
                            a_ring_sum = 0;
                            on = false;
                        }
                        else if (a_observe_cnt >= TenoDXIO.CrashWindow)
                        {
                            // 观察期满无崩溃：确认成功
                            on = true;
                            a_pending = false;
                            a_observing = false;
                        }
                        else
                        {
                            on = false;
                        }
                    }
                    else if (diff > TenoDXIO.ConfirmDiff)
                    {
                        // diff突破确认阈值，进入崩溃观察子阶段
                        a_observing = true;
                        a_observe_cnt = 0;
                        on = false;
                    }
                    else if (a_confirm_cnt >= TenoDXIO.ConfirmFrames)
                    {
                        // 确认超时 (仅在未进入观察期时生效)
                        a_pending = false;
                        a_observing = false;
                        a_max_diff = 0;
                        a_ring.Clear();
                        a_ring_sum = 0;
                        on = false;
                    }
                    else
                    {
                        on = false;
                    }
                }
                else
                {
                    // --- 检测阶段：维护累积窗口并判断触发条件 ---
                    a_large_gate = 0;
                    a_ring.Enqueue(diff);
                    a_ring_sum += diff;
                    if (a_ring.Count > TenoDXIO.WindowSize)
                        a_ring_sum -= a_ring.Dequeue();

                    int n = a_ring.Count;
                    if (n > 0)
                    {
                        float cum_avg = (float)a_ring_sum / n;
                        if (cum_avg > 0.1f)
                        {
                            float spike_ratio = diff / cum_avg;

                            if (spike_ratio > TenoDXIO.TriggerRatio
                                && diff_deriv > TenoDXIO.TriggerDeriv
                                && diff > TenoDXIO.TriggerDiffMin)
                            {
                                a_pending = true;
                                a_confirm_cnt = 0;
                                a_observing = false;
                                a_max_diff = diff;
                            }
                        }
                    }
                    on = false;
                }
            }
            else if (block == 'C') // === 组 1 ===
            {
                int c_diff = TouchStateProcessor.Override_C_Diff[physicalChannel] != -1 ? TouchStateProcessor.Override_C_Diff[physicalChannel] : TenoDXIO.BlockC_DiffThreshold;
                int c_deriv_t = TouchStateProcessor.Override_C_DerivT[physicalChannel] != -1 ? TouchStateProcessor.Override_C_DerivT[physicalChannel] : TenoDXIO.BlockC_DerivThreshold;
                int c_deriv_r = TouchStateProcessor.Override_C_DerivR[physicalChannel] != -1 ? TouchStateProcessor.Override_C_DerivR[physicalChannel] : TenoDXIO.BlockC_DerivRelease;
                int c_diff_r = TouchStateProcessor.Override_C_DiffR[physicalChannel] != -1 ? TouchStateProcessor.Override_C_DiffR[physicalChannel] : TenoDXIO.BlockC_DiffRelease;

                if (diff > c_diff || diff_deriv > c_deriv_t)
                {
                    if (diff_deriv < c_deriv_r && diff < c_diff * 1.5)
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
            else // === 组 2 (B/D/E区合并算法结构) ===
            {
                int default_diff = 15;
                int default_deriv_r = -16;

                if (block == 'B')
                {
                    default_diff = TenoDXIO.BlockB_DiffThreshold;
                    default_deriv_r = TenoDXIO.BlockB_DerivRelease;
                }
                else if (block == 'D')
                {
                    default_diff = TenoDXIO.BlockD_DiffThreshold;
                    default_deriv_r = TenoDXIO.BlockD_DerivRelease;
                }
                else if (block == 'E')
                {
                    default_diff = TenoDXIO.BlockE_DiffThreshold;
                    default_deriv_r = TenoDXIO.BlockE_DerivRelease;
                }

                int bde_diff = TouchStateProcessor.Override_BDE_Diff[physicalChannel] != -1 ? TouchStateProcessor.Override_BDE_Diff[physicalChannel] : default_diff;
                int bde_deriv_r = TouchStateProcessor.Override_BDE_DerivR[physicalChannel] != -1 ? TouchStateProcessor.Override_BDE_DerivR[physicalChannel] : default_deriv_r;

                int last_diff = GetHistory(0) - setup_raw;

                if (diff > bde_diff * 1.5)
                {
                    on = true;
                }
                else if (diff > bde_diff && last_diff > bde_diff / 2)
                {
                    on = true;
                }
                else if (is_pressed && diff > bde_diff)
                {
                    on = true;
                }

                if (diff_deriv < bde_deriv_r)
                {
                    if (diff < bde_diff * 1.5)
                    {
                        on = false;
                    }
                }

                if (diff <= bde_diff / 2)
                {
                    on = false;
                }
            }

            is_pressed = on;
            PushHistory(current_val);
            TenoDXIO.WriteLog(physicalChannel, block, logicalName, current_val, setup_raw, diff, diff_deriv, on);

            return on;
        }
    }
}
