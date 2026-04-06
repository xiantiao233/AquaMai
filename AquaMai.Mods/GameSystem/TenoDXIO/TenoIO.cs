using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;
using AquaMai.Core.Helpers;
using AquaMai.Config.Attributes;
using Manager;
using MelonLoader;

namespace AquaMai.Mods.GameSystem
{
    [ConfigSection(
      name: "TenoDXIO Touch Trigger",
      en: "TenoDXIO Touch Trigger",
      zh: "TenoDXIO Touch Trigger")]
    public class TenoDXIO
    {
        // ================= 配置文件管理 =================
        [ConfigEntry("串口号", "主控板的COM口，例如 COM92 (支持热修改，修改后自动重连)")]
        public static string COMPort = "COM92";

        [ConfigEntry("波特率")]
        public static int BaudRate = 230400;

        [ConfigEntry("启用固定触发模式", "设置为 false 则使用方差触发")]
        public static bool EnableFixedTriggerMode = true;

        [ConfigEntry("A区固定触发基础")] public static int FixedTriggerDefaultA = 50000;
        [ConfigEntry("B区固定触发基础")] public static int FixedTriggerDefaultB = 48000;
        [ConfigEntry("C区固定触发基础")] public static int FixedTriggerDefaultC = 47000;
        [ConfigEntry("D区固定触发基础")] public static int FixedTriggerDefaultD = 50000;
        [ConfigEntry("E区固定触发基础")] public static int FixedTriggerDefaultE = 50000;

        [ConfigEntry("A区灵敏度 (通常为30)")] public static int ThresholdA = 30;
        [ConfigEntry("B区灵敏度 (通常为30)")] public static int ThresholdB = 30;
        [ConfigEntry("C区灵敏度 (通常为28)")] public static int ThresholdC = 28;
        [ConfigEntry("D区灵敏度 (通常为30)")] public static int ThresholdD = 30;
        [ConfigEntry("E区灵敏度 (通常为30)")] public static int ThresholdE = 30;

        [ConfigEntry("单独区块灵敏度覆盖", "格式如 A7:80,B2:40。用英文或中文逗号分隔")]
        public static string CustomThresholdOverrides = "";

        [ConfigEntry("BCDE区方差突变触发阈值", "默认500")]
        public static int VarianceThresholdBCDE = 500;
        [ConfigEntry("BCDE区方差突变触发阈值", "")]
        public static int VarianceThresholdBCDEDown = 300;

        [ConfigEntry("B区方varThresh阈值", "默认800。")]
        public static int VarThreshB = 800;
        [ConfigEntry("C区方varThresh阈值", "默认800。")]
        public static int VarThreshC = 800;
        [ConfigEntry("D区方varThresh阈值", "默认800。")]
        public static int VarThreshD = 800;
        [ConfigEntry("E区方varThresh阈值", "默认800。")]
        public static int VarThreshE = 800;

        // ===== 本次新增：A区方差突变补偿 =====
        [ConfigEntry("A区默认方差突变阈值", "默认2000。快速扫过A区时，若方差(Variance)大于此值也会提前触发")]
        public static int VarianceThresholdADefault = 2000;

        [ConfigEntry("A区单独方差阈值覆盖", "格式如 A1:1500,A8:2500。用英文或中文逗号分隔，未指定的按默认值")]
        public static string CustomVarianceOverridesA = "";
        // ===============================================

        // ===== 本次新增：A区滑动松开与按下的判断阈值 =====
        [ConfigEntry("A区松开判定下降阈值", "默认5500。在滑动中，如果Raw值在几帧内下降超过此值，判定为松开手指")]
        public static int AreaAReleaseDropThreshold = 5500;

        [ConfigEntry("A区按下判定上升阈值", "默认5000。在刚被判定为松开(raw10)的状态下，如果Raw值上升超过此值，再次判定为按下")]
        public static int AreaAPressRiseThreshold = 5000;

        [ConfigEntry("A区防断管子偏移值", "默认600。 当被按下时，如果Raw值在几帧内下降超过此值但仍高于rawtouched+此值松开阈值，认为是断管子误判，保持按下状态")]
        public static int AreaAPressBreakThreshold = 800;

        [ConfigEntry("A区瞬发限制", "默认10(130ms), 在瞬发之后10帧数据内不会被再次激活 设为-1禁用。 \n 用于扫圈时不小心用手指扫过或者扫过边缘")]
        public static int AreaAFastSlideFpsLimit = 10;

        [ConfigEntry("A区未触发时的固定基线变化检测，上升", "")]
        public static double reaADonwTrUP = 0.6;
        [ConfigEntry("A区未触发时的固定基线变化检测，下降", "")]
        public static double reaADonwTrDown = 0.1;
        // ===============================================

        [ConfigEntry("物理通道映射顺序", "从硬件通道0到33对应的逻辑按键名称，用逗号分隔")]
        public static string TouchSheetMapping = "A7,C2,E7,D7,B6,A6,E6,D6,B5,A5,E5,D5,B4,A4,E4,D4,B3,A3,C1,E3,D3,B2,A2,E2,D2,B1,A1,E1,D1,B8,A8,E8,D8,B7";

        [ConfigEntry("Logger", "输出数据")]
        public static int LoggerTouch = 0;
        // ===============================================

        private static bool isRunning = false;
        private static Thread serialThread;
        private static SerialPort serialPort;
        private static ulong currentTouchMask = 0;

        private static readonly object dataLock = new object();
        private static readonly object configLock = new object();

        private static readonly string[] SENSOR_ORDER = {
            "A1", "A2", "A3", "A4", "A5", "A6", "A7", "A8",
            "B1", "B2", "B3", "B4", "B5", "B6", "B7", "B8",
            "C1", "C2",
            "D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8",
            "E1", "E2", "E3", "E4", "E5", "E6", "E7", "E8"
        };

        private static Dictionary<string, int> LOGICAL_TO_CHANNEL = new Dictionary<string, int>();
        private static Dictionary<string, int> SENSOR_THRESHOLDS = new Dictionary<string, int>();
        private static Dictionary<string, int> SENSOR_VARIANCE_THRESHOLDS = new Dictionary<string, int>();
        private static Dictionary<string, CapsenseState> allTrackers = new Dictionary<string, CapsenseState>();

        // 性能优化：提前计算好每个硬件通道对应的 TouchPanelArea Mask
        private static ulong[] CHANNEL_TO_MASK = new ulong[34];

        // 启动校验相关
        private static int[] startupRawBuffer = new int[34];
        private static float[] startupRawFinal = new float[34];
        private static int startupPacketsCount = 0;
        public static bool startupRawReady = false;

        // 热重载状态追踪变量
        private static string _port = "";
        private static int _baud = 0;
        private static string _map = "";
        private static string _overrides = "";
        private static int _thA = -1, _thB = -1, _thC = -1, _thD = -1, _thE = -1;
        private static int _varADefault = -1;
        private static string _varAOverrides = "";

        public static void OnBeforeEnableCheck()
        {
            MelonLogger.Msg("[TenoDXIO] 正在注册 1P 触摸触发器 (支持实时热加载)...");

            foreach (var name in SENSOR_ORDER)
            {
                allTrackers[name] = new CapsenseState(name);
            }

            TouchStatusProvider.RegisterTouchStatusProvider(0, ProvideTouchStatus);

            isRunning = true;
            serialThread = new Thread(SerialReaderThread) { IsBackground = true };
            serialThread.Start();
        }

        private static ulong ProvideTouchStatus(int playerNo)
        {
            lock (dataLock)
            {
                return currentTouchMask;
            }
        }

        private static void RefreshConfigCache()
        {
            bool requireSerialRestart = (_port != COMPort || _baud != BaudRate);
            bool requireLogicRebuild = (
                _map != TouchSheetMapping ||
                _overrides != CustomThresholdOverrides ||
                _thA != ThresholdA || _thB != ThresholdB || _thC != ThresholdC || _thD != ThresholdD || _thE != ThresholdE ||
                _varADefault != VarianceThresholdADefault ||
                _varAOverrides != CustomVarianceOverridesA
            );

            if (requireSerialRestart)
            {
                _port = COMPort;
                _baud = BaudRate;
                if (serialPort != null)
                {
                    MelonLogger.Msg("[TenoDXIO] 检测到串口号/波特率变更，正在重启硬件连接...");
                    CloseSerial();
                }
            }

            if (requireLogicRebuild)
            {
                _map = TouchSheetMapping;
                _overrides = CustomThresholdOverrides;
                _thA = ThresholdA; _thB = ThresholdB; _thC = ThresholdC; _thD = ThresholdD; _thE = ThresholdE;
                _varADefault = VarianceThresholdADefault;
                _varAOverrides = CustomVarianceOverridesA;

                lock (configLock)
                {
                    LOGICAL_TO_CHANNEL.Clear();
                    SENSOR_VARIANCE_THRESHOLDS.Clear();
                    Array.Clear(CHANNEL_TO_MASK, 0, CHANNEL_TO_MASK.Length);

                    var sheet = _map.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < sheet.Length; i++)
                    {
                        string logicalName = sheet[i].Trim().ToUpper();
                        LOGICAL_TO_CHANNEL[logicalName] = i;

                        // 预计算 Mask 避免在 Update 中进行耗时的 Enum.TryParse
                        if (Enum.TryParse<InputManager.TouchPanelArea>(logicalName, out var area))
                        {
                            CHANNEL_TO_MASK[i] = (1UL << (int)area);
                        }
                    }

                    foreach (var name in SENSOR_ORDER)
                    {
                        switch (name[0])
                        {
                            case 'A':
                                SENSOR_THRESHOLDS[name] = _thA;
                                SENSOR_VARIANCE_THRESHOLDS[name] = _varADefault;
                                break;
                            case 'B': SENSOR_THRESHOLDS[name] = _thB; break;
                            case 'C': SENSOR_THRESHOLDS[name] = _thC; break;
                            case 'D': SENSOR_THRESHOLDS[name] = _thD; break;
                            case 'E': SENSOR_THRESHOLDS[name] = _thE; break;
                            default: SENSOR_THRESHOLDS[name] = 30; break;
                        }
                    }

                    // 解析绝对灵敏度覆盖
                    if (!string.IsNullOrWhiteSpace(_overrides))
                    {
                        var pairs = _overrides.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var pair in pairs)
                        {
                            var parts = pair.Split(new[] { ':', '：' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out int val))
                            {
                                string key = parts[0].Trim().ToUpper();
                                if (SENSOR_THRESHOLDS.ContainsKey(key)) SENSOR_THRESHOLDS[key] = val;
                            }
                        }
                    }

                    // 解析A区方差灵敏度覆盖
                    if (!string.IsNullOrWhiteSpace(_varAOverrides))
                    {
                        var pairs = _varAOverrides.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var pair in pairs)
                        {
                            var parts = pair.Split(new[] { ':', '：' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out int val))
                            {
                                string key = parts[0].Trim().ToUpper();
                                if (SENSOR_VARIANCE_THRESHOLDS.ContainsKey(key)) SENSOR_VARIANCE_THRESHOLDS[key] = val;
                            }
                        }
                    }
                }
                MelonLogger.Msg("[TenoDXIO] 判定参数与映射表热重载完成！已立刻生效。");
            }
        }

        private static void CloseSerial()
        {
            if (serialPort != null)
            {
                if (serialPort.IsOpen) { try { serialPort.Close(); } catch { } }
                serialPort = null;
            }
        }

        // ================= 后台串口处理线程 =================
        private static void SerialReaderThread()
        {
            List<byte> buffer = new List<byte>();
            byte[] readBuf = new byte[4096];

            while (isRunning)
            {
                RefreshConfigCache();

                if (serialPort == null || !serialPort.IsOpen)
                {
                    try
                    {
                        serialPort = new SerialPort(_port, _baud);
                        serialPort.ReadTimeout = 100;
                        serialPort.Open();
                        MelonLogger.Msg($"[TenoDXIO] 成功连接输入串口: {_port} @ {_baud} bps");

                        startupRawReady = false;
                        startupPacketsCount = 0;
                        Array.Clear(startupRawBuffer, 0, startupRawBuffer.Length);
                        foreach (var tracker in allTrackers.Values) tracker.Reset();
                    }
                    catch (Exception)
                    {
                        Thread.Sleep(2000);
                        continue;
                    }
                }

                try
                {
                    if (serialPort.BytesToRead > 0)
                    {
                        int bytesRead = serialPort.Read(readBuf, 0, readBuf.Length);
                        for (int i = 0; i < bytesRead; i++) buffer.Add(readBuf[i]);

                        while (buffer.Count >= 70)
                        {
                            if (buffer[0] == 0x00)
                            {
                                int checksum = 0;
                                for (int i = 0; i < 69; i++) checksum += buffer[i];

                                if ((checksum & 0xFF) == buffer[69])
                                {
                                    ushort[] channels = new ushort[34];
                                    for (int i = 0; i < 34; i++)
                                    {
                                        channels[i] = (ushort)(buffer[1 + i * 2] | (buffer[2 + i * 2] << 8));
                                    }

                                    if (!startupRawReady)
                                    {
                                        for (int i = 0; i < 34; i++) startupRawBuffer[i] += channels[i];
                                        startupPacketsCount++;
                                        if (startupPacketsCount >= 10)
                                        {
                                            for (int i = 0; i < 34; i++) startupRawFinal[i] = startupRawBuffer[i] / 10.0f;
                                            startupRawReady = true;
                                            MelonLogger.Msg("[TenoDXIO] 启动底层 RAW 值校准完毕 (10帧)！");
                                        }
                                    }

                                    // ===== 核心判定流 =====
                                    ulong newTouchMask = 0;
                                    lock (configLock)
                                    {
                                        foreach (var name in SENSOR_ORDER)
                                        {
                                            if (!LOGICAL_TO_CHANNEL.TryGetValue(name, out int chIdx)) continue;

                                            ushort rawVal = channels[chIdx];
                                            int thresh = SENSOR_THRESHOLDS[name];

                                            int status = allTrackers[name].Update(rawVal, thresh, chIdx);

                                            if (status == 1)
                                            {
                                                // 优化：使用预计算好的位掩码，消除 Enum.TryParse 的高频 GC 消耗
                                                newTouchMask |= CHANNEL_TO_MASK[chIdx];
                                            }
                                        }
                                    }

                                    lock (dataLock)
                                    {
                                        currentTouchMask = newTouchMask;
                                    }

                                    buffer.RemoveRange(0, 70);
                                }
                                else
                                {
                                    buffer.RemoveAt(0);
                                }
                            }
                            else
                            {
                                buffer.RemoveAt(0);
                            }
                        }
                    }
                    else
                    {
                        Thread.Sleep(2);
                    }
                }
                catch (Exception)
                {
                    MelonLogger.Error("[TenoDXIO] 串口断开，尝试恢复中...");
                    CloseSerial();
                }
            }

            CloseSerial();
        }

        // ================= 通道状态跟踪器 =================
        public class CapsenseState
        {
            public string logicalName;
            public char block;
            public float baseline = 0.0f;
            public float rawDefault = 0.0f;

            // 优化：将 Queue 替换为 List，并自己维护数量。以零 GC 方式模拟环形缓冲，解决 .ToArray() 导致的卡顿
            public List<int> history = new List<int>(5);
            public List<int> history2 = new List<int>(12);
            public List<int> varHistory = new List<int>(12);

            public int[] lastTouch = new int[34];
            public bool[] lastTouch2 = new bool[34];

            public string subState = null;
            public string lastSide = null;
            public int currentStatus = 0;
            public int rawTouched = 0;
            public int rawTouchedLock = 0;

            public CapsenseState(string name)
            {
                logicalName = name;
                block = name[0];
            }

            public void Reset()
            {
                baseline = 0.0f;
                rawDefault = 0.0f;
                history.Clear();
                history2.Clear();
                varHistory.Clear();
                subState = null;
                lastSide = null;
                currentStatus = 0;
                rawTouched = 0;
                rawTouchedLock = 0;
            }

            // 零 GC 缓冲记录
            private void AddToList(List<int> list, int val, int maxLen)
            {
                list.Add(val);
                if (list.Count > maxLen)
                {
                    list.RemoveAt(0);
                }
            }

            private int GetBlockDefault()
            {
                switch (block)
                {
                    case 'A': return FixedTriggerDefaultA;
                    case 'B': return FixedTriggerDefaultB;
                    case 'C': return FixedTriggerDefaultC;
                    case 'D': return FixedTriggerDefaultD;
                    case 'E': return FixedTriggerDefaultE;
                    default: return 50000;
                }
            }

            private bool IsTriggered(int raw, int variance, int threshold)
            {
                if (EnableFixedTriggerMode)
                {
                    int blockDefault = GetBlockDefault();
                    int effectiveThreshold = (threshold - 30) * 100 + blockDefault;
                    return (raw > effectiveThreshold) || (raw >= 0xFF00);
                }
                else
                {
                    return (variance > threshold) || (raw >= 0xFF00);
                }
            }

            public int Update(int raw, int threshold, int chIdx)
            {
                if (baseline == 0) baseline = raw;

                int variance = raw - (int)rawDefault;

                // ============ A 区判定 ============
                if (block == 'A')
                {
                    if (!startupRawReady) return 0;
                    if (rawDefault == 0) rawDefault = startupRawFinal[chIdx];

                    float effThreshold = EnableFixedTriggerMode ? ((threshold - 30) * 100 + GetBlockDefault()) : threshold;

                    int currentVarThreshold = SENSOR_VARIANCE_THRESHOLDS.ContainsKey(logicalName)
                                                ? SENSOR_VARIANCE_THRESHOLDS[logicalName]
                                                : VarianceThresholdADefault;

                    bool isTriggered = raw > effThreshold;
                    string currentSide = isTriggered ? "above" : "below";
                    bool down = false;

                    AddToList(history2, raw, 10);
                    AddToList(varHistory, variance, 10);

                    if (lastSide != null && currentSide != lastSide)
                    {
                        history.Clear();
                        subState = null;
                        rawTouchedLock = 0;
                        rawTouched = 0;
                        currentStatus = (currentSide == "above") ? 1 : 0;
                    }

                    lastSide = currentSide;
                    AddToList(history, raw, 4);

                    if (history.Count >= 1)
                    {
                        // 原 history.Peek() 获取的是队列最早的元素（即索引0）
                        int oldest = history[0];
                        int current = raw;

                        if (currentSide == "above")
                        {
                            down = true;
                            // 逻辑修正：这里删除了原代码中完全一致的重复块
                            if (history2.Count >= 8 && rawTouchedLock == 0)
                            {
                                int oldestHistory2 = history2[0]; // 等同于原 history2.Peek()
                                int diff = current - oldestHistory2;
                                if (diff <= 500)
                                {
                                    rawTouched = oldestHistory2;
                                    rawTouchedLock = 1;
                                }
                            }

                            if (subState != "raw10")
                            {
                                if ((oldest - current >= AreaAReleaseDropThreshold) && current < rawTouched - AreaAPressBreakThreshold)
                                {
                                    currentStatus = 0;
                                    subState = "raw10";
                                    history.Clear();
                                    AddToList(history, current, 4);
                                }
                            }
                            else
                            {
                                if (current - oldest >= AreaAPressRiseThreshold)
                                {
                                    currentStatus = 1;
                                    subState = null;
                                    history.Clear();
                                    AddToList(history, current, 4);
                                }
                            }
                        }
                        else
                        {
                            int gap = (int)(effThreshold - rawDefault);
                            if (gap <= 0) gap = 100;
                            if (current - oldest >= gap * reaADonwTrUP)
                            {
                                currentStatus = 1;
                                subState = "raw01";
                                history.Clear();
                                AddToList(history, current, 4);
                            }
                            else if (oldest - current >= gap * reaADonwTrDown)
                            {
                                currentStatus = 0;
                                subState = null;
                                history.Clear();
                                AddToList(history, current, 4);
                            }

                            // 性能优化：直接取倒数第二个元素，避免 ToArray 分配内存
                            int var1 = varHistory.Count >= 2 ? varHistory[varHistory.Count - 2] : variance;
                            // 1. 计算两帧变化量 (Delta)，用于捕捉突然滑动并绝对过滤抬手抖动
                            int delta = variance - var1;
                            // 2. 状态重置：如果方差极小，说明彻底松开手了，断开触发并清除异常锁定
                            if (variance < 200)
                            {
                                currentStatus = 0;
                                if (lastTouch[chIdx] < 0) lastTouch[chIdx] = 0;
                            }
                            // 3. 冷却与触发判定
                            if (lastTouch[chIdx] > 0)
                            {
                                // 正在冷却中，数值递减，不重复触发
                                lastTouch[chIdx]--;
                            }
                            else
                            {
                                // 触发条件核心逻辑：
                                // a. 快速滑动：单帧变化率足够大 (delta > 600) 或总方差超过了配置文件中的突变阈值
                                // b. 防抬手抖动：必须是正向增加 (delta > 0)，完美排除手指抬起时的断崖式下降
                                // c. 防抢跑前夕：当前 raw 不能太接近固定基线（预留 1500 余量，马上要摸到固定基线的交给 fixed 处理）

                                bool isFastSlide = (delta > 600 || variance > currentVarThreshold);
                                bool isRising = (delta > 0);
                                bool isNotPrePress = (raw < (effThreshold - 800));

                                if (isFastSlide && isRising && isNotPrePress)
                                {
                                    currentStatus = 1; // 激活触发
                                    // 写入 FPS 冷却时间
                                    lastTouch[chIdx] = AreaAFastSlideFpsLimit >= 0 ? AreaAFastSlideFpsLimit : 10;
                                }
                            }
                        }
                    }

                    if (chIdx == LoggerTouch)
                    {
                        MelonLogger.Msg($"Raw: {raw}, Baseline: {baseline:F1}, Threshold: {threshold}, Variance: {variance},  SubState: {subState}");
                    }

                    baseline = rawDefault;
                    return currentStatus;
                }
                // ============ B、C、D、E 区判定 ============
                else
                {
                    int varThresh = 0;
                    switch (block)
                    {
                        case 'B': varThresh = VarThreshB; break;
                        case 'C': varThresh = VarThreshC; break;
                        case 'D': varThresh = VarThreshD; break;
                        case 'E': varThresh = VarThreshE; break;
                    }

                    if (baseline + varThresh > raw)
                    {
                        baseline = (baseline * 0.8f) + (raw * 0.2f);
                    }
                    variance = raw - (int)baseline;

                    if (IsTriggered(raw, variance, threshold))
                    {
                        currentStatus = 1;
                    }
                    else if (variance > VarianceThresholdBCDE)
                    {
                        currentStatus = 1;
                    }
                    else
                    {
                        currentStatus = 0;
                    }

                    // ⚠️ 注意：这段逻辑可能会吃掉上面判定成功的触发。
                    // 比如绝对阈值达到了，但方差变化平缓 (<500) 时，会被强制归零断触。请按需决定是否保留。
                    if (variance < VarianceThresholdBCDEDown)
                    {
                        currentStatus = 0;
                    }

                    return currentStatus;
                }
            }
        }
    }
}