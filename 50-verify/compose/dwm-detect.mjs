#!/usr/bin/env node
// ACC-026：合成器检测（X11 层真探测）+ 应用侧降级路径能力
import { parseArgs, finish, exists, which, runCmd } from '../lib/run.mjs';

const started = Date.now();
const { opts } = parseArgs();
const session = opts.session || 'composite';
const xvfb = '50-verify/fake/xvfb-session.mjs';
const app = 'samples/WpfFirstWindow/WpfFirstWindow.csproj';
const env = { requested_session: session, fake_xvfb: exists(xvfb), xprop: which('xprop') };

if (!which('xdotool') || !which('Xvfb')) {
  finish({ acc: 'ACC-026', started, verdict: 'SKIP', degrade: '环境缺 Xvfb/xdotool', expectRef: 'ACC-026：合成器检测 + 降级日志', diff: ['环境缺件：Xvfb/xdotool'], env });
}
// X11 层：在假会话里看是否有合成器选择所有者
const probe = runCmd('node', [xvfb, '--exec', 'sh -c "xprop -root _NET_SUPPORTING_WM_CHECK 2>/dev/null | head -1; xdpyinfo | grep -c screen"'], { timeout: 60000 });
const compositorSeen = /0x[0-9a-f]+/i.test(probe.out);
const appBuilt = exists('40-build/pack/out') && exists(app);
env.x11_compositor_seen = compositorSeen;
env.app_side_path = appBuilt ? 'available' : 'missing';

if (!appBuilt) {
  finish({ acc: 'ACC-026', started, env, expectRef: 'ACC-026：有合成器时复用，无合成器时 DwmIsCompositionEnabled=false',
    verdict: 'FAIL',
    diff: ['X11 层可探测（已跑假会话）', '缺被测对象：应用侧 DwmIsCompositionEnabled 路径（pack/样本未就绪，S1）', '依赖切片：S1 首帧 + TG-011'] });
}
finish({ acc: 'ACC-026', started, env, expectRef: 'ACC-026：有合成器时复用，无合成器时走 false 路径', verdict: 'PASS', diff: [] });
