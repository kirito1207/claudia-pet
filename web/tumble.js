// 被扔出去碰到 Claude 窗口边框：这次演不演、演哪一套（只管判断，播放在 index.html）
//   - 碰撞速度不到 MIN_SPEED 不算一次碰边（在地上慢慢滑不算）
//   - 第 n 次碰边的触发概率 = n × 50% ÷ 7，第 7 次起封顶 50%；触发后清零，1 分钟没碰过边也清零
//   - 一次甩出最多触发一次（throwStarted 时重新允许）
//   - 碰顶边：撞头；碰左右边：撞头、摔倒、摇晃三套各 1/3；碰底边：6 成摔倒 → 爬起，4 成摇晃 → 趴地 → 爬起
//     （撞头在撞上那一刻换图，摔倒和摇晃都等停稳后才演）
;(function(root) {
  const MIN_SPEED = 200, RESET_MS = 60000, FULL_AT = 7, MAX_CHANCE = 0.5
  // 每段停留的毫秒数；撞头那段从停稳时开始算
  const STEPS = {
    bonk: [['bonk', 1500]],
    fallen: [['fallen', 1200], ['getup', 1200]],
    stagger: [['stagger', 1200], ['faceplant', 1500], ['getup', 1200]],
  }
  class TumblePolicy {
    constructor(random = Math.random, now = () => Date.now()) {
      this.random = random; this.now = now
      this.hits = 0; this.lastHit = -Infinity; this.fired = false
    }
    throwStarted() { this.fired = false }
    static chance(n) { return Math.min(n, FULL_AT) * MAX_CHANCE / FULL_AT }
    // edge：'top' / 'bottom' / 'left' / 'right'；speed：碰撞时那个方向的速度（像素/秒）
    // 返回 null 或要演的那一套：'bonk' / 'fallen' / 'stagger'
    hit(edge, speed) {
      if (this.fired || !(speed >= MIN_SPEED) || !edge) return null
      const t = this.now()
      if (t - this.lastHit > RESET_MS) this.hits = 0
      this.lastHit = t; this.hits++
      if (this.random() >= TumblePolicy.chance(this.hits)) return null
      this.hits = 0; this.fired = true
      if (edge === 'top') return 'bonk'
      const r = this.random()
      if (edge === 'bottom') return r < 0.6 ? 'fallen' : 'stagger'
      return r < 1 / 3 ? 'bonk' : r < 2 / 3 ? 'fallen' : 'stagger'
    }
  }
  TumblePolicy.MIN_SPEED = MIN_SPEED; TumblePolicy.RESET_MS = RESET_MS; TumblePolicy.STEPS = STEPS
  root.TumblePolicy = TumblePolicy
  if (typeof module !== 'undefined') module.exports = TumblePolicy
})(typeof window !== 'undefined' ? window : globalThis)
