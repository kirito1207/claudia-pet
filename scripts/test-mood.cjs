const {chromium}=require('playwright');
const fs=require('fs'),path=require('path'),assert=require('assert/strict');
const {pathToFileURL}=require('url');
(async()=>{
  const browser=await chromium.launch({channel:'msedge',headless:true});
  try {
    const page=await browser.newPage({viewport:{width:260,height:362}}), errors=[];
    page.on('pageerror',e=>errors.push(e.message));
    await page.goto(pathToFileURL(path.resolve(__dirname,'../web/index.html')).href);
    const dir=path.resolve(__dirname,'../characters/claude');
    const info=JSON.parse(fs.readFileSync(path.join(dir,'character.json'),'utf8'));info.poses={};
    for(const file of fs.readdirSync(dir).filter(f=>f.endsWith('.webp'))){const key=file.replace('.webp','').split('-')[0];(info.poses[key]||=[]).push(pathToFileURL(path.join(dir,file)).href);}
    await page.evaluate(info=>init({characters:{claude:info}}),info);await page.waitForTimeout(650);
    async function reset(context=null) {
      await page.evaluate(context=>{
        motionMood.clear();setMotionActive(false);setPageHidden(false);clickSequence.cancel();
        tumbleCancel();tumbled=false;tumblePolicy.hits=0;tumblePolicy.throwStarted();window.seen=[];
        for(const t of [pending,thinkTimer,revertTimer,sulkTimer,shakeEndTimer,stuckTimer])clearTimeout(t);
        for(const key of Object.keys(charState))delete charState[key];
        sulking=false;anger=0;angerAt=Date.now();coax=0;shownAt=0;
        motionMood.random=()=>.5;setBehavior({idleEnabled:false,clickBounce:false});setPose(context||'idle',true);
        if(context)charState.claude={pose:context,event:'PreToolUse',ts:Date.now()/1000};
      },context);
    }
    // 14 × 7 = 98，过红温门槛 90
    async function shake(count=14) { await page.evaluate(count=>{setMotionActive(true);for(let i=0;i<count;i++)shaken();},count); }
    await reset();await shake();await page.waitForTimeout(1300);
    assert.equal(await page.evaluate(()=>pose),'idle','emotion played during movement');
    assert.ok(await page.evaluate(()=>angerNow()>=83),'stored anger decayed before it could be seen');
    await page.evaluate(()=>setMotionActive(false));await page.waitForTimeout(600);
    assert.equal(await page.evaluate(()=>pose),'idle','emotion played before settling delay');
    await page.waitForFunction(()=>pose==='redhot');await page.waitForTimeout(2500);
    assert.equal(await page.evaluate(()=>pose),'redhot','red-hot sequence lost playback time');
    await reset('reading');await shake();await page.evaluate(()=>setMotionActive(false));await page.waitForTimeout(1200);
    assert.equal(await page.evaluate(()=>pose),'reading','emotion replaced ongoing work');
    assert.equal(await page.evaluate(()=>motionMood.pending),true);
    await page.evaluate(()=>applyCharState('claude',{pose:'happy',event:'Stop',ts:Date.now()/1000}));
    assert.equal(await page.evaluate(()=>pose),'redhot','stored work-time anger was lost at completion');
    await reset('asking');await shake(5);await page.evaluate(()=>{charState.claude.ts-=1000;setMotionActive(false)});await page.waitForTimeout(1200);
    assert.equal(await page.evaluate(()=>pose),'asking','emotion hid an unanswered question');
    await page.evaluate(()=>applyCharState('claude',{pose:'reading',event:'PreToolUse',ts:Date.now()/1000}));
    assert.equal(await page.evaluate(()=>motionMood.pending),true);
    await page.evaluate(()=>applyCharState('claude',{pose:'happy',event:'Stop',ts:Date.now()/1000}));
    assert.equal(await page.evaluate(()=>pose),'annoyed');
    await reset();await shake(1);await page.evaluate(()=>setMotionActive(false));await page.waitForFunction(()=>pose==='dizzy');
    await reset();await shake();await page.evaluate(()=>{setMotionActive(false);setPageHidden(true)});await page.waitForTimeout(1300);
    assert.equal(await page.evaluate(()=>pose),'idle','hidden pet consumed its pending reaction');
    await page.evaluate(()=>setPageHidden(false));await page.waitForFunction(()=>pose==='redhot');
    await reset();await shake();await page.evaluate(()=>{setMotionActive(false);handleClick('pat')});await page.waitForFunction(()=>pose==='puffed');
    await reset();await page.evaluate(()=>setSessions('claude',{a:{pose:'reading',event:'PreToolUse',ts:Date.now()/1000},b:{pose:'writing',event:'PreToolUse',ts:Date.now()/1000}}));
    await shake();await page.evaluate(()=>setMotionActive(false));await page.waitForTimeout(1200);
    assert.equal(await page.evaluate(()=>!!multi),true);assert.equal(await page.evaluate(()=>motionMood.pending),true);
    await page.evaluate(()=>setSessions('claude',{}));await page.waitForFunction(()=>pose==='redhot');
    // ---- Edge hits (tumble.js): rolls[0] decides whether it triggers, rolls[1] picks fallen (<.6) or stagger ----
    await page.evaluate(()=>{const keep=setPose;window.setPose=(p,i)=>{if(p!==pose)(window.seen||=[]).push(p);return keep(p,i)};});
    async function hit(edge,rolls=[0,0]) {
      await page.evaluate(({edge,rolls})=>{tumblePolicy.random=()=>rolls.length?rolls.shift():0;
        tumbleHit({phase:'flight',edge,impactX:edge==='left'||edge==='right'?900:0,impactY:edge==='top'||edge==='bottom'?900:0});},{edge,rolls});
    }
    const seen=()=>page.evaluate(()=>window.seen);
    // 撞边加火气：用力撞一次 +5（慢慢碰不算）；抽中碰边动作只按动作算：撞头 +7、摔倒 +9、摇晃 → 趴地 +12
    const ang=()=>page.evaluate(()=>Math.round(angerNow()));
    await reset();await page.evaluate(()=>{setMotionActive(true);tumblePolicy.random=()=>.99;tumbleHit({phase:'flight',edge:'left',impactX:900,impactY:0})});
    assert.equal(await ang(),5,'hard edge hit');
    await page.evaluate(()=>tumbleHit({phase:'flight',edge:'left',impactX:150,impactY:0}));assert.equal(await ang(),5,'slow touch added anger');
    await page.evaluate(()=>tumbleHit({phase:'drag',edge:'left',impactX:900,impactY:0}));assert.equal(await ang(),5,'dragging into the edge added anger');
    for (const [edge,rolls,want] of [['top',[0],7],['bottom',[0,.1],9],['bottom',[0,.9],12]]) {
      await reset();await page.evaluate(()=>setMotionActive(true));await hit(edge,rolls);
      assert.equal(await ang(),want,`${edge} ${rolls} anger`);
    }
    await reset();
    await reset();await page.evaluate(()=>setMotionActive(true));await hit('top');
    assert.equal(await page.evaluate(()=>pose),'bonk','bump did not show at the hit');
    await hit('bottom');assert.equal(await page.evaluate(()=>tumble.kind),'bonk','second trigger in one throw');
    await page.evaluate(()=>setMotionActive(false));await page.waitForTimeout(1200);
    assert.equal(await page.evaluate(()=>pose),'bonk','bump not held after settling');
    await page.waitForFunction(()=>pose==='idle'&&!tumble);
    await reset();await page.evaluate(()=>setMotionActive(true));await hit('bottom',[0,.59]);
    assert.equal(await page.evaluate(()=>pose),'idle','floor sequence started before settling');
    await page.evaluate(()=>setMotionActive(false));
    await page.waitForFunction(()=>pose==='idle'&&!tumble,null,{timeout:5000});
    assert.ok(await page.evaluate(()=>$('impact').getAnimations().length>0),'no stand-up motion when she gets back up from the floor');
    assert.ok(info.lines.getup.includes(await page.evaluate(()=>$('bubble').textContent)),'no getting-up line');
    assert.deepEqual(await seen(),['fallen','idle']);
    await reset();await page.evaluate(()=>setMotionActive(true));await hit('bottom',[0,.6]);await page.evaluate(()=>setMotionActive(false));
    await page.waitForFunction(()=>pose==='idle'&&!tumble,null,{timeout:6000});
    assert.deepEqual(await seen(),['stagger','faceplant','idle']);
    await reset();await page.evaluate(()=>setMotionActive(true));await hit('bottom');await page.evaluate(()=>setMotionActive(false));
    await page.waitForTimeout(300);await page.evaluate(()=>handleClick('pat'));
    assert.deepEqual(await page.evaluate(()=>[pose,!!tumble]),['idle',false],'click did not help her up');
    await reset();await shake(1);await hit('bottom');await page.evaluate(()=>setMotionActive(false));
    await page.waitForTimeout(4000);
    assert.deepEqual(await seen(),['fallen','idle'],'dizzy played after a fall');
    const bubble=()=>page.evaluate(()=>$('bubble').textContent);
    await reset();await page.evaluate(()=>setMotionActive(true));await hit('bottom');await page.evaluate(()=>setMotionActive(false));
    assert.ok(info.lines.fallen.includes(await bubble()),'calm fall used the wrong lines');
    await reset();await shake();await hit('bottom');await page.evaluate(()=>setMotionActive(false));
    assert.ok(info.lines.fallenAngry.includes(await bubble()),'angry fall used the sweet lines');
    await page.waitForFunction(()=>!tumble,null,{timeout:4000});assert.ok(!info.lines.getup.includes(await bubble()),'angry fall said the calm getting-up line');
    await page.waitForFunction(()=>pose==='redhot',null,{timeout:6000});
    assert.deepEqual(await seen(),['fallen','redhot'],'angry fall should stand straight up into the anger pose, not via idle');
    // 这一撞才把火气顶过门槛（甩三下 21 + 摔倒 9 = 30，门槛 25）：直接气呼呼站起来（硬切到不耐烦、在它身上做站起来），不说「嘿咻」
    await reset();await shake(3);await hit('bottom');await page.evaluate(()=>setMotionActive(false));
    const cut=await page.evaluate(async()=>{
      for(const t0=performance.now();pose!=='annoyed'&&performance.now()-t0<6000;)await new Promise(r=>requestAnimationFrame(r));
      return {pose,src:decodeURIComponent(active.getAttribute('src')||'').split('/').pop(),spareOn:spare.classList.contains('on'),
        stretch:$('impact').getAnimations().length>0,line:$('bubble').textContent};
    });
    assert.equal(cut.pose,'annoyed');assert.equal(cut.src,'annoyed.webp','anger pose not hard-cut in');assert.equal(cut.spareOn,false);
    assert.ok(cut.stretch,'no stand-up motion on the anger pose');assert.ok(!info.lines.getup.includes(cut.line),'said a calm getting-up line before getting angry');
    const order=await seen();assert.deepEqual(order.slice(order.indexOf('fallen')),['fallen','annoyed'],'went through another pose between the fall and the anger');
    // 有火气时撞头：停在揉头那张（bonk-2），不翻到揉着头笑的 bonk-3，再交叉淡入到生气，中间不回待机
    await reset();await shake();await hit('top');await page.evaluate(()=>setMotionActive(false));
    const bonked=await page.evaluate(async()=>{
      const shown=new Set()
      for(const t0=performance.now();performance.now()-t0<7000;){
        await new Promise(r=>requestAnimationFrame(r))
        shown.add(decodeURIComponent(currentUrl).split('/').pop().replace('.webp',''))
        if(pose==='redhot')break
      }
      return {shown:[...shown],pose}
    });
    assert.equal(bonked.pose,'redhot','angry bonk did not end in anger');
    assert.ok(bonked.shown.includes('bonk-2')&&!bonked.shown.includes('bonk-m2')&&!bonked.shown.includes('bonk-3'),`angry bonk frames ${bonked.shown}`);
    const bonkOrder=await seen();assert.deepEqual(bonkOrder.slice(bonkOrder.indexOf('bonk')),['bonk','redhot'],'angry bonk went through idle');
    await reset('reading');await page.evaluate(()=>setMotionActive(true));await hit('top');
    assert.equal(await page.evaluate(()=>pose),'bonk');await page.waitForTimeout(1000);
    assert.equal(await page.evaluate(()=>pose),'reading','work not resumed after a flash');
    await reset('asking');await page.evaluate(()=>setMotionActive(true));await hit('top');
    assert.equal(await page.evaluate(()=>pose),'asking','question pose replaced');
    assert.ok(info.lines.tumbleAsk.includes(await page.evaluate(()=>$('bubble').textContent)),'question context did not just say it hurt');
    await reset();await page.evaluate(()=>setMotionActive(true));await hit('bottom');await page.evaluate(()=>setMotionActive(false));
    await page.waitForTimeout(200);await page.evaluate(()=>setMotionActive(true));
    assert.equal(await page.evaluate(()=>pose),'idle','drag did not cancel the fall');
    await reset();await page.evaluate(()=>{setBehavior({idleEnabled:false,clickBounce:false,tumbleActions:false});setMotionActive(true)});await hit('top');
    assert.equal(await page.evaluate(()=>!!tumble),false,'switched-off setting still triggered');
    assert.ok(await page.evaluate(()=>['bonk','fallen','stagger','faceplant'].every(p=>ClaudiaFx.surroundings(p))),'edge-hit effects missing');
    assert.deepEqual(errors,[]);
    console.log('PASS: flight/settling delay, full red-hot playback, work/question priority, mild dizziness, hidden deferral and calming pats; edge-hit bump/fall sequences, help-up click, mood after falls, work/question/drag/setting rules');
  } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
