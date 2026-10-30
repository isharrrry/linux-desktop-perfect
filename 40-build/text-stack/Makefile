# text-stack · libdwlinux.so（SYS-4：MOD-31/32/33/35）
#
# 用法：
#   make            → 构建 bin/libdwlinux.so 与 bin/dwp-probe
#   make test       → 跑 ABI 自检（编译期 _Static_assert + 运行期打印）
#   make probe      → 冒烟：枚举/匹配/shaping/断行/轮廓各打一行读数
#   make symbols    → 打印导出符号（TG-001 对拍输入）
#   make headers    → 取构建期头文件到 ../lock/headers/include（需网络；不入库）
#   make clean
#
# 为什么用 dlopen 而不是直接 -lharfbuzz：本机只装运行时库（libharfbuzz.so.0 等），
# 没有 *.so 开发符号链接；头文件由 `make headers` 取件只用于编译期类型。
# 这样既不猜结构布局，也不要求装 dev 包（SAFE-018：缺件就报错，不静默）。

CC        ?= gcc
CSTD      ?= -std=gnu11
OPT       ?= -O2
WARN       = -Wall -Wextra -Wno-unused-parameter
HDR_ROOT  ?= ../lock/headers/include
CFLAGS    ?= $(CSTD) $(OPT) $(WARN) -fPIC -fvisibility=default -I$(HDR_ROOT) -I$(HDR_ROOT)/freetype2 -I$(HDR_ROOT)/harfbuzz -Isrc
LDLIBS     = -ldl

SRCDIR     = src
BINDIR     = bin
OBJDIR     = obj
TARGET     = $(BINDIR)/libdwlinux.so
PROBE      = $(BINDIR)/dwp-probe
SRCS       = $(SRCDIR)/dwlinux.c
OBJS       = $(patsubst $(SRCDIR)/%.c,$(OBJDIR)/%.o,$(SRCS))
DEPS       = $(SRCDIR)/dwlinux.h

.PHONY: all clean test probe symbols headers abi-check

all: $(TARGET) $(PROBE)

$(TARGET): $(OBJS)
	@mkdir -p $(BINDIR)
	$(CC) -shared -Wl,--no-undefined -Wl,-soname,libdwlinux.so -o $@ $(OBJS) $(LDLIBS)
	@echo "== 构建完成：$@ ($$(stat -c%s $@ 2>/dev/null || echo 0) 字节)"

$(OBJDIR)/%.o: $(SRCDIR)/%.c $(DEPS)
	@mkdir -p $(OBJDIR)
	$(CC) $(CFLAGS) -c $< -o $@

$(PROBE): tools/dwp-probe.c $(TARGET) $(DEPS)
	@mkdir -p $(BINDIR)
	$(CC) $(CSTD) $(OPT) $(WARN) -Isrc -o $@ $< -L$(BINDIR) -ldwlinux -Wl,-rpath,'$$ORIGIN' $(LDLIBS)

# ABI 自检：单独把 dwlinux.h 编一遍并打印逐字段 offset（编译期断言已钉死，这里留证据）
abi-check: tests/text_abi.c $(DEPS)
	@mkdir -p $(BINDIR)
	$(CC) $(CSTD) $(OPT) $(WARN) -Isrc -o $(BINDIR)/text-abi $< -L$(BINDIR) -ldwlinux -Wl,-rpath,'$$ORIGIN'

test: abi-check
	@$(BINDIR)/text-abi

probe: $(PROBE)
	@$(PROBE) --version
	@$(PROBE) --fonts | head -c 200; echo
	@$(PROBE) --shape "DejaVu Sans" "Hello" 16
	@$(PROBE) --breaks "Hello world"
	@$(PROBE) --outline "DejaVu Sans" 72 32

symbols: $(TARGET)
	@echo "导出符号总数：$$(nm -D --defined-only $(TARGET) | wc -l)"
	@nm -D --defined-only $(TARGET) | awk '{print $$3}' | sort > $(BINDIR)/exports.txt
	@echo "清单：$(BINDIR)/exports.txt"

# 取构建期头文件（不入库）：Ubuntu 22.04 的 dev 包解包到 ../lock/headers/include
headers:
	@set -e; root=$$(cd .. && pwd)/lock/headers; mkdir -p $$root/dl $$root/include; cd $$root/dl; \
	for u in \
	  "http://mirrors.aliyun.com/ubuntu/pool/main/f/freetype/libfreetype-dev_2.10.1-2_amd64.deb" \
	  "http://mirrors.aliyun.com/ubuntu/pool/main/f/fontconfig/libfontconfig1-dev_2.11.94-0ubuntu1.1_amd64.deb"; do \
	  f=$$(basename $$u); [ -s $$f ] || curl -sSL -o $$f $$u; dpkg -x $$f $$root/x; \
	done; \
	apt-get download -o Dir::Cache::archives=$$root/dl libharfbuzz-dev >/dev/null 2>&1 || \
	  curl -sSL -o $$root/dl/hb.deb "http://mirrors.aliyun.com/ubuntu/pool/main/h/harfbuzz/libharfbuzz-dev_2.7.4-1ubuntu3.2_amd64.deb"; \
	for f in $$root/dl/*.deb; do dpkg -x $$f $$root/x 2>/dev/null || true; done; \
	cp -rn $$root/x/usr/include/. $$root/include/; \
	echo "== 头文件就绪：$$root/include"

clean:
	rm -rf $(OBJDIR) $(BINDIR)
