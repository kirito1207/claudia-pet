// 补帧翻页（character.json 的 frames）：帧图都在、按顺序翻、停在最后一张、换姿态即停、减少动画只显示第一张、
// 8 秒随机换图不插手、碰边整套播完回到待机。需要 Playwright（NODE_PATH 可指向自带运行时的 node_modules）和 Microsoft Edge。
const { chromium } = require('playwright');
const fs = require('fs'), path = require('path'), assert = require('assert/strict');
const { pathToFileURL } = require('url');
const dir = path.resolve(__dirname, '../characters/claude');
const info = JSON.parse(fs.readFileSync(path.join(dir, 'character.json'), 'utf8'));

// 静态检查：每个图名都有文件，姿态归类（文件名 - 之前）和 frames 的键一致，时长个数对得上
for (const [pose, f] of Object.entries(info.frames)) {
  assert.ok(f.seq.length >= 2, `${pose}: frames need at least two images`);
  assert.ok(f.ms.length === f.seq.length - 1 || (f.loop && f.ms.length === f.seq.length), `${pose}: ms count`);
  for (const name of f.seq) {
    assert.ok(fs.existsSync(path.join(dir, name + '.webp')), `${pose}: missing ${name}.webp`);
    assert.equal(name.split('-')[0], pose, `${name} would not be grouped under ${pose}`);
  }
}
// 碰边每段停留要够翻完
const Tumble = require('../web/tumble.js');
for (const steps of Object.values(Tumble.STEPS)) for (const [pose, ms] of steps) {
  const f = info.frames[pose]; if (!f) continue;
  const total = f.ms.reduce((a, b) => a + b, 0);
  assert.ok(ms >= total + 300, `${pose}: step ${ms} ms leaves under 300 ms on the last frame (${total})`);
}
console.log('PASS: frames config (files, grouping, timings vs tumble steps)');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    for (const reduced of [false, true]) {
      const page = await browser.newPage({ viewport: { width: 260, height: 362 }, reducedMotion: reduced ? 'reduce' : 'no-preference' });
      const errors = [];
      page.on('pageerror', e => errors.push(e.message));
      await page.goto(pathToFileURL(path.resolve(__dirname, '../web/index.html')).href);
      const m = JSON.parse(JSON.stringify(info)); m.poses = {};
      for (const file of fs.readdirSync(dir).filter(f => f.endsWith('.webp'))) {
        const key = file.replace('.webp', '').split('-')[0];
        (m.poses[key] ||= []).push(pathToFileURL(path.join(dir, file)).href);
      }
      m.sheets = {};
      for (const file of fs.readdirSync(path.join(dir, 'anim')).filter(f => f.endsWith('.webp')))
        m.sheets[file.replace('.webp', '')] = pathToFileURL(path.join(dir, 'anim', file)).href;
      await page.evaluate(m => { init({ characters: { claude: m } }); setCharacter('claude', true); setBehavior({ idleEnabled: false, clickBounce: false }) }, m);
      await page.waitForTimeout(500);
      // 记下每次换图
      await page.evaluate(() => {
        window.shown = []
        const orig = showImage
        showImage = (url, instant) => { window.shown.push([decodeURIComponent(url).split('/').pop().replace('.webp', ''), Math.round(performance.now())]); return orig(url, instant) }
      });
      const shown = () => page.evaluate(() => window.shown.map(s => s[0]));
      const clear = () => page.evaluate(() => { window.shown = [] });

      await clear();
      await page.evaluate(() => setPose('fallen'));
      if (reduced) {
        await page.waitForTimeout(1500);
        assert.deepEqual(await shown(), ['fallen-1'], 'reduced motion: only the first frame');
        assert.equal(await page.evaluate(() => flip), null);
      } else {
        assert.equal(await page.evaluate(() => document.documentElement.classList.contains('flipping')), true);
        await page.waitForTimeout(1500);
        const log = await page.evaluate(() => window.shown);
        assert.deepEqual(log.map(s => s[0]), ['fallen-1', 'fallen', 'fallen-2'], 'fallen frames in order');
        const gaps = log.slice(1).map((s, i) => s[1] - log[i][1]);
        assert.ok(Math.abs(gaps[0] - 300) < 120 && Math.abs(gaps[1] - 900) < 150, `fallen frame timing ${gaps}`);
        // 换帧不留重影：任何时刻最多一张图不透明度在 0 和 1 之间
        await page.evaluate(() => setPose('idle'));
        await page.waitForTimeout(500);
        const ghost = await page.evaluate(async () => {
          setPose('fallen'); let worst = 0
          for (const t0 = performance.now(); performance.now() - t0 < 1400;) {
            await new Promise(r => requestAnimationFrame(r))
            const mid = [imgA, imgB].filter(i => { const o = +getComputedStyle(i).opacity; return o > .05 && o < .95 }).length
            worst = Math.max(worst, mid)
          }
          return worst
        });
        assert.equal(ghost, 0, 'frames cross-faded during flip (ghosting)');
        await page.waitForTimeout(1200);
        await clear();
        // 停在最后一张：再等也不换
        await page.waitForTimeout(1200);
        assert.equal((await shown()).length, 0, 'last frame held');
        // 8 秒随机换图不插手
        await page.evaluate(() => { poseSince = Date.now() - 20000 });
        await page.waitForTimeout(2300);
        assert.equal((await shown()).length, 0, 'random 8 s switcher skipped a flip pose');
        // 换姿态立刻停翻页
        await clear();
        await page.evaluate(() => setPose('bonk'));
        await page.waitForTimeout(200);
        await page.evaluate(() => setPose('stagger'));
        await page.waitForTimeout(1300);
        assert.deepEqual(await shown(), ['bonk', 'stagger', 'stagger-2', 'stagger-3'], 'switching pose stopped the bonk flip');
        assert.equal(await page.evaluate(() => getComputedStyle(active).transitionDuration), '0s', 'flip frames cut without cross-fade');
        await page.evaluate(() => setPose('idle'));
        assert.equal(await page.evaluate(() => document.documentElement.classList.contains('flipping')), false);

        // 循环翻页（工作状态）：看书 读 2.4 秒 → 翻页 0.9 秒 → 读……一直转
        await clear();
        await page.evaluate(() => setPose('reading'));
        await page.waitForTimeout(3700);
        assert.deepEqual((await shown()).slice(0, 3), ['reading', 'reading-2', 'reading'], 'reading loops back');
        await page.evaluate(() => setPose('idle')); await page.waitForTimeout(300);
        // 碰边整套：碰底边、抽中摇晃 → 站不稳三张 → 趴地两张 → 直接站回待机（不演爬起）
        await clear();
        await page.evaluate(() => { tumblePolicy.hits = 6; tumblePolicy.throwStarted(); tumblePolicy.random = (r => () => r.shift() ?? .5)([0, .9]) });
        await page.evaluate(() => { setMotionActive(true); tumbleHit({ edge: 'bottom', impactY: 900, phase: 'fly' }); setMotionActive(false) });
        await page.waitForFunction(() => pose === 'idle' && !tumble, null, { timeout: 8000 });
        const seq = (await shown()).filter((s, i, a) => s !== a[i - 1]);
        assert.deepEqual(seq.slice(0, 5), ['stagger', 'stagger-2', 'stagger-3', 'faceplant', 'faceplant-2'], `tumble frames ${seq}`);
        assert.ok(!seq.some(s => s.startsWith('getup')), 'no getup frames any more');
        assert.ok(await page.evaluate(() => $('impact').getAnimations().length > 0), 'stand-up stretch when she gets back up from the floor');
        // 飞行中撞头：第一张停到飞行结束，停稳后才往后翻
        await page.waitForTimeout(800); await clear();
        await page.evaluate(() => { tumblePolicy.hits = 6; tumblePolicy.throwStarted(); tumblePolicy.random = () => 0; setMotionActive(true); tumbleHit({ edge: 'top', impactY: 900, phase: 'fly' }) });
        await page.waitForTimeout(1500);
        assert.deepEqual(await shown(), ['bonk'], 'bonk first frame held while still flying');
        await page.evaluate(() => setMotionActive(false));
        await page.waitForTimeout(1500);
        assert.deepEqual((await shown()).filter((s, i, a) => s !== a[i - 1]).slice(0, 3), ['bonk', 'bonk-2', 'bonk-3'], 'bonk flips on after landing');
      }
      assert.deepEqual(errors, []);
      await page.close();
    }
    console.log('PASS: flip playback (order, timing, hold, pose switch, reduced motion, random switcher, full tumble)');
  } finally { await browser.close(); }
})().catch(e => { console.error(e); process.exitCode = 1; });
