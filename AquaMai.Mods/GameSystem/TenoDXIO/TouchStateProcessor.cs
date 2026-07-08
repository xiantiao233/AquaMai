using System;
using System.Collections.Generic;
using MelonLoader;

namespace AquaMai.Mods.GameSystem
{
    public static class TouchStateProcessor
    {
        private static ulong currentTouchMask = 0;
        private static readonly object dataLock = new object();


        // ======= 新增以下三个变量 =======
        private static ulong latchedTouchMask = 0; // 用于记录瞬时按下的锁存掩码
        private static ulong lastReadResult = 0;   // 缓存上一次给游戏返回的结果
        private static DateTime lastReadTime = DateTime.MinValue; // 防止同一帧多次读取导致锁存失效

        // 逻辑掩码存储
        private static ulong[] logicalToMaskMap = new ulong[34];

        // 分别缓存各类重载参数，-1 代表使用默认值
        private static int[] override_A = new int[34];
        private static int[] override_C_Diff = new int[34];
        private static int[] override_C_DerivT = new int[34];
        private static int[] override_C_DerivR = new int[34];
        private static int[] override_C_DiffR = new int[34];
        private static int[] override_BDE_Diff = new int[34];
        private static int[] override_BDE_DerivR = new int[34];

        private static ButtonDetector[] detectors = new ButtonDetector[34];

        // 启动校验相关
        private static int[] startupRawBuffer = new int[34];
        private static int[] setupRaw = new int[34];
        private static int startupPacketsCount = 0;
        private static bool startupRawReady = false;
        private const int SKIP_FRAMES = 200;
        private const int STARTUP_FRAMES = 30;
        private static int skipPacketsCount = 0;

        public static void Init()
        {
            TenoDXIO.InitFileLogger();

            // ===============================================
            // 【核心注入】：读取并应用 MelonLoader 配置文件中的映射表
            // ===============================================
            TenoDXIO.ApplyHardwareMapping();

            InitMappings();
            LoadOverrides();
            for (int i = 0; i < 34; i++) detectors[i] = new ButtonDetector();
        }

        private static void InitMappings()
        {
            for (int i = 0; i < 34; i++)
            {
                // 改由核心全局配置文件获取映射
                string logical = HardwareConfig.PhysicalToLogicalMap[i];
                int maskShift = 0;
                char block = logical[0];
                int num = logical[1] - '1';

                switch (block)
                {
                    case 'A': maskShift = num; break;
                    case 'B': maskShift = 8 + num; break;
                    case 'C': maskShift = 16 + num; break;
                    case 'D': maskShift = 18 + num; break;
                    case 'E': maskShift = 26 + num; break;
                }
                logicalToMaskMap[i] = 1UL << maskShift;
            }
        }

        private static void LoadOverrides()
        {
            var dict_A = TenoDXIO.ParseConfigString(TenoDXIO.Override_A_Diff);
            var dict_C_diff = TenoDXIO.ParseConfigString(TenoDXIO.Override_C_Diff);
            var dict_C_deriv_t = TenoDXIO.ParseConfigString(TenoDXIO.Override_C_DerivTrigger);
            var dict_C_deriv_r = TenoDXIO.ParseConfigString(TenoDXIO.Override_C_DerivRelease);
            var dict_C_diff_r = TenoDXIO.ParseConfigString(TenoDXIO.Override_C_DiffRelease);
            var dict_BDE_diff = TenoDXIO.ParseConfigString(TenoDXIO.Override_BDE_Diff);
            var dict_BDE_deriv_r = TenoDXIO.ParseConfigString(TenoDXIO.Override_BDE_DerivRelease);

            for (int i = 0; i < 34; i++)
            {
                string logical = HardwareConfig.PhysicalToLogicalMap[i];
                override_A[i] = dict_A.ContainsKey(logical) ? dict_A[logical] : -1;
                override_C_Diff[i] = dict_C_diff.ContainsKey(logical) ? dict_C_diff[logical] : -1;
                override_C_DerivT[i] = dict_C_deriv_t.ContainsKey(logical) ? dict_C_deriv_t[logical] : -1;
                override_C_DerivR[i] = dict_C_deriv_r.ContainsKey(logical) ? dict_C_deriv_r[logical] : -1;
                override_C_DiffR[i] = dict_C_diff_r.ContainsKey(logical) ? dict_C_diff_r[logical] : -1;
                override_BDE_Diff[i] = dict_BDE_diff.ContainsKey(logical) ? dict_BDE_diff[logical] : -1;
                override_BDE_DerivR[i] = dict_BDE_deriv_r.ContainsKey(logical) ? dict_BDE_deriv_r[logical] : -1;
            }
        }

        public static (char block, int number) GetZoneInfo(int physicalChannel)
        {
            string logical = HardwareConfig.PhysicalToLogicalMap[physicalChannel];
            return (logical[0], logical[1] - '0');
        }

        public static string GetLogicalName(int physicalChannel) => HardwareConfig.PhysicalToLogicalMap[physicalChannel];

        public static void ResetCalibration()
        {
            startupRawReady = false;
            skipPacketsCount = 0;
            startupPacketsCount = 0;
            Array.Clear(startupRawBuffer, 0, startupRawBuffer.Length);
            for (int i = 0; i < 34; i++) detectors[i]?.Reset();
        }

        public static void ProcessFrame(ushort[] physicalChannels)
        {
            if (!startupRawReady)
            {
                if (skipPacketsCount < SKIP_FRAMES)
                {
                    skipPacketsCount++;
                    return;
                }

                for (int i = 0; i < 34; i++) startupRawBuffer[i] += physicalChannels[i];
                startupPacketsCount++;

                if (startupPacketsCount >= STARTUP_FRAMES)
                {
                    for (int i = 0; i < 34; i++) setupRaw[i] = startupRawBuffer[i] / STARTUP_FRAMES;
                    startupRawReady = true;
                    MelonLogger.Msg($"[TenoDXIO] 底层 RAW 值校准完毕 (已跳过前 {SKIP_FRAMES} 帧不稳定数据)！");
                }
                return;
            }

            ulong newTouchMask = 0;
            for (int physIdx = 0; physIdx < 34; physIdx++)
            {
                int currentVal = physicalChannels[physIdx];
                bool isPressed = detectors[physIdx].ProcessFrame(physIdx, currentVal, setupRaw[physIdx]);

                if (isPressed)
                {
                    newTouchMask |= logicalToMaskMap[physIdx];
                }
            }

            // ======= 改为如下代码 =======
            lock (dataLock)
            {
                currentTouchMask = newTouchMask; // 依然记录当前的物理真实状态
                latchedTouchMask |= newTouchMask; // 按位或：只要在此期间触发过，就把这一位锁死为 1
            }
        }

        public static ulong ProvideTouchStatus(int playerNo)
        {
            lock (dataLock)
            {
                // 防抖：如果游戏在 2 毫秒内多次请求读取（说明是同一帧内的重复读取）
                // 直接返回缓存的结果，不重置锁存器
                if ((DateTime.Now - lastReadTime).TotalMilliseconds < 2.0)
                {
                    return lastReadResult;
                }

                // 最终结果 = 自上次读取后瞬间按下的键 (latched) + 当前手指还真实按在上面的键 (current)
                lastReadResult = latchedTouchMask | currentTouchMask;

                // 【核心】游戏读取完毕后，将锁存器重置为当前的物理真实状态
                latchedTouchMask = currentTouchMask;
                lastReadTime = DateTime.Now;

                return lastReadResult;
            }
        }

        public class ButtonDetector
        {
            private bool is_pressed = false;
            private int diff_deriv_down_count = -1;
            private int up = 0;
            private bool lock_releasing = false;

            private int[] history_16 = new int[16];
            private int history_idx = 0;
            private bool history_filled = false;

            public void Reset()
            {
                is_pressed = false;
                diff_deriv_down_count = -1;
                up = 0;
                lock_releasing = false;
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
                var zoneInfo = GetZoneInfo(physicalChannel);
                char block = zoneInfo.block;
                string logicalName = GetLogicalName(physicalChannel);

                int diff = current_val - setup_raw;
                int diff_deriv = current_val - GetHistory(0);
                int diff_deriv_2 = current_val - GetHistory(1);
                int diff_deriv_3 = current_val - GetHistory(2);

                bool on = false;

                if (block == 'A') // === 组 0 ===
                {
                    on = is_pressed;

                    int customDiff = override_A[physicalChannel];
                    int on_default_diff = (customDiff != -1) ? customDiff : TenoDXIO.TriggerSensitivity;

                    if (diff > on_default_diff + 400 || diff < on_default_diff - 400) up = 0;

                    int on_diff = on_default_diff;

                    int last_diff = GetHistory(0) - setup_raw;
                    if (last_diff < on_default_diff && diff >= on_default_diff) up = 1;
                    else if (last_diff >= on_default_diff && diff < on_default_diff) up = -1;

                    switch (up)
                    {
                        case 0: on_diff = on_default_diff; break;
                        case 1: on_diff = TenoDXIO.HoldThreshold; break;
                        case -1: on_diff = 800; break;
                    }

                    int absolute_safe_diff = TenoDXIO.QuickReleaseLine;

                    if (diff < 200) lock_releasing = false;

                    if ((lock_releasing && diff_deriv > 150 && diff > on_diff) || diff > on_diff * 1.5)
                    {
                        lock_releasing = false;
                    }

                    if ((diff_deriv > 150 && diff > on_diff) || diff > on_diff * 1.5)
                    {
                        lock_releasing = false;
                        diff_deriv_down_count = 0;
                    }

                    if (diff > on_diff)
                    {
                        if (diff_deriv_down_count > 0) diff_deriv_down_count--;
                        else if (lock_releasing) { }
                        else
                        {
                            if (!is_pressed && diff_deriv < TenoDXIO.HoverSpeedMax && diff < TenoDXIO.HoverDiffMax) { }
                            else on = true;
                        }
                    }
                    else
                    {
                        if (diff_deriv_down_count > 0) diff_deriv_down_count--;
                        if (is_pressed && diff > 200) lock_releasing = true;
                        on = false;
                    }

                    int deriv_down = TenoDXIO.FastLiftSpeed;
                    int last3_diff = GetHistory(2) - setup_raw;

                    if (last3_diff > 2700) deriv_down = -400;

                    if (diff_deriv < deriv_down || diff_deriv_2 < deriv_down * 1.5 || diff_deriv_3 < deriv_down * 2)
                    {
                        if (diff_deriv < -800 || diff_deriv_2 < -1200 || diff_deriv_3 < -1500)
                        {
                            if (diff < 1000)
                            {
                                on = false;
                                diff_deriv_down_count = 3;
                                if (diff > 500) lock_releasing = true;
                            }
                        }
                        else if (diff < absolute_safe_diff)
                        {
                            on = false;
                            diff_deriv_down_count = 3;
                            if (diff > 200) lock_releasing = true;
                        }
                    }
                }
                else if (block == 'C') // === 组 1 ===
                {
                    int c_diff = override_C_Diff[physicalChannel] != -1 ? override_C_Diff[physicalChannel] : TenoDXIO.BlockC_DiffThreshold;
                    int c_deriv_t = override_C_DerivT[physicalChannel] != -1 ? override_C_DerivT[physicalChannel] : TenoDXIO.BlockC_DerivThreshold;
                    int c_deriv_r = override_C_DerivR[physicalChannel] != -1 ? override_C_DerivR[physicalChannel] : TenoDXIO.BlockC_DerivRelease;
                    int c_diff_r = override_C_DiffR[physicalChannel] != -1 ? override_C_DiffR[physicalChannel] : TenoDXIO.BlockC_DiffRelease;

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

                    int bde_diff = override_BDE_Diff[physicalChannel] != -1 ? override_BDE_Diff[physicalChannel] : default_diff;
                    int bde_deriv_r = override_BDE_DerivR[physicalChannel] != -1 ? override_BDE_DerivR[physicalChannel] : default_deriv_r;

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
}