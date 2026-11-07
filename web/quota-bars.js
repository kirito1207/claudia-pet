// Claude 额度条：每个额度窗口一行，显示已用比例及重置时间。
// 2026-11-07 用户定：照 Claude 自己的样子显示「用了多少」（原来显示剩余），条统一用 Claude 橙；数据里照旧存剩余比例 left（插件写的、钱袋档位都用它）
;(function (root) {
  const LOW_LEFT = 10   // 剩余 ≤ 10%（已用 ≥ 90%）算紧张
  // 已用 ≥ 90% 算紧张（low，百分比标红），其余 ok；条都是橙色
  const level = used => (used >= 100 - LOW_LEFT ? 'low' : 'ok')
  // 重置时间短写：24 小时内「3:40」（小时不补零），7 天内「周一」，再远「10/26」，按本机时区
  const WEEK = '日一二三四五六'
  function shortWhen(sec, nowSec) {
    const d = new Date(sec * 1000), ahead = sec - nowSec
    if (ahead < 86400) return `${d.getHours()}:${String(d.getMinutes()).padStart(2, '0')}`
    if (ahead < 7 * 86400) return `周${WEEK[d.getDay()]}`
    return `${d.getMonth() + 1}/${d.getDate()}`
  }

  const pctOf = left => Math.max(0, Math.min(100, Math.round((+left || 0) * 100)))   // 剩余百分比
  const usedOf = left => 100 - pctOf(left)
  function barRow(b, label, nowSec) {
    if (b.resetDone || (b.reset && b.reset <= nowSec)) return { kind: 'bar', label, pct: 0, level: 'ok', when: '已重置' }   // 过了重置时间、还没新数据：算没用
    const pct = usedOf(b.left)
    return { kind: 'bar', label, pct, level: level(pct), when: b.reset ? shortWhen(b.reset, nowSec) : null }
  }

  const SEG = /^(.+?) (剩余|已用) (\d+)%(?: · (\S+) 重置)?(.*)$/   // 旧插件写「剩余」，新的写「已用」
  function fromText(q) {
    let note = null
    const rows = String(q.text).split(/\s*｜\s*/).filter(Boolean).map(seg => {
      const m = SEG.exec(seg)
      if (!m) return { kind: 'text', text: seg, low: !!q.low }
      const [, label, kind, p, when, tail] = m
      const t = tail.trim().replace(/^·\s*/, '')
      if (t) note = t   // 保留文本数据附带的说明
      const pct = kind === '剩余' ? 100 - Math.min(100, +p) : Math.min(100, +p)
      return { kind: 'bar', label, pct, level: level(pct), when: when || null }
    })
    return { rows, note }
  }

  function of(q, nowSec) {
    if (!q || !q.text) return null
    const r = Array.isArray(q.bars) && q.bars.length ? { rows: q.bars.map(b => barRow(b, b.label, nowSec)), note: null } : fromText(q)
    if (!r.rows.some(x => x.kind === 'bar')) return null   // 一根条都没有：整块照旧按文字样式画
    if (q.note) r.note = q.note
    return r
  }

  // 画进名牌下面的小窗（只在浏览器里用）
  function render(el, result) {
    const div = (cls, text) => { const d = document.createElement('div'); if (cls) d.className = cls; if (text != null) d.textContent = text; return d }
    const span = (cls, text) => { const s = document.createElement('span'); s.className = cls; if (text != null) s.textContent = text; return s }
    const timed = result.rows.some(r => r.when)   // 有一行带重置时间就每行都留出那一格，几根条才对得齐
    el.replaceChildren(...result.rows.map(r => {
      if (r.kind === 'text') return div(r.low ? 'low' : '', r.text)
      const row = div('qbar ' + r.level)
      const track = span('qtrack'), fill = document.createElement('i')
      fill.style.width = r.pct + '%'
      track.append(fill)
      row.append(span('ql', r.label), track, span('qp', r.pct + '%'))
      if (timed) row.append(span('qt', r.when || ''))
      return row
    }))
    if (result.note) el.append(div('qnote', result.note))
  }

  // 额度只在 Code 会话收到回复时更新（Chat 里的对话插件收不到），所以网页按当前时间再算一遍：
  // 过了重置时间的窗口算没用、标「已重置」；用得最多的那个才带重置时间（同插件 quotaOf）；
  // 数据超过 STALE_MIN 分钟没更新，加一行「多久前的数据」。at 是 claude-quota.json 的修改时间（秒）。
  const STALE_MIN = 20
  const pad = n => String(n).padStart(2, '0')
  function resetText(sec, nowSec) {
    const d = new Date(sec * 1000)
    return sec - nowSec < 86400 ? `${pad(d.getHours())}:${pad(d.getMinutes())}` : `${d.getMonth() + 1}/${d.getDate()}`
  }
  function ageText(at, nowSec) {
    const min = Math.floor((nowSec - at) / 60)
    if (!(at > 0) || min < STALE_MIN) return null
    if (min < 60) return `${min} 分钟前的数据`
    if (min < 24 * 60) return `${Math.floor(min / 60)} 小时前的数据`
    const d = new Date(at * 1000)
    return `${d.getMonth() + 1}/${d.getDate()} 的数据`
  }
  function fresh(q, nowSec, at) {
    if (!q) return q
    const note = ageText(at, nowSec)
    if (!Array.isArray(q.bars) || !q.bars.length) return note ? { ...q, note } : q
    const bars = q.bars.map(b => (b.reset && b.reset <= nowSec ? { label: b.label, left: 1, resetDone: true } : b))
    const live = bars.filter(b => !b.resetDone)
    const top = live.length ? live.reduce((a, b) => (b.left < a.left ? b : a)) : null
    const text = bars.map(b => {
      if (b.resetDone) return `${b.label} 已重置`
      let piece = `${b.label} 已用 ${usedOf(b.left)}%`
      if (b === top && b.reset) piece += ` · ${resetText(b.reset, nowSec)} 重置`
      return piece
    }).join(' ｜ ')
    const out = { ...q, text, bars, low: !!top && pctOf(top.left) <= LOW_LEFT }
    if (note) out.note = note
    return out
  }

  const api = { level, shortWhen, of, render, fresh, ageText, STALE_MIN }
  root.QuotaBars = api
  if (typeof module !== 'undefined') module.exports = api
})(typeof window !== 'undefined' ? window : globalThis)
