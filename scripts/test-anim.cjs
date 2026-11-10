// 动画动作（claude-pet 动作包精灵图）：播放、对齐、睡觉来回播、再见的帧段、设置 / 省电 / 减少动画退回静态、读不到时退回静态。
// Requires Playwright (NODE_PATH may point to the bundled runtime's node_modules).
const { chromium } = require('playwright')
const fs = require('fs')
const path = require('path')
const { pathToFileURL } = require('url')
const assert = require('assert/strict')
const root = path.resolve(__dirname, '..')
const dir = path.join(root, 'characters/claude')
const url = f => pathToFileURL(f).href

// 和 Store.Manifest 同一套规则：文件名去掉 -2、-3 是姿态名；anim\ 里是精灵图
function manifest() {
  const info = JSON.parse(fs.readFileSync(path.join(dir, 'character.json'), 'utf8').replace(/^﻿/, ''))
  info.poses = {}
  for (const f of fs.readdirSync(dir).filter(f => /\.(webp|png)$/i.test(f)).sort()) {
    const stem = f.replace(/\.[^.]+$/, '')
    if (['portrait', 'portrait-card', 'avatar'].includes(stem)) continue
    if (stem === 'idle-blink') { info.blink = url(path.join(dir, f)); continue }
    if (stem === 'idle') info.blinkBase = url(path.join(dir, f))
    ;(info.poses[stem.split('-')[0]] ||= []).push(url(path.join(dir, f)))
  }
  info.sheets = {}
  for (const f of fs.readdirSync(path.join(dir, 'anim')).filter(f => f.endsWith('.webp')))
    info.sheets[f.replace(/\.webp$/, '')] = url(path.join(dir, 'anim', f))
  return { characters: { claude: info } }
}

;(async () => {
  const m = manifest()
  // 配置里引用的精灵图都在 anim\ 里
  for (const [p, a] of Object.entries(m.characters.claude.animations))
    for (const n of a.sheets) assert.ok(m.characters.claude.sheets[n], `${p} 用的 ${n}.webp 不在 anim 里`)
  // 替换掉的姿态都还留着静态立绘（关掉动画、读不到时用）；只有动画的是新加的小动作、余额钱袋和拖拽
  const replaced = ['idle', 'thinking', 'writing', 'running', 'compact', 'sleeping', 'happy', 'surprised', 'pat', 'bye', 'hum', 'stretch', 'yawn',
    'fest_spring', 'fest_lantern', 'fest_dragonboat', 'fest_midautumn', 'fest_halloween', 'fest_christmas']
  for (const p of replaced) assert.ok(m.characters.claude.animations[p], `${p} 要有动画`)
  // 被替换的静态立绘已移出项目，只留待机图兜底（程序靠它识别角色）
  assert.ok(m.characters.claude.poses.idle && m.characters.claude.blink, '待机图和眨眼帧要留着')
  for (const p of replaced.filter(p => p !== 'idle')) assert.ok(!m.characters.claude.poses[p], `${p} 的旧静态立绘应已移出`)
  // 帧数不满 120 的两张，配置里要把结束帧写对
  assert.equal(m.characters.claude.animations.qingtuan.to, 93); assert.equal(m.characters.claude.animations.fest_newyear.to, 118)   // 元旦里有吃糖葫芦（2026-11-10 起）

  const browser = await chromium.launch({ channel: 'msedge', headless: true })
  try {
    const page = await browser.newPage({ viewport: { width: 580, height: 640 } })
    const errors = []
    page.on('pageerror', e => errors.push(e.message))
    await page.addInitScript(() => { window.messages = []; window.chrome = { webview: { postMessage: msg => window.messages.push(msg) } } })
    await page.goto(url(path.join(root, 'web/index.html')))
    await page.evaluate(m => { init(m); setBehavior({ idleEnabled: false }) }, m)
    const state = () => page.evaluate(() => ({ pose, url: sheet?.url ? decodeURIComponent(sheet.url).split('/').pop() : null, frame: sheet?.frame ?? null,
      holding: !!sheet?.holding, on: document.documentElement.classList.contains('sheet-on'), front: !!sheetFront?.classList.contains('on'),
      bodyClass: $('body').className, fx: $('fx').innerHTML.length }))
    const waitSheet = () => page.waitForFunction(() => sheet?.img && document.documentElement.classList.contains('sheet-on'))

    await page.evaluate(() => { pose = null; setPose('idle', true) }); await waitSheet()
    let s = await state()
    assert.equal(s.url, '待机呼吸休闲.webp'); assert.ok(s.on && s.front); assert.equal(s.bodyClass, '')

    // 对齐：精灵图里的脚底（第 321 行）落在人物框底边，中线（第 325 列）落在人物框中间，人物高和静态立绘一样
    const place = await page.evaluate(() => {
      const b = $('body'), cv = sheetFront, S = parseFloat(cv.style.height) / 360
      return { feet: parseFloat(cv.style.top) + 321 * S, center: parseFloat(cv.style.left) + 325 * S, charH: 281 * S, bodyH: b.offsetHeight, bodyW: b.offsetWidth }
    })
    assert.ok(Math.abs(place.feet - place.bodyH) < 1, 'feet not on the bottom edge')
    assert.ok(Math.abs(place.center - place.bodyW / 2) < 1, 'not centred')
    assert.ok(Math.abs(place.charH - place.bodyH * 638 / 640) < 1, 'height differs from the still sprite')

    const steadyLayoutReads = await page.evaluate(() => {
      const place = sheetPlace
      let calls = 0
      sheetPlace = (...args) => { calls++; return place(...args) }
      try {
        for (let i = 0; i < 3; i++) sheetTick()
        return calls
      } finally { sheetPlace = place }
    })
    assert.equal(steadyLayoutReads, 0, 'steady sprite frames recomputed canvas geometry')
    const normalWidth = await page.evaluate(() => {
      $('body').style.height = '85%'
      return sheetFront.width
    })
    await page.waitForFunction(width => sheetFront.width > width, normalWidth)
    await page.evaluate(() => { $('body').style.height = '' })
    await page.waitForFunction(width => sheetFront.width === width, normalWidth)

    const pausedFrame = await page.evaluate(() => {
      setAuraMode('on')
      setPageHidden(true)
      return sheet.frame
    })
    await page.waitForTimeout(250)
    assert.ok(await page.evaluate(frame => sheet.frame === frame && sheet.timer === null && auraTimer === null, pausedFrame),
      'hidden animation or aura kept running')
    assert.equal(await page.evaluate(() => { setPageHidden(false); return sheet.frame }), pausedFrame, 'resume skipped the paused frame')
    await page.waitForFunction(frame => sheet.frame !== frame, pausedFrame)

    await page.evaluate(() => {
      setPageHidden(true)
      playSheet({ ...anim('idle'), to: 2, loop: false }, null)
    })
    await waitSheet()
    assert.ok(await page.evaluate(() => sheet.frame === 0 && sheet.timer === null && auraTimer === null),
      'a sheet loaded while hidden started a timer')
    await page.evaluate(() => setPageHidden(false))
    await page.waitForFunction(() => sheet.frame === 2)
    const held = await page.evaluate(() => ({ spriteStopped: sheet.timer === null, auraRunning: auraTimer !== null, aura: $('aura').style.transform }))
    assert.ok(held.spriteStopped && held.auraRunning, 'the final sprite frame did not hand over to the aura timer')
    await page.waitForFunction(aura => $('aura').style.transform !== aura, held.aura)
    assert.equal(await page.evaluate(() => sheet.frame), 2, 'a completed sheet advanced past the final frame')
    await page.evaluate(() => setAuraMode('off'))
    assert.ok(await page.evaluate(() => sheet.timer === null && auraTimer === null), 'hidden aura kept a timer')
    await page.evaluate(() => setAuraMode('auto'))

    // 动画姿态不画我们的特效；临时状态停满整段
    await page.evaluate(() => show('hum', true)); await waitSheet()
    s = await state(); assert.equal(s.url, '悠闲哼歌.webp'); assert.equal(s.fx, 0)
    assert.ok(await page.evaluate(() => transientMs('hum') >= 10000 && transientMs('pat') >= 10000))
    // 不耐烦 2026-11-06 换回 CrossPet 的静态图（和鼓脸、红温、赌气一串）；傲娇生气的动画改成点击反应 pout
    assert.ok(await page.evaluate(() => !anim('annoyed') && framesFor('annoyed')[0]?.includes('annoyed') && transientMs('annoyed') === 4500))
    assert.ok(await page.evaluate(() => transientMs('pout') >= 10000))
    await page.evaluate(() => clearTimeout(revertTimer))   // 下面逐个切姿态，别让哼歌的计时中途把她切回待机
    const pats = new Set()
    for (let i = 0; i < 12; i++) { await page.evaluate(() => { pose = null; setPose('pat', true) }); await waitSheet(); pats.add((await state()).url) }
    assert.ok(pats.size >= 2 && [...pats].every(u => u.startsWith('点击回应-')), 'pat should pick among click reactions')
    // 点击池：摸头 4 段 + 傲娇生气 1 段，每段机会一样
    const clickPicks = await page.evaluate(() => { const n = { pat: 0, pout: 0 }; for (let i = 0; i < 2000; i++) n[pickClickPose()]++; return n })
    assert.ok(clickPicks.pout > 300 && clickPicks.pout < 520 && clickPicks.pat > 1480, `click pool ${JSON.stringify(clickPicks)}`)
    // 每点一下都从头重播（照 dsh-pet）：演到一半再点，换一段（或同一段）从第一帧开始，台词也重来
    await page.evaluate(() => { clickBounce = false; pose = null; setPose('idle', true); performClick('pat') }); await waitSheet()
    await page.waitForTimeout(1500)
    const before = await page.evaluate(() => ({ frame: sheet.frame, pose }))
    assert.ok(['pat', 'pout'].includes(before.pose) && before.frame > 10, `click reaction playing ${JSON.stringify(before)}`)
    await page.evaluate(() => performClick('pat')); await waitSheet()
    const after = await page.evaluate(() => ({ frame: sheet.frame, pose, said: (clipLines() || linesOf(pose) || []).includes($('bubble').textContent) }))
    assert.ok(after.frame < 6, `click restarted from frame ${after.frame}`)
    assert.ok(after.said, 'restart said a line of the new reaction')
    // 每段点击动画说它自己的台词（clipLines），四段摸头动作不再共用一套摸头台词
    const clipSaid = {}
    for (let i = 0; i < 40 && Object.keys(clipSaid).length < 4; i++) {
      const r = await page.evaluate(() => { pose = null; show('pat', true); const n = decodeURIComponent(sheet.url.split('/').pop()).replace('.webp', ''); return [n, $('bubble').textContent] })
      clipSaid[r[0]] = r[1]
    }
    const clipInfo = JSON.parse(fs.readFileSync(path.join(root, 'characters/claude/character.json'), 'utf8')).clipLines
    assert.equal(Object.keys(clipSaid).length, 4, `saw clips ${Object.keys(clipSaid)}`)
    for (const [n, line] of Object.entries(clipSaid)) assert.ok(clipInfo[n]?.includes(line), `${n} said "${line}", not its own line`)
    assert.ok(await page.evaluate(() => { pose = null; show('pout', true); return linesOf('pout').includes($('bubble').textContent) }), 'pout keeps its own lines')
    await page.evaluate(() => { clearTimeout(revertTimer); setPose('idle', true) })

    // 再见从挥手那段开始
    await page.evaluate(() => { pose = null; setPose('bye', true) }); await waitSheet()
    assert.ok((await state()).frame >= 40)

    // 睡觉：入睡后在 28～76 帧来回，不会醒过来
    await page.evaluate(() => { pose = null; setPose('sleeping', true) }); await waitSheet()
    await page.waitForTimeout(7500)
    s = await state(); assert.ok(s.holding && s.frame >= 28 && s.frame <= 76, `sleeping frame ${s.frame}`)

    // 睡着时点她：先演醒过来（第 77 帧往后），演完才回待机，中间不露静态图；醒的时候再点不重来
    const wake = await page.evaluate(async () => {
      messages.length = 0; performClick('pat'); performClick('pat')
      const seen = { minFrame: 999, gaps: 0, poseDuring: pose, bubble: $('bubble').textContent }
      for (const t0 = performance.now(); pose === 'sleeping' && performance.now() - t0 < 6000;) {
        await new Promise(r => requestAnimationFrame(r))
        if (pose === 'sleeping' && sheet?.img) seen.minFrame = Math.min(seen.minFrame, sheet.frame)
        if (!document.documentElement.classList.contains('sheet-on')) seen.gaps++
      }
      return { ...seen, pose, bubbleAfter: $('bubble').textContent }
    })
    assert.equal(wake.poseDuring, 'sleeping'); assert.ok(wake.minFrame >= 77, `wake started at ${wake.minFrame}`)
    assert.equal(wake.gaps, 0); assert.equal(wake.pose, 'idle'); assert.equal(wake.bubble, '唔…你回来啦'); assert.equal(wake.bubbleAfter, '唔…你回来啦')
    // 睡着时 Claude 开始干活：也先醒过来再去干活
    await page.evaluate(() => { clearTimeout(revertTimer); pose = null; setPose('sleeping', true) }); await waitSheet()
    await page.evaluate(() => show('thinking', true))
    assert.equal((await state()).pose, 'sleeping')
    await page.waitForFunction(() => pose === 'thinking', null, { timeout: 6000 })

    // 节日打招呼：节日已经换成动画（没有静态图），当天第一次见面要演动画，不能只说台词；国庆不再打招呼
    const fest = await page.evaluate(async () => {
      const real = festivalOf; localStorage.removeItem('claudia-greeted')
      festivalOf = () => 'spring'; clearTimeout(revertTimer); setPose('idle', true); shownAt = 0
      const greeted = timeGreet('claude', false)
      await new Promise(r => setTimeout(r, 600))
      const out = { greeted, pose, url: sheet?.url ? decodeURIComponent(sheet.url).split('/').pop() : null, again: timeGreet('claude', false) && pose }
      festivalOf = real
      out.national = festivalOf(new Date('2026-10-02T10:00:00'))
      return out
    })
    assert.ok(fest.greeted); assert.equal(fest.pose, 'fest_spring'); assert.equal(fest.url, '舞狮头.webp')
    assert.equal(fest.national, null)
    await page.evaluate(() => { clearTimeout(revertTimer); setPose('idle', true) })

    // 没有动画的姿态：静态立绘 + 原来的晃动和特效
    await page.evaluate(() => setPose('dizzy', true)); await page.waitForTimeout(500)
    s = await state(); assert.equal(s.url, null); assert.equal(s.on, false); assert.equal(s.bodyClass, 'm-dizzy')
    // 多会话的左右姿态也是静态
    await page.evaluate(() => setPose('busy_left', true)); s = await state(); assert.equal(s.on, false)

    // 设置关掉动画：当前姿态立刻换回静态；再打开换回动画
    await page.evaluate(() => { setPose('thinking', true) }); await waitSheet()
    await page.evaluate(() => setBehavior({ idleEnabled: false, animatedSprites: false }))
    s = await state(); assert.equal(s.url, null); assert.equal(s.on, false); assert.equal(s.bodyClass, 'm-rock')
    await page.evaluate(() => setBehavior({ idleEnabled: false })); await waitSheet()
    assert.equal((await state()).url, '工作状态-思考冒泡.webp')

    // 省电：待机 / 睡觉用静态，干活的样子照常动
    await page.evaluate(() => { setPowerSave(true); pose = null; setPose('idle', true) }); await page.waitForTimeout(300)
    assert.equal((await state()).on, false)
    await page.evaluate(() => setPose('writing', true)); await waitSheet()
    assert.equal((await state()).url, '写代码.webp')   // 2026-11-10 写东西只用写代码，轻快记录给了听你说
    await page.evaluate(() => setPowerSave(false))

    // 系统要求减少动画：全部静态
    await page.emulateMedia({ reducedMotion: 'reduce' })
    await page.evaluate(() => { pose = null; setPose('idle', true) }); await page.waitForTimeout(300)
    assert.equal((await state()).on, false)
    await page.emulateMedia({ reducedMotion: 'no-preference' })

    // 拖着她：盖一层「被拎起来」，松手后回到原来的常态动画；被甩出火气后不盖
    await page.evaluate(() => { pose = null; setPose('idle', true) }); await waitSheet()
    await page.evaluate(() => dragSheet({ phase: 'drag' })); await page.waitForFunction(() => sheet?.key === 'drag' && sheet.img)
    assert.equal((await state()).url, '被鼠标拖拽悬空反馈.webp')
    // 松手（原地放下）：动画一直盖着，不露出静态立绘
    const gap = await page.evaluate(async () => {
      let shown = 0, frames = 0
      dragSheet({ phase: 'end' })
      // 松手那一刻：动画还得盖着（拖动时静态图的淡入淡出是关掉的，一去掉就是整张露出来）
      if (!document.documentElement.classList.contains('sheet-on') || !sheetCanvases.some(c => c.classList.contains('on'))) shown++
      for (const t0 = performance.now(); performance.now() - t0 < 1200; frames++) {
        await new Promise(r => requestAnimationFrame(r))
        if (!document.documentElement.classList.contains('sheet-on') || !sheetCanvases.some(c => c.classList.contains('on'))) shown++
      }
      return { shown, frames }
    })
    assert.equal(gap.shown, 0, `still sprite showed in ${gap.shown}/${gap.frames} frames after release`)
    s = await state(); assert.equal(s.pose, 'idle'); assert.equal(s.url, '待机呼吸休闲.webp')
    await page.evaluate(() => { setPose('annoyed', true); dragSheet({ phase: 'drag' }) })
    assert.notEqual(await page.evaluate(() => sheet?.key), 'drag')
    await page.evaluate(() => { clearTimeout(revertTimer); setPose('idle', true) })

    // 偷吃：吃的那段来回播；点她接被抓包（从第 52 帧开始）
    await page.evaluate(() => show('snack', true)); await waitSheet()
    await page.waitForTimeout(5500)
    s = await state(); assert.ok(s.holding && s.frame >= 34 && s.frame <= 51, `snack frame ${s.frame}`)
    await page.evaluate(() => performClick('pat')); await waitSheet()
    s = await state(); assert.equal(s.pose, 'snack_caught'); assert.ok(s.frame >= 52)
    await page.evaluate(() => { clearTimeout(revertTimer); setPose('idle', true) })
    // 偷吃没被抓、自己吃完：先演收起零食（第 100 帧往后），再回待机
    await page.evaluate(() => show('snack', true)); await waitSheet()
    const snackEnd = await page.evaluate(async () => {
      const seen = { minFrame: 999 }
      for (const t0 = performance.now(); pose === 'snack' && performance.now() - t0 < 14000;) {
        await new Promise(r => requestAnimationFrame(r))
        if (exiting && sheet?.img) seen.minFrame = Math.min(seen.minFrame, sheet.frame)
      }
      return { ...seen, pose }
    })
    assert.equal(snackEnd.pose, 'idle'); assert.ok(snackEnd.minFrame >= 100 && snackEnd.minFrame < 999, `snack exit frame ${snackEnd.minFrame}`)

    // 余额：第一次拿到额度不演，换档才演对应钱袋
    await page.evaluate(() => setQuota('claude', { text: 't', low: false, bars: [{ label: '5小时', left: .9 }] }))
    assert.equal((await state()).pose, 'idle')
    await page.evaluate(() => setQuota('claude', { text: 't', low: false, bars: [{ label: '5小时', left: .6 }, { label: '本周', left: .3 }] }))
    await waitSheet(); s = await state(); assert.equal(s.pose, 'quota_count'); assert.equal(s.url, '余额-数金皱眉.webp')
    await page.evaluate(() => { clearTimeout(revertTimer); setPose('idle', true) })

    // 闲置随机池：按分组展开（测试里直接限定分组，设置页已不能挑）；关掉动画就没有动画小动作
    const pool = (groups, animatedSprites = true) => page.evaluate(([g, on]) => { setBehavior({ idleEnabled: true, idleFrequency: 'lively', animatedSprites: on }); idleSettings.actions = g; clearTimeout(idleTimer)
      const extra = Object.entries(char().animations).filter(([p, a]) => a.idle && idleSettings.actions.includes(a.idle) && anim(p)).map(([p]) => p)
      return IdlePolicy.candidates(idleSettings, { pose: 'idle', quiet: 0, extra, available: ['hum'] }) }, [groups, animatedSprites])
    assert.deepEqual((await pool(['magic'])).sort(), ['animals', 'bookspirit', 'butterflies', 'dove', 'flower'])
    const groupOf = g => Object.entries(JSON.parse(fs.readFileSync(path.join(root, 'characters/claude/character.json'), 'utf8')).animations).filter(([, a]) => a.idle === g).map(([p]) => p)
    assert.deepEqual((await pool(['hum', 'fidget'])).sort(), ['hum', ...groupOf('fidget')].sort())
    for (const p of ['look', 'tap', 'cube', 'bubbles', 'nails', 'pen_spin', 'eye_rub']) assert.ok(groupOf('fidget').includes(p), `fidget group lacks ${p}`)
    // 2026-11-07 新加的手工组：开了才有，关了没有
    assert.deepEqual((await pool(['craft'])).sort(), groupOf('craft').sort()); assert.ok(groupOf('craft').length >= 5)   // 2026-11-09 手工从 12 个减到 5 个
    assert.ok(!(await pool(['play'])).some(p => groupOf('craft').includes(p)))
    // 拆礼物没有专门的触发时机，放在玩耍组里随机
    assert.ok((await pool(['play'])).includes('update'))
    await page.evaluate(() => { setBehavior({ idleEnabled: true }); idleSettings.actions = ['hum'] })
    await page.evaluate(() => { setPose('idle', true); lastActive = Date.now(); idleTick() })
    assert.equal((await state()).pose, 'hum')
    await page.evaluate(() => clearTimeout(revertTimer))
    assert.deepEqual(await pool(['fidget', 'magic', 'snack', 'claude'], false), [])
    await page.evaluate(() => setBehavior({ idleEnabled: false }))

    // 时令：按月份、时段、公历和农历日期筛选（农历用浏览器自带的日历）
    const at = s => page.evaluate(s => { setBehavior({ idleEnabled: false }); idleSettings.actions = ['food', 'season', 'play']; clearTimeout(idleTimer); return idleAnimations(new Date(s)) }, s)
    let list = await at('2026-08-19T12:30:00')   // 农历七月初七、夏天、中午
    for (const p of ['qixi', 'rice', 'watermelon', 'fan', 'watergun']) assert.ok(list.includes(p), `${p} missing on 2026-08-19 noon`)
    for (const p of ['dinner', 'snow_step', 'hotpot', 'zhongyuan', 'chongyang', 'laba']) assert.ok(!list.includes(p), `${p} should not be on 2026-08-19 noon`)
    list = await at('2026-10-18T18:00:00')   // 农历九月初九、秋天、傍晚
    for (const p of ['chongyang', 'chongyanggao', 'rice', 'leaves', 'crab']) assert.ok(list.includes(p), `${p} missing on 2026-10-18 evening`)
    list = await at('2027-01-15T09:00:00')   // 农历腊月初八、冬天、上午
    // 堆雪人、糖葫芦、年糕 2026-11-10 起归元旦，不在随机里
    for (const p of ['laba', 'snow_step', 'hand_warmer', 'sweet_potato']) assert.ok(list.includes(p), `${p} missing on 2027-01-15`)
    assert.ok(!list.includes('rice') && !list.includes('hotpot'))
    list = await at('2026-04-05T15:00:00'); assert.ok(list.includes('qingtuan') && list.includes('kite'))
    // 2026-11-07 新加的时令 / 吃喝：夏天有刨冰、听蝉、汽水，冬天有踏雪、烤红薯、暖手炉
    list = await at('2026-08-19T12:30:00')
    for (const p of ['shaved_ice', 'cicada', 'soda', 'corn', 'sunshade', 'pudding']) assert.ok(list.includes(p), `${p} missing in August`)
    for (const p of ['snow_step', 'sweet_potato', 'hand_warmer', 'snowflake']) assert.ok(!list.includes(p), `${p} should not be in August`)
    list = await at('2027-01-15T09:00:00')
    for (const p of ['snow_step', 'sweet_potato', 'hand_warmer', 'warm_breath', 'snowflake']) assert.ok(list.includes(p), `${p} missing in January`)
    for (const p of ['cicada', 'shaved_ice', 'soda']) assert.ok(!list.includes(p), `${p} should not be in January`)
    // 2026-11-10 起这些归问候：早上好=刷牙、中午好=午餐、下午好=咖啡/可可、晚上好=晚餐、晚安=拍枕头，随便什么时候都不在随机里；凌晨不再打招呼
    const anyTime = await page.evaluate(() => { setBehavior({ idleEnabled: false }); idleSettings.actions = ['fidget', 'food']; clearTimeout(idleTimer)
      return ['2026-11-07T08:00:00', '2026-11-07T12:00:00', '2026-11-07T15:00:00', '2026-11-07T18:00:00', '2027-01-15T15:00:00', '2026-11-07T23:30:00'].flatMap(s => idleAnimations(new Date(s))) })
    for (const p of ['lunch', 'dinner', 'toothbrush', 'coffee', 'cocoa', 'pillow']) assert.ok(!anyTime.includes(p), `${p} should be a greeting now, not idle`)
    const greet = await page.evaluate(() => Object.fromEntries(['greet_morning', 'greet_noon', 'greet_afternoon', 'greet_evening', 'greet_night', 'greet_dawn'].map(p => [p, anim(p)?.sheets || null])))
    assert.deepEqual(greet, { greet_morning: ['晨间刷牙'], greet_noon: ['吃午餐'], greet_afternoon: ['闻咖啡豆', '冲可可'], greet_evening: ['吃晚餐'], greet_night: ['拍枕头'], greet_dawn: null })
    // 抽签：先抽组再抽组里的，组多了也不会把哼歌挤掉
    const picks = await page.evaluate(() => { setBehavior({ idleEnabled: true, idleFrequency: 'lively' }); idleSettings.actions = ['hum', 'talent', 'play']; clearTimeout(idleTimer)
      const seen = {}; for (let i = 0; i < 300; i++) { setPose('idle', true); clearTimeout(revertTimer); lastActive = Date.now(); idleTick(); seen[pose] = (seen[pose] || 0) + 1 }
      clearTimeout(revertTimer); return seen })
    assert.ok(picks.hum > 60 && picks.hum < 140, `hum picked ${picks.hum}/300`)
    await page.evaluate(() => { setBehavior({ idleEnabled: false }); setPose('idle', true) })

    // 走路：起步动作演完（进入步伐循环）才让外壳挪窗口；外壳回 done 就站回去；这边没地方走就换方向；奔跑只朝左
    // 螃蟹步身体朝左：只往左走，指定往右不走
    assert.equal(await page.evaluate(() => startWalk('walk_crab', 1)), false)
    await page.evaluate(() => { messages.length = 0; startWalk('walk_crab') })
    await page.waitForFunction(() => messages.some(m => m.walk), null, { timeout: 5000 })
    let walkMsg = await page.evaluate(() => ({ m: messages.find(m => m.walk).walk, frame: sheet.frame, cycled: sheet.cycled }))
    assert.equal(walkMsg.m.dir, -1); assert.ok(walkMsg.m.speed > 0 && walkMsg.m.ms >= 3000); assert.ok(walkMsg.cycled && walkMsg.frame >= 37)
    await page.waitForTimeout(2500)
    s = await state(); assert.ok(s.frame >= 37 && s.frame <= 58, `crab cycle frame ${s.frame}`)
    // 走完：先演收尾（螃蟹步从第 92 帧播到最后），播完才回待机，中间一直是动画、不露静态图
    const outro = await page.evaluate(async () => {
      const keep = { img: sheet.img, canvas: sheet.canvas }
      walkEnded('done')
      // 收尾接着用正在播的这张图、这块画布：不重新读图、不交叉淡入（原来会卡一下再淡入，向左跑收尾看着生硬）
      if (sheet.img !== keep.img || sheet.canvas !== keep.canvas || sheet.frame !== 92) throw new Error('outro reloaded the sheet instead of continuing in place: frame ' + sheet.frame)
      const seen = { minFrame: 999, gaps: 0 }
      for (const t0 = performance.now(); pose !== 'idle' && performance.now() - t0 < 5000;) {
        await new Promise(r => requestAnimationFrame(r))
        if (pose === 'walk_crab' && sheet?.img) seen.minFrame = Math.min(seen.minFrame, sheet.frame)
        if (!document.documentElement.classList.contains('sheet-on')) seen.gaps++
      }
      return { ...seen, pose }
    })
    assert.equal(outro.pose, 'idle'); assert.ok(outro.minFrame >= 92, `outro started at ${outro.minFrame}`); assert.equal(outro.gaps, 0)
    await waitSheet(); assert.equal((await state()).url, '待机呼吸休闲.webp')
    // 换成另一张动画：新图读好、交叉淡入之前，旧动画接着往下播，不定格
    const fadeAdvance = await page.evaluate(async () => {
      show('hum', true)
      const f = fading; if (!f) return 'no fading sheet'
      const f0 = f.frame; await new Promise(r => setTimeout(r, 300))
      return f.frame > f0 ? 'ok' : `stuck at ${f0}`
    })
    assert.equal(fadeAdvance, 'ok', 'outgoing animation froze during the switch')
    await page.waitForFunction(() => !fading, null, { timeout: 3000 })
    await page.evaluate(() => { clearTimeout(revertTimer); show('idle', true) }); await waitSheet()
    await page.evaluate(() => { messages.length = 0; startWalk('walk_crab', -1) }); await page.waitForFunction(() => messages.some(m => m.walk))
    await page.evaluate(() => { messages.length = 0; walkEnded('blocked') })   // 左边没地方：螃蟹步不往右，换成漂浮踏步往右
    await page.waitForFunction(() => messages.some(m => m.walk)); assert.equal(await page.evaluate(() => messages.find(m => m.walk).walk.dir), 1)
    assert.equal((await state()).pose, 'walk_float')
    await page.evaluate(() => walkEnded('blocked')); assert.equal((await state()).pose, 'idle')   // 两边都没地方：站回去
    await page.evaluate(() => { messages.length = 0; startWalk('run_left') }); await page.waitForFunction(() => messages.some(m => m.walk), null, { timeout: 6000 })
    assert.equal(await page.evaluate(() => messages.find(m => m.walk).walk.dir), -1)
    await page.evaluate(() => { messages.length = 0; walkEnded('blocked') })   // 左边没地方：换成能往右走的
    await page.waitForFunction(() => messages.some(m => m.walk), null, { timeout: 6000 })
    s = await state(); assert.equal(s.pose, 'walk_float'); assert.equal(await page.evaluate(() => messages.find(m => m.walk).walk.dir), 1)
    // 走着被点 / 被拖：告诉外壳停下
    await page.evaluate(() => { messages.length = 0; performClick('pat') })
    assert.ok(await page.evaluate(() => messages.some(m => 'walk' in m && m.walk === null)))
    await page.evaluate(() => { clearTimeout(revertTimer); setPose('idle', true); messages.length = 0; startWalk('walk_float', 1) })
    await page.waitForFunction(() => messages.some(m => m.walk), null, { timeout: 6000 })
    await page.evaluate(() => { messages.length = 0; setMotionActive(true) })
    assert.ok(await page.evaluate(() => messages.some(m => 'walk' in m && m.walk === null)))
    await page.evaluate(() => { setMotionActive(false); clearTimeout(revertTimer); setPose('idle', true) })

    // 精灵图读不到：退回静态立绘，并报给外壳
    await page.evaluate(() => { char().sheets['被吓一跳'] = char().sheets['被吓一跳'] + '.missing'; pose = null; setPose('surprised', true) })
    await page.waitForFunction(() => !sheet && messages.some(x => x.imageError?.includes('.missing')))
    s = await state(); assert.equal(s.on, false); assert.equal(s.bodyClass, 'm-jolt')

    assert.deepEqual(errors, [])
    console.log('PASS: sheet playback, alignment, random click reactions, sleep hold, farewell range, drag overlay, snack caught, quota tiers, idle groups, seasonal/lunar filters, group-balanced picks, walking, still-sprite fallbacks (setting, power save, reduced motion, missing file)')
  } finally { await browser.close() }
})().catch(e => { console.error(e); process.exit(1) })
