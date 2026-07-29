using System;
using MelonLoader;

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
}
