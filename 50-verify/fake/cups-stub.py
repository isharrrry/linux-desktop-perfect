#!/usr/bin/env python3
"""Fake CUPS —— 最小 IPP 假服务端（#Fake CUPS）

作用：给 TG-010（打印）提供一条「无 cupsd 也能跑」的 IPP 端点：收下文档字节、记录作业属性，
让判据脚本能验证「提交 → 属性 → 状态」三段，而不依赖真打印机或 cupsd。

用法：
  python3 50-verify/fake/cups-stub.py --self-test
  python3 50-verify/fake/cups-stub.py --serve [--port 0] [--queue LDP-FAKE] [--job-state completed]
  python3 50-verify/fake/cups-stub.py --report

端点：HTTP/1.1 + Content-Type: application/ipp，路径 /ipp/print。
支持的操作（与 IF-041 对齐）：
  Print-Job (0x0002)、Get-Job-Attributes (0x0009)、Get-Printer-Attributes (0x000B)
未实现的 operation 一律返回 server-error-operation-not-supported (0x0501)，不静默成功。

已知偏差（逐条显式登记，禁止静默）：
  1. 不是真 CUPS：不接队列、不渲染、不出纸；只验「属性与状态」，不验「打印结果」。
  2. Get-Printer-Attributes 在 IF-041 里是「待对拍确认」：本桩件返回的 **能力集是登记默认值**，
     并在响应里带非标准 text 属性 `ldp-assumed`（逗号分隔）列出哪些是假设值，供判据识别。
  3. 只解析本桩件用到的属性（charset / natural-language / job-name / document-format / copies /
     media / printer-resolution）；其余属性丢弃并在 --report 里列明。
  4. 不做 IPP 分块传输、不做多文档、不做认证/加密；请求体超过 --max-body 直接 400。
  5. XPS 的 document-format 字符串待对拍确认：默认声明 `application/vnd.ms-xpsdocument`，
     同时接受 `application/pdf` 与 `application/octet-stream`，其余格式按 client-error-document-format-not-supported 拒绝。
"""
import argparse
import json
import os
import signal
import socket
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

# ---- IPP 常量 ----
OP_PRINT_JOB = 0x0002
OP_GET_JOB_ATTRIBUTES = 0x0009
OP_GET_PRINTER_ATTRIBUTES = 0x000B
OP_NAMES = {OP_PRINT_JOB: 'Print-Job', OP_GET_JOB_ATTRIBUTES: 'Get-Job-Attributes',
            OP_GET_PRINTER_ATTRIBUTES: 'Get-Printer-Attributes'}

STATUS_OK = 0x0000
STATUS_BAD_REQUEST = 0x0400
STATUS_DOC_FORMAT_NOT_SUPPORTED = 0x040A
STATUS_NOT_FOUND = 0x040D
STATUS_OP_NOT_SUPPORTED = 0x0501
STATUS_UNAVAILABLE = 0x0502
STATUS_NAMES = {
    STATUS_OK: 'successful-ok',
    STATUS_BAD_REQUEST: 'client-error-bad-request',
    STATUS_DOC_FORMAT_NOT_SUPPORTED: 'client-error-document-format-not-supported',
    STATUS_NOT_FOUND: 'client-error-not-found',
    STATUS_OP_NOT_SUPPORTED: 'server-error-operation-not-supported',
    STATUS_UNAVAILABLE: 'server-error-service-unavailable',
}

TAG_OPERATION = 0x01
TAG_JOB = 0x02
TAG_END = 0x03
TAG_PRINTER = 0x04
TAG_CHARSET = 0x47
TAG_NATURAL_LANGUAGE = 0x48
TAG_TEXT = 0x41
TAG_NAME = 0x42
TAG_KEYWORD = 0x44
TAG_URI = 0x45
TAG_INTEGER = 0x21
TAG_BOOLEAN = 0x22
TAG_MIMETYPE = 0x49

SUPPORTED_FORMATS = ['application/vnd.ms-xpsdocument', 'application/pdf', 'application/octet-stream']
ASSUMED_PRINTER_CAPS = ['document-format-supported', 'media-supported', 'printer-resolution-supported', 'printer-is-accepting-jobs']
UNSUPPORTED = ['Validate-Job', 'Create-Job', 'Send-Document', 'Cancel-Job', 'Purge-Jobs',
               'Get-Jobs', 'Hold-Job', 'Release-Job', 'Restart-Job', 'Get-Printer-Supported-Values']


def die(msg, code=1):
    print(f'ERROR {msg}', file=sys.stderr)
    sys.exit(code)


# ---------- IPP 编解码 ----------

def parse_ipp(body):
    if len(body) < 8:
        raise ValueError('IPP 请求体短于 8 字节')
    version = body[0:2]
    op = int.from_bytes(body[2:4], 'big')
    req_id = int.from_bytes(body[4:8], 'big')
    attrs = {}
    doc = b''
    i = 8
    while i < len(body):
        tag = body[i]
        i += 1
        if tag == TAG_END:
            doc = body[i:]
            break
        if tag < 0x10:          # 分隔符
            continue
        if i + 2 > len(body):
            break
        nlen = int.from_bytes(body[i:i + 2], 'big')
        i += 2
        name = body[i:i + nlen].decode('utf-8', 'replace')
        i += nlen
        if i + 2 > len(body):
            break
        vlen = int.from_bytes(body[i:i + 2], 'big')
        i += 2
        val = body[i:i + vlen]
        i += vlen
        attrs.setdefault(name, []).append((tag, val))
    return version, op, req_id, attrs, doc


def _enc(tag, name, value):
    nb = name.encode('utf-8')
    vb = value if isinstance(value, bytes) else str(value).encode('utf-8')
    return bytes([tag]) + len(nb).to_bytes(2, 'big') + nb + len(vb).to_bytes(2, 'big') + vb


def _enc_int(name, n):
    return (TAG_INTEGER, name, n.to_bytes(4, 'big'))


def _enc_bool(name, b):
    return (TAG_BOOLEAN, name, b'\x01' if b else b'\x00')


def build_response(req_version, status, req_id, groups):
    out = bytearray(b'\x01\x01')
    out += status.to_bytes(2, 'big')
    out += req_id.to_bytes(4, 'big')
    for group_tag, attrs in groups:
        out += bytes([group_tag])
        for tag, name, value in attrs:
            out += _enc(tag, name, value)
    out += bytes([TAG_END])
    return bytes(out)


def first(attrs, name, default=None):
    hit = attrs.get(name)
    if not hit:
        return default
    return hit[0][1]


def first_int(attrs, name, default=None):
    v = first(attrs, name)
    if v is None or len(v) != 4:
        return default
    return int.from_bytes(v, 'big')


# ---------- 服务 ----------

class State:
    def __init__(self, queue, job_state, reject, max_body):
        self.queue = queue
        self.default_job_state = job_state
        self.reject = reject
        self.max_body = max_body
        self.jobs = {}
        self.next_id = 1


def make_handler(state):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = 'HTTP/1.1'

        def log_message(self, fmt, *args):  # 静默访问日志，避免污染无人值守输出
            pass

        def _send(self, code, body=b'', ctype='application/ipp'):
            self.send_response(code)
            self.send_header('Content-Type', ctype)
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            if body:
                self.wfile.write(body)

        def do_POST(self):  # noqa: N802
            if self.path.rstrip('/') not in ('/ipp/print', ''):
                self._send(404, b'not found', 'text/plain; charset=utf-8')
                return
            length = int(self.headers.get('Content-Length') or 0)
            if length <= 0:
                self._send(400, b'empty body', 'text/plain; charset=utf-8')
                return
            if length > state.max_body:
                self._send(400, b'body too large', 'text/plain; charset=utf-8')
                return
            body = self.rfile.read(length)
            try:
                version, op, req_id, attrs, doc = parse_ipp(body)
            except ValueError as e:
                self._send(400, str(e).encode('utf-8'), 'text/plain; charset=utf-8')
                return
            groups = self._dispatch(version, op, req_id, attrs, doc)
            self._send(200, build_response(version, groups[0], req_id, groups[1]))

        def _dispatch(self, version, op, req_id, attrs, doc):
            if op == OP_GET_PRINTER_ATTRIBUTES:
                return STATUS_OK, [(TAG_OPERATION, [
                    (TAG_CHARSET, 'attributes-charset', 'utf-8'),
                    (TAG_NATURAL_LANGUAGE, 'attributes-natural-language', 'zh-cn'),
                ]), (TAG_PRINTER, [
                    (TAG_TEXT, 'printer-name', state.queue),
                    (TAG_URI, 'printer-uri-supported', f'ipp://127.0.0.1/printers/{state.queue}'),
                    _enc_bool('printer-is-accepting-jobs', not state.reject),
                    _enc_int('queued-job-count', len(state.jobs)),
                    (TAG_MIMETYPE, 'document-format-supported', SUPPORTED_FORMATS[0]),
                    (TAG_KEYWORD, 'media-supported', 'iso_a4_210x297mm'),
                    (TAG_KEYWORD, 'printer-resolution-supported', '300dpi'),
                    (TAG_TEXT, 'ldp-assumed', ','.join(ASSUMED_PRINTER_CAPS)),
                ])]
            if op == OP_PRINT_JOB:
                fmt = first(attrs, 'document-format', b'')
                fmt = fmt.decode('utf-8', 'replace') if isinstance(fmt, bytes) else str(fmt)
                if state.reject:
                    return STATUS_UNAVAILABLE, [(TAG_OPERATION, [
                        (TAG_CHARSET, 'attributes-charset', 'utf-8'),
                        (TAG_NATURAL_LANGUAGE, 'attributes-natural-language', 'zh-cn'),
                    ])]
                if fmt and fmt not in SUPPORTED_FORMATS:
                    return STATUS_DOC_FORMAT_NOT_SUPPORTED, [(TAG_OPERATION, [
                        (TAG_CHARSET, 'attributes-charset', 'utf-8'),
                        (TAG_NATURAL_LANGUAGE, 'attributes-natural-language', 'zh-cn'),
                    ])]
                assumed = []
                copies = first_int(attrs, 'copies', None)
                if copies is None:
                    copies = 1
                    assumed.append('copies')
                media = first(attrs, 'media', b'iso_a4_210x297mm')
                media = media.decode('utf-8', 'replace') if isinstance(media, bytes) else str(media)
                res = first(attrs, 'printer-resolution', b'300dpi')
                res = res.decode('utf-8', 'replace') if isinstance(res, bytes) else str(res)
                # 页数：XPS 渲染侧（MOD-24）才数得准；桩件按「文档非空 + A4」估 1 页并标 assumed
                pages = first_int(attrs, 'job-impressions', None)
                if pages is None:
                    pages = 1 if doc else 0
                    assumed.append('job-impressions')
                job_id = state.next_id
                state.next_id += 1
                job = {
                    'job-id': job_id,
                    'job-name': (first(attrs, 'job-name', b'') or b'').decode('utf-8', 'replace'),
                    'document-format': fmt or 'application/octet-stream',
                    'copies': copies, 'media': media, 'printer-resolution': res,
                    'job-impressions': pages, 'document-bytes': len(doc),
                    'job-state': state.default_job_state, 'assumed': assumed,
                }
                state.jobs[job_id] = job
                return STATUS_OK, [(TAG_OPERATION, [
                    (TAG_CHARSET, 'attributes-charset', 'utf-8'),
                    (TAG_NATURAL_LANGUAGE, 'attributes-natural-language', 'zh-cn'),
                ]), (TAG_JOB, [
                    _enc_int('job-id', job_id),
                    (TAG_URI, 'job-uri', f'ipp://127.0.0.1/jobs/{job_id}'),
                    (TAG_KEYWORD, 'job-state', job['job-state']),
                    (TAG_TEXT, 'ldp-assumed', ','.join(assumed)),
                ])]
            if op == OP_GET_JOB_ATTRIBUTES:
                job_id = first_int(attrs, 'job-id', None)
                if job_id is None:
                    job_id = max(state.jobs) if state.jobs else None
                job = state.jobs.get(job_id) if job_id is not None else None
                if not job:
                    return STATUS_NOT_FOUND, [(TAG_OPERATION, [
                        (TAG_CHARSET, 'attributes-charset', 'utf-8'),
                        (TAG_NATURAL_LANGUAGE, 'attributes-natural-language', 'zh-cn'),
                    ])]
                return STATUS_OK, [(TAG_OPERATION, [
                    (TAG_CHARSET, 'attributes-charset', 'utf-8'),
                    (TAG_NATURAL_LANGUAGE, 'attributes-natural-language', 'zh-cn'),
                ]), (TAG_JOB, [
                    _enc_int('job-id', job['job-id']),
                    (TAG_KEYWORD, 'job-state', job['job-state']),
                    _enc_int('job-impressions-completed', job['job-impressions'] if job['job-state'] == 'completed' else 0),
                    _enc_int('job-media-sheets-completed', job['copies'] if job['job-state'] == 'completed' else 0),
                ])]
            return STATUS_OP_NOT_SUPPORTED, [(TAG_OPERATION, [
                (TAG_CHARSET, 'attributes-charset', 'utf-8'),
                (TAG_NATURAL_LANGUAGE, 'attributes-natural-language', 'zh-cn'),
            ])]

    return Handler


def serve(port, queue, job_state, reject, max_body):
    state = State(queue, job_state, reject, max_body)
    srv = ThreadingHTTPServer(('127.0.0.1', port), make_handler(state))
    port = srv.server_address[1]
    print(f'READY {json.dumps({"port": port, "queue": queue, "address": f"http://127.0.0.1:{port}/ipp/print", "job_state": job_state, "reject": reject}, ensure_ascii=False)}', flush=True)

    def stop(*_):
        threading.Thread(target=srv.shutdown, daemon=True).start()

    for sig in (signal.SIGTERM, signal.SIGINT):
        signal.signal(sig, stop)
    try:
        srv.serve_forever(poll_interval=0.2)
    finally:
        srv.server_close()
    return 0


# ---------- 客户端（自检与判据共用） ----------

def ipp_request(port, op, attrs, doc=b'', req_id=1):
    body = bytearray(b'\x01\x01')
    body += op.to_bytes(2, 'big')
    body += req_id.to_bytes(4, 'big')
    body += bytes([TAG_OPERATION])
    group = [('attributes-charset', TAG_CHARSET, b'utf-8'),
             ('attributes-natural-language', TAG_NATURAL_LANGUAGE, b'zh-cn'),
             ('printer-uri', TAG_URI, f'ipp://127.0.0.1:{port}/ipp/print'.encode()),
             ('requesting-user-name', TAG_NAME, b'ldp')]
    for name, tag, value in group + attrs:
        body += _enc(tag, name, value)
    if op == OP_PRINT_JOB:
        body += bytes([TAG_END])
        body += doc
    else:
        body += bytes([TAG_END])
    import http.client
    conn = http.client.HTTPConnection('127.0.0.1', port, timeout=10)
    conn.request('POST', '/ipp/print', bytes(body), {'Content-Type': 'application/ipp'})
    resp = conn.getresponse()
    data = resp.read()
    conn.close()
    status = int.from_bytes(data[2:4], 'big') if len(data) >= 8 else -1
    return status, _parse_response_attrs(data)


def _parse_response_attrs(data):
    attrs = {}
    if len(data) < 8:
        return attrs
    i = 8
    while i < len(data):
        tag = data[i]
        i += 1
        if tag == TAG_END:
            break
        if tag < 0x10:
            continue
        nlen = int.from_bytes(data[i:i + 2], 'big'); i += 2
        name = data[i:i + nlen].decode('utf-8', 'replace'); i += nlen
        vlen = int.from_bytes(data[i:i + 2], 'big'); i += 2
        val = data[i:i + vlen]; i += vlen
        if tag in (TAG_INTEGER,) and len(val) == 4:
            attrs[name] = int.from_bytes(val, 'big')
        elif tag == TAG_BOOLEAN:
            attrs[name] = bool(val and val[0])
        else:
            attrs[name] = val.decode('utf-8', 'replace')
    return attrs


# ---------- 报告与自检 ----------

def report():
    print('Fake CUPS 偏差清单：')
    print('  1. 非真 CUPS：不接队列/不渲染/不出纸，只验作业属性与状态（约定，见 09 §8）')
    print(f'  2. Get-Printer-Attributes 能力集为登记默认值（假设项：{",".join(ASSUMED_PRINTER_CAPS)}）')
    print(f'  3. 未实现的 operation（一律返回 {STATUS_NAMES[STATUS_OP_NOT_SUPPORTED]}）：{", ".join(UNSUPPORTED)}')
    print('  4. 只解析这些属性：attributes-charset / attributes-natural-language / job-name / '
          'document-format / copies / media / printer-resolution / job-impressions / job-id')
    print('  5. 不做分块传输、多文档、认证、加密；超过 --max-body 直接 400')
    print('  6. XPS document-format 字符串待对拍确认；默认声明 application/vnd.ms-xpsdocument')
    return 0


def self_test(max_body):
    import threading
    checks = []

    def ck(name, cond, detail=''):
        checks.append((name, bool(cond), detail))

    state = State('LDP-FAKE', 'completed', False, max_body)
    srv = ThreadingHTTPServer(('127.0.0.1', 0), make_handler(state))
    port = srv.server_address[1]
    t = threading.Thread(target=srv.serve_forever, kwargs={'poll_interval': 0.05}, daemon=True)
    t.start()
    try:
        st, a = ipp_request(port, OP_GET_PRINTER_ATTRIBUTES, [])
        ck('Get-Printer-Attributes rc=ok', st == STATUS_OK, f'status=0x{st:04x}')
        ck('队列名正确', a.get('printer-name') == 'LDP-FAKE', str(a.get('printer-name')))
        ck('声明 document-format-supported', a.get('document-format-supported') == SUPPORTED_FORMATS[0])
        ck('假设项显式标注', 'document-format-supported' in str(a.get('ldp-assumed', '')))
        st, a = ipp_request(port, OP_PRINT_JOB, [
            ('job-name', TAG_NAME, b'test.xps'),
            ('document-format', TAG_MIMETYPE, SUPPORTED_FORMATS[0].encode()),
            ('copies', TAG_INTEGER, (2).to_bytes(4, 'big')),
        ], doc=b'%!XPS fake document')
        ck('Print-Job rc=ok', st == STATUS_OK, f'status=0x{st:04x}')
        job_id = a.get('job-id')
        ck('返回 job-id', isinstance(job_id, int), str(job_id))
        ck('job-state=completed', a.get('job-state') == 'completed', str(a.get('job-state')))
        ck('记录的份数=2', state.jobs.get(job_id, {}).get('copies') == 2, str(state.jobs.get(job_id, {}).get('copies')))
        ck('记录文档字节数', state.jobs.get(job_id, {}).get('document-bytes') == len(b'%!XPS fake document'))
        ck('页数为假设项', 'job-impressions' in state.jobs.get(job_id, {}).get('assumed', []))
        st, a = ipp_request(port, OP_GET_JOB_ATTRIBUTES, [('job-id', TAG_INTEGER, job_id.to_bytes(4, 'big'))])
        ck('Get-Job-Attributes rc=ok', st == STATUS_OK, f'status=0x{st:04x}')
        ck('job-state 回读 completed', a.get('job-state') == 'completed')
        ck('job-impressions-completed=1', a.get('job-impressions-completed') == 1, str(a.get('job-impressions-completed')))
        st, _ = ipp_request(port, 0x0005, [])
        ck('未实现 operation → 0x0501', st == STATUS_OP_NOT_SUPPORTED, f'status=0x{st:04x}')
        st, _ = ipp_request(port, OP_PRINT_JOB, [('document-format', TAG_MIMETYPE, b'application/x-unknown')], doc=b'x')
        ck('不支持的文档格式被拒', st == STATUS_DOC_FORMAT_NOT_SUPPORTED, f'status=0x{st:04x}')
        st, _ = ipp_request(port, OP_GET_JOB_ATTRIBUTES, [('job-id', TAG_INTEGER, (999).to_bytes(4, 'big'))])
        ck('不存在的作业 → 0x040d', st == STATUS_NOT_FOUND, f'status=0x{st:04x}')
        state.reject = True
        st, _ = ipp_request(port, OP_PRINT_JOB, [('document-format', TAG_MIMETYPE, SUPPORTED_FORMATS[0].encode())], doc=b'x')
        ck('--reject 时 Print-Job → 0x0502', st == STATUS_UNAVAILABLE, f'status=0x{st:04x}')
    finally:
        srv.shutdown()
        srv.server_close()
    bad = [c for c in checks if not c[1]]
    for name, ok, detail in checks:
        print(f'{"PASS" if ok else "FAIL"} {name}' + (f' ({detail})' if detail and not ok else ''))
    print(f'cups-stub self-test: {len(checks) - len(bad)}/{len(checks)} 通过')
    return 0 if not bad else 1


def main():
    ap = argparse.ArgumentParser(description='Fake CUPS 最小 IPP 服务端（见文件头偏差）')
    ap.add_argument('--serve', action='store_true')
    ap.add_argument('--self-test', action='store_true')
    ap.add_argument('--report', action='store_true')
    ap.add_argument('--port', type=int, default=0, help='监听端口，0=自动挑')
    ap.add_argument('--queue', default='LDP-FAKE')
    ap.add_argument('--job-state', default='completed', choices=['completed', 'aborted', 'pending', 'processing'])
    ap.add_argument('--reject', action='store_true', help='模拟队列不可用（Print-Job 返回 0x0502）')
    ap.add_argument('--max-body', type=int, default=16 * 1024 * 1024)
    ap.add_argument('--timeout', type=int, default=30)
    args = ap.parse_args()

    def on_alarm(*_):
        print(f'ERROR TIMEOUT 超过 {args.timeout}s', file=sys.stderr)
        os._exit(124)

    signal.signal(signal.SIGALRM, on_alarm)
    signal.alarm(args.timeout)
    try:
        if args.report:
            return report()
        if args.self_test:
            return self_test(args.max_body)
        if args.serve:
            return serve(args.port, args.queue, args.job_state, args.reject, args.max_body)
        ap.print_help()
        return 2
    finally:
        signal.alarm(0)


if __name__ == '__main__':
    sys.exit(main())
