using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AquaMai.Core.Helpers;
using Manager;
using MelonLoader;
using Monitor;
using Monitor.Game;
using UnityEngine;
using HarmonyLib;
using Main;

namespace AquaMai.Mods.GameSystem
{
    public partial class TenoDXIO
    {

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
                case NotesTypeID.Def.Tap: return "TAP";
                case NotesTypeID.Def.Break: return "BREAK";
                case NotesTypeID.Def.ExTap: return "EX_TAP";
                case NotesTypeID.Def.Hold: return "HOLD";
                case NotesTypeID.Def.ExHold: return "EX_HOLD";
                case NotesTypeID.Def.Star: return "STAR";
                case NotesTypeID.Def.BreakStar: return "BREAK_STAR";
                case NotesTypeID.Def.ExStar: return "EX_STAR";
                case NotesTypeID.Def.TouchTap: return "TOUCH_TAP";
                case NotesTypeID.Def.TouchHold: return "TOUCH_HOLD";
                case NotesTypeID.Def.ExBreakTap: return "EX_BREAK_TAP";
                case NotesTypeID.Def.BreakHold: return "BREAK_HOLD";
                case NotesTypeID.Def.ExBreakHold: return "EX_BREAK_HOLD";
                case NotesTypeID.Def.Slide: return "SLIDE";
                case NotesTypeID.Def.BreakSlide: return "BREAK_SLIDE";
                case NotesTypeID.Def.ExSlide: return "EX_SLIDE";
                case NotesTypeID.Def.ExBreakSlide: return "EX_BREAK_SLIDE";
                case NotesTypeID.Def.ExBreakStar: return "EX_BREAK_STAR";
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
                case NoteJudge.ETiming.Critical: return "CRITICAL";
                case NoteJudge.ETiming.FastPerfect:
                case NoteJudge.ETiming.FastPerfect2nd: return "FAST_PERFECT";
                case NoteJudge.ETiming.LatePerfect:
                case NoteJudge.ETiming.LatePerfect2nd: return "LATE_PERFECT";
                case NoteJudge.ETiming.FastGreat:
                case NoteJudge.ETiming.FastGreat2nd:
                case NoteJudge.ETiming.FastGreat3rd: return "FAST_GREAT";
                case NoteJudge.ETiming.LateGreat:
                case NoteJudge.ETiming.LateGreat2nd:
                case NoteJudge.ETiming.LateGreat3rd: return "LATE_GREAT";
                case NoteJudge.ETiming.FastGood: return "FAST_GOOD";
                case NoteJudge.ETiming.LateGood: return "LATE_GOOD";
                case NoteJudge.ETiming.TooFast: return "FAST_MISS";
                case NoteJudge.ETiming.TooLate: return "LATE_MISS";
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
