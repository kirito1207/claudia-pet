// Requires Playwright (NODE_PATH may point to the bundled runtime's node_modules).
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');
const { pathToFileURL } = require('url');
const assert = require('assert/strict');
const root = path.resolve(__dirname, '..');
(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', e => errors.push(e.message));
    await page.addInitScript(() => {
      window.messages = [];
      window.chrome = { webview: { postMessage: m => window.messages.push(m) } };
    });
    await page.goto(pathToFileURL(path.join(root, 'web/settings.html')).href);
    const state = { version: 'test', size: 1, textSize: 1, textLinked: true, physicsEnabled: true,
      idleEnabled: true, idleFrequency: 'normal', idleActions: ['hum','stretch','yawn','clawd'],
      clickBounce: true,
      notifyResults: true, notifyWaiting: true, notifyBackgroundOnly: true,
      ceilingBounce: true, physics: { gravity: 1400, throwPower: 1, restitution: .72, groundFriction: 3, elasticity: .12 } };
    await page.evaluate(s => { setState(s); showTab('physics'); }, state);
    for (const width of [388, 420, 528, 760]) {
      await page.setViewportSize({ width, height: 660 });
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `overflow at ${width}`);
      assert.equal(await page.locator('[data-physics]:visible').count(), 5);
    }
    await page.locator('#restitution').fill('0.85');
    await page.locator('#restitution').dispatchEvent('change');
    assert.ok(await page.evaluate(() => messages.some(m => m.key === 'restitution' && m.value === .85)));
    await page.evaluate(s => setState({ ...s, physicsEnabled: false }), state);
    assert.equal(await page.locator('#gravity').isDisabled(), true);
    await page.locator('#resetPhysics').click();
    assert.ok(await page.evaluate(() => messages.some(m => m.cmd === 'resetPhysics')));
    await page.setViewportSize({ width: 528, height: 760 });
    await page.evaluate(s => setState(s), state);
    await page.screenshot({ path: path.join(root, 'build/physics-settings.png'), fullPage: true });
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.screenshot({ path: path.join(root, 'build/physics-settings-dark.png'), fullPage: true });
    for (const tab of ['daily','general']) {
      await page.evaluate(tab => showTab(tab), tab);
      for (const width of [388,528]) {
        await page.setViewportSize({width,height:760});
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `${tab} overflow at ${width}`);
      }
      await page.screenshot({path:path.join(root,`build/${tab}-settings.png`),fullPage:true});
    }
    await page.evaluate(() => showTab('daily'));
    await page.locator('#idleFrequency [data-v="lively"]').click();
    assert.ok(await page.evaluate(() => messages.some(m => m.key === 'idleFrequency' && m.value === 'lively')));
    await page.locator('[data-idle-action="clawd"]').uncheck();
    assert.ok(await page.evaluate(() => messages.some(m => m.key === 'idleActions' && !m.value.includes('clawd'))));
    await page.goto(pathToFileURL(path.join(root, 'web/index.html')).href);
    const info = JSON.parse(fs.readFileSync(path.join(root, 'characters/claude/character.json'), 'utf8'));
    info.poses = {};
    for (const file of fs.readdirSync(path.join(root, 'characters/claude')).filter(f => f.endsWith('.webp'))) {
      const key = file.replace('.webp', '').split('-')[0];
      (info.poses[key] ||= []).push(pathToFileURL(path.join(root, 'characters/claude', file)).href);
    }
    // 和外壳的清单一样带上动画（被动画替换的姿态已经没有静态立绘）
    info.sheets = {};
    for (const file of fs.readdirSync(path.join(root, 'characters/claude/anim')).filter(f => f.endsWith('.webp')))
      info.sheets[file.replace('.webp', '')] = pathToFileURL(path.join(root, 'characters/claude/anim', file)).href;
    await page.setViewportSize({ width: 260, height: 362 });
    await page.evaluate(info => {
      init({ characters: { claude: info } }); setCharacter('claude', true);
      setQuota('claude', { text: '5小时 剩余 60% ｜ 本周 剩余 70%', bars: [{ label: '5小时', left: .6 }, { label: '本周', left: .7 }] });
      setQuotaStyle('bars');
    }, info);
    await page.waitForTimeout(650);
    const before = await page.locator('#quota').boundingBox();
    await page.evaluate(() => physicsImpact(0, 1400, .25));
    await page.waitForTimeout(65);
    const after = await page.locator('#quota').boundingBox();
    assert.deepEqual(after, before, 'impact must not transform quota');
    await page.evaluate(() => { setMotionActive(true); setPose('dizzy'); });
    assert.equal(await page.locator('#body').evaluate(el => getComputedStyle(el).animationName), 'none');
    assert.equal(await page.locator('#body').evaluate(el => getComputedStyle(el).transform), 'none');
    assert.equal(await page.locator('#a').evaluate(el => getComputedStyle(el).transitionDuration), '0s');
    assert.equal(await page.locator('#body img.on').count(), 1, 'moving must show just one silhouette');
    await page.evaluate(() => setMotionActive(false));
    assert.equal(await page.locator('#body').evaluate(el => getComputedStyle(el).animationName), 'dizzy');
    // Edge-hit poses: an opening move, then a loop that keeps going (they must never freeze).
    for (const [p, intro, loop] of [['bonk','bonk','wince'], ['stagger','stagger','wobble'], ['fallen','plop','breathe'], ['faceplant','flop','breathe']]) {
      await page.evaluate(p => setPose(p, true), p);
      const anim = await page.locator('#body').evaluate(el => ({ name: getComputedStyle(el).animationName, count: getComputedStyle(el).animationIterationCount }));
      assert.equal(anim.name, `${intro}, ${loop}`, p); assert.equal(anim.count, '1, infinite', p);
    }
    await page.evaluate(() => {
      petMotion.begin({ id: 99, x: 600, y: 300, downX: 700, downY: 400, px: 700, py: 400, time: 0,
        enabled: true, physics: DshPhysics.DEFAULT_PHYSICS, bounds: { minX: 0, minY: 0, maxX: 1600, maxY: 900 } });
      for (let i = 1; i <= 4; i++) petMotion.pointer({ id: 99, px: 700 + 10 * i, py: 400, time: 8 * i });
    });
    await page.waitForTimeout(50);
    const release = await page.evaluate(() => {
      const p = messages.filter(m => m.motion?.id === 99).at(-1).motion;
      petMotion.end({ id: 99, x: p.x, y: p.y, time: 33 }); return p;
    });
    await page.waitForTimeout(70);
    const flight = await page.evaluate(() => messages.filter(m => m.motion?.id === 99 && m.motion.phase === 'flight').at(-1)?.motion);
    assert.ok(flight && flight.x > release.x + 8, 'real browser rAF did not fling sideways');
    assert.ok(flight.y - release.y < flight.x - release.x, 'horizontal fling fell vertically');
    await page.evaluate(() => petMotion.stop());
    await page.evaluate(() => {
      setBehavior({idleEnabled:true,idleFrequency:'lively',idleActions:['hum']});
      setPose('idle',true); lastActive=Date.now()-200000; idleTick();
    });
    assert.equal(await page.evaluate(()=>pose),'hum');
    await page.evaluate(() => {setBehavior({idleEnabled:false});setPose('idle',true);idleTick();});
    assert.equal(await page.evaluate(()=>pose),'idle');
    const clickStart = await page.locator('#quota').boundingBox();
    await page.evaluate(()=>{setBehavior({clickBounce:true,idleEnabled:false});setPose('idle',true);handleClick('pat');});
    assert.equal(await page.evaluate(()=>pose),'idle','reaction started before Q bounce');
    await page.waitForTimeout(80);
    assert.equal(await page.locator('#body').evaluate(el=>getComputedStyle(el).animationPlayState),'paused');
    assert.deepEqual(await page.locator('#quota').boundingBox(),clickStart);
    await page.waitForFunction(()=>!clickSequence.busy);
    assert.equal(await page.evaluate(()=>pose),'pat','normal reaction did not follow bounce');
    await page.evaluate(()=>{sulking=true;coax=0;setPose('sulk',true);handleClick('pat');handleClick('pat');handleClick('pat');});
    await page.waitForFunction(()=>!clickSequence.busy);
    assert.equal(await page.evaluate(()=>pose),'forgiven','rapid pats no longer coax her');
    await page.evaluate(()=>{handleClick('pat');shownAt=0;applyCharState('claude',{pose:'reading',event:'PreToolUse',tool:'Read',ts:Date.now()/1000});});
    await page.waitForTimeout(280);
    assert.equal(await page.evaluate(()=>pose),'reading','delayed click replaced work state');
    await page.emulateMedia({reducedMotion:'reduce'});
    await page.evaluate(()=>{handleClick('poke');});
    assert.equal(await page.evaluate(()=>clickSequence.busy),false,'reduced-motion still played bounce');
    await page.waitForFunction(() => active.naturalWidth > 0);
    assert.equal(await page.evaluate(() => typeof setTucked), 'undefined');
    assert.deepEqual(errors, []);
    console.log('PASS: settings layout, quota isolation, motion clarity and real browser rAF horizontal fling');
  } finally { await browser.close(); }
})().catch(e => { console.error(e); process.exitCode = 1; });
