# WPF-on-Linux · M7b · libwpfwin32.so
#
# 用法：
#   make            → 构建 bin/libwpfwin32.so
#   make test       → 跑 ABI 静态断言自检（编译期就已经断言过，这里是再确认一遍）
#   make symbols    → 打印导出符号数（报告里的证据）
#   make clean
#
# 为什么不用 cmake：本 shim 是**单产物**（一个 .so、5 个 .c、1 个系统依赖 libX11），
# 引入 cmake 只会多一层生成物与一个构建系统依赖，收益为 0。gcc -shared -fPIC 足够。
#
# 为什么 -Wl,--no-undefined：宁可链接期就报「某个 X11 函数找不到」，
# 也不要在运行期第一次 GetMessageW 时炸。动态装载 libwpfwin32.so 的应用
# 没有任何机会补救。

CC      ?= gcc
CSTD    ?= -std=gnu11
OPT     ?= -O2
WARN     = -Wall -Wextra -Wno-unused-parameter -Wno-cast-function-type
# 排版模型开关（2026-10-31）：
#   WPF_PTS_FSP_PL_M1/M2 默认 0，只有显式 -D 才编进去。早先靠 `make CFLAGS="…"` 手传，
#   于是每次默认 `make` 都会把 M1/M2 丢掉（产物 597872 vs 602208 字节，功能静默降级）。
#   这里固化成 DEFS，`make` 与 `make CFLAGS=…` 都不会丢。
DEFS    ?= -DWPF_PTS_FSP_PL_M1=1 -DWPF_PTS_FSP_PL_M2=1
CFLAGS  ?= $(CSTD) $(OPT) $(WARN) -fPIC -fvisibility=default $(DEFS)
CFLAGS   += $(shell pkg-config --cflags dbus-1 2>/dev/null)
LDLIBS   = -lX11 -ldl -lpthread $(shell pkg-config --libs dbus-1 2>/dev/null)

SRCDIR   = src
OBJDIR   = obj
BINDIR   = bin
TARGET   = $(BINDIR)/libwpfwin32.so
# classification（MILGetClassificationTables）+ Unicode 分类表（生成物）：
#   托管侧 `PresentationCore/MS/internal/Classification` 的静态构造直接调它，是任何文本测量的必经之路。
#   两个 .c 缺一份，PresentationNative 面就少一组入口（实测缺 classification 时 TypeEntryPointNotFound
#   → 布局在 render pass 里抛 → 退出码 134）。
SRCS     = $(SRCDIR)/win32_core.c $(SRCDIR)/win32_msg.c $(SRCDIR)/win32_x11.c \
           $(SRCDIR)/win32_imm.c \
           $(SRCDIR)/win32_misc.c $(SRCDIR)/win32_exports.c \
           $(SRCDIR)/win32_classification.c $(SRCDIR)/win32_unicode_tables.c \
           $(SRCDIR)/win32_oem.c $(SRCDIR)/win32_gdiplus.c \
	$(SRCDIR)/win32_gdi_text.c $(SRCDIR)/win32_pts.c \
           $(SRCDIR)/win32_clipboard.c \
	$(SRCDIR)/win32_winspool.c \
	$(SRCDIR)/win32_ibus.c
OBJS     = $(patsubst $(SRCDIR)/%.c,$(OBJDIR)/%.o,$(SRCS))
DEPS     = $(SRCDIR)/win32_abi.h $(SRCDIR)/win32_internal.h

.PHONY: all clean test symbols abi-check

all: $(TARGET)

$(TARGET): $(OBJS)
	@mkdir -p $(BINDIR)
	$(CC) -shared -Wl,--no-undefined -Wl,-soname,libwpfwin32.so -o $@ $(OBJS) $(LDLIBS)
	@echo "== 构建完成：$@ ($$(stat -c%s $@ 2>/dev/null || echo 0) 字节)"

$(OBJDIR)/%.o: $(SRCDIR)/%.c $(DEPS)
	@mkdir -p $(OBJDIR)
	$(CC) $(CFLAGS) -I$(SRCDIR) -c $< -o $@

# ABI 自检：单独把 win32_abi.h 编一遍并**在运行期打印逐字段 offset**。
# 编译期的 _Static_assert 已经钉死了布局，这个可执行文件是给报告留的**证据**：
# 人和 CI 都能看到"托管侧 MSG 的 7 个字段落在哪”。
abi-check: tests/abi_layout.c $(DEPS)
	@mkdir -p $(BINDIR)
	$(CC) $(CSTD) $(OPT) $(WARN) -I$(SRCDIR) -o $(BINDIR)/abi-layout $< $(LDLIBS)

test: abi-check
	@$(BINDIR)/abi-layout

# 判据侧的两个 C 探针：假合成器（造「有合成器」一极）+ DWM 探测（读实现真值）
probes: $(TARGET)
	@mkdir -p $(BINDIR)
	$(CC) $(CSTD) $(OPT) $(WARN) -o $(BINDIR)/fake-compositor tools/fake-compositor.c -lX11
	$(CC) $(CSTD) $(OPT) $(WARN) -o $(BINDIR)/dwm-probe tools/dwm-probe.c -L$(BINDIR) -lwpfwin32 -Wl,-rpath,'$$ORIGIN'
	$(CC) $(CSTD) $(OPT) $(WARN) -o $(BINDIR)/clip-probe tools/clip-probe.c -L$(BINDIR) -lwpfwin32 -Wl,-rpath,'$$ORIGIN' -lX11
	$(CC) $(CSTD) $(OPT) $(WARN) -o $(BINDIR)/xdnd-probe tools/xdnd-probe.c -lX11
	@echo "== 探针就绪：$(BINDIR)/fake-compositor、$(BINDIR)/dwm-probe"

symbols: $(TARGET)
	@echo "导出符号总数：$$(nm -D --defined-only $(TARGET) | wc -l)"
	@nm -D --defined-only $(TARGET) | awk '{print $$3}' | sort > $(BINDIR)/exports.txt
	@echo "清单：$(BINDIR)/exports.txt"

clean:
	rm -rf $(OBJDIR) $(BINDIR)
