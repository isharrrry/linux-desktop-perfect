---
name: 功能请求
about: 提出一个新能力或增强
title: '[Feature] '
labels: enhancement
---

## 场景

谁在什么情况下需要它，现在卡在哪一步。

## 期望行为

写可被外部观察到的行为（结构树、读数、返回码），不要只写「支持 X」。

## 影响的子系统

- [ ] SYS-1 wpf-port（托管适配 / PBT / applier）
- [ ] SYS-2 native-shim（Win32 / GDI / GDI+ 子集）
- [ ] SYS-3 milcore（DUCE → Skia）
- [ ] SYS-4 text-stack（字体 / shaping / 行布局）
- [ ] SYS-5 wic-dwrite（图像解码 / ICC）
- [ ] SYS-6 pack（Ref / Runtime pack + targets）
- [ ] SYS-7 winforms-compat
- [ ] SYS-8 service-adapters（打印 / 媒体 / Ink / UIA / IME / DRM）

## 验收判据

给出 ACC 编号；没有编号的请求按「待裁决」处理。编号在 [《需求与验收判据文档》](../../20-spec/需求与验收判据文档.md) 定义，子系统与接口边界见 [《概要设计》](../../30-design/概要设计.md)。

## 替代方案

考虑过的绕法，以及为什么不够。

## 其他

