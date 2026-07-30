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
        internal static int[] Override_A = new int[34];
        internal static int[] Override_C_Diff = new int[34];
        internal static int[] Override_C_DerivT = new int[34];
        internal static int[] Override_C_DerivR = new int[34];
        internal static int[] Override_C_DiffR = new int[34];
        internal static int[] Override_BDE_Diff = new int[34];
        internal static int[] Override_BDE_DerivR = new int[34];

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
                Override_A[i] = dict_A.ContainsKey(logical) ? dict_A[logical] : -1;
                Override_C_Diff[i] = dict_C_diff.ContainsKey(logical) ? dict_C_diff[logical] : -1;
                Override_C_DerivT[i] = dict_C_deriv_t.ContainsKey(logical) ? dict_C_deriv_t[logical] : -1;
                Override_C_DerivR[i] = dict_C_deriv_r.ContainsKey(logical) ? dict_C_deriv_r[logical] : -1;
                Override_C_DiffR[i] = dict_C_diff_r.ContainsKey(logical) ? dict_C_diff_r[logical] : -1;
                Override_BDE_Diff[i] = dict_BDE_diff.ContainsKey(logical) ? dict_BDE_diff[logical] : -1;
                Override_BDE_DerivR[i] = dict_BDE_deriv_r.ContainsKey(logical) ? dict_BDE_deriv_r[logical] : -1;
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

        internal static bool IsStartupRawReady => startupRawReady;

        // 单通道独立更新 (校准完成后使用)
        // 允许只更新一个通道而不影响其他通道, 避免全量同步导致重复帧
        public static void ProcessChannel(int physIdx, ushort rawValue)
        {
            if (!startupRawReady) return;
            if (physIdx < 0 || physIdx >= 34) return;

            currentRawValues[physIdx] = rawValue;
            bool isPressed = detectors[physIdx].ProcessFrame(physIdx, rawValue, setupRaw[physIdx]);
            currentTouchState[physIdx] = isPressed;

            ulong bit = logicalToMaskMap[physIdx];
            lock (dataLock)
            {
                if (isPressed)
                    currentTouchMask |= bit;
                else
                    currentTouchMask &= ~bit;

                latchedTouchMask |= currentTouchMask;
            }
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

    }
}
