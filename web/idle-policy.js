;(function(root) {
  const actions = ['hum', 'stretch', 'yawn', 'clawd']
  const intervals = { quiet: 90000, normal: 45000, lively: 20000 }
  const normalize = s => ({ enabled: s?.idleEnabled !== false,
    frequency: Object.hasOwn(intervals, s?.idleFrequency) ? s.idleFrequency : 'normal',
    actions: Array.isArray(s?.idleActions) ? actions.filter(a => s.idleActions.includes(a)) : [...actions] })
  const delay = (settings, random = Math.random()) => intervals[settings.frequency] * (.8 + .4 * random)
  function candidates(settings, context) {
    if (!settings.enabled || context.hidden || context.moving || context.working || context.multi || context.farewell || context.clicking) return []
    if (!['idle', 'tired'].includes(context.pose)) return []
    return settings.actions.filter(a => context.available.includes(a) && (a !== 'yawn' || context.quiet >= 180000))
  }
  const api = { normalize, delay, candidates }
  root.IdlePolicy = api
  if (typeof module !== 'undefined') module.exports = api
})(typeof window !== 'undefined' ? window : globalThis)
