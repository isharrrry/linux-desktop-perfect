# 上游 framework pack 取件缓存（不入库）

`40-build/pack/build-pack.mjs` 的输入是上游 `Microsoft.WindowsDesktop.App` 的两个包，放在本目录：

| 文件 | 来源 | 取件命令 |
|---|---|---|
| `microsoft.windowsdesktop.app.ref.<ver>.nupkg` | NuGet 包缓存（`~/.nuget/packages/microsoft.windowsdesktop.app.ref/<ver>/`） | `node 40-build/pack/build-pack.mjs` 在文件缺失时自动从缓存目录打包生成 |
| `microsoft.windowsdesktop.app.runtime.win-x64.<ver>.nupkg` | nuget.org flat container | 见下 |

## Runtime pack 取件

```bash
mkdir -p 40-build/lock/packs
curl -sSL -o 40-build/lock/packs/microsoft.windowsdesktop.app.runtime.win-x64.10.0.11.nupkg \
  https://api.nuget.org/v3-flatcontainer/microsoft.windowsdesktop.app.runtime.win-x64/10.0.11/microsoft.windowsdesktop.app.runtime.win-x64.10.0.11.nupkg
```

**为什么取 `win-x64`**：上游只发 `win-*` RID（IF-023）。托管层是 IL，去掉 Windows 原生件后与平台无关；Linux 落点由 `runtimes/linux-x64/native/` 的自产 `.so` 提供，组装时由 `build-pack.mjs` 改布局。

## 纪律

1. 本目录的 `.nupkg` 不入版本控制（`.gitignore` 已排除）：取件命令可复算，且单件 39 MB。
2. `build-pack.mjs` 不联网；缺件时按 `LDP-E-PACK-205` 报错退出，不静默跳过。
3. 组装后清掉 NuGet 全局缓存里的同版目录（`~/.nuget/packages/microsoft.windowsdesktop.app.ref/<ver>` 等），否则 `restore` 会命中缓存里的上游件而不是自产件（IF-023 同名同版的必要代价）。

## 离线桩包

`offline-feed/` 是另一件事（D-002：dnceng 私有源不可达时的构建期空桩包），由 `make-stub-feed.mjs` 生成，与本目录无关。
