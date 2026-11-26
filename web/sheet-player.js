// Sprite sheet loading, playback, and canvas transitions.
// 新动画的第一帧显示出来时通知一下：点击 Q 弹等它（照 dsh-pet：压的是点击动画的首帧，不是旧画面）
const sheetShownWaiters = []
function whenSheetShown(cb, timeout) {
  const w = { cb, timer: setTimeout(() => { const i = sheetShownWaiters.indexOf(w); if (i >= 0) { sheetShownWaiters.splice(i, 1); cb() } }, timeout) }
  sheetShownWaiters.push(w)
  return () => { const i = sheetShownWaiters.indexOf(w); if (i >= 0) { sheetShownWaiters.splice(i, 1); clearTimeout(w.timer) } }
}
function sheetShown() { for (const w of sheetShownWaiters.splice(0)) { clearTimeout(w.timer); w.cb() } }
function sheetPlace(cv, dpr) {
  const b = $('body'), S = b.offsetHeight * 638 / 640 / SHEET.charH   // 静态待机立绘里人物约占立绘高的 638/640
  const w = SHEET.w * S, h = SHEET.h * S
  cv.style.width = w + 'px'; cv.style.height = h + 'px'
  cv.style.left = (b.offsetWidth / 2 - SHEET.centerX * S) + 'px'
  cv.style.top = (b.offsetHeight - SHEET.feetY * S) + 'px'
  if (cv.tagName === 'VIDEO') return   // 视频按样式宽高显示，不设像素尺寸
  const W = Math.max(1, Math.round(w * dpr)), H = Math.max(1, Math.round(h * dpr))
  if (cv.width !== W || cv.height !== H) { cv.width = W; cv.height = H }
}
function sheetDraw(s = sheet) {
  if (!s?.img) return
  const dpr = window.devicePixelRatio || 1
  if (s.layoutVersion !== sheetLayoutVersion || s.dpr !== dpr) {
    sheetPlace(s.canvas, dpr)
    s.layoutVersion = sheetLayoutVersion
    s.dpr = dpr
  }
  if (s.video) return   // 视频自己出画面
  const ctx = s.canvas.getContext('2d'), c = s.frame % SHEET.cols, r = Math.floor(s.frame / SHEET.cols)
  const w = s.img.width / SHEET.cols, h = SHEET.h * w / SHEET.w
  ctx.clearRect(0, 0, s.canvas.width, s.canvas.height)
  ctx.imageSmoothingEnabled = true; ctx.imageSmoothingQuality = 'high'
  ctx.drawImage(s.img, c * w, r * h, w, h, 0, 0, s.canvas.width, s.canvas.height)
}
function sheetCanAdvance(s) {
  return !!s?.img && !s.video && (s.frame < s.to || !!s.hold || s.loop || !!s.cycle && s.frame >= s.cycle[1])
}
function sheetStep(s) {
  if (s.holding) {   // 来回播，没有接缝
    if (s.frame >= s.hold[1]) s.dir = -1; else if (s.frame <= s.hold[0]) s.dir = 1
    s.frame += s.dir
  } else if (s.cycle && s.frame >= s.cycle[1]) s.frame = s.cycle[0]   // 走路的步伐循环
  else if (s.frame < s.to) s.frame++
  else if (s.hold) { s.holding = true; s.dir = -1; s.frame-- }
  else s.frame = s.from
  if (s.video) videoSeek(s, s.frame)
}
function stopSheetClock(s) {
  clearTimeout(s.timer)
  s.timer = null
}
function startSheetClock(s, draw) {
  if (!s || s.timer !== null || pageHidden || !sheetCanAdvance(s)) return
  const period = 1000 / (s.fps || SHEET.fps)   // 每个动画可以有自己的帧率（animations[].fps；派活按 15 帧，2026-11-24）
  let due = performance.now() + period
  const tick = () => {
    const now = performance.now()
    if (now >= due) {
      // Recover small scheduling slips without replaying a burst after a long stall.
      const elapsed = 1 + Math.floor((now - due) / period)
      due = elapsed > 3 ? now + period : due + elapsed * period
      draw(Math.min(elapsed, 3))
    }
    if (s.timer !== null && sheetCanAdvance(s)) schedule()
    else s.timer = null
  }
  const schedule = () => { s.timer = setTimeout(tick, Math.max(1, Math.ceil(due - performance.now()))) }
  schedule()
}
function sheetTick(steps = 1) {
  const s = sheet
  if (pageHidden || !sheetCanAdvance(s)) return
  auraPulse()   // 和这一帧一起出，不多出一帧
  while (steps-- > 0 && sheetCanAdvance(s)) sheetStep(s)
  sheetDraw()
  if (s.cycle && !s.cycled && s.frame >= s.cycle[0]) { s.cycled = true; s.onCycle?.() }   // 起步动作演完、开始迈步
  if (sheet === s && !sheetCanAdvance(s)) updateAnimationTimers()
}
// still：这个姿态的静态图。动画淡入、静态图淡出之后，把底下藏着的静态图换成它，动画停下时露出来的是对的那张
// a.keepUrl：接着演正在播的这一张（走路收尾、离场动作），不另抽一张
function playSheet(a, still, key = null) {
  const from = a.from ?? 0
  const url = a.keepUrl && sheet?.url && a.urls.includes(sheet.url) ? sheet.url : pick(a.urls)
  const name = a.sheets?.find(n => char()?.sheets?.[n] === url)
  const video = !a.hold && name && char()?.videos?.[name]   // 来回播（hold）视频倒着放会卡，照旧精灵图；走路和 dsh-pet 一样走视频
  const asset = url
  const s = { key, url, asset, from, to: a.to ?? (a.frames ?? SHEET.frames) - 1, fps: a.fps, hold: a.hold, loop: !!a.loop, cycle: a.cycle, cycled: false, onCycle: null,
    frame: from, dir: 1, holding: false,
    canvas: sheetCanvases.find(c => c !== sheetFront), img: null, timer: null }
  if (video) return playVideo(a, s, video, still)
  // 还是正在播的这张图（已经解码好）：沿用这块画布和这份图，直接跳到新的起点接着播。
  // 原来每次都重新读、重新解码（约 0.3 秒，画面停在半路）再交叉淡入，走路收尾、睡醒、收起零食、点击重播时看着是卡一下、叠个重影再换掉（2026-11-07 用户反馈向左跑收尾生硬）
  const prev = sheet
  if (prev?.img && prev.asset === asset && prev.key === key && sheetFront === prev.canvas && !prev.cancelLoad) {
    // 收尾时跳到循环里最像的那一帧（exit.jump）：换到另一块画布上画、两块交叉淡入，不硬切——
    // 派活的小人一直在跑，最像的帧也和当前帧差相邻两帧的三倍，硬切会闪一下（2026-11-26）。两块画布共用这份解好的图，不重新解码
    if (a.fadeJump) {
      const img = prev.img
      prev.img = null   // 交给新的这份；淡出的那块只留最后画出来的画面
      fadeOut(prev)
      sheet = s; s.img = img
      clearTimeout(sheetReleaseTimer)
      sheetDraw()
      s.canvas.classList.add('on'); prev.canvas.classList.remove('on')
      sheetFront = s.canvas
      updateAnimationTimers()
      sheetShown()
      if (still) setTimeout(() => { if (sheet === s) showImage(still, true) }, 450)
      setTimeout(() => { if (fading === prev) { endFade(); prev.canvas.width = 1; prev.canvas.height = 1 } }, 220)
      return
    }
    stopSheetClock(prev); prev.cancelLoad?.()
    Object.assign(s, { canvas: prev.canvas, img: prev.img, layoutVersion: prev.layoutVersion, dpr: prev.dpr })
    prev.img = null   // 交给新的这份，别在 disposeSheet 里关掉
    sheet = s
    clearTimeout(sheetReleaseTimer)
    sheetDraw()
    updateAnimationTimers()
    sheetShown()
    if (still) setTimeout(() => { if (sheet === s) showImage(still, true) }, 450)
    return
  }
  // 新图解码时旧动作照常播，等新首帧画好再冻结旧动作并淡出。
  if (prev?.img && sheetFront === prev.canvas) fadeOut(prev)
  else disposeSheet()
  if (fading && fading.canvas === s.canvas) endFade()   // 新图要画到这块画布上：淡出的那个先停
  sheet = s
  updateAnimationTimers()
  clearTimeout(sheetReleaseTimer)
  const loaded = img => {
    if (sheet !== s) { img.close?.(); return }
    s.cancelLoad = null
    s.img = img
    sheetDraw()
    const old = sheetFront
    const outgoing = fading
    if (outgoing) stopSheetClock(outgoing)
    s.canvas.classList.add('on'); if (old && old !== s.canvas) old.classList.remove('on')
    sheetFront = s.canvas
    document.documentElement.classList.add('sheet-on')
    updateAnimationTimers()
    sheetShown()
    if (still) setTimeout(() => { if (sheet === s) showImage(still, true) }, 450)
    if (outgoing) setTimeout(() => {
      if (fading !== outgoing) return
      endFade()
      outgoing.canvas.width = 1; outgoing.canvas.height = 1
    }, 220)
  }
  const failed = () => {
    if (sheet !== s) return
    window.chrome?.webview?.postMessage({ imageError: s.url })
    // 读不到：不退回静态立绘，改播待机动画（只试一次，待机本身也读不到就停在原来的画面）
    const idle = !s.fallback && anim('idle')
    if (idle && idle.urls.some(u => u !== s.url)) { const k = { ...idle, urls: idle.urls.filter(u => u !== s.url) }; playSheet(k, null); sheet.fallback = true; return }
    stopSheet()
  }
  s.cancelLoad = loadSheet(s.asset, loaded, s.asset === s.url ? failed : () => {
    if (sheet !== s) return
    s.asset = s.url
    s.cancelLoad = loadSheet(s.url, loaded, failed)
  })
}

// 读一张精灵图交给 done（ImageBitmap 或 <img>，都能 drawImage）；读不到调 fail。
// 每张 5120×5400，解码一张约 0.3 秒：用 <img> 时解码发生在第一次画上画布的时候，网页帧循环整个停住约 0.3 秒
// （实测 290–345 ms，拖动起步换「被拎起来」时会顿一下）。先 fetch 成 Blob 再 createImageBitmap，解码在后台线程，
// 主线程只剩第一次画时约 75 ms 的上传（2026-11-06）。画出来的像素一样。
// 拿不到时退回 <img>：测试里的 file:// 地址、读失败、4 秒还没解完（页面在后台时解码可能一直不返回，所以 <img> 也只用 onload 不用 decode()）
function loadSheet(url, done, fail) {
  const controller = new AbortController()
  const { signal } = controller
  let timer = null, img = null, blob = null, objectUrl = null
  const releaseUrl = () => {
    if (objectUrl) URL.revokeObjectURL(objectUrl)
    objectUrl = null
  }
  const viaImg = () => {
    if (signal.aborted) return
    clearTimeout(timer)
    controller.abort()
    if (blob) objectUrl = URL.createObjectURL(blob)
    blob = null
    img = new Image()
    img.onload = () => { releaseUrl(); done(img) }
    img.onerror = () => { releaseUrl(); fail() }
    img.src = objectUrl || url
  }
  if (window.createImageBitmap && /^https:/.test(url)) {
    timer = setTimeout(viaImg, 4000)
    fetch(url, { signal })
      .then(r => {
        if (!r.ok) throw new Error(r.status)
        return r.blob()
      })
      .then(data => {
        if (signal.aborted) return
        blob = data
        return createImageBitmap(data)
      })
      .then(bitmap => {
        clearTimeout(timer)
        if (!bitmap) return
        if (signal.aborted) bitmap.close()
        else done(bitmap)
      }, viaImg)
  } else viaImg()
  return () => {
    clearTimeout(timer)
    controller.abort()
    if (img) {
      img.onload = img.onerror = null
      img.removeAttribute('src')
    }
    releaseUrl()
  }
}
// 旧动作在接班首帧未就绪时继续播；交接后只保留最后画出来的画面。
let fading = null
function advanceFadingSheet(steps) {
  if (pageHidden) return
  while (steps-- > 0 && sheetCanAdvance(fading)) sheetStep(fading)
  sheetDraw(fading)
}
function fadeOut(s) {
  endFade()
  stopSheetClock(s); s.cancelLoad?.()
  fading = s
  startSheetClock(s, advanceFadingSheet)
}
function endFade(canvas = null) {
  const f = fading
  if (!f || (canvas && f.canvas !== canvas)) return
  fading = null; stopSheetClock(f); if (f.video) f.canvas.pause(); else f.img?.close?.(); f.img = null
}
function disposeSheet() {
  if (!sheet) return
  stopSheetClock(sheet)
  sheet.cancelLoad?.()
  if (sheet.video) sheet.canvas.pause(); else sheet.img?.close?.()
  sheet = null
}
function stopSheet() {
  if (sheet?.img && sheetFront === sheet.canvas) { fadeOut(sheet); sheet = null }
  else disposeSheet()
  if (!sheetFront && !document.documentElement.classList.contains('sheet-on')) { updateAnimationTimers(); return }
  if (fading) stopSheetClock(fading)
  for (const c of [...sheetCanvases, ...sheetVideos]) c.classList.remove('on')
  sheetFront = null
  document.documentElement.classList.remove('sheet-on')
  updateAnimationTimers()
  clearTimeout(sheetReleaseTimer)
  sheetReleaseTimer = setTimeout(() => { if (!sheet) { endFade(); for (const c of sheetCanvases) { c.width = 1; c.height = 1 } } }, 220)
}

// ---- 视频动画（2026-11-25 试验，照 dsh-pet）----
// anim\ 里有和精灵图同名的 .webm（VP9 + 透明通道）时改播视频：帧号照旧（character.json 的 cycle / to / exit 都按帧号写），
// 换算成时间 = 帧号 ÷ 帧率。视频先整个 fetch 成 blob 再交给 <video>：外壳读文件不支持分段读取，blob 地址才能随意跳
// （跳回循环段开头要用）。两个 <video> 轮流用，和精灵图画布同一套交叉淡入
const videoBlobs = new Map()   // 视频地址 → blob 地址，读过一次就留着（一段一两 MB）
let videoRaf = 0
function loadVideo(url, done, fail) {
  let cancelled = false
  const hit = videoBlobs.get(url)
  if (hit) Promise.resolve().then(() => { if (!cancelled) done(hit) })
  else fetch(url).then(r => { if (!r.ok) throw new Error('HTTP ' + r.status); return r.blob() })
    .then(b => { const u = URL.createObjectURL(b); videoBlobs.set(url, u); if (!cancelled) done(u) })
    .catch(() => { if (!cancelled) done(url) })   // fetch 读不了（测试用 file:// 打开页面时）：直接把地址交给 <video>，真读不出来由 <video> 报错再退回精灵图
  return () => { cancelled = true }
}
function videoTime(s, frame) { return (frame + .02) / (s.fps || SHEET.fps) }
function videoSeek(s, frame) {
  const v = s.canvas
  if (v?.tagName === 'VIDEO' && v.readyState >= 1) v.currentTime = videoTime(s, frame)
}
function videoCanAdvance(s) { return !!s?.video && !!s.img && (s.loop || !!s.cycle || s.frame < s.to) }
// 该播的播、该停的停：当前这段在播就让它播（藏起来时暂停），淡出中的那段播到淡完，其余全停
function videoSync() {
  if (typeof sheetVideos === 'undefined') return
  const s = sheet, play = !pageHidden && videoCanAdvance(s) ? s.canvas : null
  for (const v of sheetVideos) {
    if (v === play || (!pageHidden && fading?.video && fading.canvas === v)) { if (v.paused) v.play().catch(() => {}) }
    else if (!v.paused) v.pause()
  }
  if (play && !videoRaf) videoRaf = requestAnimationFrame(videoLoop)
  if (!play && videoRaf) { cancelAnimationFrame(videoRaf); videoRaf = 0 }
}
// 每一刷新看一次视频播到哪一帧：到循环段末尾跳回开头，到 to 就停在 to（和精灵图的 sheetStep 同一套规则）
function videoLoop() {
  videoRaf = 0
  const s = sheet
  if (!s?.video || !s.img || pageHidden) return
  const v = s.canvas, fps = s.fps || SHEET.fps, t = v.currentTime * fps
  let f = Math.floor(t + 1e-3)
  if (s.cycle && t >= s.cycle[1] + .9) { f = s.cycle[0]; videoSeek(s, f) }   // 最后一帧快放完时就跳，不多露一帧
  else if (s.loop && (t >= s.to + .9 || v.ended)) { f = s.from; videoSeek(s, f) }
  else if (!s.loop && !s.cycle && (f >= s.to || v.ended)) { f = s.to; v.pause(); videoSeek(s, f) }
  if (f !== s.frame) { s.frame = f; auraPulse() }
  if (s.cycle && !s.cycled && s.frame >= s.cycle[0]) { s.cycled = true; s.onCycle?.() }
  if (sheet === s && videoCanAdvance(s)) videoRaf = requestAnimationFrame(videoLoop)
  else updateAnimationTimers()
}
function playVideo(a, s, url, still) {
  const prev = sheet
  s.video = true; s.asset = url
  // 还是正在播的这段视频：直接跳到新的起点接着播（离场动作、点击重播）
  if (prev?.video && prev.img && prev.asset === url && prev.key === s.key && sheetFront === prev.canvas && !prev.cancelLoad && !a.fadeJump) {   // fadeJump：换另一个 <video> 交叉淡入，见 playSheet
    Object.assign(s, { canvas: prev.canvas, img: prev.img, layoutVersion: prev.layoutVersion, dpr: prev.dpr })
    prev.img = null
    sheet = s
    clearTimeout(sheetReleaseTimer)
    videoSeek(s, s.from)
    updateAnimationTimers()
    sheetShown()
    if (still) setTimeout(() => { if (sheet === s) showImage(still, true) }, 450)
    return
  }
  if (prev?.img && sheetFront === prev.canvas) fadeOut(prev)
  else disposeSheet()
  s.canvas = sheetVideos.find(v => v !== sheetFront)
  if (fading && fading.canvas === s.canvas) endFade()
  sheet = s
  updateAnimationTimers()
  clearTimeout(sheetReleaseTimer)
  const v = s.canvas
  watchVideoErrors(v)
  const show = () => {
    if (sheet !== s) return
    s.cancelLoad = null
    s.img = v
    sheetDraw()
    const old = sheetFront, outgoing = fading
    v.classList.add('on'); if (old && old !== v) old.classList.remove('on')
    sheetFront = v
    document.documentElement.classList.add('sheet-on')
    updateAnimationTimers()
    sheetShown()
    prefetchVideos()
    if (still) setTimeout(() => { if (sheet === s) showImage(still, true) }, 450)
    if (outgoing) setTimeout(() => { if (fading === outgoing) endFade() }, 220)
  }
  // 读不出来：重读这段视频再试（videoTrouble），不退回精灵图（2026-11-26 用户定：以后只用视频、精灵图要删）
  const failed = () => { if (sheet === s) videoTrouble(s, 'load ' + (v.error ? 'error ' + v.error.code : 'failed')) }
  s.replay = { a, still }   // 出错重来时用
  s.cancelLoad = loadVideo(url, blob => {
    if (sheet !== s) return
    let done = false
    const ready = () => { if (done) return; done = true; v.removeEventListener('seeked', ready); show() }
    const seek = () => {
      v.removeEventListener('loadeddata', seek)
      if (sheet !== s) return
      if (s.from === 0 && v.currentTime < .5 / (s.fps || SHEET.fps)) return ready()   // 从第 0 帧起：读出来就是这一帧，不用再跳一次
      v.addEventListener('seeked', ready)
      v.currentTime = videoTime(s, s.from)
      setTimeout(ready, 400)   // 个别情况跳到同一个位置不发 seeked：兜底
    }
    if (v.src !== blob) { v.src = blob; v.addEventListener('loadeddata', seek) }
    else if (v.readyState >= 2) seek()
    else v.addEventListener('loadeddata', seek)
  }, failed)
}
// ---- 视频出错、卡住（2026-11-26）----
// 出错事件挂在 <video> 元素上、按「现在谁在用这个元素」处理：原来挂在某一次播放上，同一段视频接着用（收尾、点击重播）以后
// 再出错就没人管，画面上留着一个解不出画面的空视频——人不见了、只剩光晕，点她换个动作才回来（Linux 上见过）。
// 处理：重读这段视频、从出错的那一帧接着播；连着两次还不行，改播待机视频；都在日志里记一笔（外壳收 videoError）。
// 看门狗：正在显示、该往前播的视频 1.5 秒没动，也按出错处理（解码卡住时不一定报错）
function watchVideoErrors(v) {   // 第一次用这个 <video> 时挂上（本文件比 index.html 里的 sheetVideos 先加载，不能在顶层挂）
  if (v.troubleWatched) return
  v.troubleWatched = true
  v.addEventListener('error', () => {
    const s = sheet?.video && sheet.canvas === v ? sheet : null
    if (s) videoTrouble(s, 'error ' + (v.error?.code ?? '?'))
  })
}
function videoTrouble(s, why) {
  if (sheet !== s || !s.video || s.recovering) return
  const tries = (s.tries || 0) + 1
  window.chrome?.webview?.postMessage({ videoError: `${decodeURIComponent(s.asset.split('/').pop())} ${why}（第 ${tries} 次）` })
  const r = s.replay
  const idle = anim('idle')
  if (tries > 2 || !r) {
    // 两次都不行：这一段先不播，改播待机视频（待机自己出错就停在原地，不再循环重来）
    if (idle && !idle.urls.includes(s.url)) { s.cancelLoad?.(); sheet = null; playSheet(idle, null); if (sheet) sheet.tries = 0 }
    else s.recovering = true   // 待机自己也不行：停在这一帧，不再让看门狗一遍遍重来、刷日志
    return
  }
  // 等 0.4 秒再重读：马上重读时解码器多半还没缓过来，Linux 上实测三次都在一秒内接连失败。等的时候画面停在原处
  s.recovering = true
  s.canvas.pause()
  setTimeout(() => {
    if (sheet !== s) return   // 这期间换了别的动作
    const frame = s.frame
    videoBlobs.delete(s.asset)   // 重新读一遍文件，不用可能坏掉的那份
    s.cancelLoad?.(); stopSheetClock(s)
    s.img = null   // 旧的这个先留在台上，新的读好了淡入时再换下
    sheet = null
    playSheet({ ...r.a, from: Math.max(r.a.from ?? 0, Math.min(frame, s.to)), keepUrl: true }, r.still, s.key)
    if (sheet) sheet.tries = tries
  }, 400)
}
setInterval(() => {
  const s = sheet
  if (!s?.video || !s.img || s.cancelLoad || s.recovering || pageHidden || s.canvas !== sheetFront) { if (s) s.stuckSince = 0; return }
  const v = s.canvas
  if (v.error) return videoTrouble(s, 'error ' + v.error.code)
  if (!videoCanAdvance(s) || v.ended) { s.stuckSince = 0; return }
  // 该播却停着（刚换上、播放被打断）：先叫它播，不算卡住——原来按「时间不动」一律当卡住重读，刚开始播时常误判（Qt 版实测）
  if (v.paused) { v.play().catch(() => {}); s.stuckSince = 0; return }
  if (v.currentTime !== s.lastTime) { s.lastTime = v.currentTime; s.stuckSince = 0; return }
  if (!s.stuckSince) s.stuckSince = Date.now()
  else if (Date.now() - s.stuckSince > 1500) { s.stuckSince = 0; videoTrouble(s, `stall @${v.currentTime.toFixed(2)}s rs${v.readyState}`) }
}, 500)
// 点击反应的视频先读进内存（待机播起来以后、每个角色一次）：第一次点她也不用临时读文件
let videoPrefetched = null
function prefetchVideos() {
  const c = char()
  if (!c?.videos || videoPrefetched === c) return
  videoPrefetched = c
  setTimeout(() => {
    for (const p of CLICK_POSES) for (const n of c.animations?.[p]?.sheets || []) if (c.videos[n]) loadVideo(c.videos[n], () => {}, () => {})
  }, 1500)
}
