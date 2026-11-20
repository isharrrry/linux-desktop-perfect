# PM-0112 层 alpha 本地化 · 仓库内补丁与重建配方

本目录让 `e453e71495e4ca94bc9b428bcb7ba4666e4e7a3b5c2a20ae44c4cdd0460c1ae8`
可以**从仓库一键重建**，不再只依赖 `deploy-freeze/` 里的冻件包。

## 文件

| 文件 | 作用 |
|---|---|
| `PM-0112-local-layer-alpha.patch` | 唯一改动，11 行，sha256 `4f626b12cf7be020100591b26c313dd3588a1f95dc63a884d2d1cca903097762` |
| `apply-and-build.sh` | 校验基线 → 打补丁 → 校验改动后 sha → 调构建入口 → 报告产物 sha → 逆回 |

## 唯一改动（`40-build/milcore/Rendering/SkiaRenderBackend.cs:275`）

```
-            double layerAlpha = opacity * maskAlpha;
+            double layerAlpha = Math.Clamp(v.Opacity, 0.0, 1.0) * maskAlpha;
```

累计 `opacity`（`:226`）不动，只保留三处用途：`<=0` 整树剪枝、`>1` 钳制、`:308` 传子节点。
契约见架构裁决件 `PM-0112-层alpha本地化-修法裁决-20261120.md`（冻结 C1–C7）。

## 身份链

```
源件  1c1bc80b7baf791edc7801228f1cf2ca5866f6c1c2e6d50230648987313c310f  (822 行)
  +   4f626b12cf7be020100591b26c313dd3588a1f95dc63a884d2d1cca903097762  (11 行 patch)
= 源件 3681da3f088f417246985c6d51a99954469bb784c1257127efb46c7685ce0240  (822 行)
  → NativeAOT 构建
= 件   e453e71495e4ca94bc9b428bcb7ba4666e4e7a3b5c2a20ae44c4cdd0460c1ae8  (5,156,304 B)
```

## 用法

```bash
cd <repo-root>
bash 40-build/milcore/patches/apply-and-build.sh
```

脚本自检顺序（任一步失败即停，不产出件）：

1. 基线 sha 必须是 `1c1bc80b…`（**不是 HEAD 的 `ec11937e…`**，工作区本来就带 11 条已跟踪改动）
2. `patch -p1` 后 sha 必须是 `3681da3f…`，且行数仍 822、`git diff --stat` 只有 1 行
3. 调 `node 40-build/milcore-bridge/build-bridge.mjs`
4. 报告产物 sha，期望 `e453e714…`
5. **逆回**用途：`git apply -R` 或 `patch -R -p1`；禁止 `git checkout --` 该文件

## 注意

- 本目录只对本事项加文件，**不改** `SkiaRenderBackend.cs` 本身；工作区保持 `1c1bc80b…` clean 基线。
- 构建入口与产物路径以 `build-bridge.mjs` 为准；脚本用 `find` 定位产物，若目录结构变动请以入口输出为准。
- 与 v3 trace 件是**两回事**：`e453e714…` 不含 `[OPAC]` 钩子；合并件 `5a0b5396…` 才含。
