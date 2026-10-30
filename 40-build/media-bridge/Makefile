CC     ?= cc
CSTD   ?= -std=gnu11
OPT    ?= -O2
WARN   ?= -Wall -Wextra
SRCDIR := src
BINDIR := bin
TARGET := $(BINDIR)/libldpmedia.so

.PHONY: all clean probe
all: $(TARGET)

$(TARGET): $(SRCDIR)/media_bridge.c $(SRCDIR)/media_bridge.h
	@mkdir -p $(BINDIR)
	$(CC) $(CSTD) $(OPT) $(WARN) -fPIC -shared -o $@ $(SRCDIR)/media_bridge.c -Isrc

probe: $(TARGET)
	@mkdir -p $(BINDIR)
	$(CC) $(CSTD) $(OPT) $(WARN) -Isrc -o $(BINDIR)/media-probe tools/media-probe.c -L$(BINDIR) -lldpmedia -Wl,-rpath,'$$ORIGIN'
	@echo "== media 探针就绪：$(BINDIR)/media-probe"

clean:
	rm -rf $(BINDIR)
