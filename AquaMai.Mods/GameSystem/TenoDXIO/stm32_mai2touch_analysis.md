# STM32 (mai2touch_app.c) ↔ C# (TenoIO) 通讯分析报告

## 一、系统拓扑

```
┌──────────────┐    CDC (USB Serial)    ┌──────────────┐    I2C     ┌──────────────┐
│   C# TenoIO  │ ◄──── 70 bytes ────► │  STM32H503   │ ◄───────► │  PSoC × 2    │
│   (Windows)  │    @ 60Hz / 115200    │  (mai2touch) │           │  0x08, 0x09  │
└──────────────┘                       └──────────────┘           └──────────────┘
                                             │
                                    I2C 每设备 35 bytes:
                                    Status(1) + 17ch × 2bytes(34)
```

- **2 个 PSoC 触摸控制器**：地址 0x08、0x09，各管理 17 个物理通道（合计 34 通道）
- **STM32 角色**：I2C→CDC 数据转发 + 配置下发 + 校准状态管理
- **通讯介质**：TinyUSB CDC (USB虚拟串口)，C# 端通过 `SerialPort(COMPort, 115200)` 连接

---

## 二、CDC 数据包格式

### 2.1 上行帧：STM32 → PC（70 字节，@60Hz）

| 偏移 | 大小 | 字段 |
|------|------|------|
| 0 | 1 | **Status** — 运行状态码（见下方状态码表） |
| 1 ~ 34 | 34 | **Device 0 原始数据** — PSoC 0x08 的 17 通道 × 2 bytes/通道 |
| 35 ~ 68 | 34 | **Device 1 原始数据** — PSoC 0x09 的 17 通道 × 2 bytes/通道 |
| 69 | 1 | **Checksum** — 前 69 字节累加和的低 8 位 |

### 2.2 下行帧：PC → STM32（139 字节）

| 偏移 | 大小 | 字段 |
|------|------|------|
| 0 | 1 | **Header** — 固定 0xAA |
| 1 | 1 | **Command** — 固定 0x01 |
| 2 ~ 35 | 34 | **Res** — 34 通道分辨率参数 |
| 36 ~ 69 | 34 | **Mod** — 34 通道 Mod IDAC 参数 |
| 70 ~ 103 | 34 | **Sns** — 34 通道 Sense Div 参数 |
| 104 ~ 137 | 34 | **Div** — 34 通道 Mod Div 参数 |
| 138 | 1 | **Checksum** — 前 138 字节累加和的低 8 位 |

> **与 C# 端 `SerialThreadManager.SendHardwareConfig()` 完全对应**：C# 构造 139 字节帧（0xAA + 0x01 + 34×4 配置 + 校验），STM32 收到后按 17 通道拆分写入两个 PSoC。

---

## 三、内部状态机分析

```
                        ┌──────────────┐
                        │  INIT_WAIT   │  上电等待 PSoC 就绪
                        └──────┬───────┘
                               │ >=1 个 PSoC 回应 I2C
                               ▼
                        ┌──────────────────┐
               ┌───────│ WAIT_HOST_CONFIG  │  发送 0x01 请求配置
               │       └────────┬──────────┘
               │                │ PC 下发 139 字节配置帧 (校验通过)
               │                ▼
               │       ┌─────────────────────┐
               │       │ WRITE_CONFIG_TO_PSOC│  发送 0x02
               │       └────────┬────────────┘
               │                │ 写入完成
               │                ▼
               │       ┌─────────────────────┐
               │       │  WAIT_CALIBRATION   │  透传 PSoC 状态码 (0x11~0x15→0x02)
               │       └────────┬────────────┘
               │                │ 所有 PSoC 报告 0x02 (校准完成)
               │                ▼
               │       ┌─────────────────────┐
               │       │      RUNNING        │  60Hz 推送扫描数据
               │       └────────┬────────────┘
               │                │ PSoC 报告 0x00 (崩溃重启)
               └────────────────┘
```

### 状态详细说明

| 状态 | CDC Status | 行为 |
|------|-----------|------|
| **INIT_WAIT** | — | 轮询 I2C 检测 PSoC 在线（50ms 间隔），>=1 个存活即进入下一态 |
| **WAIT_HOST_CONFIG** | `0x01` | 60Hz 不断向上位机发送 0x01，表示"我需要配置"，C# 端 `SerialThreadManager` 收到后调用 `SendHardwareConfig()` |
| **WRITE_CONFIG_TO_PSOC** | `0x02` | 将 136 字节配置载荷拆分成 2×68 字节：<br>— Device 0 取通道 0~16：`Res[0..16] + Mod[0..16] + Sns[0..16] + Div[0..16]`<br>— Device 1 取通道 17~33<br>通过 I2C 写入 PSoC 的 EZI2C_OFFSET_CONFIG (86)，再写状态字节 0x01 触发校准 |
| **WAIT_CALIBRATION** | `0x11~0x15` (透传) | 每 100ms 轮询 PSoC 状态寄存器：<br>— `0x11~0x15`：校准进行中<br>— `0x02`：校准完成<br>— `0x00`：PSoC 崩溃 → 退回 WAIT_HOST_CONFIG |
| **RUNNING** | `0x00` | 交替读取两个 PSoC（8ms 间隔），打包 70 字节帧 @ 60Hz 上传。PSoC 状态 `0x00` 时自动退回 WAIT_HOST_CONFIG（自愈机制） |

---

## 四、通讯时序流程

### 4.1 完整上电→运行序列

```
时间 ────────────────────────────────────────────────────────►

STM32                            PC (C#)
  │                                │
  │  INIT_WAIT                     │
  │  ├─ 检测 PSoC 0x08 ✔          │
  │  └─ 进入 WAIT_HOST_CONFIG      │
  │                                │
  │  [CDC TX] Status=0x01 ────────►│ SerialReaderThread
  │  [CDC TX] Status=0x01 ────────►│   ├─ status == 0x01
  │  [CDC TX] Status=0x01 ────────►│   └─ SendHardwareConfig()
  │                                │
  │ ◄──────── [CDC RX] 139 bytes ─│ 0xAA, 0x01, 34×4 config, checksum
  │                                │
  │  WRITE_CONFIG_TO_PSOC          │
  │  ├─ Device 0: I2C 写 68B 配置  │
  │  ├─ Device 0: I2C 写 0x01 启动 │
  │  ├─ Device 1: I2C 写 68B 配置  │
  │  └─ Device 1: I2C 写 0x01 启动 │
  │                                │
  │  WAIT_CALIBRATION              │
  │  [CDC TX] Status=0x11 ────────►│ (透传 PSoC 校准进度)
  │  [CDC TX] Status=0x12 ────────►│
  │  [CDC TX] Status=0x15 ────────►│
  │  [CDC TX] Status=0x02 ────────►│
  │                                │
  │  RUNNING                       │
  │  [CDC TX] Status=0x00 + 34ch ─►│ ProcessFrame(channelsCache)
  │  [CDC TX] Status=0x00 + 34ch ─►│ ProcessFrame(channelsCache)
  │  ...每 5ms 一帧...              │
```

### 4.2 C# 端解析对应

```
CDC frame[0]  (status byte)
  │
  ├── 0x00 → SerialReaderThread 提取 34 通道 ushort 值
  │           → IIR 滤波 → TouchStateProcessor.ProcessFrame()
  │
  ├── 0x01 → SerialReaderThread.SendHardwareConfig()
  │           下发 139 字节配置帧
  │
  └── 其他 → 非 0x00 非 0x01 的状态码不被 C# 端处理
              (0x02/0x11~0x15 等等，C# 端忽略)
```

### 4.3 CRC 校验与数据对齐

**上行（STM32→PC）**：C# 端 `SerialReaderThread` 对每 70 字节窗口计算前 69 字节累加和的低 8 位，与第 70 字节比对。失败则步进 1 字节重新对齐（字节级滑动窗口）。

**下行（PC→STM32）**：STM32 端 `process_cdc_rx()` 用 `tud_cdc_n_peek()` 查找 0xAA 帧头，丢弃坏字节，对齐后读 139 字节完整帧，校验通过才写入 `host_config_payload`。

---

## 五、I2C 数据流细节

### 5.1 PSoC 内存映射

| I2C 地址 | 偏移 | 方向 | 说明 |
|----------|------|------|------|
| 0x08/0x09 | 0x00 (STATUS) | R/W | PSoC 状态字：0x00=崩溃, 0x01=启动校准, 0x02=校准完成, 0x11~0x15=校准进度 |
| 0x08/0x09 | 0x00~0x22 | R | 35 字节原始数据：Status(1) + 17ch × 2byte(34) |
| 0x08/0x09 | 0x56 (CONFIG) | W | 68 字节配置：17ch × 4 参数 (Res/Mod/Sns/Div) |

### 5.2 运行时 I2C 轮询策略

- **交替轮询**：每 8ms 切换一个 PSoC（两个 PSoC 交替读取）
- **中断驱动**：`HAL_I2C_Mem_Read_IT` + `MemRxCpltCallback` / `ErrorCallback`
- **写后延迟**：配置写入后 `HAL_Delay(5ms)` 给 PSoC 缓冲时间
- **校准轮询保护**：100ms 间隔防 DDOS（防止过快 I2C 查询卡死 PSoC）

---

## 六、关键设计要点

### 6.1 自愈机制
- RUNNING 状态下检测到任一 PSoC 报告 `0x00`（崩溃重启）→ 自动退回 `WAIT_HOST_CONFIG` 重新走校准流程
- `WAIT_CALIBRATION` 下检测到 PSoC 报告 `0x00` → 同样退回

### 6.2 流式解析（下行帧）
- 使用 `tud_cdc_n_peek()` 预查而非盲目读取，避免缓冲区溢出
- 以 0xAA 作为帧同步头，错位时每次丢弃 1 个坏字节逐步对齐
- 校验通过才触发状态切换，防止垃圾数据破坏配置

### 6.3 数据组装（上行帧）
- Device 0 的 35 字节 I2C 数据去掉 status 字节 → 34 字节有效载荷写入 `cdc_tx_frame[1..34]`
- Device 1 的 35 字节 I2C 数据去掉 status 字节 → 34 字节有效载荷写入 `cdc_tx_frame[35..68]`
- 最终 70 字节 = Status(1) + 34ch × 2byte(68) + Checksum(1)

### 6.4 配置拆包（下行→I2C）
```
上位机 136 字节排列:
  [0..33]   = 34ch Res
  [34..67]  = 34ch Mod
  [68..101] = 34ch Sns  
  [102..135]= 34ch Div

拆分为 PSoC 0 (通道 0~16):
  [0..16]   = Res[0..16]   (17字节)
  [17..33]  = Mod[0..16]   (17字节)
  [34..50]  = Sns[0..16]   (17字节)
  [51..67]  = Div[0..16]   (17字节)
  → 68 字节

PSoC 1 (通道 17~33):
  同理，offset=17
  → 68 字节
```

---

## 七、与 C# 端代码的精确对应

| STM32 端 | C# 端 | 说明 |
|----------|-------|------|
| `MAI2TOUCH_CDC_FRAME_LENGTH = 70` | `SerialReaderThread` 70 字节校验窗口 | 帧长度匹配 |
| `MAI2TOUCH_HOST_RX_FRAME_LENGTH = 139` | `SendHardwareConfig()` `frame[139]` | 配置帧长度匹配 |
| `MAI2TOUCH_CDC_PERIOD_MS = 5` | ~200Hz 数据率 | 5ms 周期 |
| CDC TX `status=0x01` | `status == 0x01` → `SendHardwareConfig()` | 请求配置触发 |
| CDC TX `status=0x00` | `status == 0x00` → 34ch 数据解析 | 正常数据帧 |
| `0xAA` 帧头 | `frame[0] = 0xAA` | 下行帧同步 |
| Checksum 累加取低 8 位 | `checksum += frame[i]; frame[138] = (byte)(checksum & 0xFF)` | 校验算法一致 |
