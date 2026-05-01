using System;
using System.Collections.Generic;
using System.IO;
using AquaMai.Core.Helpers;
using AquaMai.Config.Attributes;
using Manager;
using MelonLoader;
using UnityEngine;
using HarmonyLib;
using Main;

namespace AquaMai.Mods.GameSystem
{
    [ConfigSection(
      name: "TenoDXIO Touch Trigger",
      en: "TenoDXIO Touch Trigger",
      zh: "TenoDXIO Touch Trigger")]
    public class TenoDXIO
    {
        // ================= 配置文件管理 =================
        [ConfigEntry("串口号", "主控板的COM口，例如 COM92 (修改后需重启生效)")]
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

        [ConfigEntry("单独区块灵敏度覆盖", "格式如 A7:80,B2:40。用英文或中文逗号分隔 (修改后需重启生效)")]
        public static string CustomThresholdOverrides = "";

        [ConfigEntry("BCDE区方差突变触发阈值", "默认500")]
        public static int VarianceThresholdBCDE = 600;
        [ConfigEntry("BCDE区方差突变触发阈值", "")]
        public static int VarianceThresholdBCDEDown = 300;

        [ConfigEntry("B区方varThresh阈值", "默认530")]
        public static int VarThreshB = 530;
        [ConfigEntry("C区方varThresh阈值", "默认400")]
        public static int VarThreshC = 400;
        [ConfigEntry("D区方varThresh阈值", "默认450")]
        public static int VarThreshD = 450;
        [ConfigEntry("E区方varThresh阈值", "默认150")]
        public static int VarThreshE = 150;

        [ConfigEntry("A区默认方差突变阈值", "默认2000。快速扫过A区时，若方差(Variance)大于此值也会提前触发")]
        public static int VarianceThresholdADefault = 3000;

        [ConfigEntry("A区单独方差阈值覆盖", "格式如 A1:1500,A8:2500。用英文或中文逗号分隔，未指定的按默认值 (修改后需重启生效)")]
        public static string CustomVarianceOverridesA = "";

        [ConfigEntry("A区Delta触发默认阈值(var001)", "默认450。用于捕捉突然滑动")]
        public static int Var001Default = 600;

        [ConfigEntry("A区单独Delta阈值覆盖(var001)", "格式如 A1:500,A8:400。用英文或中文逗号分隔，未指定的按默认值 (修改后需重启生效)")]
        public static string CustomVar001OverridesA = "A2:300,A3:400,A6:300,A7:400";

        [ConfigEntry("A区松开判定下降阈值", "默认5500。在滑动中，如果Raw值在几帧内下降超过此值，判定为松开手指")]
        public static int AreaAReleaseDropThreshold = 1500;

        [ConfigEntry("A区按下判定上升阈值", "默认5000。在刚被判定为松开(raw10)的状态下，如果Raw值上升超过此值，再次判定为按下")]
        public static int AreaAPressRiseThreshold = 1000;

        [ConfigEntry("A区防断管子偏移值", "默认600。 当被按下时，如果Raw值在几帧内下降超过此值但仍高于rawtouched+此值松开阈值，认为是断管子误判，保持按下状态")]
        public static int AreaAPressBreakThreshold = 700;

        [ConfigEntry("A区瞬发限制", "默认10(130ms), 在瞬发之后10帧数据内不会被再次激活 设为-1禁用。 \n 用于扫圈时不小心用手指扫过或者扫过边缘")]
        public static int AreaAFastSlideFpsLimit = -1;

        [ConfigEntry("A区未触发时的固定基线变化检测，上升", "")]
        public static double reaADonwTrUP = 0.8;
        [ConfigEntry("A区未触发时的固定基线变化检测，下降", "")]
        public static double reaADonwTrDown = 0.25;

        [ConfigEntry("物理通道映射顺序", "从硬件通道0到33对应的逻辑按键名称，用逗号分隔 (修改后需重启生效)")]
        public static string TouchSheetMapping = "A8,E8,D8,B7,A7,C2,E7,D7,B6,A6,E6,D6,B5,A5,E5,D5,B4,A4,E4,D4,B3,A3,C1,E3,D3,B2,A2,E2,D2,B1,A1,E1,D1,B8";

        [ConfigEntry("Logger", "输出数据(游戏内Log)")]
        public static int LoggerTouch = -1;

        [ConfigEntry("启用数据输出到文件", "设为 true 会把输入流写出至TenoDX_Logs文件夹中")]
        public static bool EnableFileLog = false;

        [ConfigEntry("文件日志输出区域", "可填A,B,C或特定传感器A1,B2。用逗号分隔 (修改后需重启生效)")]
        public static string FileLogTargets = "A";

        // ================= 模块状态与文件日志 =================
        private static readonly bool[] fileLogTargetBlocks = new bool[256];
        private static readonly HashSet<string> fileLogTargetNames = new HashSet<string>();

        private static string logDirectory;
        private static int logFilePart = 1;
        private static StreamWriter logWriter;
        private static long currentLogSize = 0;
        private static readonly long MAX_LOG_SIZE = 4096 * 1024; // 2 MB
        private static readonly object fileLock = new object();

        // ===== 时间显示挂载器 =====
        public class TenoTimeDisplay : MonoBehaviour
        {
            private GUIStyle style;
            void OnGUI()
            {
                if (style == null)
                {
                    style = new GUIStyle();
                    style.fontSize = 180;
                    style.normal.textColor = Color.black;
                    style.alignment = TextAnchor.UpperCenter;
                    style.fontStyle = FontStyle.Bold;
                }
                GUI.depth = -1000;
                GUI.Label(new Rect(0, 10, Screen.width, 50), DateTime.Now.ToString("HH:mm:ss.fff"), style);
            }
        }

        [HarmonyPatch(typeof(GameMainObject), "Awake")]
        [HarmonyPostfix]
        public static void MountTimeUI(GameMainObject __instance)
        {
            if (__instance.gameObject.GetComponent<TenoTimeDisplay>() == null && EnableFileLog)
            {
                __instance.gameObject.AddComponent<TenoTimeDisplay>();
                MelonLogger.Msg("[TenoDXIO] 时间UI组件挂载成功！");
            }
        }

        public static void OnBeforeEnableCheck()
        {
            MelonLogger.Msg("[TenoDXIO] 正在注册 1P 触摸触发器 (逻辑模块已解耦分离)...");

            ParseConfigs();
            InitFileLogger();

            TouchStateProcessor.Init();
            TouchStatusProvider.RegisterTouchStatusProvider(0, TouchStateProcessor.ProvideTouchStatus);

            SerialThreadManager.Start();
        }

        private static void ParseConfigs()
        {
            fileLogTargetNames.Clear();
            Array.Clear(fileLogTargetBlocks, 0, fileLogTargetBlocks.Length);

            if (!string.IsNullOrEmpty(FileLogTargets))
            {
                foreach (var t in FileLogTargets.Split([','], StringSplitOptions.RemoveEmptyEntries))
                {
                    string target = t.Trim();
                    if (target.Length == 1) fileLogTargetBlocks[target[0]] = true;
                    else fileLogTargetNames.Add(target);
                }
            }
        }

        public static Dictionary<string, int> ParseOverrideString(string input)
        {
            var dict = new Dictionary<string, int>();
            if (string.IsNullOrWhiteSpace(input)) return dict;

            input = input.Replace("，", ",");
            foreach (var pair in input.Split([','], StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out int val))
                {
                    dict[parts[0].Trim()] = val;
                }
            }
            return dict;
        }

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
                logWriter = new StreamWriter(path, true, System.Text.Encoding.UTF8)
                {
                    AutoFlush = true
                };
                currentLogSize = 0;
                logFilePart++;
            }
        }

        public static void WriteToFileLog(string name, int raw, float baseline, int threshold, int variance, string subStateStr, int status)
        {
            if (!EnableFileLog || logWriter == null) return;

            // 零GC过滤
            if (!fileLogTargetBlocks[name[0]] && !fileLogTargetNames.Contains(name)) return;

            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{name}] Raw:{raw} Base:{baseline:F1} Thresh:{threshold} Var:{variance} Sub:{subStateStr} Stat:{status}";

            lock (fileLock)
            {
                try
                {
                    logWriter.WriteLine(line);
                    currentLogSize += line.Length + 2;

                    if (currentLogSize >= MAX_LOG_SIZE)
                    {
                        OpenNewLogFile();
                    }
                }
                catch { }
            }
        }
    }
}