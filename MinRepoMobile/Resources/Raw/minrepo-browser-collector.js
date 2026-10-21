// Chromeの通常セッションで公開ページを取得し、HTMLだけをアプリへ渡します。
// Cookie・認証ヘッダーはファイルへ含めず、確認用応答が返ったら停止します。
(async () => {
  if (location.protocol !== 'https:' || !['min-repo.com', 'www.min-repo.com'].includes(location.hostname)) {
    alert('みんレポの店舗一覧か日別レポートで開いてください。'); return;
  }
  if (document.getElementById('minrepo-collector')) return;
  const source = new URL(location.href);
  source.hash = ''; source.search = '';
  const storeMode = source.pathname.startsWith('/tag/');
  if (!storeMode && !/^\/\d+\/$/.test(source.pathname)) {
    alert('店舗一覧か日別レポートで開いてください。'); return;
  }
  const dialog = document.createElement('dialog'); dialog.id = 'minrepo-collector';
  dialog.style.cssText = 'position:fixed;max-width:480px;width:90%;padding:20px;border:2px solid #17365d;border-radius:12px;background:white;color:#172b4d;font:16px sans-serif;z-index:2147483647';
  const today = new Date();
  const iso = d => `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`;
  const start = new Date(today); start.setDate(start.getDate()-30);
  dialog.innerHTML = `<h2 style="margin:0 0 12px">みんレポ取得</h2>
    <p>完了までChromeを開いておいてください。取得後、アプリで保存ファイルを読み込みます。</p>
    <div id="minrepo-period" ${storeMode?'':'hidden'}>
      <label>開始日 <input id="minrepo-from" type="date" value="${iso(start)}"></label><br>
      <label>終了日 <input id="minrepo-to" type="date" value="${iso(today)}"></label><br>
      <label>最大日数 <input id="minrepo-max" type="number" min="1" max="100" value="31" style="width:70px"></label>
    </div><p><label><input id="minrepo-bonus" type="checkbox" checked> BB・RBなどの詳細も取得する</label></p>
    <p><label>取得間隔（秒） <input id="minrepo-delay" type="number" min="5" max="60" value="5" style="width:70px"></label></p>
    <p id="minrepo-status" role="status">準備できました。</p>
    <button id="minrepo-start" type="button">取得して保存</button>
    <button id="minrepo-cancel" type="button">閉じる</button>`;
  document.body.appendChild(dialog); dialog.showModal();
  const field = id => dialog.querySelector('#minrepo-'+id);
  const controller = new AbortController(); let running = false;
  field('cancel').onclick = () => { controller.abort(); dialog.close(); dialog.remove(); };
  field('start').onclick = async () => {
    if (running) return;
    const from = field('from').value, to = field('to').value;
    const maximum = storeMode ? Number(field('max').value) : 1;
    const delay = Number(field('delay').value) * 1000;
    if ((storeMode && (!from || !to || from > to)) || !Number.isInteger(maximum) || maximum<1 || maximum>100 ||
        !Number.isFinite(delay) || delay<5000 || delay>60000) {
      field('status').textContent = '日付・日数・取得間隔を確認してください。'; return;
    }
    running = true; field('start').disabled = true; field('cancel').textContent = '中止する';
    let wake;
    const status = text => { field('status').textContent = text; };
    const pages = new Map(); let last = 0, policy;
    const escape = s => String(s).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;');
    const clean = html => html.replace(/<dialog\b[^>]*id=["']minrepo-collector["'][^>]*>[\s\S]*?<\/dialog\s*>/gi,'').replace(/<(script|style|iframe|noscript)\b[^>]*>[\s\S]*?<\/\1\s*>/gi,'');
    const allowed = u => u.origin === location.origin && !u.username && !u.password &&
      (/^\/\d+\/$/.test(u.pathname) || u.pathname.startsWith('/tag/') || u.pathname === '/robots.txt');
    // robotsの抽出器専用グループを優先し、なければワイルドカードを採用します。
    const parsePolicy = text => {
      if (/<\s*(html|script)\b/i.test(text)) throw Error('robots.txtを確認できませんでした。');
      const groups=[]; let group;
      for (const raw of text.split('\n')) {
        const line=raw.split('#')[0].trim(), sep=line.indexOf(':'); if(sep<0)continue;
        const key=line.slice(0,sep).toLowerCase(), value=line.slice(sep+1).trim();
        if(key==='user-agent') {
          if(!group || group.rules.length) {group={agents:[],rules:[]};groups.push(group);}
          group.agents.push(value.toLowerCase());
        } else if(group && value && ['allow','disallow'].includes(key)) group.rules.push({path:value,allow:key==='allow'});
      }
      const product='minrepomobileextractor';
      let chosen=groups.filter(g=>g.agents.some(a=>a!=='*' && (product.includes(a)||a.includes(product))));
      if(!chosen.length) chosen=groups.filter(g=>g.agents.includes('*'));
      return u => {
        const path=u.pathname+u.search;
        const matches=chosen.flatMap(g=>g.rules).filter(r=>{
          const end=r.path.endsWith('$');
          const pattern=r.path.replace(/\$$/,'').split('*').map(s=>s.replace(/[.*+?^${}()|[\]\\]/g,'\\$&')).join('.*');
          return new RegExp('^'+pattern+(end?'$':'')).test(path);
        }).sort((a,b)=>b.path.replace(/[\*$]/g,'').length-a.path.replace(/[\*$]/g,'').length || Number(b.allow)-Number(a.allow));
        return !matches.length || matches[0].allow;
      };
    };
    const request = async u => {
      if (!allowed(u) || (policy && !policy(u))) throw Error('自動取得が許可されていないページです。');
      const wait=Math.max(0,last+delay-Date.now());
      if(wait) await new Promise((resolve,reject)=>{
        const abort=()=>{clearTimeout(timer);reject(Error('中止しました。'));};
        const timer=setTimeout(()=>{controller.signal.removeEventListener('abort',abort);resolve();},wait);
        controller.signal.addEventListener('abort',abort,{once:true});
      });
      controller.signal.throwIfAborted(); last=Date.now();
      const response=await fetch(u.href,{credentials:'same-origin',redirect:'error',signal:controller.signal});
      if(!response.ok) {const error=Error(`ページを取得できませんでした: HTTP ${response.status}`);error.status=response.status;throw error;}
      return response.text();
    };
    const getPage = async u => {
      if(!allowed(u)||(policy&&!policy(u)))throw Error('自動取得が許可されていないページです。');
      if(pages.has(u.href)) return new DOMParser().parseFromString(pages.get(u.href),'text/html');
      const html=u.href===location.href.split('#')[0] ? document.documentElement.outerHTML : await request(u);
      const doc=new DOMParser().parseFromString(html,'text/html');
      if(!doc.querySelector('h1')) throw Error('確認用ページまたは空のページが返されました。対象ページをChromeで表示してから取得し直してください。');
      if(pages.size>=2000)throw Error('ページ数が多すぎます。期間を短くしてください。');
      pages.set(u.href,clean(html)); return doc;
    };
    const identity = doc => {
      const title=doc.querySelector('h1')?.textContent.trim()||'';
      const day=title.match(/(\d{1,2})\/(\d{1,2})/);
      const published=doc.body.textContent.match(/(20\d{2})年\s*(\d{1,2})月\s*\d{1,2}日/);
      if(!day||!published) throw Error('レポートの日付を確認できませんでした。');
      const year=Number(published[1])-(Number(day[1])-Number(published[2])>6?1:0);
      return {title,date:`${year}-${day[1].padStart(2,'0')}-${day[2].padStart(2,'0')}`,
        store:title.replace(/^\s*\d{1,2}\/\d{1,2}\s*(?:\([^)]*\))?\s*/,'')};
    };
    const reportLinks = doc => {
      const elements=Array.from(doc.querySelectorAll('h1,h2,h3,table'));
      const heading=elements.findIndex(e=>/^H/.test(e.tagName)&&/データ\s*一覧/.test(e.textContent));
      const table=elements.slice(heading+1).find(e=>e.tagName==='TABLE');
      if(heading<0||!table) return [];
      return [...new Set(Array.from(table.querySelectorAll('a[href]')).filter(a=>/\d{1,2}\/\d{1,2}/.test(a.textContent))
        .map(a=>new URL(a.getAttribute('href'),source)).filter(u=>allowed(u)&&/^\/\d+\/$/.test(u.pathname)&&!u.search).map(u=>u.href))];
    };
    const selected=[]; const seen=new Set(); let selectedStore;
    try {
      try {wake=await navigator.wakeLock?.request('screen');} catch {}
      const robots=await request(new URL('/robots.txt',source)); policy=parsePolicy(robots);
      const bonus=field('bonus').checked;
      for(let page=1;page<=(storeMode?5:1)&&selected.length<maximum;page++) {
        status(`レポート一覧を確認中 ${page}`);
        const listUrl=page===1?source:new URL(`page/${page}/`,source);
        let candidates;
        try {candidates=storeMode?reportLinks(await getPage(listUrl)):[source.href];}
        catch(error){if(page>1&&error.status===404)break;throw error;}
        let newCount=0, older=false;
        for(const value of candidates) {
          if(seen.has(value))continue;seen.add(value);newCount++;
          const topUrl=new URL(value); status(`日別レポートを確認中 ${selected.length+1}/${maximum}`);
          const info=identity(await getPage(topUrl));
          if(storeMode && info.date<from){older=true;break;}
          if(storeMode && info.date>to)continue;
          if(selectedStore && info.store!==selectedStore) throw Error('別の店舗のレポートが含まれています。店舗一覧から取得し直してください。');
          selectedStore=info.store;
          const allUrl=new URL('?kishu=all',topUrl); const all=await getPage(allUrl);
          const details=new Set();
          for(const row of all.querySelectorAll('table tr')) {
            const cells=Array.from(row.querySelectorAll('td'));
            if(cells.length!==5)continue;
            const link=cells[0].querySelector('a[href]');if(!link)continue;
            const u=new URL(link.getAttribute('href'),allUrl);
            const text=i=>cells[i].textContent.replace(/[,\s]/g,'').replaceAll('−','-');
            const repair=!/^[+-]?\d+$/.test(text(2)) || !/^\d+$/.test(text(3)) ||
              (Number(text(3))>0&&!/^[\d.]+%$/.test(text(4)));
            if((bonus||repair)&&allowed(u)&&u.pathname===topUrl.pathname&&u.searchParams.has('kishu')&&u.searchParams.get('kishu')!=='all') details.add(u.href);
          }
          let n=0;
          for(const detail of details){status(`詳細を取得中 ${selected.length+1}日目 ${++n}/${details.size}`);await getPage(new URL(detail));}
          selected.push({url:topUrl.href,title:info.title});
          if(selected.length>=maximum)break;
        }
        if(older||!newCount)break;
      }
      if(!selected.length)throw Error('指定期間のレポートがありません。');
      const ids=new Set(selected.map(r=>new URL(r.url).pathname));
      for(const key of pages.keys()) if(!ids.has(new URL(key).pathname))pages.delete(key);
      if(storeMode) pages.set(source.href,`<h1>${escape(selectedStore)}</h1><h2>データ一覧</h2><table><tr><th>日付</th></tr>${selected.map(r=>`<tr><td><a href="${escape(r.url)}">${escape(r.title)}</a></td></tr>`).join('')}</table>`);
      const archive={Format:'minrepo-browser-pages',Version:1,SourceUrl:source.href,FromDate:storeMode?from:null,
        ToDate:storeMode?to:null,FetchBonusDetails:bonus,Pages:Array.from(pages,([Url,Html])=>({Url,Html}))};
      const blob=new Blob([JSON.stringify(archive)],{type:'application/json'});
      if(blob.size>128*1024*1024)throw Error('ファイルが128MBを超えます。期間を短くしてください。');
      const download=document.createElement('a'); const objectUrl=URL.createObjectURL(blob);
      download.href=objectUrl;download.download=`minrepo-browser-${iso(new Date())}.json`;
      download.click();setTimeout(()=>URL.revokeObjectURL(objectUrl),60000);
      status(`${selected.length}日分を保存しました。アプリに戻り「取得ファイルを読み込む」を押してください。`);
    } catch(error) {status(controller.signal.aborted?'中止しました。':`停止しました: ${error.message}`);}
    finally {running=false;field('start').disabled=false;field('cancel').textContent='閉じる';try{await wake?.release();}catch{}}
  };
})();
