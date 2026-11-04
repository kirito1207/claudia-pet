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
    assert.deepEqual(errors,[]);
    console.log('PASS: flight/settling delay, full red-hot playback, work/question priority, mild dizziness, hidden deferral and calming pats');
  } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
