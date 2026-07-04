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
        // ================= 串口配置 =================
        [ConfigEntry("串口号", "主控板的COM口，例如 COM92 (修改后需重启生效)")]
        public static string COMPort = "COM92";

        [ConfigEntry("IIR滤波器系数", "可选值: 1(关闭滤波), 2(即1/2), 4(即1/4), 8(即1/8), 16(即1/16)")]
        public static int IIRFilterFactor = 1;

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

        // 通用字符串配置解析器
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

            // 缓存当前时间字符串，避免在 OnGUI 中高频引发 GC
            private string currentTimeText = "";

            void Update()
            {
                // Update 严格跟随引擎每帧执行一次
                // 在这里处理字符串分配，彻底消除时间刷新带来的 GC 卡顿
                currentTimeText = DateTime.Now.ToString("HH:mm:ss.fff");
            }

            void OnGUI()
            {
                // 核心性能优化：屏蔽 Layout 及各类输入事件
                // 只有在真正的屏幕重绘 (Repaint) 阶段才执行渲染逻辑
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
                    // 升级为八向描边，彻底压住亮橙色背景的高光干扰
                    // 左右上下
                    GUI.Label(new Rect(rect.x - w, rect.y, rect.width, rect.height), currentTimeText, outlineStyle);
                    GUI.Label(new Rect(rect.x + w, rect.y, rect.width, rect.height), currentTimeText, outlineStyle);
                    GUI.Label(new Rect(rect.x, rect.y - w, rect.width, rect.height), currentTimeText, outlineStyle);
                    GUI.Label(new Rect(rect.x, rect.y + w, rect.width, rect.height), currentTimeText, outlineStyle);
                    // 四个对角线
                    GUI.Label(new Rect(rect.x - w, rect.y - w, rect.width, rect.height), currentTimeText, outlineStyle);
                    GUI.Label(new Rect(rect.x + w, rect.y + w, rect.width, rect.height), currentTimeText, outlineStyle);
                    GUI.Label(new Rect(rect.x - w, rect.y + w, rect.width, rect.height), currentTimeText, outlineStyle);
                    GUI.Label(new Rect(rect.x + w, rect.y - w, rect.width, rect.height), currentTimeText, outlineStyle);
                }

                // 绘制主文字
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