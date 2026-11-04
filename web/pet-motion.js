// Claudia window/input adapter. All spring, launch and flight formulas come from DshPhysics.
;(function(root) {
  class PetMotion {
    constructor(physics, emit, requestFrame, cancelFrame, now) {
      this.core = physics; this.emit = emit; this.request = requestFrame; this.cancel = cancelFrame; this.now = now;
      this.frame = null; this.token = 0; this.phase = null;
    }
    stop() {
      ++this.token;
      if (this.frame !== null) this.cancel(this.frame);
      this.frame = null; this.phase = null;
    }
    begin(p) {
      this.stop(); this.id = p.id; this.cfg = p.physics; this.bounds = p.bounds;
      this.enabled = p.enabled;
      this.state = { x: p.x, y: p.y, vx: 0, vy: 0 };
      this.origin = { x: p.x, y: p.y, px: p.downX, py: p.downY };
      this.target = { x: p.x, y: p.y }; this.trail = [];
      this.swingX = p.x; this.swingDir = 0; this.swings = []; this.lastShake = -1000;
      this.phase = 'drag'; this.last = this.now();
      this.pointer(p); this.schedule();
    }
    pointer(p) {
      if (this.phase !== 'drag' || p.id !== this.id) return;
      // Same as dsh-pet onPointerMove: record screen trajectory, update target, never move here.
      this.trail.push({ t: p.time, x: p.px, y: p.py });
      // Keep a longer raw trail than dsh-pet's 200ms: when the host lags, points from after the real mouse-up
      // can arrive before the mouse-up itself. end() cuts them off, then applies dsh-pet's own trimTrail.
      while (this.trail.length && p.time - this.trail[0].t > 1000) this.trail.shift();
      const b = this.bounds;
      this.target = {
        x: Math.max(b.minX, Math.min(b.maxX, this.origin.x + p.px - this.origin.px)),
        y: Math.max(b.minY, Math.min(b.maxY, this.origin.y + p.py - this.origin.py))
      };
    }
    end(p) {
      if (this.phase !== 'drag' || p.id !== this.id) return;
      if (this.frame !== null) this.cancel(this.frame);
      this.frame = null;
      // Host's actual position is authoritative; never catch up to the pointer on mouse-up.
      this.state.x = p.x; this.state.y = p.y;
      // Only motion up to the actual mouse-up counts (as in dsh-pet, where no pointermove follows pointerup).
      this.trail = this.core.trimTrail(this.trail.filter(s => s.t <= p.time), p.time);
      const velocity = this.enabled && this.core.estimateReleaseVelocity(this.trail, p.time, this.cfg);
      this.trail = [];
      if (!velocity) { this.phase = null; this.emit({ id: this.id, phase: 'end', x: p.x, y: p.y }); return; }
      this.state.vx = velocity.vx; this.state.vy = velocity.vy;
      this.phase = 'flight'; this.last = this.now(); this.schedule();
    }
    update(p) {
      if (p.id !== this.id || !this.phase) return;
      const dx = p.bounds.minX - this.bounds.minX, dy = p.bounds.minY - this.bounds.minY;
      this.state.x += dx; this.state.y += dy;
      this.origin.x += dx; this.origin.y += dy; this.target.x += dx; this.target.y += dy;
      this.bounds = p.bounds; this.cfg = p.physics; this.enabled = p.enabled;
    }
    schedule() {
      const token = this.token;
      this.frame = this.request(() => { if (token !== this.token) return; this.frame = null; this.step(); });
    }
    step() {
      if (!this.phase) return;
      const time = this.now(), rawDt = (time - this.last) / 1000; this.last = time;
      const s = this.state, b = this.bounds;
      let impactX = 0, impactY = 0, edge = null, atRest = false, shake = false;
      if (this.phase === 'drag') {
        const dt = Math.min(rawDt, 1 / 30);
        if (this.enabled) {
          s.vx = this.core.springStep(s.vx, s.x, this.target.x, dt, this.cfg.throwPower);
          s.vy = this.core.springStep(s.vy, s.y, this.target.y, dt, this.cfg.throwPower);
          s.x += s.vx * dt; s.y += s.vy * dt;
        } else { s.x = this.target.x; s.y = this.target.y; }
        if (!this.swingDir) {
          if (Math.abs(s.x - this.swingX) > 18) { this.swingDir = Math.sign(s.x - this.swingX); this.swingX = s.x; }
        } else if ((s.x - this.swingX) * this.swingDir > 0) this.swingX = s.x;
        else if ((this.swingX - s.x) * this.swingDir > 18) {
          this.swingDir = -this.swingDir; this.swingX = s.x;
          this.swings.push(time); this.swings = this.swings.filter(t => time - t < 900);
          if (this.swings.length >= 2 && time - this.lastShake > 300) { shake = true; this.lastShake = time; }
        }
      } else {
        const next = this.core.throwStep(s, rawDt, b, this.cfg);
        if (next.bounced) {
          if (next.x === b.minX || next.x === b.maxX) impactX = Math.abs(s.vx);
          if (next.y === b.minY || next.y === b.maxY) impactY = Math.abs(s.vy);
          // Which edge took the harder hit (used by tumble.js).
          if (impactX || impactY) edge = impactX >= impactY ? (next.x === b.minX ? 'left' : 'right') : (next.y === b.minY ? 'top' : 'bottom');
        }
        this.state = { x: next.x, y: next.y, vx: next.vx, vy: next.vy }; atRest = next.atRest;
      }
      // Claudia's sole geometry adaptation: its complete window stays inside Claude.
      const current = this.state;
      if (current.x < b.minX || current.x > b.maxX || b.maxX === b.minX) {
        current.x = Math.max(b.minX, Math.min(b.maxX, current.x)); current.vx = 0;
      }
      if (current.y < b.minY || current.y > b.maxY || b.maxY === b.minY) {
        current.y = Math.max(b.minY, Math.min(b.maxY, current.y)); current.vy = 0;
      }
      const phase = atRest ? 'end' : this.phase;
      this.emit({ id: this.id, phase, x: current.x, y: current.y, impactX, impactY, edge, shake });
      if (atRest) this.phase = null; else this.schedule();
    }
  }
  root.PetMotion = PetMotion;
  if (typeof module !== 'undefined') module.exports = PetMotion;
})(typeof window !== 'undefined' ? window : globalThis);
