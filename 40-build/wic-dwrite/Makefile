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

.PHONY: all clean symbols

all: $(TARGET)

$(TARGET): $(OBJS)
	@mkdir -p $(BINDIR)
	$(CC) -shared -fPIC -o $@ $(OBJS) -Wl,-soname,libwpfwic.so

$(OBJDIR)/%.o: $(SRCDIR)/%.c
	@mkdir -p $(OBJDIR)
	$(CC) $(CSTD) $(OPT) $(WARN) -fPIC -fvisibility=default -c -o $@ $<

symbols: $(TARGET)
	@nm -D --defined-only $(TARGET) | awk '{print $$3}' | sort | head -60

clean:
	rm -rf $(OBJDIR) $(BINDIR)
