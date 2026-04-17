using System;
using System.Collections.Generic;
using MelonLoader;

namespace AquaMai.Mods.GameSystem
{
    public static class TouchStateProcessor
    {
        public enum TouchSubState : byte { None, Raw10, Raw01 }
        public enum TouchSide : byte { None, Above, Below }

        private static ulong currentTouchMask = 0;
        private static readonly object dataLock = new object();

        private static Dictionary<string, int> LOGICAL_TO_CHANNEL = new Dictionary<string, int>();
        private static Dictionary<string, CapsenseState> allTrackers = new Dictionary<string, CapsenseState>();
        private static ulong[] CHANNEL_TO_MASK = new ulong[34];

        // 启动校验相关
        private static int[] startupRawBuffer = new int[34];
        private static float[] startupRawFinal = new float[34];
        private static int startupPacketsCount = 0;
        private static bool startupRawReady = false;

        private static readonly string[] SENSOR_ORDER = {
            "A1", "A2", "A3", "A4", "A5", "A6", "A7", "A8",
            "B1", "B2", "B3", "B4", "B5", "B6", "B7", "B8",
            "C1", "C2",
            "D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8",
            "E1", "E2", "E3", "E4", "E5", "E6", "E7", "E8"
        };

        public static void Init()
        {
            ParseMappings();
        }

        private static void ParseMappings()
        {
            LOGICAL_TO_CHANNEL.Clear();
            Array.Clear(CHANNEL_TO_MASK, 0, CHANNEL_TO_MASK.Length);

            string[] mappings = TenoDXIO.TouchSheetMapping.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < mappings.Length && i < 34; i++)
            {
                string logical = mappings[i].Trim();
                LOGICAL_TO_CHANNEL[logical] = i;

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
                CHANNEL_TO_MASK[i] = 1UL << maskShift;
            }

            var customThresh = TenoDXIO.ParseOverrideString(TenoDXIO.CustomThresholdOverrides);
            var customVarA = TenoDXIO.ParseOverrideString(TenoDXIO.CustomVarianceOverridesA);
            var customVar001 = TenoDXIO.ParseOverrideString(TenoDXIO.CustomVar001OverridesA);

            foreach (var name in SENSOR_ORDER)
            {
                if (!allTrackers.ContainsKey(name))
                    allTrackers[name] = new CapsenseState(name);

                var tracker = allTrackers[name];
                char block = name[0];

                int baseThresh = 30;
                switch (block)
                {
                    case 'A': baseThresh = TenoDXIO.ThresholdA; break;
                    case 'B': baseThresh = TenoDXIO.ThresholdB; break;
                    case 'C': baseThresh = TenoDXIO.ThresholdC; break;
                    case 'D': baseThresh = TenoDXIO.ThresholdD; break;
                    case 'E': baseThresh = TenoDXIO.ThresholdE; break;
                }
                if (customThresh.TryGetValue(name, out int overrideTh)) baseThresh = overrideTh;
                tracker.cachedThreshold = baseThresh;

                if (block == 'A')
                {
                    tracker.cachedVarThreshold = customVarA.TryGetValue(name, out int varA) ? varA : TenoDXIO.VarianceThresholdADefault;
                    tracker.cachedVar001Threshold = customVar001.TryGetValue(name, out int var001) ? var001 : TenoDXIO.Var001Default;
                }
            }
        }

        public static void ResetCalibration()
        {
            startupRawReady = false;
            startupPacketsCount = 0;
            Array.Clear(startupRawBuffer, 0, startupRawBuffer.Length);
            foreach (var tracker in allTrackers.Values) tracker.Reset();
        }

        public static void ProcessFrame(ushort[] channels)
        {
            // 校准启动底板
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
                return;
            }

            ulong newTouchMask = 0;
            foreach (var name in SENSOR_ORDER)
            {
                if (!LOGICAL_TO_CHANNEL.TryGetValue(name, out int chIdx)) continue;

                ushort rawVal = channels[chIdx];
                var tracker = allTrackers[name];

                int status = tracker.Update(rawVal, tracker.cachedThreshold, chIdx);

                if (status == 1)
                {
                    newTouchMask |= CHANNEL_TO_MASK[chIdx];
                }
            }

            lock (dataLock)
            {
                currentTouchMask = newTouchMask;
            }
        }

        public static ulong ProvideTouchStatus(int playerNo)
        {
            lock (dataLock)
            {
                return currentTouchMask;
            }
        }

        // ================= 零GC环形缓冲区 =================
        public class FastQueue
        {
            private int[] data;
            private int head;
            public int Count { get; private set; }
            private int limit;

            public FastQueue(int limit)
            {
                this.limit = limit;
                data = new int[limit];
            }

            public void Add(int val)
            {
                if (Count < limit)
                {
                    data[Count++] = val;
                }
                else
                {
                    data[head] = val;
                    head = (head + 1) % limit;
                }
            }

            public int this[int index]
            {
                get
                {
                    if (Count < limit) return data[index];
                    return data[(head + index) % limit];
                }
            }

            public void Clear()
            {
                head = 0;
                Count = 0;
            }
        }

        // ================= 通道状态跟踪器 =================
        public class CapsenseState
        {
            public string logicalName;
            public char block;
            public float baseline = 0.0f;
            public float rawDefault = 0.0f;

            public FastQueue history = new FastQueue(4);
            public FastQueue history2 = new FastQueue(10);
            public FastQueue varHistory = new FastQueue(10);

            public int cachedThreshold;
            public int cachedVarThreshold;
            public int cachedVar001Threshold;

            public int lastTouchFrames = 0;
            public TouchSubState subState = TouchSubState.None;
            public TouchSide lastSide = TouchSide.None;
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
                subState = TouchSubState.None;
                lastSide = TouchSide.None;
                currentStatus = 0;
                rawTouched = 0;
                rawTouchedLock = 0;
                lastTouchFrames = 0;
            }

            private int GetBlockDefault()
            {
                return block switch
                {
                    'A' => TenoDXIO.FixedTriggerDefaultA,
                    'B' => TenoDXIO.FixedTriggerDefaultB,
                    'C' => TenoDXIO.FixedTriggerDefaultC,
                    'D' => TenoDXIO.FixedTriggerDefaultD,
                    'E' => TenoDXIO.FixedTriggerDefaultE,
                    _ => 50000,
                };
            }

            private bool IsTriggered(int raw, int variance, int threshold)
            {
                if (TenoDXIO.EnableFixedTriggerMode)
                {
                    int effectiveThreshold = (threshold - 30) * 100 + GetBlockDefault();
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

                if (chIdx == TenoDXIO.LoggerTouch)
                {
                    MelonLogger.Msg($"Raw: {raw}, Baseline: {baseline:F1}, Threshold: {threshold}, Variance: {variance}");
                }

                if (block == 'A')
                {
                    if (rawDefault == 0) rawDefault = startupRawFinal[chIdx];

                    float effThreshold = TenoDXIO.EnableFixedTriggerMode ? ((threshold - 30) * 100 + GetBlockDefault()) : threshold;
                    bool isTriggered = raw > effThreshold;
                    TouchSide currentSide = isTriggered ? TouchSide.Above : TouchSide.Below;

                    history2.Add(raw);
                    varHistory.Add(variance);

                    if (lastSide != TouchSide.None && currentSide != lastSide)
                    {
                        history.Clear();
                        subState = TouchSubState.None;
                        rawTouchedLock = 0;
                        rawTouched = 0;
                        currentStatus = (currentSide == TouchSide.Above) ? 1 : 0;
                    }

                    lastSide = currentSide;
                    history.Add(raw);

                    if (history.Count >= 1)
                    {
                        int oldest = history[0];
                        int current = raw;

                        if (currentSide == TouchSide.Above)
                        {
                            if (history2.Count >= 8 && rawTouchedLock == 0)
                            {
                                int oldestHistory2 = history2[0];
                                if (current > effThreshold)
                                {
                                    rawTouched = current;
                                    rawTouchedLock = 1;
                                }
                            }

                            if (subState != TouchSubState.Raw10)
                            {
                                if ((oldest - current >= TenoDXIO.AreaAReleaseDropThreshold) && current < rawTouched - TenoDXIO.AreaAPressBreakThreshold)
                                {
                                    currentStatus = 0;
                                    subState = TouchSubState.Raw10;
                                    history.Clear();
                                    history.Add(current);
                                }
                            }
                            else
                            {
                                if (current - oldest >= TenoDXIO.AreaAPressRiseThreshold)
                                {
                                    currentStatus = 1;
                                    subState = TouchSubState.None;
                                    history.Clear();
                                    history.Add(current);
                                }
                            }
                        }
                        else
                        {
                            int gap = (int)(effThreshold - rawDefault);
                            if (gap <= 0) gap = 100;
                            if (current - oldest >= gap * TenoDXIO.reaADonwTrUP)
                            {
                                currentStatus = 1;
                                subState = TouchSubState.Raw01;
                                history.Clear();
                                history.Add(current);
                            }
                            else if (oldest - current >= gap * TenoDXIO.reaADonwTrDown)
                            {
                                currentStatus = 0;
                                subState = TouchSubState.None;
                                history.Clear();
                                history.Add(current);
                            }

                            int var1 = varHistory.Count >= 2 ? varHistory[varHistory.Count - 2] : variance;
                            int delta = variance - var1;

                            if (variance < 200)
                            {
                                currentStatus = 0;
                                if (lastTouchFrames < 0) lastTouchFrames = 0;
                            }

                            if (lastTouchFrames > 0)
                            {
                                lastTouchFrames--;
                            }
                            else
                            {
                                bool isFastSlide = (delta > cachedVar001Threshold || variance > cachedVarThreshold);
                                bool isRising = (delta > 0);
                                bool isNotPrePress = (raw < (effThreshold - 800));

                                if (isFastSlide && isRising && isNotPrePress)
                                {
                                    currentStatus = 1;
                                    lastTouchFrames = TenoDXIO.AreaAFastSlideFpsLimit >= 0 ? TenoDXIO.AreaAFastSlideFpsLimit : 10;
                                }
                            }
                        }
                    }

                    baseline = rawDefault;
                    string subStr = subState == TouchSubState.None ? "None" : (subState == TouchSubState.Raw10 ? "raw10" : "raw01");
                    TenoDXIO.WriteToFileLog(logicalName, raw, baseline, threshold, variance, subStr, currentStatus);
                    return currentStatus;
                }
                else
                {
                    int varThresh = 0;
                    switch (block)
                    {
                        case 'B': varThresh = TenoDXIO.VarThreshB; break;
                        case 'C': varThresh = TenoDXIO.VarThreshC; break;
                        case 'D': varThresh = TenoDXIO.VarThreshD; break;
                        case 'E': varThresh = TenoDXIO.VarThreshE; break;
                    }

                    if (baseline + varThresh > raw)
                    {
                        baseline = (baseline * 0.8f) + (raw * 0.2f);
                    }
                    variance = raw - (int)baseline;

                    if (IsTriggered(raw, variance, threshold) || (variance > TenoDXIO.VarianceThresholdBCDE && TenoDXIO.VarianceThresholdBCDE > 0))
                    {
                        currentStatus = 1;
                    }
                    else
                    {
                        currentStatus = 0;
                    }

                    if (variance < TenoDXIO.VarianceThresholdBCDEDown && TenoDXIO.VarianceThresholdBCDEDown > 0)
                    {
                        currentStatus = 0;
                    }

                    TenoDXIO.WriteToFileLog(logicalName, raw, baseline, threshold, variance, "None", currentStatus);
                    return currentStatus;
                }
            }
        }
    }
}