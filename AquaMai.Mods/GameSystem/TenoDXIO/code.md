# A 区大面积自电容触摸算法说明

> 本报告以 C# 代码中的默认配置为准。
>
> 本报告不采用上一轮临时调参建议，不使用“灵敏档”“平衡档”等修改后的配置。
>
> 以下默认值对应代码中的 A 区状态机参数：
>
> ```csharp
> AEdgeOn = 280;
> ALargeOn = 850;
> AFastRiseDeriv = 70;
> AEdgeMinDeriv = 1;
>
> AShortTapPeak = 350;
> APendingSettleDeriv = 0;
> AFastPendingCancel = 160;
>
> ACleanRelease = 120;
> AReleasePeakRatio = 0.62f;
> AReleaseMinDrop = 130;
> AReleaseDropRatio = 0.20f;
> AReleaseDeriv = -4;
>
> ARepressRise = 110;
> ARepressDeriv = 18;
> ARepressSlowRise = 240;
> ARepressSignalMin = 230;
>
> ABaselineTrackRange = 120;
> ABaselineQuietDeriv = 6;
> ABaselineAlpha = 0.02f;
> ABaselineMaxStep = 0.5f;
> ```

---

# 1. 算法目标

A 区是大面积自电容区域，与普通小按键不同：

- 触摸信号幅度大；
- 单指边缘触摸和双指大面积触摸的最终计数差异明显；
- 大面积按下会快速穿过边缘触发阈值；
- 手指平移、另一只手划过、手部抖动会造成信号大幅波动；
- 快速连击时，信号可能没有完全回到启动 Raw。

因此 A 区不能只使用：

```text
diff > 固定阈值
```

也不能只使用：

```text
deriv < 负值就释放
```

当前算法使用以下几个部分：

```text
1. 动态基线
2. 普通边缘触摸检测
3. 快速上升候选状态
4. 大面积触摸确认
5. 按下期间峰值和比例释放
6. 释放后的谷底追踪
7. 快速重按和慢速重按
8. 候选状态自动取消
```

所有判断都是逐次采样完成，不需要等待未来数据。

---

# 2. 基本数据

## 2.1 启动基线差值

原始差值：

```text
diff = current_val - setup_raw
```

例如：

```text
setup_raw = 2619.5
current_val = 3000
diff = 380.5
```

`diff` 适合：

- 输出日志；
- 观察相对启动状态的变化；
- 观察实际硬件波形。

但算法内部不完全依赖 `diff`，因为启动后的环境可能发生漂移。

---

## 2.2 动态信号

算法内部维护：

```text
baseline = 动态基线
signal = current_val - baseline
```

动态基线开始时通常为：

```text
baseline = setup_raw
```

如果静置 Raw 从 `2620` 慢慢漂移到 `2630`，基线可以缓慢追踪：

```text
baseline ≈ 2630
signal ≈ 0
```

这样可以减少长期环境变化造成的误触。

---

## 2.3 当前导数

```text
deriv = current_val - previous_raw
```

含义：

```text
deriv > 0：
    当前信号正在上升。

deriv < 0：
    当前信号正在下降。

deriv ≈ 0：
    当前信号接近稳定或变化很小。
```

已知静置导数一般为：

```text
-5～+5
```

因此，较大的正值通常代表按下，较大的负值通常代表抬手、移出或电容快速下降。

---

# 3. A 区状态变量

每一个物理通道必须独立维护自己的状态。

```csharp
private bool is_pressed;

// 动态基线
private double a_baseline;
private bool a_baseline_initialized;

// 当前按下周期中的最大signal
private double a_peak;

// 释放后等待下一次重按
private bool a_after_release;

// 释放后记录到的最低Raw
private double a_valley_raw;
private bool a_valley_valid;

// 快速上升候选状态
private bool a_fast_pending;

// 快速候选期间的最大signal
private double a_pending_peak;
```

各状态的作用：

```text
is_pressed：
    当前是否向游戏输出按下。

a_baseline：
    动态静置基线。

a_peak：
    当前这次按下达到过的最大signal。
    释放时用它计算相对下降比例。

a_after_release：
    表示刚刚发生过有效释放。
    此状态用于识别信号尚未回到启动基线时的快速重按。

a_valley_raw：
    释放后的最低Raw。
    快速重按使用“从谷底上升了多少”，而不是使用固定绝对阈值。

a_fast_pending：
    快速经过边缘阈值，但还不能判断是边缘还是大面积。
    进入该状态后暂不立即触发。

a_pending_peak：
    快速候选期间观察到的峰值。
    如果最终没有达到大面积阈值，但峰值足够可信，
    可以作为快速边缘触摸确认。
```

---

# 4. 状态机总览

```text
未按下
  │
 ├─ signal达到边缘线，deriv较慢
 │       └─ 普通边缘触发
 │
 ├─ signal达到边缘线，deriv很快
  │       └─ fast_pending
  │              ├─ signal达到大面积线
  │              │       └─ 大面积触发
  │              │
  │              ├─ signal上升停止，候选峰值足够
  │              │       └─ 快速边缘触发
  │              │
 │              └─ 信号下降且峰值不足
  │                      └─ 取消候选
  │
已按下
 │
  ├─ signal非常接近基线
 │       └─ 彻底释放
  │
 ├─ signal相对峰值下降足够，且deriv为负
 │       └─ 手势释放
  │
  └─ 其他情况
          └─ 保持按下

释放后
  │
 ├─ 继续下降
  │       └─ 刷新valley_raw
  │
  ├─ 从谷底快速上升
 │       └─ 快速重按
 │
 ├─ 从谷底慢速累计上升
  │       └─ 慢速重按
 │
  └─ 回到静置区域
          └─ 结束after_release状态
```

---

# 5. 普通边缘触摸算法

普通边缘触摸的判断条件：

```text
signal >= AEdgeOn
deriv >= AEdgeMinDeriv
deriv < AFastRiseDeriv
```

代码默认值：

```text
AEdgeOn = 280
AEdgeMinDeriv = 1
AFastRiseDeriv = 70
```

代入后：

```text
signal >= 280
deriv >= 1
deriv < 70
```

含义：

```text
signal达到边缘触摸强度；
当前Raw仍然有轻微上升；
但上升速度没有快到疑似大面积按下。
```

例如：

```text
signal = 300
deriv = 35
```

满足：

```text
300 >= 280
35 >= 1
35 < 70
```

因此立即触发。

---

## 5.1 AEdgeOn

配置：

```csharp
public static int AEdgeOn = 280;
```

判断：

```text
signal >= AEdgeOn
```

### 降低后的微观影响

例如从：

```text
280 → 260
```

只需要更小的 signal 就能进入边缘判定。

### 降低后的宏观影响

优点：

```text
- 边缘滑入更灵敏；
- 浅边缘触摸更容易成功；
- 边缘触发更早。
```

风险：

```text
- 悬空靠近的信号更容易跨线；
- 长期环境漂移的安全余量变小；
- 低强度手部动作可能进入触摸检测。
```

### 提高后的影响

```text
- 降低悬空误触；
- 提高抗环境变化能力；
- 边缘滑入需要更强信号；
- 浅边缘触摸可能漏判。
```

### 建议范围

```text
防误触优先：300～320
代码默认值：280
更灵敏：260～270
不建议：低于240
```

---

## 5.2 AEdgeMinDeriv

配置：

```csharp
public static int AEdgeMinDeriv = 1;
```

判断：

```text
deriv >= AEdgeMinDeriv
```

该条件用于防止下降沿被误判为新按下。

### 降低到 0 的影响

如果设置：

```text
AEdgeMinDeriv = 0
```

则：

```text
signal已经超过边缘线
deriv=0
```

时也可以触发。

优点：

```text
- 慢速滑入更容易触发；
- 跨过阈值后短暂稳定时仍可触发。
```

风险：

```text
- 稳定悬空信号超过边缘线时可能触发；
- 释放过程中的稳定残余信号可能重新进入普通触发。
```

### 设置为负数的影响

不建议：

```text
AEdgeMinDeriv < 0
```

因为下降过程也可能满足条件：

```text
signal >= AEdgeOn
deriv = -5
```

可能被当作新按下。

### 推荐

```text
默认：1
更保守：2～3
仅测试：0
禁止：负数
```

---

## 5.3 AFastRiseDeriv

配置：

```csharp
public static int AFastRiseDeriv = 70;
```

判断：

```text
deriv >= AFastRiseDeriv
```

时，不走普通边缘直接触发，而是进入快速候选。

### 降低后的影响

例如：

```text
70 → 50
```

更多上升动作会进入快速候选。

微观效果：

```text
signal刚超过AEdgeOn时不会立即输出；
等待后续数据判断。
```

宏观效果：

```text
- 大面积提前触发风险降低；
- 快速边缘触摸可能延迟到转折点确认；
- 触摸响应会更依赖AShortTapPeak。
```

### 提高后的影响

例如：

```text
70 → 120
```

很多中速上涨会绕过候选，直接在 `AEdgeOn` 触发。

宏观效果：

```text
- 普通边缘响应更直接；
- 大面积按下可能在经过280时提前触发；
- 防提前触发能力下降。
```

### 推荐范围

```text
严格防提前：50～65
代码默认值：70
边缘即时性优先：80～100
不建议：高于120
```

---

# 6. 快速上升候选状态

当信号快速经过边缘阈值时：

```text
signal >= AEdgeOn
deriv >= AFastRiseDeriv
```

算法执行：

```text
a_fast_pending = true;
a_pending_peak = signal;
on = false;
```

此时不会立刻输出按下。

原因是当前信号可能属于：

```text
快速单指边缘；
快速双指按压；
手掌快速接近；
另一只手快速划过。
```

必须继续观察当前流式数据。

---

## 6.1 AShortTapPeak

配置：

```csharp
public static int AShortTapPeak = 350;
```

判断：

```text
a_pending_peak >= AShortTapPeak
```

该配置决定：

```text
快速候选最终没有达到大面积阈值时，
它至少要达到多高才可以作为快速边缘触摸确认。
```

### 降低后的影响

例如：

```text
350 → 320
```

优点：

```text
- 快速擦边更容易成功；
- 快速浅边缘操作漏判减少。
```

风险：

```text
- 悬空快速晃动的峰值更容易被接受；
- 快速接近但未真正触碰的信号可能被确认。
```

### 提高后的影响

例如：

```text
350 → 400
```

优点：

```text
- 短边缘触发更加可信；
- 虚空快速晃动更难误触。
```

风险：

```text
- 快速浅边缘触摸可能漏判；
- 需要更强的边缘动作。
```

### 推荐范围

```text
灵敏：320～340
代码默认值：350
保守：380～420
```

必须保持：

```text
AShortTapPeak > AEdgeOn
```

---

## 6.2 APendingSettleDeriv

配置：

```csharp
public static int APendingSettleDeriv = 0;
```

判断：

```text
deriv <= APendingSettleDeriv
```

并且：

```text
a_pending_peak >= AShortTapPeak
```

才允许快速边缘候选补触发。

### 设置为 0 的思路

表示：

```text
必须等到上升停止或已经开始下降，
才能确认快速边缘。
```

例如：

```text
deriv = 20：
    仍可能继续上升，不确认。

deriv = 3：
    仍然等待。

deriv = 0：
    可以确认。

deriv = -5：
    可以确认。
```

这是防止大面积提前触发的关键配置之一。

### 提高后的影响

例如：

```text
0 → 5
```

优点：

```text
- 快速边缘响应更早；
- 某些硬件峰顶没有明显负导数时更容易触发。
```

风险：

```text
- 大面积触摸仍在上涨但deriv变小时，
  可能被过早当成短边缘触摸。
```

### 推荐范围

```text
防提前优先：0
折中：1～2
不建议：大于5
```

---

## 6.3 AFastPendingCancel

配置：

```csharp
public static int AFastPendingCancel = 160;
```

候选取消条件之一：

```text
signal < AFastPendingCancel
```

### 降低后的影响

```text
- 候选状态保持更久；
- 较弱的快速边缘信号有更多机会完成确认；
- 候选取消延迟；
- 弱虚空信号可能在状态中停留更久。
```

### 提高后的影响

```text
- 虚空弱候选更快清理；
- 下降沿恢复更快；
- 很浅的快速边缘操作可能尚未确认就被取消。
```

### 推荐范围

```text
代码默认值：160
保守：180～220
灵敏：120～150
```

必须低于：

```text
AEdgeOn
```

---

# 7. 大面积触摸确认

配置：

```csharp
public static int ALargeOn = 850;
```

快速候选中，如果：

```text
signal >= ALargeOn
deriv >= 0
```

则直接触发。

示例：

```text
signal=300, deriv=100
signal=550, deriv=250
signal=780, deriv=230
signal=900, deriv=170
```

过程：

```text
300：
    快速候选，不触发。

550：
    快速候选，不触发。

780：
    仍未达到大面积阈值，不触发。

900：
    达到850，确认按下。
```

---

## 7.1 ALargeOn降低后的影响

```text
- 大面积操作更早触发；
- 经过边缘阈值后的等待时间减少；
- 大面积提前触发风险增加；
- 中等面积触摸可能被当成大面积。
```

## 7.2 ALargeOn提高后的影响

```text
- 大面积提前触发风险降低；
- 大面积按下响应更晚；
- 如果信号没有达到该值，则完全依赖快速短边缘补触发。
```

## 7.3 推荐范围

```text
快速响应优先：750～800
代码默认值：850
防提前优先：900～1000
```

不要使用 `ALargeOn` 解决普通边缘灵敏度问题。

---

# 8. 按下期间释放算法

## 8.1 峰值跟踪

按下期间：

```text
if (signal > a_peak)
    a_peak = signal;
```

峰值不会因为当前信号下降而降低。

例如：

```text
signal：
400 → 800 → 1200 → 900
```

峰值保持：

```text
a_peak = 1200
```

这样可以判断当前下降是否足够深。

---

## 8.2 AReleasePeakRatio

配置：

```csharp
public static float AReleasePeakRatio = 0.62f;
```

释放比例条件：

```text
signal <= a_peak × 0.62
```

示例一：

```text
peak=1200
signal=900

比例线=1200×0.62=744
900>744
```

不会释放。

示例二：

```text
peak=1400
signal=1200

比例线=1400×0.62=868
1200>868
```

不会释放。

示例三：

```text
peak=500
signal=300

比例线=500×0.62=310
300<=310
```

可以进入释放判断。

### 该参数的宏观含义

```text
比例越高：
    越容易提前释放；
    平移出更灵敏；
    按住防抖变弱。

比例越低：
    越需要深度下降；
    按住更稳定；
    平移释放更慢。
```

推荐：

```text
稳定：0.52～0.58
代码默认：0.62
释放优先：0.64～0.66
不建议：高于0.70
```

---

## 8.3 AReleaseMinDrop

配置：

```csharp
public static int AReleaseMinDrop = 130;
```

当前峰值至少下降：

```text
peak_drop = a_peak - signal
```

并且：

```text
peak_drop >= 130
```

### 微观影响

峰值为 `500` 时：

```text
signal必须低于或等于370左右，
才可能通过绝对下降量条件。
```

峰值为 `1400` 时：

```text
signal必须比峰值低至少130。
```

但实际还要同时满足比例条件。

### 降低后的宏观影响

```text
- 小幅边缘触摸更容易释放；
- 平移出区块更及时；
- 手指姿态变化更容易造成释放。
```

### 提高后的宏观影响

```text
- 按住期间更不容易因小下降断触；
- 边缘平移释放更慢；
- 低峰值触摸可能需要下降到很低。
```

推荐：

```text
灵敏：100～120
代码默认：130
稳定：150～180
```

---

## 8.4 AReleaseDropRatio

配置：

```csharp
public static float AReleaseDropRatio = 0.20f;
```

实际需要下降：

```text
required_drop =
    max(
        AReleaseMinDrop,
        a_peak × AReleaseDropRatio
    )
```

例如峰值为 `1400`：

```text
1400×0.20=280
required_drop=max(130,280)=280
```

例如峰值为 `500`：

```text
500×0.20=100
required_drop=max(130,100)=130
```

因此：

```text
低峰值使用固定最小下降量；
高峰值使用峰值比例下降量。
```

### 降低后的宏观影响

```text
- 高峰值按压可以更早释放；
- 大面积操作连击更快；
- 对大面积按住期间的下降抖动保护变弱。
```

### 提高后的宏观影响

```text
- 高峰值按压更难误释放；
- 另一只手移开造成的下降更不容易断触；
- 抬手释放可能变慢。
```

推荐：

```text
释放优先：0.15～0.18
代码默认：0.20
防断触：0.25～0.30
```

---

## 8.5 AReleaseDeriv

配置：

```csharp
public static int AReleaseDeriv = -4;
```

判断：

```text
deriv <= -4
```

该参数只代表：

```text
当前信号正在下降。
```

它不能单独触发释放，还必须同时满足：

```text
下降量条件
峰值比例条件
```

### 向0调整

```text
-4 → -3 → -2
```

适用于：

```text
手指平移出区块；
缓慢抬手；
信号连续小幅下降。
```

风险：

```text
缓慢姿态变化也可能满足下降趋势。
```

### 向负数调整

```text
-4 → -6 → -10
```

适用于：

```text
按住期间容易因为轻微下降误释放；
需要更明显的抬手动作。
```

风险：

```text
平移离开释放变慢；
慢速连击可能漏掉释放沿。
```

推荐范围：

```text
平移优先：-2～-3
代码默认：-4
稳定优先：-6～-10
不建议：0或正数
```

---

## 8.6 ACleanRelease

配置：

```csharp
public static int ACleanRelease = 120;
```

判断：

```text
signal <= 120
```

时立即释放。

它不要求：

```text
deriv为负
峰值下降比例满足
```

因为已经非常接近动态基线，可以直接认为触摸结束。

### 提高后的影响

```text
- 快速抬手释放更早；
- 快速连击更容易重新开始；
- 小幅边缘按压可能在信号下降到较高位置时断触。
```

### 降低后的影响

```text
- 按住更稳定；
- 必须接近基线才释放；
- 释放时间增加。
```

推荐：

```text
稳定：80～100
代码默认：120
快速释放：130～160
```

不要把它设置成 `300～500`，因为该条件不包含峰值比例保护。

---

# 9. 释放后快速连击

## 9.1 为什么需要谷底

快速连击可能出现：

```text
第一次按下：
signal=1200

释放过程：
signal=700
signal=450
signal=280

再次按下：
signal=400
signal=600
```

此时如果仍然使用：

```text
signal >= 280
```

则无法区分：

```text
释放残余；
重新按下；
另一只手造成的干扰。
```

因此记录：

```text
valley_raw = 释放后最低Raw
```

重新按下使用：

```text
rise_from_valley = currentRaw - valleyRaw
```

---

## 9.2 ARepressRise

配置：

```csharp
public static int ARepressRise = 110;
```

快速重按条件：

```text
rise_from_valley >= 110
```

### 降低后的影响

```text
- 连击更灵敏；
- 更早产生再次按下；
- 释放回弹误触增加。
```

### 提高后的影响

```text
- 需要更明确的再次按压；
- 防止自然回弹；
- 快速小幅连击可能漏判。
```

推荐：

```text
灵敏：90～100
代码默认：110
保守：130～160
```

---

## 9.3 ARepressDeriv

配置：

```csharp
public static int ARepressDeriv = 18;
```

快速重按条件：

```text
deriv >= 18
```

### 降低后的影响

```text
- 较慢的再次按下也可以进入快速重按；
- 连击更加灵敏；
- 回弹误触概率提高。
```

### 提高后的影响

```text
- 需要明显的上升沿；
- 回弹更难触发；
- 柔和连击可能漏判。
```

推荐：

```text
灵敏：12～15
代码默认：18
保守：25～35
```

---

## 9.4 ARepressSlowRise

配置：

```csharp
public static int ARepressSlowRise = 240;
```

慢速重按条件：

```text
rise_from_valley >= 240
deriv >= AEdgeMinDeriv
```

它用于处理：

```text
deriv没有达到快速重按阈值；
但相对谷底的累计上升已经足够明显。
```

### 降低后的影响

```text
- 慢速连击更容易识别；
- 释放后缓慢回弹更容易造成误触。
```

### 提高后的影响

```text
- 慢速重按需要更明显；
- 防止缓慢回弹；
- 轻柔连续操作可能漏判。
```

推荐：

```text
灵敏：210～230
代码默认：240
保守：280～320
```

必须满足：

```text
ARepressSlowRise > ARepressRise
```

---

## 9.5 ARepressSignalMin

配置：

```csharp
public static int ARepressSignalMin = 230;
```

重按必须同时满足：

```text
signal >= 230
```

该条件用于避免：

```text
信号仍然接近动态基线；
只是受到噪声或轻微回弹；
却被当成再次按下。
```

### 降低后的影响

```text
- 浅边缘连击更容易触发；
- 低位回弹误触增加；
- 与静置区域的距离变小。
```

### 提高后的影响

```text
- 回弹更难触发；
- 浅连击可能漏判；
- 中高强度连击影响较小。
```

推荐关系：

```text
ARepressSignalMin >= ABaselineTrackRange + 80
```

默认值：

```text
230 >= 120 + 80
```

保留了约 `30` 计数额外余量。

---

# 10. 动态基线算法

## 10.1 ABaselineTrackRange

配置：

```csharp
public static int ABaselineTrackRange = 120;
```

只有：

```text
abs(signal) <= 120
```

才允许基线追踪。

### 扩大后的影响

```text
- 环境漂移恢复更快；
- 释放残余更可能被吸收；
- 慢速触摸可能被吞掉；
- 大面积触摸后边缘失效风险增加。
```

### 缩小后的影响

```text
- 基线更加稳定；
- 真实触摸不容易被吸收；
- 温漂恢复较慢。
```

推荐：

```text
80～150
```

不建议超过 `180`。

---

## 10.2 ABaselineQuietDeriv

配置：

```csharp
public static int ABaselineQuietDeriv = 6;
```

只有：

```text
abs(deriv) <= 6
```

才认为当前变化安静。

### 提高后的影响

```text
- 更多数据可以更新基线；
- 漂移适应更快；
- 慢速触摸更容易被吸收。
```

### 降低后的影响

```text
- 基线更新更谨慎；
- 抗触摸能力更强；
- 传感器噪声稍大时基线恢复较慢。
```

推荐：

```text
4～8
```

已知静置通常为 `±5`，所以代码默认值 `6` 是保守折中。

---

## 10.3 ABaselineAlpha

配置：

```csharp
public static float ABaselineAlpha = 0.02f;
```

基线步长：

```text
baseline_step = signal × 0.02
```

例如：

```text
signal = 50
baseline_step = 1
```

但还会受到：

```text
ABaselineMaxStep = 0.5
```

限制，最终每次最多移动 `0.5`。

### 提高后的影响

```text
- 温漂跟踪更快；
- 慢速触摸信号更容易被削弱；
- 边缘滑入可能需要更长时间达到阈值。
```

### 降低后的影响

```text
- 真实触摸更不容易被吃掉；
- 环境变化恢复更慢；
- 静置偏置可能保留较久。
```

推荐：

```text
0.01～0.03
```

---

## 10.4 ABaselineMaxStep

配置：

```csharp
public static float ABaselineMaxStep = 0.5f;
```

即使根据比例计算出的步长较大，每次基线最多移动：

```text
+0.5
或
-0.5
```

### 提高后的影响

```text
- 基线快速适应环境漂移；
- 长时间慢速触摸可能被跟踪；
- 边缘信号增量被削弱。
```

### 降低后的影响

```text
- 基线更加稳定；
- 真实触摸保护更好；
- 温漂适应较慢。
```

推荐：

```text
0.3～0.8
```

代码默认值 `0.5` 适合先保证触摸信号不被快速吸收。

---

# 11. 外部配置文件修改规则

如果修改外部配置文件，应修改配置项对应的数值。

逻辑形式如下：

```text
A区 - 边缘触摸阈值 = 280
A区 - 大面积触摸阈值 = 850
A区 - 快速上升导数 = 70
A区 - 边缘最小上升导数 = 1

A区 - 快速短点击峰值 = 350
A区 - 快速候选稳定导数 = 0
A区 - 快速候选取消阈值 = 160

A区 - 彻底释放阈值 = 120
A区 - 峰值释放比例 = 0.62
A区 - 最小释放下降量 = 130
A区 - 最小释放下降比例 = 0.20
A区 - 释放导数 = -4

A区 - 快速重按上升量 = 110
A区 - 快速重按导数 = 18
A区 - 慢速重按上升量 = 240
A区 - 重按最低信号 = 230

A区 - 基线追踪范围 = 120
A区 - 基线静置导数 = 6
A区 - 基线追踪系数 = 0.02
A区 - 基线最大步长 = 0.5
```

实际文件可能使用：

```text
键=值
键:值
JSON
YAML
```

必须保持原文件格式。例如原文件是：

```ini
AEdgeOn=280
```

就只修改右侧数字：

```ini
AEdgeOn=270
```

不要把 C# 声明复制进外部配置文件。

---

# 12. 配置修改是否生效

需要注意配置加载优先级。

常见情况是：

```text
C#字段初始值
    ↓
外部配置文件读取
    ↓
外部配置覆盖代码默认值
```

因此，如果外部配置文件已经存在：

```text
即使修改了C#默认值，
旧配置文件仍可能继续使用旧值。
```

修改流程：

```text
1. 关闭游戏；
2. 找到实际加载的配置文件；
3. 修改A区对应配置项；
4. 保存文件；
5. 重新启动游戏；
6. 确认日志或配置界面显示新数值；
7. 再开始测试。
```

如果配置系统会自动重新生成配置：

```text
先备份配置文件；
删除或重命名旧配置；
启动程序生成新配置；
确认A区字段出现；
再进行修改。
```

---

# 13. 单通道配置覆盖

原有配置：

```csharp
public static string Override_A_Diff = "";
```

例如：

```text
A1:270,A2:280,A3:300
```

当前算法中该配置覆盖：

```text
AEdgeOn
```

不覆盖：

```text
ALargeOn
AReleasePeakRatio
ARepressRise
```

因此：

```text
A1:270
```

表示 A1 的普通边缘触摸线为 `270`，但快速大面积触摸仍然使用：

```text
ALargeOn = 850
```

推荐使用场景：

```text
大多数通道正常；
只有少数物理通道边缘较弱。
```

不建议用全局降低方式解决单个异常通道。

---

# 14. 推荐使用的代码默认值

以下是本算法报告的基准配置，不使用上一轮临时调整结果：

```csharp
public static int AEdgeOn = 280;
public static int ALargeOn = 850;
public static int AFastRiseDeriv = 70;
public static int AEdgeMinDeriv = 1;

public static int AShortTapPeak = 350;
public static int APendingSettleDeriv = 0;
public static int AFastPendingCancel = 160;

public static int ACleanRelease = 120;
public static float AReleasePeakRatio = 0.62f;
public static int AReleaseMinDrop = 130;
public static float AReleaseDropRatio = 0.20f;
public static int AReleaseDeriv = -4;

public static int ARepressRise = 110;
public static int ARepressDeriv = 18;
public static int ARepressSlowRise = 240;
public static int ARepressSignalMin = 230;

public static int ABaselineTrackRange = 120;
public static int ABaselineQuietDeriv = 6;
public static float ABaselineAlpha = 0.02f;
public static float ABaselineMaxStep = 0.5f;
```

这些值的整体思路是：

```text
AEdgeOn=280：
    给边缘触摸足够灵敏度。

ALargeOn=850：
    防止大面积触摸经过边缘线时提前触发。

AFastRiseDeriv=70：
    快速上涨先进入候选。

AShortTapPeak=350：
    快速边缘峰值达到可信范围后允许补触发。

APendingSettleDeriv=0：
    必须等上升停止或下降，避免提前确认。

AReleasePeakRatio=0.62：
    允许平移离开及时释放，
    但1200降到900仍不会断触。

AReleaseMinDrop=130：
    防止极小变化触发释放。

AReleaseDropRatio=0.20：
    高峰值触摸需要相对明显下降。

ARepressRise=110：
    支持快速连续抬按。

ARepressSignalMin=230：
    防止接近基线的噪声进入重按。

ABaselineTrackRange=120：
    只允许真正接近静置状态更新基线。

ABaselineMaxStep=0.5：
    防止基线追赶真实触摸。
```

---

# 15. 最终原则

```text
1. 边缘灵敏度优先调整AEdgeOn。
2. 快速边缘漏判优先调整AShortTapPeak。
3. 大面积提前触发优先降低AFastRiseDeriv或提高ALargeOn。
4. 不要用提高ALargeOn的方式解决普通边缘漏触。
5. 平移释放优先调整AReleaseDeriv和AReleasePeakRatio。
6. 按住断触优先降低AReleasePeakRatio或提高AReleaseMinDrop。
7. 快速连击必须先确保释放状态正常。
8. 重按误触优先提高ARepressRise和ARepressSignalMin。
9. 不要扩大ABaselineTrackRange来解决边缘灵敏度。
10. 不要在释放时把currentRaw写入a_baseline。
11. A区每个物理通道必须使用独立ButtonDetector实例。
12. 每轮只修改一组相关配置，避免无法判断问题来源。