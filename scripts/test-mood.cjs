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
    async function shake(count=12) { await page.evaluate(count=>{setMotionActive(true);for(let i=0;i<count;i++)shaken();},count); }
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
    assert.equal(await page.evaluate(()=>$('impact').getAnimations().length),0,'getup frames already end standing: no extra stand-up motion');
    assert.deepEqual(await seen(),['fallen','getup','idle']);
    await reset();await page.evaluate(()=>setMotionActive(true));await hit('bottom',[0,.6]);await page.evaluate(()=>setMotionActive(false));
    await page.waitForFunction(()=>pose==='idle'&&!tumble,null,{timeout:6000});
    assert.deepEqual(await seen(),['stagger','faceplant','getup','idle']);
    await reset();await page.evaluate(()=>setMotionActive(true));await hit('bottom');await page.evaluate(()=>setMotionActive(false));
    await page.waitForTimeout(300);await page.evaluate(()=>handleClick('pat'));
    assert.equal(await page.evaluate(()=>pose),'getup','click did not help her up');
    await reset();await shake(1);await hit('bottom');await page.evaluate(()=>setMotionActive(false));
    await page.waitForTimeout(4000);
    assert.deepEqual(await seen(),['fallen','getup','idle'],'dizzy played after a fall');
    const bubble=()=>page.evaluate(()=>$('bubble').textContent);
    await reset();await page.evaluate(()=>setMotionActive(true));await hit('bottom');await page.evaluate(()=>setMotionActive(false));
    assert.ok(info.lines.fallen.includes(await bubble()),'calm fall used the wrong lines');
    await reset();await shake();await hit('bottom');await page.evaluate(()=>setMotionActive(false));
    assert.ok(info.lines.fallenAngry.includes(await bubble()),'angry fall used the sweet lines');
    await page.waitForFunction(()=>pose==='getup');assert.ok(info.lines.getupAngry.includes(await bubble()));
    await page.waitForFunction(()=>pose==='redhot',null,{timeout:6000});
    assert.deepEqual(await seen(),['fallen','getup','idle','redhot'],'anger did not wait for the fall');
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
