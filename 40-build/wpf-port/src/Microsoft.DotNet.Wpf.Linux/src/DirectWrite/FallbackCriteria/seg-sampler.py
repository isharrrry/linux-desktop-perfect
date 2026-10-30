#!/usr/bin/env python3
# =====================================================================================
# **段数 / Σ虚拟 采样器（python 版）**（T2 车道；`#15` 波后复取 `D-F1c`① 专用）
#
# 【为什么又换一版】`seg-instrument.sh`（bash 版）实测**每 tick ≈2 s**（`tree_pids` 每层都对
#   `/proc/[0-9]*` 全表 fork 一次 awk ⇒ 单次 3900+ 次 fork；再叠加 python3 解释器启动）
#   ⇒ 被测进程只活 0.9 s 的档位**只采到 1 tick**（读数因此不可用，已自纠）。
#   本版：**单进程 python**，每 tick 只读 `/proc/*/status` 的 PPid（无 fork）、`maps` 直接读文本、
#   `smaps_rollup` 直接读 ⇒ 实测每 tick ≲20 ms，可以 0.05 s 一跳。
#
# 【段数的定义（逐字写死，可复算）】
#   `/proc/<pid>/maps` 里**每一行** = 内核眼中**一段**（段数 = 行数）。
#   "某文件的段数" = `maps` 第 6 列起 pathname 等于该文件的**行数**。
#   Σ虚拟 = Σ(end−start)（B）；ΣRss 取 `smaps` 逐段 `Rss:`（只在峰值 tick 采，代价高）。
#
# 【判据（预登记 ⑧）】主判据 = **段数 / Σ虚拟**；**RSS 只作旁证**。
#   权威峰值（MB）= `/usr/bin/time -v` 的 `Maximum resident set size`（短跑只有它靠得住）。
#   ⚠️ **Σ虚拟 的绝对值不可作判据**：.NET 的 `/memfd:doublemapper`（GC 写屏障）本身就映射
#      30+ GB 虚拟（实测 647 段 / 36.6 GB），与字体无关 ⇒ 判据一律针对**字体文件族**，
#      本仪器把 `FONT_*` 与 `ALL_*` **分开单列**（免得把运行时地板读成字体占用）。
#
# 【纪律】杀进程**只按 PID**（整棵树 TERM → 2 s → KILL）；**绝不用 `pkill -f`**。
# =====================================================================================
import os
import re
import shutil
import signal
import subprocess
import sys
import time

FONT_RE = re.compile(r'(/fonts/|\.tt[cf]$|\.ot[cf]$)')

SELFCHECK = (len(sys.argv) > 1 and sys.argv[1] == '--selfcheck')
if SELFCHECK:
    font = os.environ.get('SEG_SELFCHECK_FONT',
                          '/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc')
    if not os.path.isfile(font):
        print('SEG-SELFCHECK=FAIL reason=font-missing:%s' % font)
        sys.exit(1)
    outdir = '/tmp/seg-selfcheck-%d' % os.getpid()
    limit = 40.0
    _py = ("import mmap,time\n"
           "f=open(%r,'rb')\n"
           "m=mmap.mmap(f.fileno(),0,access=mmap.ACCESS_READ)\n"
           "_=m[0]\n"
           "time.sleep(6)\n" % font)
    argv = [sys.executable, '-c', _py]
else:
    if len(sys.argv) < 4:
        print('用法: seg-sampler.py <秒上限> <输出目录> <命令…>   |   seg-sampler.py --selfcheck')
        sys.exit(2)
    limit = float(sys.argv[1]); outdir = sys.argv[2]; argv = sys.argv[3:]

os.makedirs(outdir, exist_ok=True)


def tree_pids(root):
    """根 + 全部后代（只读 /proc/<pid>/status 的 PPid，无 fork）。"""
    ppid = {}
    for e in os.listdir('/proc'):
        if not e.isdigit():
            continue
        try:
            with open('/proc/%s/status' % e, 'rb') as f:
                for line in f:
                    if line.startswith(b'PPid:'):
                        ppid[e] = line.split()[1].decode()
                        break
        except Exception:
            continue
    out, frontier = [root], [root]
    while frontier:
        nxt = []
        for p in frontier:
            for c, par in ppid.items():
                if par == p and c not in out:
                    out.append(c); nxt.append(c)
        frontier = nxt
    return out


def read_maps(pid):
    """返回 [(size_bytes, perms, offset, pathname), ...]；读不到 ⇒ None。"""
    rows = []
    try:
        with open('/proc/%s/maps' % pid, 'rb') as f:
            for line in f:
                parts = line.rstrip(b'\n').split(None, 5)
                if len(parts) < 5:
                    continue
                try:
                    a, b = parts[0].split(b'-')
                    sz = int(b, 16) - int(a, 16)
                except Exception:
                    continue
                perm = parts[1].decode('utf-8', 'replace')
                off = parts[2].decode('utf-8', 'replace')
                path = parts[5].decode('utf-8', 'replace') if len(parts) > 5 else ''
                rows.append((sz, perm, off, path))
    except Exception:
        return None
    return rows


def read_rss_kb(pid):
    try:
        with open('/proc/%s/status' % pid, 'rb') as f:
            for line in f:
                if line.startswith(b'VmRSS:'):
                    return int(line.split()[1])
    except Exception:
        pass
    return 0


def read_rollup(pid):
    """smaps_rollup 六列：Rss/Private_Clean/Private_Dirty/Shared_Clean/Shared_Dirty/Anonymous（KB）。"""
    keys = {'Rss:': 0, 'Private_Clean:': 1, 'Private_Dirty:': 2,
            'Shared_Clean:': 3, 'Shared_Dirty:': 4, 'Anonymous:': 5}
    vals = [0] * 6
    try:
        with open('/proc/%s/smaps_rollup' % pid, 'rb') as f:
            for line in f:
                k = line.split(b':')[0].decode('utf-8', 'replace') + ':'
                if k in keys:
                    vals[keys[k]] = int(line.split()[1])
    except Exception:
        return None
    return vals


def read_smaps_font_rss(pid):
    """逐段 Rss（只在峰值 tick 采）：按 pathname 聚合 {path: [段数, Σ虚拟B, ΣRssKB]}。"""
    agg = {}
    cur = None
    try:
        f = open('/proc/%s/smaps' % pid, 'rb')
    except Exception:
        return agg
    with f:
        for line in f:
            head = line[:16]
            if b'-' in head and not line.startswith(b' '):
                # 段头行：形如 `7f2a...-7f2a... perms offset dev inode path`
                parts = line.rstrip(b'\n').split(None, 5)
                if len(parts) > 1 and b'-' in parts[0]:
                    try:
                        a, b = parts[0].split(b'-')
                        sz = int(b, 16) - int(a, 16)
                    except Exception:
                        cur = None; continue
                    path = parts[5].decode('utf-8', 'replace') if len(parts) > 5 else ''
                    cur = path
                    if FONT_RE.search(path):
                        e = agg.setdefault(path, [0, 0, 0])
                        e[0] += 1; e[1] += sz
            elif line.startswith(b'Rss:') and cur and FONT_RE.search(cur):
                try:
                    agg[cur][2] += int(line.split()[1])
                except Exception:
                    pass
    return agg


def main():
    t0 = time.time()
    proc = subprocess.Popen(argv, stdout=open(os.path.join(outdir, 'stdout.txt'), 'wb'),
                            stderr=open(os.path.join(outdir, 'stderr.txt'), 'wb'))
    pid = str(proc.pid)
    meta = open(os.path.join(outdir, 'meta.txt'), 'w')
    meta.write('CMD=%s\nPID=%s START=%s LOADAVG=%s\n'
               % (' '.join(argv), pid, time.strftime('%F %T'),
                  open('/proc/loadavg').read().split()[:3]))
    meta.flush()
    samp = open(os.path.join(outdir, 'sample.txt'), 'w')
    tickf = os.path.join(outdir, '.tick.tsv')
    best = {'seg': -1, 'virt': -1, 'rss': 0, 'fontseg': 0, 'fontvirt': 0,
            't': None, 'pids': [], 'roll': None, 'maxtick': None}
    n = 0
    while proc.poll() is None:
        n += 1
        pids = tree_pids(pid)
        seg = 0; virt = 0; rss = 0
        roll = [0] * 6
        rows = []
        for p in pids:
            m = read_maps(p)
            if m is None:
                continue
            for sz, perm, off, path in m:
                rows.append((p, sz, perm, off, path))
            seg += len(m); virt += sum(r[0] for r in m); rss += read_rss_kb(p)
            r = read_rollup(p)
            if r:
                for i in range(6):
                    roll[i] += r[i]
        fseg = sum(1 for r in rows if FONT_RE.search(r[4]))
        fvirt = sum(r[1] for r in rows if FONT_RE.search(r[4]))
        ts = time.strftime('%H:%M:%S') + ('.%03d' % ((time.time() % 1) * 1000))
        samp.write('%s seg=%d virt_mb=%d rss_mb=%d font_seg=%d font_virt_mb=%d '
                   'priv_dirty_kb=%d priv_clean_kb=%d shared_clean_kb=%d shared_dirty_kb=%d '
                   'anon_kb=%d pids=%d\n'
                   % (ts, seg, virt // (1 << 20), rss // 1024, fseg, fvirt // (1 << 20),
                      roll[2], roll[1], roll[3], roll[4], roll[5], len(pids)))
        # 峰值 tick：**字体段数优先**（主判据），并列时取 Σ虚拟更大者
        take = (fseg > best['fontseg']) or (fseg == best['fontseg'] and fvirt > best['fontvirt'])
        if take:
            best.update({'seg': seg, 'virt': virt, 'rss': rss, 'fontseg': fseg,
                         'fontvirt': fvirt, 't': ts, 'pids': list(pids), 'roll': list(roll),
                         'maxtick': n})
            with open(tickf, 'w') as tf:
                for r in sorted(rows, key=lambda x: -x[1]):
                    tf.write('%s\t%d\t%s\t%s\t%s\n' % (r[0], r[1], r[2], r[3], r[4]))
            for p in pids:
                try:
                    shutil.copyfile('/proc/%s/maps' % p, os.path.join(outdir, 'maps-at-peak-%s.txt' % p))
                except Exception:
                    pass
        if time.time() - t0 > limit:
            meta.write('WATCHDOG=kill_TERM at %ss（**只按 PID**）\n' % limit)
            for p in tree_pids(pid):
                try: os.kill(int(p), signal.SIGTERM)
                except Exception: pass
            time.sleep(2)
            for p in tree_pids(pid):
                try: os.kill(int(p), signal.SIGKILL)
                except Exception: pass
            break
        time.sleep(0.05)
    rc = proc.wait()
    samp.close()
    try:
        rows = []
        with open(tickf) as tf:
            for line in tf:
                a = line.rstrip('\n').split('\t')
                if len(a) == 5:
                    rows.append((a[0], int(a[1]), a[2], a[3], a[4]))
        agg = {}
        for p, sz, perm, off, path in rows:
            e = agg.setdefault(path, [0, 0])
            e[0] += 1; e[1] += sz
        sa = open(os.path.join(outdir, 'segments-at-peak.txt'), 'w')
        sa.write('# SEGMENTS-AT-PEAK @ %s  (tick #%s, pids=%s)\n'
                 % (best['t'], best['maxtick'], ','.join(best['pids'])))
        sa.write('# 口径：/proc/<pid>/maps 每行=一段；段数=行数；Σ虚拟=Σ(end-start)\n')
        sa.write('ALL_SEGMENTS\t%d\n' % best['seg'])
        sa.write('ALL_VIRT_MB\t%.1f\n' % (best['virt'] / (1 << 20)))
        sa.write('ALL_RSS_MB\t%.1f\n' % (best['rss'] / 1024))
        fseg = fvirt = ffiles = 0
        for path in sorted(agg, key=lambda k: -agg[k][1]):
            c, v = agg[path]
            sa.write('FILE_SEG\t%d\t%.1f\t%s\n' % (c, v / (1 << 20), path))
            if FONT_RE.search(path):
                fseg += c; fvirt += v; ffiles += 1
        sa.write('FONT_SEGMENTS\t%d\n' % fseg)
        sa.write('FONT_VIRT_MB\t%.1f\n' % (fvirt / (1 << 20)))
        sa.write('FONT_FILES\t%d\n' % ffiles)
        sa.write('# 字体族逐文件（段数 / Σ虚拟MB / 路径）\n')
        for path in sorted(agg, key=lambda k: -agg[k][1]):
            c, v = agg[path]
            if FONT_RE.search(path):
                sa.write('%d\t%.1f\t%s\n' % (c, v / (1 << 20), path))
        sa.write('# 字体段逐段明细（pid / 字节 / perms / 文件内偏移 / 路径）\n')
        for p, sz, perm, off, path in rows:
            if FONT_RE.search(path):
                sa.write('%s\t%d\t%s\t%s\t%s\n' % (p, sz, perm, off, path))
        sa.close()
    except Exception as e:
        meta.write('SEG-DUMP-ERROR=%r\n' % (e,))
    r = best['roll'] or [0] * 6
    meta.write('RC=%d\n' % rc)
    meta.write('PEAK_FONT_SEGMENTS=%d（**字体面主判据**）\n' % best['fontseg'])
    meta.write('PEAK_FONT_VIRT_MB=%.1f\n' % (best['fontvirt'] / (1 << 20)))
    meta.write('PEAK_ALL_SEGMENTS=%d\n' % best['seg'])
    meta.write('PEAK_ALL_VIRT_MB=%.1f\n' % (best['virt'] / (1 << 20)))
    meta.write('SNAP_AT_PEAK time=%s rss_kb=%d priv_dirty_kb=%d priv_clean_kb=%d '
               'shared_clean_kb=%d shared_dirty_kb=%d anon_kb=%d\n'
               % (best['t'], best['rss'], r[2], r[1], r[3], r[4], r[5]))
    meta.write('PEAK_RSS_KB_SAMPLED=%d（**旁证**）\n' % best['rss'])
    mx = 'NA'
    try:
        for line in open(os.path.join(outdir, 'stderr.txt')):
            if 'Maximum resident set size' in line:
                mx = line.split(':')[1].strip()
                break
    except Exception:
        pass
    meta.write('TIMEV_MAX_RSS_KB=%s（**权威峰值**：/usr/bin/time -v）\n' % mx)
    meta.write('WALL_S=%.1f  TICKS=%d  INTERVAL=0.05s\n' % (time.time() - t0, n))
    meta.close()
    return rc


if SELFCHECK:
    d = outdir
    fseg = -1; fvirt = -1.0
    for line in open(os.path.join(d, 'segments-at-peak.txt')):
        if line.startswith('FONT_SEGMENTS\t'):
            fseg = int(line.split('\t')[1])
        if line.startswith('FONT_VIRT_MB\t'):
            fvirt = float(line.split('\t')[1])
    if fseg >= 1 and fvirt > 0:
        print('SEG-SELFCHECK=PASS（字体段数=%d Σ虚拟=%.1fMB —— 期望 1 段 ≈18.6MB）' % (fseg, fvirt))
        shutil.rmtree(d, ignore_errors=True)
        sys.exit(0)
    print('SEG-SELFCHECK=FAIL（字体段数=%s Σ虚拟=%s）——**不许**用本仪器下结论' % (fseg, fvirt))
    sys.exit(1)
sys.exit(rc if rc >= 0 else 1)
