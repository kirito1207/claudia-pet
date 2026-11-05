;(function(root) {
  // clawd 之后是动画小动作的分组（发呆 / 魔法 / 偷吃零食 / Claude 梗 / 才艺 / 玩耍 / 吃喝 / 时令 / 走路），
  // 具体动作由网页按角色的 animations 展开，经 context.extra 传进来
  const actions = ['hum', 'stretch', 'yawn', 'clawd', 'fidget', 'magic', 'snack', 'claude', 'talent', 'play', 'food', 'season', 'walk']
  const intervals = { quiet: 90000, normal: 45000, lively: 20000 }
  const normalize = s => ({ enabled: s?.idleEnabled !== false,
    frequency: Object.hasOwn(intervals, s?.idleFrequency) ? s.idleFrequency : 'normal',
    actions: Array.isArray(s?.idleActions) ? actions.filter(a => s.idleActions.includes(a)) : [...actions] })
  const delay = (settings, random = Math.random()) => intervals[settings.frequency] * (.8 + .4 * random)
  function candidates(settings, context) {
    if (!settings.enabled || context.hidden || context.moving || context.working || context.multi || context.farewell || context.clicking) return []
    if (!['idle', 'tired'].includes(context.pose)) return []
    return [...settings.actions.filter(a => context.available.includes(a) && (a !== 'yawn' || context.quiet >= 180000)), ...(context.extra || [])]
  }
  const api = { normalize, delay, candidates }
  root.IdlePolicy = api
  if (typeof module !== 'undefined') module.exports = api
})(typeof window !== 'undefined' ? window : globalThis)
