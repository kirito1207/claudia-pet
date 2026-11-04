;(function(root) {
  class MotionMood {
    constructor(ready, schedule = (fn, ms) => setTimeout(fn, ms), cancel = id => clearTimeout(id), random = Math.random) {
      this.ready = ready; this.schedule = schedule; this.cancelTimer = cancel; this.random = random;
      this.moving = false; this.settling = false; this.hidden = false;
      this.pending = false; this.afterWork = false; this.contextSaid = null; this.timer = null; this.token = 0;
    }
    get blocked() { return this.moving || this.settling || this.hidden }
    cancelWait() { ++this.token; if (this.timer !== null) this.cancelTimer(this.timer); this.timer = null; this.settling = false }
    begin() { this.cancelWait(); this.moving = true }
    mark(afterWork = false) {
      this.pending = true; this.afterWork ||= !!afterWork;
      if (!this.blocked) this.wait();
    }
    end() { this.moving = false; if (this.pending && !this.hidden) this.wait() }
    wait() {
      this.cancelWait(); this.settling = true;
      const token = this.token;
      this.timer = this.schedule(() => {
        if (token !== this.token) return;
        this.timer = null; this.settling = false; this.attempt();
      }, 800 + this.random() * 400);
    }
    setHidden(hidden) {
      this.hidden = hidden;
      if (hidden) this.cancelWait(); else if (this.pending && !this.moving) this.wait();
    }
    attempt() { if (this.pending && !this.blocked) this.ready() }
    take() {
      if (!this.pending || this.blocked) return null;
      const result = { afterWork: this.afterWork }; this.clear(); return result;
    }
    clear() { this.cancelWait(); this.pending = false; this.afterWork = false; this.contextSaid = null }
  }
  root.MotionMood = MotionMood;
  if (typeof module !== 'undefined') module.exports = MotionMood;
})(typeof window !== 'undefined' ? window : globalThis);
