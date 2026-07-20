using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AquaMai.Core.Helpers;
using AquaMai.Config.Attributes;
using Manager;
using MelonLoader;
using Monitor;
using Monitor.Game;
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

        // ================= 动态解析与懒加载配置 =================
        private static ScanParams _paramsA;
        public static ScanParams ParamsA => _paramsA ?? (_paramsA = ParseScanParams(TenoDXIO.ScanConfig_A, 12, 15, 2, 2, 'A'));

        private static ScanParams _paramsB;
        public static ScanParams ParamsB => _paramsB ?? (_paramsB = ParseScanParams(TenoDXIO.ScanConfig_B, 10, 25, 4, 4, 'B'));

        private static ScanParams _paramsC;
        public static ScanParams ParamsC => _paramsC ?? (_paramsC = ParseScanParams(TenoDXIO.ScanConfig_C, 12, 30, 4, 4, 'C'));

        private static ScanParams _paramsD;
        public static ScanParams ParamsD => _paramsD ?? (_paramsD = ParseScanParams(TenoDXIO.ScanConfig_D, 8, 10, 2, 2, 'D'));

        private static ScanParams _paramsE;
        public static ScanParams ParamsE => _paramsE ?? (_paramsE = ParseScanParams(TenoDXIO.ScanConfig_E, 8, 8, 2, 2, 'E'));

        // 安全解析方法：解析失败时自动回退至默认值
        private static ScanParams ParseScanParams(string configStr, int defRes, int defMod, int defSns, int defDiv, char defGroup)
        {
            var p = new ScanParams { Res = defRes, Mod = defMod, Sns = defSns, Div = defDiv, DetGroup = defGroup };
            if (string.IsNullOrWhiteSpace(configStr)) return p;

            try
            {
                string[] parts = configStr.Replace("，", ",").Split([','], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5)
                {
                    p.Res = int.Parse(parts[0].Trim());
                    p.Mod = int.Parse(parts[1].Trim());
                    p.Sns = int.Parse(parts[2].Trim());
                    p.Div = int.Parse(parts[3].Trim());
                    p.DetGroup = parts[4].Trim().ToUpper()[0];
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[TenoDXIO] 硬件扫描参数解析失败: {configStr}，将使用默认值。错误: {e.Message}");
            }
            return p;
        }

        public static string[] PhysicalToLogicalMap = new string[34] {
            "A5", "E5", "D5", "B4", "A4", "E4", "D4", "B3", "A3", "C1", "E3", "D3", "B2", "A2", "E2", "D2", "B1",
            "A1", "E1", "D1", "B8", "A8", "E8", "D8", "B7", "A7", "C2", "E7", "D7", "B6", "A6", "E6", "D6", "B5"
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

        // ================= 硬件扫描参数配置 =================
        [ConfigEntry("硬件扫描参数 - A区", "格式: Res,Mod,Sns,Div,DetGroup (默认: 12,15,2,2,A)")]
        public static string ScanConfig_A = "12,15,2,2,A";
        [ConfigEntry("硬件扫描参数 - B区", "格式: Res,Mod,Sns,Div,DetGroup (默认: 10,25,4,4,B)")]
        public static string ScanConfig_B = "10,25,4,4,B";
        [ConfigEntry("硬件扫描参数 - C区", "格式: Res,Mod,Sns,Div,DetGroup (默认: 12,30,4,4,C)")]
        public static string ScanConfig_C = "12,30,4,4,C";
        [ConfigEntry("硬件扫描参数 - D区", "格式: Res,Mod,Sns,Div,DetGroup (默认: 8,10,2,2,D)")]
        public static string ScanConfig_D = "8,10,2,2,D";
        [ConfigEntry("硬件扫描参数 - E区", "格式: Res,Mod,Sns,Div,DetGroup (默认: 8,8,2,2,E)")]
        public static string ScanConfig_E = "8,8,2,2,E";

        // ================= UI 与 日志配置 =================
        [ConfigEntry("启用数据输出到文件", "设为 true 会把输入流与判定写出至文件夹，并在屏幕上方悬挂时钟")]
        public static bool EnableFileLog = false;

        [ConfigEntry("输出日志的区域", "如 A,B,C。若留空则记录所有大区的日志数据")]
        public static string LogZones = "";

        [ConfigEntry("启用判定日志", "设为 true 会在日志中记录判定事件（音符类型、判定结果、时间差）")]
        public static bool EnableJudgeLog = false;

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
        [ConfigEntry("A区 - 边缘突变触发速度(Deriv)", "边缘划过/轻触的突变速度阈值。默认 120")]
        public static int EdgeTriggerDeriv = 150;
        [ConfigEntry("A区 - 边缘突变触发最低形变(Diff)", "满足突变时所需的最低Diff防噪线。默认 200")]
        public static int EdgeTriggerMinDiff = 300;

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

        // ================= 单独通道灵敏度覆盖 =================
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

        // ================ 静态反射缓存 ================
        private static PropertyInfo _buttonIdProp;
        private static int GetButtonId(NoteBase note)
        {
            if (_buttonIdProp == null)
                _buttonIdProp = typeof(NoteBase).GetProperty("ButtonId", BindingFlags.Instance | BindingFlags.NonPublic);
            return (int)_buttonIdProp.GetValue(note);
        }

        // ================ Slide 判定对象映射 ================
        // 用于在 SlideJudge.Initialize 回调中追溯所属 SlideRoot，从而获取轨道号等信息
        private static readonly ConcurrentDictionary<SlideJudge, SlideRoot> SlideJudgeMap = new ConcurrentDictionary<SlideJudge, SlideRoot>();

        // ================ 笔记类型名称映射 ================
        private static string GetNoteTypeName(NotesTypeID.Def type)
        {
            switch (type)
            {
                case NotesTypeID.Def.Tap:          return "TAP";
                case NotesTypeID.Def.Break:        return "BREAK";
                case NotesTypeID.Def.ExTap:        return "EX_TAP";
                case NotesTypeID.Def.Hold:         return "HOLD";
                case NotesTypeID.Def.ExHold:       return "EX_HOLD";
                case NotesTypeID.Def.Star:         return "STAR";
                case NotesTypeID.Def.BreakStar:    return "BREAK_STAR";
                case NotesTypeID.Def.ExStar:       return "EX_STAR";
                case NotesTypeID.Def.TouchTap:     return "TOUCH_TAP";
                case NotesTypeID.Def.TouchHold:    return "TOUCH_HOLD";
                case NotesTypeID.Def.ExBreakTap:   return "EX_BREAK_TAP";
                case NotesTypeID.Def.BreakHold:    return "BREAK_HOLD";
                case NotesTypeID.Def.ExBreakHold:  return "EX_BREAK_HOLD";
                case NotesTypeID.Def.Slide:        return "SLIDE";
                case NotesTypeID.Def.BreakSlide:   return "BREAK_SLIDE";
                case NotesTypeID.Def.ExSlide:      return "EX_SLIDE";
                case NotesTypeID.Def.ExBreakSlide: return "EX_BREAK_SLIDE";
                case NotesTypeID.Def.ExBreakStar:  return "EX_BREAK_STAR";
                case NotesTypeID.Def.ConnectSlide: return "CONNECT_SLIDE";
                default: return type.ToString();
            }
        }

        // ================ 判定时间枚举名称映射 ================
        private static string GetTimingName(NoteJudge.ETiming timing)
        {
            // 简化为 5 类判定：Critical / FastPerfect / LatePerfect / FastGreat / LateGreat /
            // FastGood / LateGood / FastMiss / LateMiss
            switch (timing)
            {
                case NoteJudge.ETiming.Critical:       return "CRITICAL";
                case NoteJudge.ETiming.FastPerfect:
                case NoteJudge.ETiming.FastPerfect2nd: return "FAST_PERFECT";
                case NoteJudge.ETiming.LatePerfect:
                case NoteJudge.ETiming.LatePerfect2nd: return "LATE_PERFECT";
                case NoteJudge.ETiming.FastGreat:
                case NoteJudge.ETiming.FastGreat2nd:
                case NoteJudge.ETiming.FastGreat3rd:   return "FAST_GREAT";
                case NoteJudge.ETiming.LateGreat:
                case NoteJudge.ETiming.LateGreat2nd:
                case NoteJudge.ETiming.LateGreat3rd:   return "LATE_GREAT";
                case NoteJudge.ETiming.FastGood:       return "FAST_GOOD";
                case NoteJudge.ETiming.LateGood:       return "LATE_GOOD";
                case NoteJudge.ETiming.TooFast:        return "FAST_MISS";
                case NoteJudge.ETiming.TooLate:        return "LATE_MISS";
                default: return timing.ToString();
            }
        }

        // ================ 日志系统（统一文件） ================
        private static string logDirectory;
        private static int logFilePart = 1;
        private static StreamWriter logWriter;
        private static long currentLogSize = 0;
        private static readonly long MAX_LOG_SIZE = 4096 * 1024; // 4 MB
        private static readonly object fileLock = new object();

        // 解析映射表配置
        public static void ApplyHardwareMapping()
        {
            if (string.IsNullOrWhiteSpace(HardwareMapping)) return;
            string[] rawMaps = HardwareMapping.Replace("，", ",").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
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

        // -------- 统一日志写入 ----------
        // 每条日志均有 [墙钟时间HH:mm:ss.fff] [游戏时间ms] [条目类型] 前缀
        // 墙钟时间由 DateTime.Now 实时获取，游戏时间因条目类型而异：
        //   HW: 读取 TouchStateProcessor.CurrentGameTimeMs（主线程每帧同步到串口线程）
        //   FRAME/JUDGE: 直接调用 NotesManager.GetCurrentMsec()（主线程安全）

        /// <summary> 写入硬件层日志（串口线程调用）</summary>
        public static void WriteLog(int physicalChannel, char block, string logicalName,
            int raw, int setupRaw, int diff, int diff_deriv, bool isPressed)
        {
            if (!EnableFileLog || logWriter == null) return;

            if (!string.IsNullOrWhiteSpace(LogZones) && !LogZones.Contains(block.ToString())) return;

            float gameTime = TouchStateProcessor.CurrentGameTimeMs;
            int status = isPressed ? 1 : 0;
            string wallTime = DateTime.Now.ToString("HH:mm:ss.fff");

            // [墙钟时间] [游戏时间ms] [HW] [Ch:物理通道] [Block:区块] [逻辑名] Raw:原始值 Base:基线 Diff:差值 Deriv:变化率 Stat:状态
            string line = $"[{wallTime}] [{gameTime:F3}] [HW] [Ch:{physicalChannel:D2}] [Block:{block}] [{logicalName}] " +
                          $"Raw:{raw} Base:{setupRaw} Diff:{diff} Deriv:{diff_deriv} Stat:{status}";

            WriteLineToFile(line);
        }

        /// <summary> 写入帧同步标记（主线程 GameMainObject.Update Postfix 调用）</summary>
        private static void WriteFrameMarker()
        {
            if (!EnableFileLog || logWriter == null) return;

            float gameTime = NotesManager.GetCurrentMsec();
            int frameNum = TouchStateProcessor.CurrentFrameNumber;
            float unityTime = Time.realtimeSinceStartup;
            string wallTime = DateTime.Now.ToString("HH:mm:ss.fff");

            string line = $"[{wallTime}] [{gameTime:F3}] [FRAME] Frame:{frameNum} UnityTime:{unityTime:F3}";
            WriteLineToFile(line);
        }

        /// <summary> 写入判定日志（主线程调用）</summary>
        private static void WriteJudgeEntry(in TouchStateProcessor.JudgeLogEntry entry)
        {
            if (!EnableFileLog || !EnableJudgeLog || logWriter == null) return;

            string wallTime = DateTime.Now.ToString("HH:mm:ss.fff");

            // [墙钟时间] [游戏时间ms] [JUDGE] [Frame:帧号] [Ch:物理通道] [逻辑名]
            // Note:音符类型 Timing:判定枚举 Msec:时间差ms | CurrRaw:原始值 Base:基线 Diff:差值 Stat:状态
            string line = $"[{wallTime}] [{entry.GameTimeMs:F3}] [JUDGE] [Frame:{entry.FrameNumber}] " +
                          $"[Ch:{entry.PhysicalChannel:D2}] [{entry.LogicalName}] " +
                          $"Note:{entry.NoteTypeStr} Timing:{GetTimingName(entry.Timing)} " +
                          $"Msec:{entry.DiffMsec:F2}ms | " +
                          $"CurrRaw:{entry.CurrentRaw} Base:{entry.SetupRaw} " +
                          $"Diff:{entry.CurrentRaw - entry.SetupRaw} Stat:{(entry.TouchState ? 1 : 0)}";

            WriteLineToFile(line);
        }

        /// <summary> 刷新判定缓冲（帧末尾将队列中所有待写入的判定事件刷入文件）</summary>
        private static void FlushJudgeBuffer()
        {
            // 无论日志是否启用，都要清空缓冲防止内存泄漏
            bool loggingEnabled = EnableJudgeLog && EnableFileLog;
            while (TouchStateProcessor.JudgeLogBuffer.TryDequeue(out var entry))
            {
                if (loggingEnabled)
                {
                    WriteJudgeEntry(entry);
                }
            }
        }

        /// <summary> 线程安全地写入文件</summary>
        private static void WriteLineToFile(string line)
        {
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

        // ================= 生命周期注入核心 =================
        public static void OnBeforeEnableCheck()
        {
            MelonLogger.Msg("[TenoDXIO] 正在注册 1P 触摸触发器 (逻辑模块已解耦分离)...");
            TouchStateProcessor.Init();
            TouchStatusProvider.RegisterTouchStatusProvider(0, TouchStateProcessor.ProvideTouchStatus);
            SerialThreadManager.Start();
        }

        // ================= Unity UI 组件 =================
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

        // ==========================================================
        // ================= 统一日志系统初始化 =================
        // ==========================================================

        public static void InitFileLogger()
        {
            if (!EnableFileLog) return;
            try
            {
                logDirectory = Path.Combine(Environment.CurrentDirectory, "TenoDX_Logs", "Log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                Directory.CreateDirectory(logDirectory);
                OpenNewLogFile();
                MelonLogger.Msg($"[TenoDXIO] 统一日志已启动，保存至: {logDirectory}");
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
                string path = Path.Combine(logDirectory, $"touch_log_part{logFilePart}.txt");
                logWriter = new StreamWriter(path, true, System.Text.Encoding.UTF8) { AutoFlush = true };
                currentLogSize = 0;
                logFilePart++;
            }
        }

        // ==========================================================
        // ================= 帧同步 Hook =================
        // 在每个 Unity 帧末尾写入 FRAME 标记，同步游戏时间轴
        // ==========================================================

        [HarmonyPatch(typeof(GameMainObject), "Update")]
        [HarmonyPostfix]
        private static void OnGameUpdate()
        {
            // 更新共享时间基准（供串口线程读取用于 HW 日志时间戳）
            TouchStateProcessor.CurrentGameTimeMs = NotesManager.GetCurrentMsec();
            TouchStateProcessor.CurrentFrameNumber++;

            // 刷新判定缓冲：将本帧积累的判定事件写入日志文件
            FlushJudgeBuffer();

            // 写入帧同步标记
            WriteFrameMarker();
        }

        // ==========================================================
        // ================= 判定日志 Hook - 标准音符 =================
        // 捕获 TapNote、BreakNote、StarNote 等使用基类 NoteBase.Judge() 的音符
        // ==========================================================

        [HarmonyPatch(typeof(NoteBase), "Judge")]
        [HarmonyPostfix]
        private static void OnNoteBaseJudge(NoteBase __instance, bool __result,
            NoteJudge.ETiming ___JudgeResult, float ___JudgeTimingDiffMsec,
            int ___NoteIndex, int ___MonitorIndex)
        {
            if (!EnableFileLog || !EnableJudgeLog) return;
            if (!__result) return;

            int buttonId = GetButtonId(__instance);

            // 通过 NotesManager 获取音符数据以确定类型
            NoteData noteData = null;
            try
            {
                var reader = NotesManager.Instance(___MonitorIndex)?.getReader();
                var noteList = reader?.GetNoteList();
                if (noteList != null && ___NoteIndex >= 0 && ___NoteIndex < noteList.Count)
                    noteData = noteList[___NoteIndex];
            }
            catch { /* 安全忽略 */ }

            string noteTypeStr = "UNKNOWN";
            if (noteData != null)
                noteTypeStr = GetNoteTypeName(noteData.type.getEnum());

            int physCh = TouchStateProcessor.GetPhysicalChannelForButton(buttonId);
            string logicalName = physCh >= 0 ? TouchStateProcessor.GetLogicalName(physCh) : "??";

            TouchStateProcessor.JudgeLogBuffer.Enqueue(new TouchStateProcessor.JudgeLogEntry
            {
                GameTimeMs = NotesManager.GetCurrentMsec(),
                FrameNumber = TouchStateProcessor.CurrentFrameNumber,
                ButtonId = buttonId,
                MonitorId = ___MonitorIndex,
                Timing = ___JudgeResult,
                DiffMsec = ___JudgeTimingDiffMsec,
                NoteTypeStr = noteTypeStr,
                PhysicalChannel = physCh,
                LogicalName = logicalName,
                CurrentRaw = physCh >= 0 ? TouchStateProcessor.GetCurrentRaw(physCh) : (ushort)0,
                SetupRaw = physCh >= 0 ? TouchStateProcessor.GetSetupRaw(physCh) : 0,
                TouchState = physCh >= 0 && TouchStateProcessor.GetTouchState(physCh)
            });
        }

        // ==========================================================
        // ================= 判定日志 Hook - 长按音符头部 =================
        // HoldNote 不使用基类 Judge()，而是使用 JudgeHoldHead() 处理按下判定
        // ==========================================================

        [HarmonyPatch(typeof(HoldNote), "JudgeHoldHead")]
        [HarmonyPostfix]
        private static void OnHoldNoteJudgeHead(HoldNote __instance, bool __result,
            NoteJudge.ETiming ___JudgeHeadResult, float ___JudgeTimingDiffMsec,
            int ___NoteIndex, int ___MonitorIndex)
        {
            if (!EnableFileLog || !EnableJudgeLog) return;
            if (!__result) return;

            int buttonId = GetButtonId(__instance);

            NoteData noteData = null;
            try
            {
                var reader = NotesManager.Instance(___MonitorIndex)?.getReader();
                var noteList = reader?.GetNoteList();
                if (noteList != null && ___NoteIndex >= 0 && ___NoteIndex < noteList.Count)
                    noteData = noteList[___NoteIndex];
            }
            catch { }

            string noteTypeStr = "HOLD";
            if (noteData != null)
                noteTypeStr = GetNoteTypeName(noteData.type.getEnum());

            int physCh = TouchStateProcessor.GetPhysicalChannelForButton(buttonId);
            string logicalName = physCh >= 0 ? TouchStateProcessor.GetLogicalName(physCh) : "??";

            TouchStateProcessor.JudgeLogBuffer.Enqueue(new TouchStateProcessor.JudgeLogEntry
            {
                GameTimeMs = NotesManager.GetCurrentMsec(),
                FrameNumber = TouchStateProcessor.CurrentFrameNumber,
                ButtonId = buttonId,
                MonitorId = ___MonitorIndex,
                Timing = ___JudgeHeadResult,
                DiffMsec = ___JudgeTimingDiffMsec,
                NoteTypeStr = noteTypeStr,
                PhysicalChannel = physCh,
                LogicalName = logicalName,
                CurrentRaw = physCh >= 0 ? TouchStateProcessor.GetCurrentRaw(physCh) : (ushort)0,
                SetupRaw = physCh >= 0 ? TouchStateProcessor.GetSetupRaw(physCh) : 0,
                TouchState = physCh >= 0 && TouchStateProcessor.GetTouchState(physCh)
            });
        }

        // ==========================================================
        // ================= 判定日志 Hook - 触摸音符 =================
        // TouchNoteB 重写了 Judge()，需要单独 Hook
        // ==========================================================

        [HarmonyPatch(typeof(TouchNoteB), "Judge")]
        [HarmonyPostfix]
        private static void OnTouchNoteBJudge(TouchNoteB __instance, bool __result,
            NoteJudge.ETiming ___JudgeResult, float ___JudgeTimingDiffMsec,
            int ___NoteIndex, int ___MonitorIndex)
        {
            if (!EnableFileLog || !EnableJudgeLog) return;
            if (!__result) return;

            int buttonId = GetButtonId(__instance);

            string noteTypeStr = "TOUCH";
            int physCh = TouchStateProcessor.GetPhysicalChannelForButton(buttonId);
            string logicalName = physCh >= 0 ? TouchStateProcessor.GetLogicalName(physCh) : "??";

            TouchStateProcessor.JudgeLogBuffer.Enqueue(new TouchStateProcessor.JudgeLogEntry
            {
                GameTimeMs = NotesManager.GetCurrentMsec(),
                FrameNumber = TouchStateProcessor.CurrentFrameNumber,
                ButtonId = buttonId,
                MonitorId = ___MonitorIndex,
                Timing = ___JudgeResult,
                DiffMsec = ___JudgeTimingDiffMsec,
                NoteTypeStr = noteTypeStr,
                PhysicalChannel = physCh,
                LogicalName = logicalName,
                CurrentRaw = physCh >= 0 ? TouchStateProcessor.GetCurrentRaw(physCh) : (ushort)0,
                SetupRaw = physCh >= 0 ? TouchStateProcessor.GetSetupRaw(physCh) : 0,
                TouchState = physCh >= 0 && TouchStateProcessor.GetTouchState(physCh)
            });
        }

        // ==========================================================
        // ================= 幻灯片判定映射建立 =================
        // 在 SlideRoot 绑定 SlideJudge 时记录映射关系
        // ==========================================================

        [HarmonyPatch(typeof(SlideRoot), "SetJudgeObject")]
        [HarmonyPostfix]
        private static void OnSlideRootSetJudgeObject(SlideRoot __instance, SlideJudge slideJudge)
        {
            // 使用 TryAdd，同个 slideJudge 只会记录一次
            SlideJudgeMap.TryAdd(slideJudge, __instance);
        }

        // ==========================================================
        // ================= 判定日志 Hook - 幻灯片音符 =================
        // 幻灯片使用 SlideJudge.Initialize 回调，需要通过映射表找到 SlideRoot
        // ==========================================================

        [HarmonyPatch(typeof(SlideJudge), "Initialize")]
        [HarmonyPostfix]
        private static void OnSlideJudgeInitialize(SlideJudge __instance,
            NoteJudge.ETiming judge, float msec, bool isBreak)
        {
            if (!EnableFileLog || !EnableJudgeLog) return;

            // 从映射表中查找所属的 SlideRoot
            if (!SlideJudgeMap.TryGetValue(__instance, out var slideRoot)) return;

            if (judge == NoteJudge.ETiming.End) return;

            int buttonId = slideRoot.ButtonId;
            int monitorId = slideRoot.MonitorId;

            // 幻灯片音符类型根据 Break 标志确定
            string noteTypeStr = isBreak ? "BREAK_SLIDE" : "SLIDE";

            int physCh = TouchStateProcessor.GetPhysicalChannelForButton(buttonId);
            string logicalName = physCh >= 0 ? TouchStateProcessor.GetLogicalName(physCh) : "??";

            TouchStateProcessor.JudgeLogBuffer.Enqueue(new TouchStateProcessor.JudgeLogEntry
            {
                GameTimeMs = NotesManager.GetCurrentMsec(),
                FrameNumber = TouchStateProcessor.CurrentFrameNumber,
                ButtonId = buttonId,
                MonitorId = monitorId,
                Timing = judge,
                DiffMsec = msec,
                NoteTypeStr = noteTypeStr,
                PhysicalChannel = physCh,
                LogicalName = logicalName,
                CurrentRaw = physCh >= 0 ? TouchStateProcessor.GetCurrentRaw(physCh) : (ushort)0,
                SetupRaw = physCh >= 0 ? TouchStateProcessor.GetSetupRaw(physCh) : 0,
                TouchState = physCh >= 0 && TouchStateProcessor.GetTouchState(physCh)
            });
        }
    }
}
