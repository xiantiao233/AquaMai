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

        // ================= A区 大面积自电容状态机参数 =================

        // -------------------- 普通按下 --------------------

        [ConfigEntry(
            "A区 - 边缘触摸阈值",
            "普通单指边缘触摸的最低Diff。数值越低越灵敏。默认 320")]
        public static int AEdgeOn = 320;

        [ConfigEntry(
            "A区 - 大面积触摸阈值",
            "快速大面积按下时不会在边缘阈值处提前触发，而是达到此阈值后触发。默认 850")]
        public static int ALargeOn = 850;

        [ConfigEntry(
            "A区 - 快速上升导数",
            "超过此deriv时认为可能是大面积快速按下，暂不在边缘阈值处触发。默认 90")]
        public static int AFastRiseDeriv = 90;

        [ConfigEntry(
            "A区 - 边缘最小上升导数",
            "普通边缘触摸触发时所需的最小正向deriv，防止下降沿误触。默认 2")]
        public static int AEdgeMinDeriv = 2;


        // -------------------- 快速上升候选 --------------------

        [ConfigEntry(
            "A区 - 快速短点击峰值",
            "快速上升未达到大面积阈值，但稳定或下降前达到此峰值时，作为边缘短点击触发。默认 390")]
        public static int AShortTapPeak = 390;

        [ConfigEntry(
            "A区 - 快速候选稳定导数",
            "快速候选的deriv下降到此值以下时，认为上升已经结束。默认 3")]
        public static int APendingSettleDeriv = 3;

        [ConfigEntry(
            "A区 - 快速候选取消阈值",
            "快速候选信号跌到此Diff以下时取消，防止候选状态卡住。默认 180")]
        public static int AFastPendingCancel = 180;


        // -------------------- 释放 --------------------

        [ConfigEntry(
            "A区 - 彻底释放阈值",
            "信号回到动态基线附近并低于此值时立即释放。默认 105")]
        public static int ACleanRelease = 105;

        [ConfigEntry(
            "A区 - 峰值释放比例",
            "当前Diff低于本次峰值乘以此比例时，才允许通过下降手势释放。默认 0.52")]
        public static float AReleasePeakRatio = 0.52f;

        [ConfigEntry(
            "A区 - 最小释放下降量",
            "相对本次按下峰值至少下降这么多计数才允许释放。默认 180")]
        public static int AReleaseMinDrop = 180;

        [ConfigEntry(
            "A区 - 最小释放下降比例",
            "相对峰值至少下降此比例才允许释放。默认 0.25")]
        public static float AReleaseDropRatio = 0.25f;

        [ConfigEntry(
            "A区 - 释放导数",
            "deriv低于此值且同时满足峰值比例和下降量时释放。默认 -12")]
        public static int AReleaseDeriv = -12;


        // -------------------- 释放后连续重按 --------------------

        [ConfigEntry(
            "A区 - 快速重按上升量",
            "释放后，相对下降谷底快速上升这么多计数即可重新触发。默认 120")]
        public static int ARepressRise = 120;

        [ConfigEntry(
            "A区 - 快速重按导数",
            "快速重按所需的最小正向deriv。默认 20")]
        public static int ARepressDeriv = 20;

        [ConfigEntry(
            "A区 - 慢速重按上升量",
            "连续操作较慢时，相对下降谷底上升这么多计数也可重新触发。默认 280")]
        public static int ARepressSlowRise = 280;

        [ConfigEntry(
            "A区 - 重按最低信号",
            "快速重按时，相对动态基线至少达到此Diff，防止释放回弹误触。默认 250")]
        public static int ARepressSignalMin = 250;


        // -------------------- 动态基线 --------------------

        [ConfigEntry(
            "A区 - 基线追踪范围",
            "只有信号与动态基线的距离不超过此值时才允许追踪基线。默认 120")]
        public static int ABaselineTrackRange = 120;

        [ConfigEntry(
            "A区 - 基线静置导数",
            "只有deriv绝对值不超过此值时才允许追踪基线。默认 6")]
        public static int ABaselineQuietDeriv = 6;

        [ConfigEntry(
            "A区 - 基线追踪系数",
            "动态基线每次向当前Raw移动的比例。默认 0.02")]
        public static float ABaselineAlpha = 0.02f;

        [ConfigEntry(
            "A区 - 基线最大步长",
            "动态基线每次调用最多移动的计数，防止吸收真实触摸。默认 0.5")]
        public static float ABaselineMaxStep = 0.5f;

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
        public static int BlockB_DiffThreshold = 20;
        [ConfigEntry("B区 - diff_deriv 突变抑制线", "默认 -15")]
        public static int BlockB_DerivRelease = -20;
        [ConfigEntry("D区 - Diff 触发线", "默认 3")]
        public static int BlockD_DiffThreshold = 20;
        [ConfigEntry("D区 - diff_deriv 突变抑制线", "默认 -4")]
        public static int BlockD_DerivRelease = -18;
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
