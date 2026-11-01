# libSystem.IO.Compression.Native.so —— .NET 的 System.IO.Compression 要 P/Invoke 这个库名
CC      ?= gcc
SRCDIR  := src
OBJDIR  := obj
BINDIR  := bin
TARGET  := $(BINDIR)/libSystem.IO.Compression.Native.so
SRCS    := $(wildcard $(SRCDIR)/*.c)
OBJS    := $(patsubst $(SRCDIR)/%.c,$(OBJDIR)/%.o,$(SRCS))
CFLAGS  := -O2 -g -fPIC -fvisibility=default -Wall -Wextra -std=gnu11
LDLIBS  := -lz

all: $(TARGET)
$(TARGET): $(OBJS)
	@mkdir -p $(BINDIR)
	$(CC) -shared -Wl,--no-undefined -Wl,-soname,$(notdir $(TARGET)) -o $@ $(OBJS) $(LDLIBS)
	@echo "== 构建完成：$@ ($(shell stat -c%s $@) 字节)"
$(OBJDIR)/%.o: $(SRCDIR)/%.c
	@mkdir -p $(OBJDIR)
	$(CC) $(CFLAGS) -c -o $@ $<
clean:
	rm -rf $(OBJDIR) $(BINDIR)
.PHONY: all clean
