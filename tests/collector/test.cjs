// 本番のブックマーク用JSをDOM環境で実行し、保存ファイルと要求先を検証します。
// 実サイトへの通信、Cookieの読出し、確認用応答の解決は行いません。
const {JSDOM}=require('jsdom');
const fs=require('node:fs');
const assert=require('node:assert/strict');
const script=fs.readFileSync('MinRepoMobile/Resources/Raw/minrepo-browser-collector.js','utf8');
const identity='<h1>10/8(木) 取込試験店</h1><time>2026年10月9日</time><div>勝率 0/1</div><div>総差枚 -300</div>';
const all=identity+`<h2>全台 データ一覧</h2><table><tr><th>機種</th><th>台番</th><th>差枚</th><th>G数</th><th>出率</th></tr><tr><td><a href='/1/?kishu=test'>機種A</a></td><td>101</td><td>-300</td><td>1000</td><td>90%</td></tr></table><a href='/1/'>レポートトップに戻る</a>`;
const detail=identity+`<h2>機種A データ一覧</h2><table><tr><th>台番</th><th>差枚</th><th>G数</th><th>出率</th><th>BB</th><th>RB</th></tr><tr><td>101</td><td>-300</td><td>1000</td><td>90%</td><td>2</td><td>1</td></tr></table><a href='/1/'>レポートトップに戻る</a>`;
const listing='<h1>取込試験店</h1><h2>データ一覧</h2><table><tr><td><a href="/1/">10/8(木)</a></td></tr></table><a href="/999/">9/1別欄広告</a>';
async function run({store=false,bonus=true,robots='User-agent: *\nAllow: /',pages={},cancel=false,missing=false}={}){
 const url=store?'https://min-repo.com/tag/test/':'https://min-repo.com/1/';
 const dom=new JSDOM(store?listing:identity,{url,runScripts:'outside-only'}),w=dom.window;
 w.HTMLDialogElement.prototype.showModal=function(){};w.HTMLDialogElement.prototype.close=function(){};
 let clock=100000,blob;const calls=[];
 w.Date.now=()=>clock;w.Blob=Blob;
 w.setTimeout=(f,ms)=>{clock+=ms;queueMicrotask(f);return 0;};w.clearTimeout=()=>{};
 w.URL.createObjectURL=b=>{blob=b;return 'blob:fixture';};w.URL.revokeObjectURL=()=>{};
 w.HTMLAnchorElement.prototype.click=function(){};
 w.fetch=async(value,options)=>{
   calls.push({url:value,at:clock});
   assert.equal(options.redirect,'error');assert.equal(options.credentials,'same-origin');
   if(cancel){w.document.querySelector('#minrepo-cancel').onclick();options.signal.throwIfAborted();}
   const u=new URL(value);const key=u.pathname+u.search;
   let text=u.pathname==='/robots.txt'?robots:pages[key]??({'/1/':identity,'/1/?kishu=all':missing?all.replace('<td>-300</td>','<td>-</td>'):all,'/1/?kishu=test':detail}[key]);
   return {ok:text!==undefined,status:text===undefined?404:200,text:async()=>text};
 };
 await w.eval(script);
 const field=id=>w.document.querySelector('#minrepo-'+id);
 field('bonus').checked=bonus;field('from').value='2026-10-08';field('to').value='2026-10-08';field('max').value='2';
 await field('start').onclick();
 const status=field('status')?.textContent;
 const archive=blob?JSON.parse(await blob.text()):null;
 for(let i=1;i<calls.length;i++)assert.ok(calls[i].at-calls[i-1].at>=5000,'5秒間隔');
 dom.window.close();return {archive,calls,status};
}
(async()=>{
 let r=await run();assert.equal(r.archive.Pages.length,3);assert.equal(r.archive.FetchBonusDetails,true);assert.equal(r.archive.Pages.find(p=>p.Url.endsWith('kishu=test')).Html,detail);assert.ok(r.archive.Pages.every(p=>!p.Html.includes('minrepo-collector')));console.log('PASS collector: 日別→詳細→JSON、Cookie非出力、5秒間隔');
 // Androidと.NETでも同じ本番JSが出力したファイルを取り込みます。
 fs.writeFileSync('tests/fixtures/collector-output.json',JSON.stringify(r.archive));
 r=await run({bonus:false});assert.equal(r.archive.Pages.length,2);assert.ok(!r.calls.some(c=>c.url.endsWith('kishu=test')));console.log('PASS collector: BB/RB未選択で不要な詳細通信なし');
 r=await run({bonus:false,missing:true});assert.ok(r.calls.some(c=>c.url.endsWith('kishu=test')));console.log('PASS collector: 欠損行だけ詳細を追加');
 r=await run({store:true});assert.equal(r.archive.FromDate,'2026-10-08');assert.equal(r.archive.Pages.length,4);assert.ok(r.calls.some(c=>c.url.endsWith('/page/2/')));assert.ok(!r.calls.some(c=>c.url.includes('/999/')));assert.ok(r.archive.Pages.find(p=>p.Url.includes('/tag/')).Html.includes('10/8'));console.log('PASS collector: 店舗・期間・一覧最終404・広告リンク除外');
 r=await run({robots:'User-agent: *\nDisallow: /1/'});assert.equal(r.archive,null);assert.equal(r.calls.length,1);console.log('PASS collector: robots禁止は表示済みページも保存しない');
 r=await run({pages:{'/1/?kishu=all':'<script>location.reload()</script>'}});assert.equal(r.archive,null);assert.match(r.status,/確認用ページ/);console.log('PASS collector: HTTP 200の確認用HTMLは拒否');
 r=await run({robots:'<html>確認中</html>'});assert.equal(r.archive,null);assert.equal(r.calls.length,1);console.log('PASS collector: 不明なrobotsは停止');
 r=await run({cancel:true});assert.equal(r.archive,null);assert.equal(r.calls.length,1);console.log('PASS collector: 中止後に通信・保存しない');
})().catch(e=>{console.error(e);process.exitCode=1;});
