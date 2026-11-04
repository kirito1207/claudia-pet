;(function(root) {
  class ClickSequence {
    constructor(visual, active) { this.visual = visual; this.active = active; this.pending = []; this.token = 0; this.cancelVisual = null }
    get busy() { return this.pending.length > 0 }
    play(action, enabled) {
      if (!enabled) { this.cancel(); action(); return }
      this.pending.push(action)
      if (this.pending.length > 64) this.pending.shift()
      this.cancelVisual?.()
      const token = ++this.token
      this.active(true)
      this.cancelVisual = this.visual(() => {
        if (token !== this.token) return
        this.cancelVisual = null
        const actions = this.pending; this.pending = []
        this.active(false)
        // Each tap still counts for existing interactions (e.g. three pats to coax her).
        for (const action of actions) action()
      })
    }
    cancel() {
      ++this.token; this.cancelVisual?.(); this.cancelVisual = null; this.pending = []; this.active(false)
    }
  }
  root.ClickSequence = ClickSequence
  if (typeof module !== 'undefined') module.exports = ClickSequence
})(typeof window !== 'undefined' ? window : globalThis)
