# 调试记录

## 2026-07-29: Mai2Touch 映射错误排查与修正

### 现象
STM32 发送 mai2touch 测试帧，测试器显示触发的区块是 **B5 + E3**，预期应为 **A1 + B5**。

### 排查过程

#### 1. 检查 mai2touch.c 代码
```c
touch_bits = (1ULL << 12) | (1ULL << 28);
```
bit12 和 bit28 被置位，代码本身没有语法错误。

#### 2. 检查 RX 解析器 bug
发现 `{` 帧头未被存入 packet，导致包长度 4 字节≠5 → dispatch 静默丢弃所有命令。已修正为 4 字节包 + `packet[2]` 查命令。

#### 3. 分析位映射关系
在测试器中 B5=bit12, E3=bit28。这正是代码中 `(1<<12) | (1<<28)` 对应的游戏内部编号。说明**测试器按游戏内部编号解读 bit 位**，而代码误用了 PhysicalToLogicalMap 的物理通道索引。

### 根因：两套映射体系混淆

| 映射体系 | 用途 | 编号规则 | A1 位置 | B5 位置 |
|---------|------|---------|--------|--------|
| **游戏内部编号** | Mai2Touch ASCII 协议 | A1~A8=0~7, B1~B8=8~15, ... | **bit 0** | **bit 12** |
| **PhysicalToLogicalMap** | Tenodata 70字节二进制协议 | 物理通道 0~33 按接线顺序 | 物理通道 12 = bit 12 | 物理通道 28 = bit 28 |

这两套映射是**完全独立**的：
- `PhysicalToLogicalMap[i]` 定义了物理通道 i 对应的逻辑区名（如 "A1"）
- 游戏内部编号直接对应 `TouchStateProcessor.logicalToMaskMap` 中的 `maskShift`
- Mai2Touch 协议诞生于原始 SEGA 硬件，使用的是游戏内部编号，从不知道 PhysicalToLogicalMap 的存在

### 修正

**Mai2Touch 协议文档** (`libs/Mai2Touch_Impl.md`):
- 第五节映射表改为游戏内部编号顺序
- 增加 A1 + B5 双触发示例帧: `28 01 00 04 00 00 00 00 29`
- 增加注释说明两套映射的区别

**STM32 代码** (`mai2touch.c`):
```c
// 修正前 (错误)
touch_bits = (1ULL << 12) | (1ULL << 28);  // PhysicalToLogicalMap 索引

// 修正后 (正确)
touch_bits = (1ULL << 0) | (1ULL << 12);   // 游戏内部编号: A1=bit0, B5=bit12
```

**C# 项目** (`HardwareConfig.cs`):
- PhysicalToLogicalMap 本身无变化，仅修正了顺序与 STM32 tenodata_config.c 保持一致
