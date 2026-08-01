using System;
using System.Collections.Generic;
using AquaMai.Config.Attributes;

namespace AquaMai.Mods.GameSystem
{
    // ==========================================
    // 游戏内可调的算法参数与映射
    // （与 HardwareConfig 区分：HardwareConfig 是硬编码的硬件扫描参数表，此处为游戏内可通过 ConfigEntry 调整的阈值与映射）
    // ==========================================
    [ConfigSection(
      name: "TenoDXIO Touch Trigger",
      en: "TenoDXIO Touch Trigger",
      zh: "TenoDXIO Touch Trigger")]
    public partial class TenoDXIO
    {
        // ================= 串口配置 =================
        [ConfigEntry("串口号", "主控板的COM口，例如 COM92 (修改后需重启生效)")]
        public static string COMPort = "COM92";

        [ConfigEntry("IIR滤波器系数", "可选值: 1(关闭滤波), 2(即1/2), 4(即1/4), 8(即1/8), 16(即1/16)")]
        public static int IIRFilterFactor = 1;

        // ================= 硬件引脚映射配置 =================
        [ConfigEntry("硬件引脚通道映射", "按0-33的物理通道顺序，填入对应的游戏区块，用逗号分隔")]
        public static string HardwareMapping = "E4,D4,B3,A3,C1,E3,D3,B2,A2,E2,D2,B1,A1,E1,D1,B8,A8,E8,D8,B7,A7,C2,E7,D7,B6,A6,E6,D6,B5,A5,E5,D5,B4,A4";

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

        // ================= A区 累积-导数双鉴算法参数 =================
        [ConfigEntry("A区 - 大信号直通阈值", "diff >= 此值时进入大信号通道。默认 700")]
        public static int TriggerSensitivity = 700;
        [ConfigEntry("A区 - 大信号门控帧数", "设为1可在diff首次跨过700时延迟1帧确认，防止提前判定。默认 1(推荐)")]
        public static int LargeSignalGate = 1;
        [ConfigEntry("A区 - 大信号门控deriv上限", "大信号跨700时，若deriv超过此值则启用门控；已稳定信号(deriv≤此值)跳过门控直接触发。默认 300")]
        public static int LargeGateDerivMax = 300;
        [ConfigEntry("A区 - 累积窗口大小", "滑动窗口帧数。默认 8")]
        public static int WindowSize = 8;
        [ConfigEntry("A区 - 触发比例阈值", "spike_ratio = diff / cum_avg > 此值。默认 1.8")]
        public static float TriggerRatio = 1.8f;
        [ConfigEntry("A区 - 触发导数阈值", "deriv > 此值。默认 28")]
        public static int TriggerDeriv = 28;
        [ConfigEntry("A区 - 触发最小Diff", "diff > 此值，过滤噪声。默认 55")]
        public static int TriggerDiffMin = 55;
        [ConfigEntry("A区 - 确认超时帧数", "pending 阶段超时帧数。默认 10")]
        public static int ConfirmFrames = 10;
        [ConfigEntry("A区 - 确认Diff阈值", "diff 突破此值进入崩溃观察。默认 200")]
        public static int ConfirmDiff = 200;
        [ConfigEntry("A区 - 释放硬下限", "释放阈值不会低于此值。默认 35")]
        public static int ReleaseFloor = 35;
        [ConfigEntry("A区 - 动态释放比例", "释放阈值 = max(floor, peak * ratio)。默认 0.25")]
        public static float ReleaseRatio = 0.35f;
        [ConfigEntry("A区 - 快速释放导数", "deriv < 此值时立即释放。默认 -40")]
        public static int SharpReleaseDeriv = -40;
        [ConfigEntry("A区 - 崩溃观察窗口", "崩溃观察帧数。默认 7")]
        public static int CrashWindow = 7;
        [ConfigEntry("A区 - 崩溃导数阈值", "观察期内 deriv 低于此值且 diff 低于CrashDiffThreshold则判定悬空取消。默认 -8")]
        public static int CrashDerivThreshold = -8;
        [ConfigEntry("A区 - 崩溃Diff阈值", "崩溃判定配合使用的 diff 上限。默认 280")]
        public static int CrashDiffThreshold = 280;
        [ConfigEntry("A区 - 增长判定导数底线", "观察期内 deriv 需高于此值的帧数达标才确认，过滤站定不前的虚空信号。默认 3")]
        public static int GrowthFloor = 3;
        [ConfigEntry("A区 - 可信触摸阈值", "观察期内 a_max_diff 达到此值则豁免崩溃/增长检查，直接确认。用于快速扫过等场景。默认 350")]
        public static int ConfidentDiffThreshold = 350;

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
    }
}
