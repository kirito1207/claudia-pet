const assert = require('assert/strict');
const fs = require('fs');
const vm = require('vm');
const P = require('../web/dsh-physics.js');
const Motion = require('../web/pet-motion.js');
const near = (a, b) => assert.ok(Math.abs(a - b) < 1e-8, `${a} != ${b}`);
const bounds = { minX: 0, minY: 0, maxX: 1600, maxY: 900 };
const cfg = { ...P.DEFAULT_PHYSICS };
function harness() {
  let time = 0, seq = 0; const pending = new Map(), messages = [];
  const m = new Motion(P, p => messages.push(p), cb => { pending.set(++seq, cb); return seq; }, id => pending.delete(id), () => time);
  return { m, messages, frame(dt = 16) {
    time += dt; const callbacks = [...pending.values()]; pending.clear(); callbacks.forEach(cb => cb());
  } };
}
// A small (40px), fast horizontal stroke must retain DSH's lateral speed, not fall vertically.
for (const dir of [-1, 1]) {
  const h = harness();
  h.m.begin({ id: 1, x: 600, y: 300, downX: 700, downY: 400, px: 700, py: 400, time: 0, physics: cfg, bounds, enabled: true });
  const trail = [{ t: 0, x: 700, y: 400 }];
  for (let i = 1; i <= 4; i++) {
    const p = { id: 1, px: 700 + i * 10 * dir, py: 400, time: i * 8 };
    h.m.pointer(p); trail.push({ t: p.time, x: p.px, y: p.py }); h.frame(8);
  }
  const expected = P.estimateReleaseVelocity(trail, 33, cfg);
  assert.ok(expected && expected.vx * dir > 500);
  const actualX = Math.round(h.m.state.x), actualY = Math.round(h.m.state.y);
  h.m.end({ id: 1, x: actualX, y: actualY, time: 33 });
  near(h.m.state.x, actualX); near(h.m.state.vx, expected.vx);
  h.frame();
  assert.ok((h.m.state.x - actualX) * dir > 8, 'short horizontal fling lost its lateral motion');
  assert.ok(Math.abs(h.m.state.y - actualY) < 1, 'first step fell instead of flinging');
  h.m.stop();
}
// Pure back-and-forth motion has no net direction: DSH puts the pet down, without starting gravity.
const h = harness();
h.m.begin({ id: 2, x: 600, y: 300, downX: 700, downY: 400, px: 700, py: 400, time: 0, physics: cfg, bounds, enabled: true });
h.m.pointer({ id: 2, px: 750, py: 400, time: 25 }); h.frame(25);
h.m.pointer({ id: 2, px: 700, py: 400, time: 50 }); h.frame(25);
h.m.end({ id: 2, x: 610, y: 300, time: 51 });
assert.equal(h.m.phase, null); h.frame(); near(h.m.state.y, 300);
assert.equal(h.messages.at(-1).phase, 'end');

// Optional numerical parity against the actual installed dsh-pet bundle, not a second homemade reference.
if (process.argv[2]) {
  const context = {}; vm.runInNewContext(fs.readFileSync(process.argv[2], 'utf8'), context);
  const R = context.PetShared;
  for (const power of [.2, 1, 2.5]) {
    const physics = { ...cfg, throwPower: power };
    for (const dt of [.008, .016, .033, .06]) {
      near(P.springStep(-210, 300, 330, Math.min(dt, 1 / 30), power), R.springStep(-210, 300, 330, Math.min(dt, 1 / 30), power));
      let a = { x: 700, y: 300, vx: 800, vy: -100 }, b = { ...a };
      for (let i = 0; i < 400; i++) {
        const na = P.throwStep(a, dt, bounds, physics), nb = R.throwStep(b, dt, bounds, physics);
        for (const key of ['x','y','vx','vy']) near(na[key], nb[key]);
        assert.equal(na.atRest, nb.atRest); a = na; b = nb;
      }
      for (const width of [10, 20, 40, 120]) {
        const trail = Array.from({ length: 6 }, (_, i) => ({ t: i * 8, x: 700 + width * i / 5, y: 400 }));
        const av = P.estimateReleaseVelocity(trail, 41, physics), bv = R.estimateReleaseVelocity(trail, 41, physics);
        assert.equal(!!av, !!bv); if (av) { near(av.vx, bv.vx); near(av.vy, bv.vy); }
      }
    }
  }
  console.log('PASS: exact numerical parity with installed dsh-pet (4,800 flight steps, spring and small-stroke release cases)');
}
console.log('PASS: small fast horizontal fling, continuous release and no-gravity gentle put-down');
