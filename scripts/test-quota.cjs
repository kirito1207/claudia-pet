// 额度按当前时间重算（QuotaBars.fresh）：过了重置时间算满、太久没更新标出多久前，以及在页面里画出来不顶走人物。
// 页面部分需要 Playwright（NODE_PATH 可指向自带运行时的 node_modules）和 Microsoft Edge。
const fs = require('fs');
const path = require('path');
const { pathToFileURL } = require('url');
const assert = require('assert/strict');
const QuotaBars = require('../web/quota-bars.js');
const root = path.resolve(__dirname, '..');

const now = 1_800_000_000;
const sample = {
  text: '5小时 剩余 93% ｜ 本周 剩余 36% · 06:00 重置', low: false,
  bars: [{ label: '5小时', left: .93, reset: now + 14000 }, { label: '本周', left: .36, reset: now + 7200 }],
};

// 刚写的：数字照旧，不加备注
let q = QuotaBars.fresh(sample, now, now - 60);
assert.match(q.text, /^5小时 剩余 93% ｜ 本周 剩余 36% · \d\d:\d\d 重置$/);
assert.equal(q.note, undefined);
assert.equal(q.low, false);

// 本周过了重置时间：本周算满、标已重置；重置时间改标在剩下的 5 小时窗口上
q = QuotaBars.fresh(sample, now + 7300, now - 60);
assert.match(q.text, /^5小时 剩余 93% · \d\d:\d\d 重置 ｜ 本周 已重置$/);
assert.equal(q.bars[1].left, 1);
assert.equal(q.bars[1].resetDone, true);
const rows = QuotaBars.of(q, now + 7300).rows;
assert.deepEqual(rows.map(r => [r.pct, r.when]).at(-1), [100, '已重置']);

// 全部过了重置时间
q = QuotaBars.fresh(sample, now + 15000, now);
assert.equal(q.text, '5小时 已重置 ｜ 本周 已重置');
assert.equal(q.low, false);

// 快用完：剩余 ≤ 10% 算紧张；那个窗口一重置就不再紧张
const tight = { text: 'x', low: true, bars: [{ label: '5小时', left: .08, reset: now + 600 }, { label: '本周', left: .5, reset: now + 86400 * 3 }] };
assert.equal(QuotaBars.fresh(tight, now, now).low, true);
assert.equal(QuotaBars.fresh(tight, now + 700, now).low, false);
assert.match(QuotaBars.fresh(tight, now + 700, now).text, /本周 剩余 50% · \d+\/\d+ 重置$/);

// 多久前的数据
assert.equal(QuotaBars.ageText(now - 19 * 60, now), null);
assert.equal(QuotaBars.ageText(now - 25 * 60, now), '25 分钟前的数据');
assert.equal(QuotaBars.ageText(now - 3 * 3600 - 5, now), '3 小时前的数据');
assert.match(QuotaBars.ageText(now - 3 * 86400, now), /^\d+\/\d+ 的数据$/);
assert.equal(QuotaBars.ageText(undefined, now), null);   // 旧外壳不传时间：不标
assert.equal(QuotaBars.fresh(sample, now, now - 3600).note, '1 小时前的数据');
// 没有 bars 的旧数据：文字原样，只加备注
assert.deepEqual(QuotaBars.fresh({ text: 'a' }, now, now - 3600), { text: 'a', note: '1 小时前的数据' });
assert.equal(QuotaBars.fresh(null, now, now), null);
console.log('PASS: quota fresh (reset passed, low recompute, stale note)');

(async () => {
  let chromium;
  try { ({ chromium } = require('playwright')); } catch { console.log('SKIP: page checks (no Playwright)'); return; }
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', e => errors.push(e.message));
    await page.addInitScript(() => { window.chrome = { webview: { postMessage() {} } }; });
    await page.goto(pathToFileURL(path.join(root, 'web/index.html')).href);
    const info = JSON.parse(fs.readFileSync(path.join(root, 'characters/claude/character.json'), 'utf8'));
    info.poses = {};
    for (const file of fs.readdirSync(path.join(root, 'characters/claude')).filter(f => f.endsWith('.webp'))) {
      const key = file.replace('.webp', '').split('-')[0];
      (info.poses[key] ||= []).push(pathToFileURL(path.join(root, 'characters/claude', file)).href);
    }
    info.sheets = {};
    for (const file of fs.readdirSync(path.join(root, 'characters/claude/anim')).filter(f => f.endsWith('.webp')))
      info.sheets[file.replace('.webp', '')] = pathToFileURL(path.join(root, 'characters/claude/anim', file)).href;
    await page.setViewportSize({ width: 260, height: 362 });
    await page.evaluate(info => { init({ characters: { claude: info } }); setCharacter('claude', true); }, info);
    await page.waitForTimeout(400);

    // 人物位置看 #stage 的布局位置（offsetTop 不受 transform 动画影响）
    const show = (style, ageMin, resetIn) => page.evaluate(([style, ageMin, resetIn]) => {
      const t = Date.now() / 1000;
      setQuotaStyle(style);
      setQuota('claude', { text: '5小时 剩余 93% ｜ 本周 剩余 36% · 06:00 重置', low: false,
        bars: [{ label: '5小时', left: .93, reset: t + 3600 }, { label: '本周', left: .36, reset: t + resetIn }] }, t - ageMin * 60);
      return { text: document.getElementById('quota').innerText, foot: document.getElementById('foot').offsetHeight, bodyTop: document.getElementById('stage').offsetTop };
    }, [style, ageMin, resetIn]);

    for (const style of ['text', 'bars']) {
      const freshView = await show(style, 1, 7200);
      assert.doesNotMatch(freshView.text, /前的数据/, style);
      const stale = await show(style, 90, 7200);
      assert.match(stale.text, /1 小时前的数据/, style);
      assert.equal(stale.bodyTop, freshView.bodyTop, `${style}: stale note must not push the character`);
      const reset = await show(style, 90, -10);
      assert.match(reset.text, /已重置/, style);
      assert.doesNotMatch(reset.text, /36%/, style);
      console.log(`  ${style}: foot ${freshView.foot}px → ${stale.foot}px with note`);
    }

    // 每分钟重算：数据不变，时间一过就自动变成已重置
    await page.evaluate(() => {
      const t = Date.now() / 1000;
      setQuotaStyle('text');
      setQuota('claude', { text: 'x', bars: [{ label: '本周', left: .36, reset: t + 1 }] }, t);
    });
    assert.match(await page.locator('#quota').innerText(), /本周 剩余 36%/);
    await page.waitForTimeout(1500);
    await page.evaluate(() => applyQuota('claude'));   // 等同定时器那一下
    assert.match(await page.locator('#quota').innerText(), /本周 已重置/);
    assert.deepEqual(errors, []);
    console.log('PASS: quota page (text/bars stale note, reset passed, re-evaluate)');
  } finally {
    await browser.close();
  }
})().catch(e => { console.error(e); process.exitCode = 1; });
