# wpf-port · WPF 托管层的 Linux 移植链（SYS-1：MOD-02/03/04）

子系统定位见 [《03-软件设计说明》](../../30-design/设计文档包/03-软件设计说明.md) §1.1：`40-build/wpf-port/` 承载托管适配——**移植工程（Linux 工程）+ Linux 补丁 + shim**，产物即自产托管件。

## 一句话

`40-build/build.sh` 回答「上游 csproj 能不能在 Linux 上编」；本目录的 `build-port.mjs` 回答「自产件怎么编出来」。
后者的每个工程**显式列出**上游源文件（`$(UpstreamWpfRoot)` 指向派生树）+ 本目录的 `.Linux.cs` 补丁 + `shims/`，因此不受上游 csproj 里 `vcxproj`/Windows 工具链的阻塞。

## 布局（与移植件的相对路径约定强相关，勿随手改）

```text
40-build/wpf-port/
├── BuildHygiene.props                     # 产物目录排除（D-R8）
├── Directory.Packages.props               # 关掉上层 40-build 的中央包管理（NU1008）
├── build-port.mjs                         # 构建入口（依赖序 + 两轮破环 + --map）
└── src/
    ├── Linux/build/                       # 移植脚手架（csproj 用 ../../../ 从 <asm> 目录算过来）
    │   ├── Directory.Upstream.props       # WpfLinuxRoot / UpstreamWpfRoot / 门面引用遮蔽
    │   ├── SelfBuiltConfig.props          # 自产件用哪个配置（唯一声明处）
    │   ├── WpfMarkupCompile.Linux.targets # XAML 编译链校验（PBT 缺失即报错）
    │   ├── excludes/                      # 不参与移植的上游源文件清单
    │   ├── keys/WcpPublicKey.snk          # **公钥**（PublicSign；不是私钥）
    │   └── MilBridge/src/MilBridge.Resolver/  # DllImport resolver（P/Invoke 目标名 → .so）
    └── Microsoft.DotNet.Wpf.Linux/src/
        └── <19 个 Linux 工程>/            # PresentationCore、PresentationFramework、主题、UIA、DWF、Provider…
```

## 构建

```bash
node 40-build/appliers/apply.mjs                 # 先有派生树（UpstreamWpfRoot 指向它）
node 40-build/wpf-port/build-port.mjs --list     # 看依赖序
node 40-build/wpf-port/build-port.mjs --map      # 全链构建 + 产物映射到派生树 artifacts/bin
node 40-build/wpf-port/build-port.mjs --only PresentationCore   # 单件
```

**为什么是两轮**：`PresentationFramework ⇄ System.Printing` 是循环引用（各自 `HintPath` 指向对方的 stub 产物），干净树上第一轮必有一侧红；第二轮用第一轮落地的 stub 收敛。实测两轮必收敛。

**为什么 `--map`**：`50-verify/build/identity.mjs`（ACC-004）与 `40-build/pack/build-pack.mjs`（MOD-51/52）都按派生树 `artifacts/bin/<asm>/Release/net10.0/` 取件；映射把自产件落到该位置，不改派生树源码。

## 现状（2026-10-30）

| 项 | 读数 |
|---|---|
| 构建成功率 | **18/18**（读数 `build-report.json`，生成物不入库） |
| 自产件身份 | `AssemblyVersion=10.0.0.0`、`FileVersion=10.0.1126.37416`，与 [#pack 文件清单](../pack/FrameworkList.xml) 一致（**ACC-004**） |
| WPF Profile 覆盖 | **13/21**，缺 5 个主题件 + `System.Windows.Controls.Ribbon` + `UIAutomationClient`/`UIAutomationClientSideProviders` |

## 纪律

1. **移植件可改，但必须同跑**：改任何 `*.Linux.cs` / `shims/*.cs` / `*.Linux.csproj` 后跑一次 `build-port.mjs --map`，并重跑 ACC-004。
2. **身份只改一处**：[shims/LinuxAssemblyIdentity.cs](src/Microsoft.DotNet.Wpf.Linux/src/shims/LinuxAssemblyIdentity.cs) 是自产件身份的唯一声明处；它必须 **> 4.0.0.0**（netCore 的空门面）且 **== 清单值**（10.0.0.0）。
3. **公钥不是私钥**：`keys/WcpPublicKey.snk` 是公开签名用的公钥，可入库；任何私钥文件禁止入库。
4. **降级要登记**：MSFT 公钥（`b77a5c561934e089`）不可得 → **D-003**；必抛桩件自产替身身份不同 → **D-004**（[《已知坑与降级清单》](../../70-operate/已知坑清单.md)）。
