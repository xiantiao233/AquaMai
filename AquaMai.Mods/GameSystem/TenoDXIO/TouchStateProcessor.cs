using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Manager;
using MelonLoader;

namespace AquaMai.Mods.GameSystem
{
    public static class TouchStateProcessor
    {
        private static ulong currentTouchMask = 0;
        private static readonly object dataLock = new object();

        private static ulong latchedTouchMask = 0;
        private static ulong lastReadResult = 0;
        private static DateTime lastReadTime = DateTime.MinValue;

        // ========= 判定日志集成支持 =========
        // 主线程每帧写入、串口线程读取的共享时间基准（单位: ms，对应 NotesManager.GetCurrentMsec()）
        public static volatile float CurrentGameTimeMs;
        // 主线程每帧递增的帧号
        public static volatile int CurrentFrameNumber;

        // 存储最新 34 通道原始电容值（串口线程 ProcessFrame 中写入，主线程只读）
        private static readonly ushort[] currentRawValues = new ushort[34];
        // 每通道当前按下状态
        private static readonly bool[] currentTouchState = new bool[34];

        // ButtonId(0-7) → 物理通道号(0-33) 反向映射
        // 用于判定日志中根据轨道号查出对应的物理通道
        private static readonly int[] buttonIdToPhysicalChannel = new int[8];

        // ========= 判定事件缓冲 =========
        // 主线程 Harmony Patch 产生判定事件入队，帧末尾统一出队写入日志文件
        public static readonly ConcurrentQueue<JudgeLogEntry> JudgeLogBuffer = new ConcurrentQueue<JudgeLogEntry>();

        // 判定日志条目
        public struct JudgeLogEntry
        {
            public float GameTimeMs;        // 游戏内毫秒时间 NotesManager.GetCurrentMsec()
            public int FrameNumber;         // 当前帧号
            public int ButtonId;            // 0-7 轨道号
            public int MonitorId;           // 0=1P, 1=2P
            public NoteJudge.ETiming Timing;   // 15 级判定枚举
            public float DiffMsec;          // 判定时间差（负数=提前触发）
            public string NoteTypeStr;      // "TAP" / "HOLD" / "SLIDE" / "TOUCH" / "BREAK"
            public int PhysicalChannel;     // 0-33 物理通道
            public string LogicalName;      // "A5" 等逻辑名称
            public ushort CurrentRaw;       // 该通道当前原始电容值
            public int SetupRaw;            // 该通道校准基线
            public bool TouchState;         // 当前是否按下
        }

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

            TenoDXIO.ApplyHardwareMapping();

            InitMappings();
            LoadOverrides();
            for (int i = 0; i < 34; i++) detectors[i] = new ButtonDetector();
        }

        private static void InitMappings()
        {
            // 初始化 ButtonId→物理通道 反向映射
            for (int _i = 0; _i < buttonIdToPhysicalChannel.Length; _i++) buttonIdToPhysicalChannel[_i] = -1;

            for (int i = 0; i < 34; i++)
            {
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

                // 构建 ButtonId → 物理通道的反查表（以首次出现的 A 区映射为准）
                if (block == 'A' && num >= 0 && num < 8 && buttonIdToPhysicalChannel[num] == -1)
                {
                    buttonIdToPhysicalChannel[num] = i;
                }
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

        // ========= 新增：公开原始值和校准基线的访问（供主线程判定日志使用） =========
        public static ushort GetCurrentRaw(int physChannel) => currentRawValues[physChannel];
        public static int GetSetupRaw(int physChannel) => setupRaw[physChannel];
        public static bool GetTouchState(int physChannel) => currentTouchState[physChannel];
        public static int GetPhysicalChannelForButton(int buttonId) =>
            buttonId >= 0 && buttonId < 8 ? buttonIdToPhysicalChannel[buttonId] : -1;

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

                // 存储最新原始值（判定日志读取）
                currentRawValues[physIdx] = physicalChannels[physIdx];

                bool isPressed = detectors[physIdx].ProcessFrame(physIdx, currentVal, setupRaw[physIdx]);

                // 存储按下状态
                currentTouchState[physIdx] = isPressed;

                if (isPressed)
                {
                    newTouchMask |= logicalToMaskMap[physIdx];
                }
            }

            lock (dataLock)
            {
                currentTouchMask = newTouchMask;
                latchedTouchMask |= newTouchMask;
            }
        }

        public static ulong ProvideTouchStatus(int playerNo)
        {
            lock (dataLock)
            {
                if ((DateTime.Now - lastReadTime).TotalMilliseconds < 2.0)
                {
                    return lastReadResult;
                }

                lastReadResult = latchedTouchMask | currentTouchMask;

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
            private bool edge_holding = false;
            private bool edge_armed = false;
            private int edge_cooldown = 0;
            private int edge_prev_deriv = 0;

            private int[] history_16 = new int[16];
            private int history_idx = 0;
            private bool history_filled = false;

            public void Reset()
            {
                is_pressed = false;
                diff_deriv_down_count = -1;
                up = 0;
                lock_releasing = false;
                edge_holding = false;
                edge_armed = false;
                edge_cooldown = 0;
                edge_prev_deriv = 0;
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
                    int zoneNum = zoneInfo.number;
                    bool isEdgeA = (zoneNum >= 2 && zoneNum <= 5);

                    if (isEdgeA)
                    {
                        // ===== v2 Armed+Impact 边缘算法 (A2/A3/A4/A5) =====
                        // 边缘区域信号弱 (Diff 200-500), Deriv 可能仅 6-38
                        // 双通道检测: Deriv预武装 + Delta-Diff增量武装
                        int armSpeed = TenoDXIO.EdgeArmSpeed;
                        int edgeDelta = TenoDXIO.EdgeDeltaArm;
                        int customMinDiff = override_A[physicalChannel];
                        int minDiff = (customMinDiff != -1) ? customMinDiff : TenoDXIO.EdgeMinDiff;
                        int impactSpd = TenoDXIO.ImpactSpeedCap;

                        // 边缘区域自动放宽急刹车 (边缘d2仅 -20~-200, 中心 -100~-1500)
                        int impactAccel = TenoDXIO.ImpactAccel;
                        if (impactAccel < -45) impactAccel = -45;

                        // d2 = 二阶导 (当前 Deriv - 上一帧 Deriv)
                        int d2 = diff_deriv - edge_prev_deriv;
                        edge_prev_deriv = diff_deriv;

                        // delta_diff = 跨帧 Diff 跳变 = diff_deriv (base抵消)
                        int deltaDiff = diff_deriv;

                        // 冷却递减
                        if (edge_cooldown > 0) edge_cooldown--;

                        if (!is_pressed)
                        {
                            on = false;

                            if (edge_cooldown == 0)
                            {
                                // 1. Deriv 预武装
                                if (diff_deriv > armSpeed) edge_armed = true;

                                // 2. Delta-Diff 增量武装 (跨帧跳变)
                                if (deltaDiff > edgeDelta) edge_armed = true;

                                // 3. 撞击确认 (双通道)
                                if (edge_armed && diff > minDiff)
                                {
                                    // 通道A: 标准急刹车 (d2急刹车特征)
                                    // diff_deriv >= -20 守卫: 排除手指离开的负向信号被误判为急刹车
                                    if (d2 < impactAccel && diff_deriv >= -20 && diff_deriv < impactSpd)
                                    {
                                        on = true;
                                        edge_armed = false;
                                        edge_cooldown = 0;
                                    }
                                    // 通道B: 增量跳变确认 (无需d2, 跳变本身隐含到达)
                                    // 仅当信号仍在增长时确认 (deltaDiff>0隐含diff_deriv为正)
                                    else if (deltaDiff > edgeDelta && diff_deriv < impactSpd)
                                    {
                                        on = true;
                                        edge_armed = false;
                                        edge_cooldown = 0;
                                    }
                                }

                                // 4. 防错解除武装
                                if (edge_armed && diff_deriv < 40 && d2 >= -50)
                                {
                                    edge_armed = false;
                                }
                            }
                        }
                        else
                        {
                            // 5. 释放判定
                            if (diff_deriv < TenoDXIO.EdgeFastLift || diff < TenoDXIO.EdgeSafeRelease)
                            {
                                on = false;
                                edge_armed = false;
                                edge_cooldown = 2;  // 2帧冷却防立即重武装
                            }
                            else
                            {
                                on = true;
                            }
                        }
                    }
                    else
                    {
                        // ===== 原中心区算法 (A1/A6/A7/A8) =====
                        on = is_pressed;

                        int customDiff = override_A[physicalChannel];
                        int on_default_diff = (customDiff != -1) ? customDiff : TenoDXIO.TriggerSensitivity;

                        bool is_fast_edge_strike = (diff_deriv >= TenoDXIO.EdgeTriggerDeriv) && (diff >= TenoDXIO.EdgeTriggerMinDiff);

                        if (is_fast_edge_strike)
                        {
                            edge_holding = true;
                        }
                        else if (diff < TenoDXIO.EdgeTriggerMinDiff - 50 || !is_pressed)
                        {
                            edge_holding = false;
                        }

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

                        if (edge_holding)
                        {
                            on_diff = Math.Min(on_diff, TenoDXIO.EdgeTriggerMinDiff);
                        }

                        int absolute_safe_diff = TenoDXIO.QuickReleaseLine;

                        if (diff < 200) lock_releasing = false;

                        if ((lock_releasing && diff_deriv > 150 && diff > on_diff) || diff > on_diff * 1.5 || is_fast_edge_strike)
                        {
                            lock_releasing = false;
                        }

                        if ((diff_deriv > 150 && diff > on_diff) || diff > on_diff * 1.5 || is_fast_edge_strike)
                        {
                            lock_releasing = false;
                            diff_deriv_down_count = 0;
                        }

                        if (diff > on_diff || is_fast_edge_strike)
                        {
                            if (diff_deriv_down_count > 0) diff_deriv_down_count--;
                            else if (lock_releasing) { }
                            else
                            {
                                if (!is_pressed && diff_deriv < TenoDXIO.HoverSpeedMax && diff < TenoDXIO.HoverDiffMax && !is_fast_edge_strike) { }
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
