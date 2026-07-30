# Claude 工作记录

## 2026-07-30: 修复 TenoData 读取的校准死锁

### 问题
串口线程在处理 `status == 0x00` 数据帧时，只调用 `ProcessPSoCBlockIfChanged` → `ProcessChannel`，但 `ProcessChannel` 第170行要求 `startupRawReady == true` 才会处理数据。而 `startupRawReady` 只由 `ProcessFrame` 设置，但 `ProcessFrame` 从未被调用。

结果：校准永远无法完成，所有34通道触摸数据被静默丢弃。

### 修复
1. **TouchStateProcessor.cs**: 新增 `internal static bool IsStartupRawReady` 属性暴露校准状态
2. **SerialThreadManager.cs**: 
   - 新增 `ExtractAllChannels()` 方法 — 从原始帧提取全部34通道数据并应用 IIR 滤波，与 Python `run2/main.py` 的解码逻辑一致
   - 预热后先提取全部通道 → 若未校准则调用 `ProcessFrame(channelsCache)` 完成校准 → 校准完成后切换到 PSoC 变化检测优化
   - `ProcessPSoCBlockIfChanged` 简化为仅做判定处理（IIR 已由 `ExtractAllChannels` 完成）

### 修改文件
- `TouchStateProcessor.cs` — 添加 `IsStartupRawReady` 属性
- `SerialThreadManager.cs` — 新增 `ExtractAllChannels`，重构核心数据流

### 背景
用户需要将项目迁移到单片机（MCU），需要对现有代码进行合理分离，保留串口处理逻辑、判定逻辑和配置文件，为后续迁移做准备。

### 执行内容

#### 1. 代码执行流程分析
输出了 `code_info.md`，按执行顺序详细分析了从启动初始化→串口数据读取→触摸判定→游戏Hook注入→日志系统的完整流程。

#### 2. 代码分离重构
将3个源文件拆分为6个文件，职责划分如下：

**新增文件:**
- `HardwareConfig.cs` — 从 `TenoIO.cs` 提取。硬件扫描参数表 (`HardwareConfig` 静态类 + `ScanParams`)，包含 5 个区块的懒加载解析配置、`PhysicalToLogicalMap` 映射表、`GetParams()` 方法。这是**硬编码**的硬件参数，不参与游戏内配置系统。
- `TenoDXIOConfig.cs` — 从 `TenoIO.cs` 提取。`TenoDXIO` 的 partial 类，包含 `[ConfigSection]` 和所有 `[ConfigEntry]` 字段（串口、映射、扫描配置、UI/日志、A/C/B/D/E 区判定阈值、通道灵敏度覆盖），以及 `ApplyHardwareMapping()` 和 `ParseConfigString()` 工具方法。这是**游戏内可调**的配置。
- `ButtonDetector.cs` — 从 `TouchStateProcessor.cs` 提取。34 通道触摸判定算法核心，包含 A区累积-导数双鉴算法、C区 diff/deriv 阈值判定、B/D/E区 diff 阶梯判定的完整状态机。

**修改文件:**
- `TenoIO.cs` — `TenoDXIO` 改为 `partial class`，移除了 `[ConfigSection]` 和所有 `[ConfigEntry]` 配置项、`ApplyHardwareMapping()`、`ParseConfigString()`（转移到 `TenoDXIOConfig.cs`）。保留：Harmony Hook、日志系统、UI组件、生命周期管理。
- `TouchStateProcessor.cs` — 移除了 `ButtonDetector` 内部类（已提取为顶层类）。将 `override_*` 数组从 `private` 改为 `internal` 并重命名为 PascalCase (`Override_*`)，供 `ButtonDetector` 跨文件访问。

**未修改文件:**
- `SerialThreadManager.cs` — 保持原样，已良好分离。

### 文件结构
```
TenoDXIO/
├── HardwareConfig.cs       ← 硬编码硬件扫描参数
├── TenoDXIOConfig.cs       ← 游戏内可调配置（与 HardwareConfig 区分）
├── TenoIO.cs               ← 主入口：Hook、日志、UI、生命周期
├── TouchStateProcessor.cs  ← 触摸状态管理、校准、映射
├── ButtonDetector.cs       ← 触摸判定算法
└── SerialThreadManager.cs  ← 串口管理与数据包解析
```

### 迁移到MCU时需要的文件
- `HardwareConfig.cs` — 硬件扫描参数配置
- `TenoDXIOConfig.cs` — 判定阈值等可调参数
- `ButtonDetector.cs` — 判定算法
- `SerialThreadManager.cs` — 串口协议和数据包解析逻辑

### 编译结果
- 错误: 0
- 警告: 5（全部来自其他未修改文件）
- 逻辑行为: 无变化
