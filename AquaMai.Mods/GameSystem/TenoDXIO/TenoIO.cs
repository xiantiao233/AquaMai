using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;
using AquaMai.Core.Helpers;
using AquaMai.Config.Attributes;
using Manager;
using MelonLoader;
using UnityEngine;

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
        public static string CustomThresholdOverrides = "A7:80";

        [ConfigEntry("BCDE区方差突变触发阈值", "默认200。方差(Variance)大于此值即使未过绝对灵敏度线也会触发")]
        public static int VarianceThresholdBCDE = 200;

        // ===== 本次新增：A区滑动松开与按下的判断阈值 =====
        [ConfigEntry("A区松开判定下降阈值", "默认3500。在滑动中，如果Raw值在几帧内下降超过此值，判定为松开手指")]
        public static int AreaAReleaseDropThreshold = 3500;

        [ConfigEntry("A区按下判定上升阈值", "默认3000。在刚被判定为松开(raw10)的状态下，如果Raw值上升超过此值，再次判定为按下")]
        public static int AreaAPressRiseThreshold = 3000;
        // ===============================================

        [ConfigEntry("物理通道映射顺序", "从硬件通道0到33对应的逻辑按键名称，用逗号分隔")]
        public static string TouchSheetMapping = "A7,C2,E7,D7,B6,A6,E6,D6,B5,A5,E5,D5,B4,A4,E4,D4,B3,A3,C1,E3,D3,B2,A2,E2,D2,B1,A1,E1,D1,B8,A8,E8,D8,B7";
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
        private static Dictionary<string, CapsenseState> allTrackers = new Dictionary<string, CapsenseState>();

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

        // ================= 热加载与字典重建 =================
        private static void RefreshConfigCache()
        {
            bool requireSerialRestart = (_port != COMPort || _baud != BaudRate);
            bool requireLogicRebuild = (
                _map != TouchSheetMapping ||
                _overrides != CustomThresholdOverrides ||
                _thA != ThresholdA || _thB != ThresholdB || _thC != ThresholdC || _thD != ThresholdD || _thE != ThresholdE
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

                lock (configLock)
                {
                    LOGICAL_TO_CHANNEL.Clear();
                    var sheet = _map.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < sheet.Length; i++)
                    {
                        LOGICAL_TO_CHANNEL[sheet[i].Trim().ToUpper()] = i;
                    }

                    foreach (var name in SENSOR_ORDER)
                    {
                        switch (name[0])
                        {
                            case 'A': SENSOR_THRESHOLDS[name] = _thA; break;
                            case 'B': SENSOR_THRESHOLDS[name] = _thB; break;
                            case 'C': SENSOR_THRESHOLDS[name] = _thC; break;
                            case 'D': SENSOR_THRESHOLDS[name] = _thD; break;
                            case 'E': SENSOR_THRESHOLDS[name] = _thE; break;
                            default: SENSOR_THRESHOLDS[name] = 30; break;
                        }
                    }

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
                                                if (Enum.TryParse<InputManager.TouchPanelArea>(name, out var area))
                                                {
                                                    newTouchMask |= (1UL << (int)area);
                                                }
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
            public float rwadefault = 0.0f;

            public Queue<int> history = new Queue<int>();
            public Queue<int> history2 = new Queue<int>();

            public string subState = null;
            public string lastSide = null;
            public int currentStatus = 0;
            public int rawtouched = 0;
            public int rawtouchedlock = 0;

            public CapsenseState(string name)
            {
                logicalName = name;
                block = name[0];
            }

            public void Reset()
            {
                baseline = 0.0f;
                rwadefault = 0.0f;
                history.Clear();
                history2.Clear();
                subState = null;
                lastSide = null;
                currentStatus = 0;
                rawtouched = 0;
                rawtouchedlock = 0;
            }

            private void AddToQueue(Queue<int> q, int val, int maxLen)
            {
                q.Enqueue(val);
                while (q.Count > maxLen) q.Dequeue();
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

                int variance = 0;

                // ============ A 区判定 ============
                if (block == 'A')
                {
                    if (!startupRawReady) return 0;
                    if (rwadefault == 0) rwadefault = startupRawFinal[chIdx];

                    float effThreshold = EnableFixedTriggerMode ? ((threshold - 30) * 100 + GetBlockDefault()) : threshold;
                    string currentSide = (raw > effThreshold) ? "above" : "below";

                    AddToQueue(history2, raw, 10);

                    if (lastSide != null && currentSide != lastSide)
                    {
                        history.Clear();
                        subState = null;
                        rawtouchedlock = 0;
                        rawtouched = 0;
                        currentStatus = (currentSide == "above") ? 1 : 0;
                    }

                    lastSide = currentSide;
                    AddToQueue(history, raw, 4);

                    if (history.Count >= 1)
                    {
                        int oldest = history.Peek();
                        int current = raw;

                        if (currentSide == "above")
                        {
                            if (history2.Count >= 8 && rawtouchedlock == 0)
                            {
                                int diff = current - history2.Peek();
                                if (diff <= 500)
                                {
                                    rawtouched = history2.Peek();
                                    rawtouchedlock = 1;
                                }
                            }

                            if (subState != "raw10")
                            {
                                // ✅ 这里使用了在配置文件中引入的下降释放阈值
                                if ((oldest - current >= AreaAReleaseDropThreshold) && current < rawtouched - 100)
                                {
                                    currentStatus = 0;
                                    subState = "raw10";
                                    history.Clear();
                                    AddToQueue(history, current, 4);
                                }
                            }
                            else
                            {
                                // ✅ 这里使用了在配置文件中引入的上升按下阈值
                                if (current - oldest >= AreaAPressRiseThreshold)
                                {
                                    currentStatus = 1;
                                    subState = null;
                                    history.Clear();
                                    AddToQueue(history, current, 4);
                                }
                            }
                        }
                        // else
                        // {
                        //     float gap = effThreshold - rwadefault;
                        //     if (gap <= 0) gap = 100;

                        //     if (subState != "raw01")
                        //     {
                        //         if (current - oldest >= gap * 0.25f)
                        //         {
                        //             currentStatus = 1;
                        //             subState = "raw01";
                        //             history.Clear();
                        //             AddToQueue(history, current, 4);
                        //         }
                        //     }
                        //     else
                        //     {
                        //         if (oldest - current >= gap * 0.05f)
                        //         {
                        //             currentStatus = 0;
                        //             subState = null;
                        //             history.Clear();
                        //             AddToQueue(history, current, 4);
                        //         }
                        //     }
                        // }
                    }

                    baseline = rwadefault;
                    variance = raw - (int)rwadefault;
                    return currentStatus;
                }
                // ============ B、C、D、E 区判定 ============
                else
                {
                    int varThresh = 0;
                    switch (block)
                    {
                        case 'B': varThresh = 600; break;
                        case 'C': varThresh = 500; break;
                        case 'D': varThresh = 800; break;
                        case 'E': varThresh = 600; break;
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

                    return currentStatus;
                }
            }
        }
    }
}