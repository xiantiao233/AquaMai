using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Threading;
using AquaMai.Core.Helpers;
using AquaMai.Config.Attributes;
using Manager;
using MelonLoader;
using UnityEngine; // 新增：用于显示时间戳UI
using HarmonyLib; // 用于使用 HarmonyPatch
using Main;       // GameMainObject 所在的命名空间

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

        [ConfigEntry("A区固定触发基础")] public static int FixedTriggerDefaultA = 50300;
        [ConfigEntry("B区固定触发基础")] public static int FixedTriggerDefaultB = 48000;
        [ConfigEntry("C区固定触发基础")] public static int FixedTriggerDefaultC = 47000;
        [ConfigEntry("D区固定触发基础")] public static int FixedTriggerDefaultD = 50000;
        [ConfigEntry("E区固定触发基础")] public static int FixedTriggerDefaultE = 50000;

        [ConfigEntry("A区灵敏度 (通常为30)")] public static int ThresholdA = 30;
        [ConfigEntry("B区灵敏度 (通常为30)")] public static int ThresholdB = 25;
        [ConfigEntry("C区灵敏度 (通常为28)")] public static int ThresholdC = 5;
        [ConfigEntry("D区灵敏度 (通常为30)")] public static int ThresholdD = 25;
        [ConfigEntry("E区灵敏度 (通常为30)")] public static int ThresholdE = 13;

        [ConfigEntry("单独区块灵敏度覆盖", "格式如 A7:80,B2:40。用英文或中文逗号分隔")]
        public static string CustomThresholdOverrides = "";

        [ConfigEntry("BCDE区方差突变触发阈值", "默认500")]
        public static int VarianceThresholdBCDE = 600;
        [ConfigEntry("BCDE区方差突变触发阈值", "")]
        public static int VarianceThresholdBCDEDown = 300;

        [ConfigEntry("B区方varThresh阈值", "默认800。")]
        public static int VarThreshB = 530;
        [ConfigEntry("C区方varThresh阈值", "默认800。")]
        public static int VarThreshC = 400;
        [ConfigEntry("D区方varThresh阈值", "默认800。")]
        public static int VarThreshD = 450;
        [ConfigEntry("E区方varThresh阈值", "默认800。")]
        public static int VarThreshE = 150;

        // ===== A区方差突变补偿 =====
        [ConfigEntry("A区默认方差突变阈值", "默认2000。快速扫过A区时，若方差(Variance)大于此值也会提前触发")]
        public static int VarianceThresholdADefault = 3000;

        [ConfigEntry("A区单独方差阈值覆盖", "格式如 A1:1500,A8:2500。用英文或中文逗号分隔，未指定的按默认值")]
        public static string CustomVarianceOverridesA = "";

        // ===== 新增：A区Delta触发(var001)配置 =====
        [ConfigEntry("A区Delta触发默认阈值(var001)", "默认450。用于捕捉突然滑动")]
        public static int Var001Default = 600;

        [ConfigEntry("A区单独Delta阈值覆盖(var001)", "格式如 A1:500,A8:400。用英文或中文逗号分隔，未指定的按默认值")]
        public static string CustomVar001OverridesA = "A2:300,A3:400,A6:300,A7:400";

        // ===== A区滑动松开与按下的判断阈值 =====
        [ConfigEntry("A区松开判定下降阈值", "默认5500。在滑动中，如果Raw值在几帧内下降超过此值，判定为松开手指")]
        public static int AreaAReleaseDropThreshold = 1500;

        [ConfigEntry("A区按下判定上升阈值", "默认5000。在刚被判定为松开(raw10)的状态下，如果Raw值上升超过此值，再次判定为按下")]
        public static int AreaAPressRiseThreshold = 1000;

        [ConfigEntry("A区防断管子偏移值", "默认600。 当被按下时，如果Raw值在几帧内下降超过此值但仍高于rawtouched+此值松开阈值，认为是断管子误判，保持按下状态")]
        public static int AreaAPressBreakThreshold = 700;

        [ConfigEntry("A区瞬发限制", "默认10(130ms), 在瞬发之后10帧数据内不会被再次激活 设为-1禁用。 \n 用于扫圈时不小心用手指扫过或者扫过边缘")]
        public static int AreaAFastSlideFpsLimit = -1;

        [ConfigEntry("A区未触发时的固定基线变化检测，上升", "")]
        public static double reaADonwTrUP = 0.6;
        [ConfigEntry("A区未触发时的固定基线变化检测，下降", "")]
        public static double reaADonwTrDown = 0.1;

        [ConfigEntry("物理通道映射顺序", "从硬件通道0到33对应的逻辑按键名称，用逗号分隔")]
        public static string TouchSheetMapping = "A8,E8,D8,B7,A7,C2,E7,D7,B6,A6,E6,D6,B5,A5,E5,D5,B4,A4,E4,D4,B3,A3,C1,E3,D3,B2,A2,E2,D2,B1,A1,E1,D1,B8";

        [ConfigEntry("Logger", "输出数据(游戏内Log)")]
        public static int LoggerTouch = -1;

        // ===== 本次新增：自定义文件日志配置 =====
        [ConfigEntry("启用数据输出到文件", "设为 true 会把输入流写出至TenoDX_Logs文件夹中")]
        public static bool EnableFileLog = false;

        [ConfigEntry("文件日志输出区域", "可填A,B,C或特定传感器A1,B2。用逗号分隔")]
        public static string FileLogTargets = "A,B";
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
        private static Dictionary<string, int> SENSOR_VAR001_THRESHOLDS = new Dictionary<string, int>(); // 新增字典
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
        private static int _var001Default = -1;       // 新增
        private static string _var001Overrides = ""; // 新增

        // 文件输出相关追踪与锁
        private static string _fileLogTargetsStr = "";
        private static HashSet<string> fileLogTargetsSet = new HashSet<string>();
        private static string logDirectory;
        private static int logFilePart = 1;
        private static StreamWriter logWriter;
        private static long currentLogSize = 0;
        private static readonly long MAX_LOG_SIZE = 512 * 1024; // 2 MB
        private static readonly object fileLock = new object();

        // ===== 本次新增：时间显示挂载器 =====
        public class TenoTimeDisplay : MonoBehaviour
        {
            private GUIStyle style;

            void OnGUI()
            {
                // 延迟初始化，防止 Start() 未执行导致 null 报错
                if (style == null)
                {
                    style = new GUIStyle();
                    style.fontSize = 250;
                    style.normal.textColor = Color.black;
                    style.alignment = TextAnchor.UpperCenter;
                    style.fontStyle = FontStyle.Bold;
                }

                // 设置极小的深度值，强制渲染在最顶层，防止被游戏其他UI遮挡
                GUI.depth = -1000;

                GUI.Label(new Rect(0, 10, Screen.width, 50), DateTime.Now.ToString("HH:mm:ss.fff"), style);
            }
        }
        [HarmonyPatch(typeof(GameMainObject), "Awake")]
        [HarmonyPostfix]
        public static void MountTimeUI(GameMainObject __instance)
        {
            // 防止重复挂载
            if (__instance.gameObject.GetComponent<TenoTimeDisplay>() == null)
            {
                __instance.gameObject.AddComponent<TenoTimeDisplay>();
                MelonLogger.Msg("[TenoDXIO] 时间UI组件挂载成功！");
            }
        }


        public static void OnBeforeEnableCheck()
        {
            MelonLogger.Msg("[TenoDXIO] 正在注册 1P 触摸触发器 (支持实时热加载)...");

            // 初始化新启动的文件日志目录
            InitFileLogger();

            foreach (var name in SENSOR_ORDER)
            {
                allTrackers[name] = new CapsenseState(name);
            }

            TouchStatusProvider.RegisterTouchStatusProvider(0, ProvideTouchStatus);

            isRunning = true;
            serialThread = new Thread(SerialReaderThread) { IsBackground = true };
            serialThread.Start();
        }

        // ===== 本次新增：文件日志引擎 =====
        private static void InitFileLogger()
        {
            if (!EnableFileLog) return;
            try
            {
                logDirectory = Path.Combine(Environment.CurrentDirectory, "TenoDX_Logs", "Log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                Directory.CreateDirectory(logDirectory);
                OpenNewLogFile();
                MelonLogger.Msg($"[TenoDXIO] 数据文件日志已启动，保存至: {logDirectory}");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[TenoDXIO] 初始化日志引擎失败: " + e.Message);
            }
        }

        private static void OpenNewLogFile()
        {
            lock (fileLock)
            {
                if (logWriter != null)
                {
                    logWriter.Flush();
                    logWriter.Close();
                }
                string path = Path.Combine(logDirectory, $"data_part{logFilePart}.txt");
                logWriter = new StreamWriter(path, true, System.Text.Encoding.UTF8);
                logWriter.AutoFlush = true; // 确保程序突然关掉也不会丢数据
                currentLogSize = 0;
                logFilePart++;
            }
        }

        public static void WriteToFileLog(string name, int raw, float baseline, int threshold, int variance, string subState, int status)
        {
            if (!EnableFileLog || logWriter == null) return;

            // 过滤：只有在 LogTargets 中包含了当前区域块名（如'A'）或者完整块名（如'A1'）才写入
            bool shouldLog = false;
            lock (configLock)
            {
                if (fileLogTargetsSet.Contains(name) || fileLogTargetsSet.Contains(name[0].ToString()))
                {
                    shouldLog = true;
                }
            }

            if (!shouldLog) return;

            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{name}] Raw:{raw} Base:{baseline:F1} Thresh:{threshold} Var:{variance} Sub:{subState} Stat:{status}";

            lock (fileLock)
            {
                try
                {
                    logWriter.WriteLine(line);
                    // 粗略估算写入字节：一行字符串长度 + 回车换行符长度
                    currentLogSize += line.Length + 2;

                    if (currentLogSize >= MAX_LOG_SIZE)
                    {
                        OpenNewLogFile();
                    }
                }
                catch { }
            }
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
                _varAOverrides != CustomVarianceOverridesA ||
                _var001Default != Var001Default ||                  // 新增校验
                _var001Overrides != CustomVar001OverridesA ||       // 新增校验
                _fileLogTargetsStr != FileLogTargets
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
                _var001Default = Var001Default;                     // 新增赋值
                _var001Overrides = CustomVar001OverridesA;          // 新增赋值
                _fileLogTargetsStr = FileLogTargets;

                lock (configLock)
                {
                    LOGICAL_TO_CHANNEL.Clear();
                    SENSOR_VARIANCE_THRESHOLDS.Clear();
                    SENSOR_VAR001_THRESHOLDS.Clear();               // 新增清理
                    fileLogTargetsSet.Clear();
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
                                SENSOR_VAR001_THRESHOLDS[name] = _var001Default; // 赋初值
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

                    // ===== 解析A区 var001 覆盖 =====
                    if (!string.IsNullOrWhiteSpace(_var001Overrides))
                    {
                        var pairs = _var001Overrides.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var pair in pairs)
                        {
                            var parts = pair.Split(new[] { ':', '：' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out int val))
                            {
                                string key = parts[0].Trim().ToUpper();
                                if (SENSOR_VAR001_THRESHOLDS.ContainsKey(key)) SENSOR_VAR001_THRESHOLDS[key] = val;
                            }
                        }
                    }

                    // 解析文件日志输出区域目标
                    if (!string.IsNullOrWhiteSpace(_fileLogTargetsStr))
                    {
                        var targets = _fileLogTargetsStr.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var tgt in targets)
                        {
                            fileLogTargetsSet.Add(tgt.Trim().ToUpper());
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

                if (chIdx == LoggerTouch)
                {
                    MelonLogger.Msg($"Raw: {raw}, Baseline: {baseline:F1}, Threshold: {threshold}, Variance: {variance}");
                }

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
                                if (history2.Count >= 8 && rawTouchedLock == 0)
                                {
                                    // 最简单的修复：只要当前值明显高于基线，就抓取当前值作为触摸基准
                                    if (current > effThreshold)
                                    {
                                        rawTouched = current;
                                        rawTouchedLock = 1;
                                    }
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
                                // ===== 获取当前A区的var001动态配置 =====
                                int var001 = SENSOR_VAR001_THRESHOLDS.ContainsKey(logicalName)
                                                ? SENSOR_VAR001_THRESHOLDS[logicalName]
                                                : Var001Default;

                                // 触发条件核心逻辑
                                bool isFastSlide = (delta > var001 || variance > currentVarThreshold);
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

                    baseline = rawDefault;

                    // 写出本帧日志到文件
                    WriteToFileLog(logicalName, raw, baseline, threshold, variance, subState ?? "None", currentStatus);

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

                    if (variance < VarianceThresholdBCDEDown)
                    {
                        currentStatus = 0;
                    }

                    // 写出本帧日志到文件
                    WriteToFileLog(logicalName, raw, baseline, threshold, variance, "None", currentStatus);

                    return currentStatus;
                }
            }
        }
    }
}