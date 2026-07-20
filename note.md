# AquaMai Mod 项目接手笔记

## 项目概述

AquaMai 是一个 MaiMai 中二节奏（Sega 街机音游）的 MelonLoader mod。
本项目为该 mod 的**电容触摸控制器**子系统，实现了自定义触摸屏驱动、底层电容信号处理、游戏判定日志及可视化分析。
主仓库: https://github.com/hykilpikonna/AquaDX （原上游，本 fork 做了大量定制）

---

## 目录结构

```
AquaMai.Mods/GameSystem/TenoDXIO/
  TenoIO.cs              -- 配置定义 + 日志系统 + Harmony Hook + UI时钟
  TouchStateProcessor.cs  -- 电容信号处理算法 + 判定日志数据结构
  SerialThreadManager.cs  -- 串口通信线程（读主控板数据、下发放置参数）
  ai提示引导词.md         -- 与AI对话时的上下文提示

log_analyzer-4.html       -- 日志离线分析工具（浏览器打开）

输出游戏程序/            -- 反编译的游戏源码（参考用，不参与编译）
  NoteJudge.cs            -- 判定算法 + ETiming 枚举(15级)
  Monitor/NoteBase.cs     -- 音符基类（Judge()/NoteCheck()/EndNote()）
  Monitor/TouchNoteB.cs   -- 触摸音符
  Monitor/HoldNote.cs     -- 长按音符（JudgeHoldHead() 不走基类 Judge）
  Monitor/SlideRoot.cs    -- 幻灯片音符（继承 MonoBehaviour，不继承 NoteBase）
  Monitor/SlideJudge.cs   -- 幻灯片判定显示组件
  Manager/NotesManager.cs -- 音符管理器（GetCurrentMsec() 等）
  Main/GameMainObject.cs  -- Unity MonoBehaviour 主循环（Update）
```

---

## 整体数据流

```
主控板(STM32) --串口 115200bps--> SerialThreadManager (后台线程)
                                       |
                                       v
                               TouchStateProcessor.ProcessFrame()
                                 (34通道电容原始值 -> 去抖/滤波/触发算法)
                                       |
                                       v
                               TouchStateProcessor.ProvideTouchStatus()
                                 (ulong位掩码, 每bit代表一个按键状态)
                                       |
                                       v
                               游戏 TouchStatusProvider (每帧读取)
                                       |
                                       v
                               InputManager.InGameTouchPanelAreaDown()
                                       |
                                       v
                               NoteBase.NoteCheck() -> Judge() -> EndNote()
                                 (音符判定: 命中/提前/延迟/Miss)
                                       |
                                       v
                              TenoIO Harmony Patch (Postfix)
                                捕获判定结果 -> ConcurrentQueue缓冲
                                       |
                                       v
                              每帧末尾 FlushJudgeBuffer() -> 写入日志文件
```

## 两条线程

| 线程 | 速率 | 职责 |
|------|------|------|
| 串口线程 | ~166Hz (6ms每帧) | SerialThreadManager 持续读串口 → ProcessFrame() → 原始值缓存/触摸掩码 |
| Unity主线程 | ~60Hz | GameMainObject.Update → 游戏逻辑 → 音符判定 → 日志写入 |

### 线程同步机制

- **TouchStateProcessor.CurrentGameTimeMs** (`volatile float`): 主线程每帧将 NotesManager.GetCurrentMsec() 写入该变量，串口线程 ProcessFrame 中读取作为 HW 日志时间戳。串口线程不能直接调用 NotesManager.GetCurrentMsec()（Unity API 非线程安全）。
- **TouchStateProcessor.CurrentFrameNumber** (`volatile int`): 主线程每帧递增，供日志关联帧号。
- **ConcurrentQueue\<JudgeLogEntry\>**: 主线程 Harmony Hook 产生判定事件入队，同一帧末尾 FlushJudgeBuffer 出队写入文件（也发生在主线程）。
- **lock(dataLock)**: ProvideTouchStatus 中保护 currentTouchMask/latchedTouchMask。
- **lock(fileLock)**: WriteLineToFile 中保护 StreamWriter（避免串口线程的 HW 日志与主线程的 JUDGE/FRAME 日志并发写入冲突）。

### 锁存（Latch）机制

`ProvideTouchStatus()` 使用 latchedTouchMask 解决串口~166Hz与游戏~60Hz的频率差问题：
- 串口线程每帧用 `latchedTouchMask |= newTouchMask` 累积状态变化
- 游戏调用 ProvideTouchStatus 时，返回累积的 `latchedTouchMask | currentTouchMask` 后重置 latch
- 这样即使串口在两帧游戏之间更新了多次，游戏也不会丢失中间的状态变化

---

## 模块详解

### 1. TenoIO.cs — 配置 + 日志 + Hook

#### 配置系统 （`TenoDXIO` 类）

所有可调参数使用 `[ConfigEntry]` 标注，在游戏内 AquaMai 配置界面可实时修改。
核心参数分组：

| 分组 | 参数 | 说明 |
|------|------|------|
| 串口 | COMPort | 默认 COM92 |
| IIR | IIRFilterFactor | 1=关闭, 2/4/8/16=对应系数的低通滤波 |
| A区 | TriggerSensitivity(650), HoldThreshold(450), QuickReleaseLine(1200), EdgeTriggerDeriv(150) | 主触摸区，算法最复杂 |
| C区 | BlockC_DiffThreshold(25), BlockC_DerivThreshold(25), ... | 传感器条 |
| BDE区 | 各自独立的 DiffThreshold / DerivRelease | 侧边传感器 |
| 单通道覆盖 | Override_A_Diff / Override_C_Diff / Override_BDE_Diff | 格式 "A1:600,B3:500" 对个别通道单独调参 |
| 日志 | EnableFileLog, EnableJudgeLog, LogZones | 日志系统开关 |

#### 日志系统

统一的文本日志，3 种条目混排，全部以游戏毫秒时间戳开头：

**HW** (硬件传感器数据, 串口线程写入):
```
[00:52:04.147] [12345.678] [HW] [Ch:03] [Block:A] [A5] Raw:2345 Base:1800 Diff:545 Deriv:120 Stat:1
```

**FRAME** (帧同步标记, 主线程每帧末尾写入):
```
[00:52:04.147] [12345.678] [FRAME] Frame:7215 UnityTime:146.562
```

**JUDGE** (判定事件, 主线程 Harmony Hook 写入):
```
[00:52:04.147] [12345.678] [JUDGE] [Frame:7215] [Ch:03] [A5] Note:TAP Timing:FAST_PERFECT Msec:-1.23ms | CurrRaw:2200 Base:1800 Diff:400 Stat:1
```

日志文件 4MB 自动分割（touch_log_part1.txt, part2.txt...），位于 `TenoDX_Logs/Log_yyyyMMdd_HHmmss/`。

#### Harmony Hook 一览

| 目标方法 | Patch | 用途 |
|----------|-------|------|
| GameMainObject.Awake | Postfix | 挂载屏幕时钟 UI (TenoTimeDisplay) |
| GameMainObject.Update | Postfix | 更新时间基准 + FlushJudgeBuffer + 写 FRAME |
| NoteBase.Judge | Postfix | 捕获 Tap/Break/Star 等标准音符判定 |
| HoldNote.JudgeHoldHead | Postfix | 捕获长按头部判定（HoldNote 不走基类 Judge） |
| TouchNoteB.Judge | Postfix | 捕获触摸音符判定 |
| SlideRoot.SetJudgeObject | Postfix | 建立 SlideJudge→SlideRoot 映射表 |
| SlideJudge.Initialize | Postfix | 捕获幻灯片判定（SlideRoot 不继承 NoteBase） |

#### 判定类别简化映射

游戏原生的 `NoteJudge.ETiming` 有 15 个精细级别（FastPerfect2nd, FastGreat3rd 等），
日志中简化为 5 类 + fast/late 标记：

| 日志输出 | 对应游戏 ETiming | 显示类别 |
|----------|-----------------|----------|
| CRITICAL | Critical | Critical |
| FAST_PERFECT | FastPerfect, FastPerfect2nd | Perfect (Fast) |
| LATE_PERFECT | LatePerfect, LatePerfect2nd | Perfect (Late) |
| FAST_GREAT | FastGreat, FastGreat2nd, FastGreat3rd | Great (Fast) |
| LATE_GREAT | LateGreat, LateGreat2nd, LateGreat3rd | Great (Late) |
| FAST_GOOD | FastGood | Good (Fast) |
| LATE_GOOD | LateGood | Good (Late) |
| FAST_MISS | TooFast | Miss (Fast) |
| LATE_MISS | TooLate | Miss (Late) |

---

### 2. TouchStateProcessor.cs — 触摸检测算法

#### 启动校准

1. 跳过前 200 帧 (SKIP_FRAMES) 不稳定数据
2. 累积 30 帧 (STARTUP_FRAMES) 原始值取平均作为基线 (setupRaw)
3. 此后每帧用 `diff = current - setupRaw` 做触发判断

#### ButtonDetector — 各区块算法

**A区** (主触摸区，按键轨道 A1-A8):
- 基础触发：`diff > on_default_diff`
- 边缘触发：`diff_deriv >= EdgeTriggerDeriv && diff >= EdgeTriggerMinDiff` 快速响应
- 长按保持：触发后切换 HoldThreshold 为保持阈值（防断触）
- 快速释放：`diff_deriv < deriv_down` 检测极速抬手
- 防悬空误触：`diff_deriv < HoverSpeedMax && diff < HoverDiffMax` 时抑制误触发

**C区** (窄传感器条 C1-C2):
- 使用 diff 和 diff_deriv 双条件触发
- 单独的释放阈值体系 (c_diff_r / c_deriv_r)

**B/D/E区** (侧边传感器):
- 简单的 diff 阈值触发 + deriv 释放逻辑
- 各区的 DiffThreshold/DerivRelease 独立配置

#### 物理通道映射

34 个物理通道 (0-33) 映射到逻辑名称 (A1-A8, B1-B8, C1-C2, D1-D8, E1-E8)：
```
映射表: A5, E5, D5, B4, A4, E4, D4, B3, A3, C1, E3, D3, B2, A2, E2, D2, B1,
        A1, E1, D1, B8, A8, E8, D8, B7, A7, C2, E7, D7, B6, A6, E6, D6, B5
```

注意：A区 (A1-A8) 对应游戏 8 个轨道的触摸按键，而物理通道号与逻辑名称的对应关系由接线决定（通过 HardwareMapping 配置可改）。

---

### 3. SerialThreadManager.cs — 串口通信

- 波特率 115200，后台线程持续运行
- 协议：每帧 70 字节，含校验和（最后1字节）
- 状态字节 0x00 = 正常扫描数据，0x01 = 主控请求配置
- 滑动窗口缓冲区 8192 字节，帧对齐用校验和
- 预热 100 帧丢弃冷启动不稳定数据
- 热插拔：断开后自动重试连接（每 2 秒）
- IIR 滤波器：`filterHistory[i] += (raw - filterHistory[i]) / factor`

---

### 4. log_analyzer-4.html — 离线分析工具

纯浏览器端 HTML/JS，Chart.js 绘制。

#### 日志解析

三组正则分别匹配 HW / FRAME / JUDGE 条目，**所有条目都有 [墙钟时间] 前缀**：

```
HW_REGEX    = /^\[(\S+)\] \[([\d.]+)\] \[HW\] \[Ch:(\d+)\] \[Block:(.)\] \[(\w+)\] Raw:(\d+) Base:([\d.]+) Diff:(-?\d+) Deriv:(-?\d+) Stat:(\d+)/
FRAME_REGEX = /^\[(\S+)\] \[([\d.]+)\] \[FRAME\] Frame:(\d+) UnityTime:([\d.]+)/
JUDGE_REGEX = /^\[(\S+)\] \[([\d.]+)\] \[JUDGE\] \[Frame:(\d+)\] \[Ch:(\d+)\] \[(\w+)\] Note:(\w+) Timing:(\w+) Msec:(-?[\d.]+)ms \| CurrRaw:(\d+) Base:(\d+) Diff:(-?\d+) Stat:(\d+)/
```

各字段索引：m[1]=wallTime, m[2]=gameTimeMs, 其后因条目类型而异。

#### 两大图表模式

**完整模式** (有 HW 数据时)：
- 5 条波形：Raw / Base / Diff / Deriv / Stat
- JUDGE 散点叠加（紫色三角，y=时间差ms）
- FRAME 垂直参考线 + 帧号标注

**判定模式** (仅 JUDGE + FRAME，无 HW)：
- 纯散点图：x=游戏ms，y=判定时间差ms
- 5 类判定用不同颜色：Critical(金) / Perfect(绿) / Great(蓝) / Good(橙) / Miss(红)

#### 墙钟时间同步

每条日志自带 `[HH:mm:ss.fff]` 墙钟时间前缀，解析后每个条目有 `wallTime`（原始字符串）和 `wallSec`（距基准的秒偏移）两个字段。
- 过滤和 x 轴均基于 `wallSec`（墙钟秒偏移），不再依赖可能为 0 的游戏 ms
- FRAME 条目作为时间锚点，用于确定 `wallTimeBase`（加载时取第一条 FRAME 的墙钟秒数）
- 墙钟时间选择器直接输入 `01:16:37.000` 格式，通过 `wallTimeToSeconds()` 转换为秒数后与 `wallSec` 比较

---

## 坑 / 注意事项

### Harmony 相关

1. **`NoteBase.ButtonId` 是 protected auto-property**
   `protected int ButtonId { get; set; }` → 没有 backing field，Harmony `___` 前缀无法注入。
   **必须用反射**：`typeof(NoteBase).GetProperty("ButtonId", BindingFlags.NonPublic|BindingFlags.Instance).GetValue(note)`
   已在 `GetButtonId()` 中静态缓存 PropertyInfo。

2. **HoldNote 不走基类 Judge()**
   HoldNote 重写了 NoteCheck()，在 NoteCheck 中调用 `JudgeHoldHead()` 而非 `base.Judge()`。
   → 需要单独 Hook `HoldNote.JudgeHoldHead`，其判定结果存在 `JudgeHeadResult` 而非 `JudgeResult`。

3. **SlideRoot 继承 MonoBehaviour 而非 NoteBase**
   幻灯片音符没有 ButtonId 属性继承自 NoteBase，但有自己独立的 `public int ButtonId { get; set; }`。
   判定通过 `SlideJudge.Initialize(judge, msec, isBreak)` 回调，需要通过 `ConcurrentDictionary<SlideJudge, SlideRoot>` 映射表追溯所属 SlideRoot。
   映射在 `SlideRoot.SetJudgeObject(SlideJudge)` 时建立。

4. **NoteBase.Judge() 返回值语义**
   `Judge()` 返回 true 表示本帧完成了判定（包括 TooLate miss），返回 false 表示尚未到判定时机。
   `if (!__result) return;` 用于避免在同一个音符上重复记录，不可移除。

5. **判定枚举值**
   `NoteJudge.ETiming` 只有 15 个值 + End，**不存在** `FastMiss` / `LateMiss`。
   TooFast 和 TooLate 本身就是 Miss 类判定。映射时要小心，编译期不会检查不存在的枚举成员。

### 线程安全

6. **NotesManager.GetCurrentMsec() 只能在主线程调用**
   串口线程中读取游戏时间 → 使用 `TouchStateProcessor.CurrentGameTimeMs`（主线程每帧更新的 volatile float）。

7. **ConcurrentQueue 必须始终清空**
   `FlushJudgeBuffer()` 即使日志关闭也要 TryDequeue 全部。否则 Harmony Hook 持续入队但无人消费 → 内存泄漏。

8. **ProvideTouchStatus 的 2ms 防重入**
   ```csharp
   if ((DateTime.Now - lastReadTime).TotalMilliseconds < 2.0) return lastReadResult;
   ```
   游戏一帧内可能多次调用 `ProvideTouchStatus`（每个轨道检查都会调用），防重入保证 latch 语义。

### .NET Framework 版本

9. **目标 .NET Framework 较老，不支持 C# 新语法**
   - **没有 `Array.Fill()`** → 用 for 循环代替
   - **没有 `List<>` 集合表达式** `[1, 2, 3]` → 用 `new int[] { 1, 2, 3 }` 或 `new List<int> { 1, 2, 3 }`
   - **没有 `Index`/`Range`** 不能用 `list[^1]` 或 `list[1..3]`
   - 字符串分割需要 `Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries)` 或 `Split([','], StringSplitOptions.RemoveEmptyEntries)`（后者在较新的 C# 可用但实际项目可能不支持）

### 构建

10. **使用 build.ps1 构建**
    在项目根目录执行 `powershell -ExecutionPolicy Bypass -File build.ps1`。
    直接 `dotnet build` 可能因缺少构建步骤而失败。

### 日志分析器

11. **JUDGE_REGEX 的 Timing 字段是简化后的名称**
    正则中 `Timing:(\w+)` 匹配 CRITICAL / FAST_PERFECT / LATE_PERFECT / FAST_GREAT / LATE_GREAT / FAST_GOOD / LATE_GOOD / FAST_MISS / LATE_MISS。
    所有下划线连接的单词都能被 `\w+` 匹配。

12. **墙钟时间插值的边界条件**
    `wallTimeToGameMs()` 需要至少一个 FRAME 条目才能工作。
    如果起止墙钟时间超出 FRAME 覆盖范围，则取最近的 FRAME 值，不会报错。
    首次加载自动用最早和最晚的 FRAME 填充默认时间范围。

### 游戏时间为 [0.000] 的问题

16. **HW 日志游戏时间是 0 的原因**
    `WriteLog()` 读取 `TouchStateProcessor.CurrentGameTimeMs`（主线程每帧同步到串口线程的 volatile）。
    但串口线程在 `OnBeforeEnableCheck()` 时立即启动，此时游戏主循环尚未开始，
    `CurrentGameTimeMs` 的默认值为 0。同样 FRAME/JUDGE 条目从 `NotesManager.GetCurrentMsec()` 读取，
    若在游戏场景加载前或音钟尚未启动时调用也会返回 0。
    **解决方案**: 所有日志行加上墙钟时间 `[HH:mm:ss.fff]` 前缀作为主要时间轴，
    无论游戏时间是否正常都能与屏幕录像准确对应。

### 串口

13. **主控板启动后需要发送配置**
    状态字节 0x01 表示主控请求配置下发。`SendHardwareConfig()` 组装 139 字节帧（每通道 Res/Mod/Sns/Div），相同配置每秒最多发一次（防抖）。

14. **IIR 滤波器冷启动**
    前 100 帧数据丢弃（warmupFrames=100），避免冷启动时 IIR 滤波器的瞬态响应导致误触发。
    重新连接串口时重置。

15. **帧同步丢失恢复**
   滑动窗口 + 校验和定位帧边界。校验失败时逐字节滑动重对齐。

---

## 调试与测试技巧

- **无触摸控制器时纯键盘测试**: 游戏支持键盘映射到 JVS 按键。日志中不会出现 HW 条目（无串口数据），但 JUDGE 和 FRAME 条目正常输出。HTML 分析器会自动切换为判定散点图模式。
- **日志快速检查**: EnableFileLog=true + EnableJudgeLog=true，启动游戏后查看 TenoDX_Logs/ 目录。
- **调参**: 游戏内 AquaMai 配置菜单可实时调整各区块阈值，无需重启。
