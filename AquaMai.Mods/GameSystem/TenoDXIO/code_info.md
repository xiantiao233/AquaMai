# TenoDXIO 代码执行流程分析

## 概述

该项目通过串口读取硬件自电容触摸数据，经过滤波和判定算法处理后，Hook 游戏引擎将触摸信息注入到游戏中。

---

## 一、启动初始化流程

### 1. 入口：`TenoDXIO.OnBeforeEnableCheck()`
被 MelonLoader 在 Mod 加载时调用，执行顺序：

```
OnBeforeEnableCheck()
  ├── TouchStateProcessor.Init()
  │     ├── TenoDXIO.InitFileLogger()          // 初始化文件日志系统
  │     ├── TenoDXIO.ApplyHardwareMapping()     // 解析 HardwareMapping 配置，填充 PhysicalToLogicalMap
  │     ├── InitMappings()                      // 构建 logicalToMaskMap 和 buttonIdToPhysicalChannel 反向映射
  │     ├── LoadOverrides()                     // 解析各通道灵敏度覆盖配置
  │     └── new ButtonDetector()[34]            // 为34个物理通道各创建一个检测器实例
  ├── TouchStatusProvider.RegisterTouchStatusProvider(0, ...)  // 注册触摸状态提供者
  └── SerialThreadManager.Start()               // 启动串口后台线程
```

### 2. `InitMappings()` 逻辑
- 遍历 34 个物理通道，根据 `PhysicalToLogicalMap[i]` 解析出 `block`（A/B/C/D/E）和 `num`（1-8）
- 计算逻辑掩码位移：A区→0-7, B区→8-15, C区→16-17, D区→18-25, E区→26-33
- 构建 `logicalToMaskMap[i] = 1UL << maskShift`
- A区通道同时建立 ButtonId(0-7) → 物理通道(0-33) 的反向映射

### 3. `LoadOverrides()` 逻辑
- 解析 `Override_A_Diff`、`Override_C_Diff` 等7组覆盖配置字符串（格式："A1:600,B2:10"）
- 为每个物理通道填充对应的覆盖值，未配置的通道值为 -1（使用默认值）

---

## 二、串口数据读取流程（后台线程）

### 1. `SerialThreadManager.SerialReaderThread()` 主循环

```
while (isRunning)
  ├── 若串口未打开：创建 SerialPort(COMPort, 115200)，重置校准状态
  ├── 读取字节到 streamBuffer[8192]
  ├── 寻找70字节数据包（校验和验证）
  │     ├── status == 0x00（正常扫描数据）
  │     │     ├── 预热期（前100帧）→ 丢弃数据
  │     │     ├── 否则：提取34通道 ushort 值
  │     │     ├── IIR 滤波（若 IIRFilterFactor > 1）
  │     │     │     filterHistory[i] += (raw - filterHistory[i]) / IIRFilterFactor
  │     │     └── 调用 TouchStateProcessor.ProcessFrame(channelsCache)
  │     └── status == 0x01（主控请求配置）
  │           └── SendHardwareConfig() 下发34通道硬件扫描参数
  └── 残余数据平移到缓冲区头部
```

### 2. 串口数据包格式（70字节）

| 偏移 | 大小 | 说明 |
|------|------|------|
| 0 | 1 | 状态字节：0x00=扫描数据, 0x01=请求配置 |
| 1-68 | 68 | 34个通道的16位原始值（每通道2字节，小端序） |
| 69 | 1 | 校验和（前69字节累加的低8位） |

### 3. 硬件配置下发格式（139字节）

| 偏移 | 大小 | 说明 |
|------|------|------|
| 0 | 1 | 帧头 0xAA |
| 1 | 1 | 命令 0x01 |
| 2-35 | 34 | 34通道 Res 参数 |
| 36-69 | 34 | 34通道 Mod 参数 |
| 70-103 | 34 | 34通道 Sns 参数 |
| 104-137 | 34 | 34通道 Div 参数 |
| 138 | 1 | 校验和 |

---

## 三、触摸判定处理流程

### 1. 校准阶段 `TouchStateProcessor.ProcessFrame()`

```
校准阶段（startupRawReady == false）
  ├── 跳过前 SKIP_FRAMES(200) 帧不稳定数据
  ├── 累积 STARTUP_FRAMES(30) 帧的原始值
  └── 计算平均值作为基线 setupRaw[i]
```

### 2. 正常处理阶段

```
foreach physIdx in 0..33
  ├── 存储 currentRawValues[physIdx]
  ├── 调用 detectors[physIdx].ProcessFrame(physIdx, currentVal, setupRaw[physIdx])
  │     ├── 计算: diff = current_val - setup_raw
  │     ├── 计算: diff_deriv = current_val - history[0]（一阶导数）
  │     ├── 根据 block 执行不同算法：
  │     │     ├── A区：累积-导数双鉴算法
  │     │     ├── C区：diff/deriv 阈值判定
  │     │     └── B/D/E区：diff 阶梯判定
  │     └── 更新 history 缓冲区
  └── 聚合 newTouchMask
```

### 3. A区判定算法（累积-导数双鉴算法）详细状态机

**状态：IDLE（空闲检测）**
- 维护滑动窗口累积量 `a_ring`（默认8帧窗口）
- 触发条件（三者同时满足）：
  - `spike_ratio = diff / cum_avg > TriggerRatio`（默认1.8）
  - `diff_deriv > TriggerDeriv`（默认28）
  - `diff > TriggerDiffMin`（默认55）
- 触发后进入 PENDING 状态
- **大信号直通**：若 `diff >= TriggerSensitivity`（默认700），通过门控计数器后直接判定按下

**状态：PENDING（确认等待）**
- 计数器递增，超时（ConfirmFrames，默认10帧）则取消
- 若 `diff > ConfirmDiff`（默认200）→ 进入 OBSERVING 子状态
- 若超时无确认 → 回 IDLE

**状态：OBSERVING（崩溃观察）**
- 观察期 CrashWindow 帧（默认7帧）
- 若检测到导数崩溃：`diff_deriv < CrashDerivThreshold && diff < CrashDiffThreshold` → 判定为悬空误触，取消
- 观察期满无崩溃 → 确认按下

**状态：PRESSED（按下保持）**
- 动态峰值跟踪：`a_max_diff = max(a_max_diff, diff)`
- 释放阈值 = `max(ReleaseFloor, a_max_diff * ReleaseRatio)`
- 释放条件：`diff < releaseThresh || diff_deriv < SharpReleaseDeriv`（默认-40）

### 4. C区判定算法

```
按下条件: diff > BlockC_DiffThreshold || diff_deriv > BlockC_DerivThreshold
抑制条件: diff_deriv < BlockC_DerivRelease && diff < diff_threshold * 1.5
松开条件: diff < BlockC_DiffRelease
```

### 5. B/D/E区判定算法

```
按下条件（任一满足）:
  - diff > threshold * 1.5          （强信号直通）
  - diff > threshold && last_diff > threshold / 2  （阶梯确认）
  - is_pressed && diff > threshold  （保持状态）

抑制条件: diff_deriv < deriv_release && diff < threshold * 1.5
松开条件: diff <= threshold / 2
```

---

## 四、触摸状态提供给游戏

### `TouchStateProcessor.ProvideTouchStatus(int playerNo)`

- 被游戏引擎每帧调用
- 返回 `latchedTouchMask | currentTouchMask`（保持锁存 + 当前状态）
- 每次读取后重置 `latchedTouchMask = currentTouchMask`
- 2ms 内的重复读取返回缓存结果

---

## 五、游戏 Hook 注入流程

### 1. 帧同步 Hook：`GameMainObject.Update` (Postfix)
每 Unity 帧末尾执行：
```
OnGameUpdate()
  ├── TouchStateProcessor.CurrentGameTimeMs = NotesManager.GetCurrentMsec()
  ├── TouchStateProcessor.CurrentFrameNumber++
  ├── FlushJudgeBuffer()     // 刷出判定日志缓冲
  └── WriteFrameMarker()     // 写入 FRAME 标记到日志
```

### 2. UI 挂载 Hook：`GameMainObject.Awake` (Postfix)
- 若 `EnableFileLog == true`，挂载 `TenoTimeDisplay` 组件显示墙上时钟

### 3. 判定日志 Hooks
按优先级顺序触发：

| Hook 目标 | 方法 | 覆盖音符类型 |
|-----------|------|-------------|
| `NoteBase.Judge` | Postfix | Tap, Break, ExTap, Star, BreakStar, ExStar |
| `HoldNote.JudgeHoldHead` | Postfix | Hold, ExHold, BreakHold, ExBreakHold |
| `TouchNoteB.Judge` | Postfix | TouchTap, TouchHold |
| `SlideRoot.SetJudgeObject` | Postfix | 建立 SlideJudge→SlideRoot 映射 |
| `SlideJudge.Initialize` | Postfix | Slide, BreakSlide, ExSlide, ExBreakSlide |

判定日志条目放入 `JudgeLogBuffer` 队列，帧末由 `FlushJudgeBuffer()` 统一写入文件。

---

## 六、文件日志系统

### 日志类型

| 类型 | 标记 | 写入线程 | 说明 |
|------|------|---------|------|
| HW | `[HW]` | 串口线程 | 每个通道每帧的原始值、基线、diff、deriv、状态 |
| FRAME | `[FRAME]` | 主线程 | 每 Unity 帧的帧号和时间同步标记 |
| JUDGE | `[JUDGE]` | 主线程 | 音符判定事件（类型、判定、时间差） |

### 日志文件管理
- 目录：`{CurrentDirectory}/TenoDX_Logs/Log_yyyyMMdd_HHmmss/`
- 文件名：`touch_log_part{N}.txt`
- 单文件最大 4MB，超出自动切分
- 线程安全写入（lock）

---

## 七、数据流向总结

```
硬件串口 ──70字节包──> SerialReaderThread ──34ch ushort[]──> ProcessFrame
                                                                  │
                                                    ┌─────────────┘
                                                    ▼
                                              校准/基线计算
                                                    │
                                                    ▼
                                         ButtonDetector × 34
                                         (A/C/BDE 算法判定)
                                                    │
                                          ┌─────────┴─────────┐
                                          ▼                   ▼
                                   currentTouchMask      WriteLog(HW)
                                          │
                                          ▼
                                 ProvideTouchStatus()
                                          │
                                          ▼
                                    游戏引擎输入
                                          │
                                          ▼
                              Harmony Hooks (判定日志)
                                          │
                                          ▼
                                   JudgeLogBuffer
                                          │
                                          ▼
                              FlushJudgeBuffer → WriteLog(JUDGE)
```
