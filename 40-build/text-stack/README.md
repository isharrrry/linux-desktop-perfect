# text-stack · `libdwlinux.so`（SYS-4：MOD-31/32/33/35）

子系统定位见 [《03-软件设计说明》](../../30-design/设计文档包/03-软件设计说明.md) §3.4：字体枚举、shaping、断行、CFF 轮廓的 Linux 落点，产物 `libdwlinux.so` + 托管行布局实现（ADR-007）。

## 现状（2026-10-30）

| 构件 | 状态 | 判据读数 |
|---|---|---|
| MOD-31 字体枚举、匹配与回退链（`FcFontList`/`FcFontMatch`/`FcFontSort`） | 已实现 | **ACC-013 PASS**（两类路径回退链前 10 项一致、三类样本无缺字）；`--fonts`/`--match`/`--fallback` |
| MOD-32 shaping（HarfBuzz `hb_shape`） | 已实现 | **ACC-014 PASS**（拉丁/天城文/CJK 三组，与 `hb-oracle.py` 逐项一致） |
| MOD-33 断行（ICU `ubrk_*`，UAX#14）与双向（`ubidi_*`） | 已实现 | **ACC-015 DEGRADED**：换行与双向双路径逐项一致；`FlowDocument`/分页宿主集成未做 |
| MOD-35 轮廓（`FT_Outline_Decompose`） | 已实现 | **ACC-016 PASS**（CFF/OTF 与 TrueType 两类，96 px 非空且包围盒不退化） |
| MOD-34 段落/PTS 转接 | 未实现 | 依赖 MOD-16 缺口表（R-002） |
| MOD-36 IME 桥 | 未实现 | 依赖 D-Bus 会话（S2 的 ACC-017） |

## 构建

```bash
make -C 40-build/text-stack headers   # 取构建期头文件到 ../lock/headers（需网络；不入库）
make -C 40-build/text-stack           # → bin/libdwlinux.so + bin/dwp-probe
make -C 40-build/text-stack test      # ABI 自检（编译期断言 + 运行期打印）
make -C 40-build/text-stack probe     # 五条冒烟读数
make -C 40-build/text-stack symbols   # 导出符号（TG-001 对拍输入）
```

**为什么 `headers` 是独立目标**：本机（Ubuntu 22.04）只装运行时库 `libharfbuzz.so.0` / `libfreetype.so.6` / `libfontconfig.so.1` / `libicui18n.so.70`，没有 `-dev` 包也没有 `pkg-config`。实现用「编译期头拿类型 + 运行期 `dlopen` 绑定」：头文件只用于类型与 `FT_Outline` 布局（不猜偏移），符号在 `dwp_init()` 里绑定，缺任何一个立即返回错误码（`LDP-E-TEXT-404`/`407`），不静默。

## ABI

`src/dwlinux.h` 是唯一声明处；`tests/text_abi.c` 用 `_Static_assert` 钉死窗口结构（`DwpPoint` 12 字节、`DwpMatch` = 256+1024+4）。错误码取 [《03-软件设计说明》](../../30-design/设计文档包/03-软件设计说明.md) §3.4 的既有编号（`LDP-E-TEXT-403~412`），不新造编号。

## 判据与已知缺口

- **双路径对拍**：ACC-014 比较本库与 [hb-oracle.py](../../50-verify/text/hb-oracle.py)（Python `ctypes` 直接调系统库，不共享代码）。
- **ACC-016 的口径**：实现即 `FT_Outline_Decompose`，故「与 FreeType 一致」由实现路径保证；独立第二路径（Python 侧读 `FT_Outline`）**尚未实现**，读数 `env.freetype_consistency` 已如实标注，不得读成「已独立对拍」。
- **`cluster` 单位**：`dwp_shape` 输出的 `clusters` 是 HarfBuzz 的 UTF-8 字节偏移；上游 `TextAnalyzer` 需要 UTF-16 索引，换算在托管侧做（MOD-32 契约，见 04 §2.3）。
