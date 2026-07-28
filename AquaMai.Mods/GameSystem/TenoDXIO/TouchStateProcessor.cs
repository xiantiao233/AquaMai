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
                var zoneInfo = GetZoneInfo(physicalChannel);
                char block = zoneInfo.block;
                string logicalName = GetLogicalName(physicalChannel);

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
                    int largeDiffThresh = override_A[physicalChannel] != -1 ? override_A[physicalChannel] : TenoDXIO.TriggerSensitivity;

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
