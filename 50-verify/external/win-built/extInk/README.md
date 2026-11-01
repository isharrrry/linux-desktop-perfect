# extInk —— 纯 Ink 样本与 ISF 替代路径验证

## 结论速览

| 项 | 结果 |
|---|---|
| **纯 Ink 样本（Windows 编译）** | ✅ 编译成功（0 错 0 警）|
| **在 Linux 上运行** | ❌ **不可用** —— 启动后原生 SIGSEGV |
| **ISF 序列化（数据层）** | ✅ **完全可用** —— 往返顶点数一致、**幂等（字节完全一致）** |

**⇒ Ink 的「数据层」通、「渲染/输入层」断。**

## 样本

### `PureInk/`（`ExtInk_Pure.dll` 7168 B）
`Window "ExtInk Pure [EXT-41]"` 900×700 + `InkCanvas`(Name="墨迹", Height=400, Background=AliceBlue) +
`TextBlock`(`strokes=N`，订阅 `StrokesChanged`) + `Button`「清空」。

**Linux 运行读数**：
- `windowFound=true`（窗口建出来了）
- `exited={"code":null,"sig":"SIGSEGV"}` —— **崩了**
- `shot-before.png` = **328 字节**（空白）
- **UIA 节点 0 个**（`LdpA11y` 抓不到树，因为进程已死）
- 画一笔前后像素差 = **0**（画面没变，因为已经空了）

**日志**（`run.log`，4 行）：`[READY] ExtInk Pure started` → 字体回退两条 → 进程消失，**无托管异常**。
**带 `WPF_LINUX_KEY_DIAG=1`**（`diag.log`，46 行）：崩溃发生在 `[PAINT] BeginPaint hwnd=0x2400005 (第 6 次)` 之后。

### `PureInk2/`（`ExtInk_Pure2.dll`）—— 对照实验
**去掉** `StrokesChanged` 订阅、**去掉** `Background`、**去掉**按钮，只留裸 `InkCanvas` + TextBlock。

**Linux 运行读数**：
- 进程存活 9 秒后仍 **core dump**（`timeout: the monitored command dumped core`）
- 截图 **229 字节**（几乎全黑）

**⇒ 去掉事件订阅和背景后仍然崩 ⇒ 不是应用层代码的问题，是 `InkCanvas` 控件本身。**

### `IsfTest/`（`IsfTest.dll` 6656 B）—— 替代验证路径
用代码构造 `StrokeCollection`（5 个 `StylusPoint`），`Save` 到 ISF 再 `Load` 回来。

**两次实测**：

**① 往返（顶点数）**
```
[SAVE] strokes=1 points=5
[SAVE] ok bytes=46
[LOAD] strokes=1
[LOAD] points=5
[VERIFY] count_match=True
[LOAD] first=(10.015748031496063, 10.015748031496063)   ← 原值 (10,10)
[LOAD] last=(130.01574803149606, 120)                    ← 原值 (130,120)
[VERIFY] first_match=False   ← 有 ~0.0157 的舍入误差
```

**② 幂等性（关键）**
```
[R1] bytes=45 points=5
[R2] bytes=45 points=5
[IDEMPOTENT] byte_identical=True (len 45 vs 45)   ← **两次保存字节完全一致**
[VERIFY] points_roundtrip=True
```

**⇒ `StrokeCollection.Save`/`Load` 在 Linux 上**完全可用**，且**幂等**。**

## 原生崩溃取证（gdb）

```
Thread 1 "dotnet" received signal SIGSEGV, Segmentation fault.
0x00007fff7a9a56e7 in ?? ()
#0  0x00007fff7a9a56e7 in ?? ()      ← JIT 代码区
#1  0x0000555555652c88 in ?? ()
#2  0x0000000000000000 in ?? ()      ← **经 null 函数指针调用**
```

**与 K-259 里 `ExtB_InkMedia_InkOnly` 的崩溃模式**完全一致**（`#2=0x0`，栈帧链断裂）。**

## 这意味着什么

| 层 | 状态 | 证据 |
|---|---|---|
| **ISF 序列化/反序列化** | ✅ **通** | 往返顶点数一致 + 字节幂等 |
| **InkCanvas 渲染** | ❌ **断** | 截图 229/328 字节全黑，两种样本都崩 |
| **笔输入** | ❌ **断** | 崩溃前就拿不到可用的画笔 |

**⇒ 想用 Ink，只能走「离线处理 ISF」这条路**：在 Windows 上（或任何能跑 `StrokeCollection` 的地方）把笔画存成 ISF，
在 Linux 侧加载/分析/转成其它格式。**实时手写不可用。**

## 未定位的部分（如实）

**崩溃调用的是哪个 null 函数指针，我没查到。**
`#2=0x0` 说明栈帧链在第 3 帧就断了，JIT 代码没有符号；`ptrace_scope` 限制下只能跟踪自己的子进程（用了 `gdb` 直接启动）。
**候选**（未验证）：`PenImc`/`WISP` 的 COM 槽位回调在本仓是返回 0 的安全桩（K-198 那一层），`InkCanvas` 初始化时会调用它们。
