# libwpfwic.so —— WindowsCodecs/mscms 的最小面（D-018 骨架）
CC      ?= cc
CSTD    ?= -std=gnu11
OPT     ?= -O2
WARN    ?= -Wall -Wextra -Wno-unused-parameter
SRCDIR  := src
BINDIR  := bin
OBJDIR  := obj
TARGET  := $(BINDIR)/libwpfwic.so
SRCS    := $(wildcard $(SRCDIR)/*.c)
OBJS    := $(patsubst $(SRCDIR)/%.c,$(OBJDIR)/%.o,$(SRCS))

.PHONY: all clean symbols probes

all: $(TARGET)

$(TARGET): $(OBJS)
	@mkdir -p $(BINDIR)
	$(CC) -shared -fPIC -o $@ $(OBJS) -Wl,-soname,libwpfwic.so -lz

$(OBJDIR)/%.o: $(SRCDIR)/%.c
	@mkdir -p $(OBJDIR)
	$(CC) $(CSTD) $(OPT) $(WARN) -fPIC -fvisibility=default -Isrc -c -o $@ $<

# 探针：解码链路 + PNG 直解
probes: $(TARGET)
	@mkdir -p $(BINDIR)
	$(CC) $(CSTD) $(OPT) $(WARN) -Isrc -o $(BINDIR)/wic-dec-probe tools/wic-dec-probe.c -ldl
	$(CC) $(CSTD) $(OPT) $(WARN) -rdynamic -Isrc -o $(BINDIR)/wic-stream-probe tools/wic-stream-probe.c -ldl
	$(CC) $(CSTD) $(OPT) $(WARN) -Isrc -o $(BINDIR)/ldp-png-probe tools/../src/png_probe.c $(OBJDIR)/png_decode.o -lz 2>/dev/null || true
	@echo "== WIC 探针就绪：$(BINDIR)/wic-dec-probe"

symbols: $(TARGET)
	@nm -D --defined-only $(TARGET) | awk '{print $$3}' | sort | head -60

clean:
	rm -rf $(OBJDIR) $(BINDIR)
