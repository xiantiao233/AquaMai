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
    // ==========================================
    // 全局统一硬件扫描参数表 (独立硬编码，不参与游戏内配置系统)
    // ==========================================
    public static class HardwareConfig
    {
        public class ScanParams
        {
            public int Res;
            public int Mod;
            public int Sns;
            public int Div;
            public char DetGroup;
        }

        // 预留的硬件参数配置
        public static readonly ScanParams ParamsA = new ScanParams { Res = 12, Mod = 15, Sns = 2, Div = 2, DetGroup = 'A' };
        public static readonly ScanParams ParamsB = new ScanParams { Res = 10, Mod = 25, Sns = 4, Div = 4, DetGroup = 'B' };
        public static readonly ScanParams ParamsC = new ScanParams { Res = 12, Mod = 30, Sns = 4, Div = 4, DetGroup = 'C' };
        public static readonly ScanParams ParamsD = new ScanParams { Res = 8, Mod = 10, Sns = 2, Div = 2, DetGroup = 'D' };
        public static readonly ScanParams ParamsE = new ScanParams { Res = 8, Mod = 8, Sns = 2, Div = 2, DetGroup = 'E' };

        // 物理通道映射表数组，去除了 readonly 以支持被配置文件覆盖。
        // 这里的默认值就是你给出的正确顺序，防止配置文件未生成或被清空时发生越界崩溃。
        public static string[] PhysicalToLogicalMap = new string[34] {
            "A5", "E5", "D5", "B4", "A4", "E4", "D4", "B3", "A3", "C1", "E3", "D3", "B2", "A2", "E2", "D2", "B1", // 0-16
            "A1", "E1", "D1", "B8", "A8", "E8", "D8", "B7", "A7", "C2", "E7", "D7", "B6", "A6", "E6", "D6", "B5"  // 17-33
        };

        public static ScanParams GetParams(char block)
        {
            switch (block)
            {
                case 'A': return ParamsA;
                case 'B': return ParamsB;
                case 'C': return ParamsC;
                case 'D': return ParamsD;
                case 'E': return ParamsE;
                default: return ParamsA;
            }
        }
    }

    // ==========================================
    // 动态配置系统 (游戏内可调的算法参数与映射)
    // ==========================================
    [ConfigSection(
      name: "TenoDXIO Touch Trigger",
      en: "TenoDXIO Touch Trigger",
      zh: "TenoDXIO Touch Trigger")]
    public class TenoDXIO
    {
        // ================= 串口配置 =================
        [ConfigEntry("串口号", "主控板的COM口，例如 COM92 (修改后需重启生效)")]
        public static string COMPort = "COM92";

        [ConfigEntry("IIR滤波器系数", "可选值: 1(关闭滤波), 2(即1/2), 4(即1/4), 8(即1/8), 16(即1/16)")]
        public static int IIRFilterFactor = 1;

        // ================= 硬件引脚映射配置 =================
        [ConfigEntry("硬件引脚通道映射", "按0-33的物理通道顺序，填入对应的游戏区块，用逗号分隔")]
        public static string HardwareMapping = "A5,E5,D5,B4,A4,E4,D4,B3,A3,C1,E3,D3,B2,A2,E2,D2,B1,A1,E1,D1,B8,A8,E8,D8,B7,A7,C2,E7,D7,B6,A6,E6,D6,B5";

        // ================= UI 与 日志配置 =================
        [ConfigEntry("启用数据输出到文件", "设为 true 会把输入流与判定写出至文件夹，并在屏幕上方悬挂时钟")]
        public static bool EnableFileLog = false;

        [ConfigEntry("输出日志的区域", "如 A,B,C。若留空则记录所有大区的日志数据")]
        public static string LogZones = "";

        [ConfigEntry("UI - 时钟字体大小", "默认 300")]
        public static int ClockFontSize = 300;

        [ConfigEntry("UI - 时钟字体颜色(Hex)", "格式 #RRGGBB，例如 #FFFFFF")]
        public static string ClockFontColor = "#FFFFFF";

        [ConfigEntry("UI - 时钟描边颜色(Hex)", "格式 #RRGGBB，例如 #0055FF")]
        public static string ClockOutlineColor = "#1A1A1A";

        [ConfigEntry("UI - 时钟描边宽度", "默认 3，设为 0 则关闭描边")]
        public static int ClockOutlineWidth = 3;

        // ================= A区 核心判定参数 =================
        [ConfigEntry("A区 - 基础触发灵敏度", "Trigger Sensitivity: 默认 650")]
        public static int TriggerSensitivity = 650;

        [ConfigEntry("A区 - 长按防断触能力", "Hold Threshold: 默认 450")]
        public static int HoldThreshold = 450;

        [ConfigEntry("A区 - 连击极速抬手线", "Quick Release Line: 默认 1200")]
        public static int QuickReleaseLine = 1200;

        [ConfigEntry("A区 - 悬空防误触拦截(Diff)", "Hover Diff Max: 默认 1000")]
        public static int HoverDiffMax = 1000;

        [ConfigEntry("A区 - 悬空防误触拦截(diff_deriv)", "Hover Speed Max: 默认 15")]
        public static int HoverSpeedMax = 15;

        [ConfigEntry("A区 - 极速抽离灵敏度", "Fast Lift Speed: 默认 -250")]
        public static int FastLiftSpeed = -250;

        // ================= C区 判定参数 =================
        [ConfigEntry("C区 - Diff 触发线", "默认 25")]
        public static int BlockC_DiffThreshold = 25;

        [ConfigEntry("C区 - diff_deriv 突变触发线", "默认 25")]
        public static int BlockC_DerivThreshold = 25;

        [ConfigEntry("C区 - diff_deriv 突变触发抑制线", "默认 -20")]
        public static int BlockC_DerivRelease = -20;

        [ConfigEntry("C区 - Diff 松开线", "默认 15")]
        public static int BlockC_DiffRelease = 15;

        // ================= B/D/E区 独立判定参数 =================
        [ConfigEntry("B区 - Diff 触发线", "默认 8")]
        public static int BlockB_DiffThreshold = 8;
        [ConfigEntry("B区 - diff_deriv 突变抑制线", "默认 -15")]
        public static int BlockB_DerivRelease = -15;

        [ConfigEntry("D区 - Diff 触发线", "默认 3")]
        public static int BlockD_DiffThreshold = 3;
        [ConfigEntry("D区 - diff_deriv 突变抑制线", "默认 -4")]
        public static int BlockD_DerivRelease = -4;

        [ConfigEntry("E区 - Diff 触发线", "默认 15")]
        public static int BlockE_DiffThreshold = 15;
        [ConfigEntry("E区 - diff_deriv 突变抑制线", "默认 -16")]
        public static int BlockE_DerivRelease = -16;

        // ================= 单独通道灵敏度覆盖 (支持跨区填写) =================
        [ConfigEntry("A区 - Diff 触发线覆盖", "格式如 A1:600, B2:10 (未填写的通道使用默认值)")]
        public static string Override_A_Diff = "";

        [ConfigEntry("C区 - Diff 触发线覆盖", "支持跨区填写，如 C1:20")]
        public static string Override_C_Diff = "";
        [ConfigEntry("C区 - diff_deriv 突变触发线覆盖", "支持跨区填写")]
        public static string Override_C_DerivTrigger = "";
        [ConfigEntry("C区 - diff_deriv 突变抑制线覆盖", "支持跨区填写")]
        public static string Override_C_DerivRelease = "";
        [ConfigEntry("C区 - Diff 松开线覆盖", "支持跨区填写")]
        public static string Override_C_DiffRelease = "";

        [ConfigEntry("BDE区 - Diff 触发线覆盖", "支持跨区填写，如 B1:10,D3:12,E4:16")]
        public static string Override_BDE_Diff = "";
        [ConfigEntry("BDE区 - diff_deriv 突变抑制线覆盖", "支持跨区填写")]
        public static string Override_BDE_DerivRelease = "";

        // 解析映射表配置，将其覆写到底层逻辑数组中
        public static void ApplyHardwareMapping()
        {
            if (string.IsNullOrWhiteSpace(HardwareMapping)) return;
            // 兼容中英文逗号
            string[] rawMaps = HardwareMapping.Replace("，", ",").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            // 安全注入，防止数组越界
            for (int i = 0; i < 34 && i < rawMaps.Length; i++)
            {
                HardwareConfig.PhysicalToLogicalMap[i] = rawMaps[i].Trim().ToUpper();
            }
        }

        public static Dictionary<string, int> ParseConfigString(string configStr)
        {
            var dict = new Dictionary<string, int>();
            if (string.IsNullOrWhiteSpace(configStr)) return dict;

            string input = configStr.Replace("，", ",");
            foreach (var pair in input.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out int val))
                {
                    dict[parts[0].Trim().ToUpper()] = val;
                }
            }
            return dict;
        }

        private static string logDirectory;
        private static int logFilePart = 1;
        private static StreamWriter logWriter;
        private static long currentLogSize = 0;
        private static readonly long MAX_LOG_SIZE = 4096 * 1024; // 2 MB
        private static readonly object fileLock = new object();

        // ================= 生命周期注入核心 =================
        public static void OnBeforeEnableCheck()
        {
            MelonLogger.Msg("[TenoDXIO] 正在注册 1P 触摸触发器 (逻辑模块已解耦分离)...");
            TouchStateProcessor.Init();
            TouchStatusProvider.RegisterTouchStatusProvider(0, TouchStateProcessor.ProvideTouchStatus);
            SerialThreadManager.Start();
        }

        public class TenoTimeDisplay : MonoBehaviour
        {
            private GUIStyle style;
            private GUIStyle outlineStyle;
            private string lastFontColor = "";
            private string lastOutlineColor = "";
            private int lastFontSize = -1;

            private string currentTimeText = "";

            void Update()
            {
                currentTimeText = DateTime.Now.ToString("HH:mm:ss.fff");
            }

            void OnGUI()
            {
                if (Event.current.type != EventType.Repaint) return;

                if (style == null || lastFontSize != ClockFontSize || lastFontColor != ClockFontColor || lastOutlineColor != ClockOutlineColor)
                {
                    style = new GUIStyle();
                    style.fontSize = ClockFontSize;
                    style.alignment = TextAnchor.UpperCenter;
                    style.fontStyle = FontStyle.Bold;

                    outlineStyle = new GUIStyle();
                    outlineStyle.fontSize = ClockFontSize;
                    outlineStyle.alignment = TextAnchor.UpperCenter;
                    outlineStyle.fontStyle = FontStyle.Bold;

                    Color mainColor = Color.white;
                    Color outColor = Color.black;

                    if (!ColorUtility.TryParseHtmlString(ClockFontColor, out mainColor))
                        MelonLogger.Warning($"[TenoDXIO] 无法解析字体颜色: {ClockFontColor}");
                    if (!ColorUtility.TryParseHtmlString(ClockOutlineColor, out outColor))
                        MelonLogger.Warning($"[TenoDXIO] 无法解析描边颜色: {ClockOutlineColor}");

                    style.normal.textColor = mainColor;
                    outlineStyle.normal.textColor = outColor;

                    lastFontSize = ClockFontSize;
                    lastFontColor = ClockFontColor;
                    lastOutlineColor = ClockOutlineColor;
                }

                GUI.depth = -1000;
                Rect rect = new Rect(0, 10, Screen.width, 50);
                int w = ClockOutlineWidth;

                if (w > 0)
                {
                    GUI.Label(new Rect(rect.x - w, rect.y, rect.width, rect.height), currentTimeText, outlineStyle);
                    GUI.Label(new Rect(rect.x + w, rect.y, rect.width, rect.height), currentTimeText, outlineStyle);
                    GUI.Label(new Rect(rect.x, rect.y - w, rect.width, rect.height), currentTimeText, outlineStyle);
                    GUI.Label(new Rect(rect.x, rect.y + w, rect.width, rect.height), currentTimeText, outlineStyle);
                    GUI.Label(new Rect(rect.x - w, rect.y - w, rect.width, rect.height), currentTimeText, outlineStyle);
                    GUI.Label(new Rect(rect.x + w, rect.y + w, rect.width, rect.height), currentTimeText, outlineStyle);
                    GUI.Label(new Rect(rect.x - w, rect.y + w, rect.width, rect.height), currentTimeText, outlineStyle);
                    GUI.Label(new Rect(rect.x + w, rect.y - w, rect.width, rect.height), currentTimeText, outlineStyle);
                }

                GUI.Label(rect, currentTimeText, style);
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

        public static void InitFileLogger()
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
                logWriter = new StreamWriter(path, true, System.Text.Encoding.UTF8) { AutoFlush = true };
                currentLogSize = 0;
                logFilePart++;
            }
        }

        public static void WriteLog(int physicalChannel, char block, string logicalName, int raw, int setupRaw, int diff, int diff_deriv, bool isPressed)
        {
            if (!EnableFileLog || logWriter == null) return;

            if (!string.IsNullOrWhiteSpace(LogZones) && !LogZones.Contains(block.ToString())) return;

            int status = isPressed ? 1 : 0;
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [Ch:{physicalChannel:D2}] [Block:{block}] [{logicalName}] Raw:{raw} Base:{setupRaw} Diff:{diff} Deriv:{diff_deriv} Stat:{status}";

            lock (fileLock)
            {
                try
                {
                    logWriter.WriteLine(line);
                    currentLogSize += line.Length + 2;
                    if (currentLogSize >= MAX_LOG_SIZE) OpenNewLogFile();
                }
                catch { }
            }
        }
    }
}