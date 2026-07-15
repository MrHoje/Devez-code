(function(){
  'use strict';
  const $=id=>document.getElementById(id);
  const savedWebFontSize=(()=>{try{const value=Number(localStorage.getItem('devez-dashboard-font-size'));return value>=9&&value<=32?value:0}catch(e){return 0}})();
  const state={socket:null,clientId:null,controllerId:null,sessions:[],projects:[],folders:[],selected:null,term:null,agent:'',theme:null,appTheme:'dark',fontFamily:'Cascadia Mono',fontSize:savedWebFontSize||16,baseFontSize:16,fontSizePinned:!!savedWebFontSize,reconnect:0,starting:false,offline:false,resizeRequest:null,mobileGrid:false,mobilePan:0,mobilePanMax:0,autoClaimPending:false,follow:true,userScrollUntil:0,outputChunks:[],outputTimer:0,cursorTimer:0,terminalInputCleanup:null,expandedProjects:new Set(),seenProjects:new Set(),expandedFolders:new Set(),seenFolders:new Set(),collapsedSessions:new Set(),seenSessions:new Set(),showHiddenByProject:new Map(),seenHiddenProjects:new Set()};
  const mobileQuery=matchMedia('(max-width:720px), (max-height:520px) and (pointer:coarse)');
  const svg={eye:'<svg viewBox="0 0 24 24"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12Z"/><circle cx="12" cy="12" r="3"/></svg>',eyeOff:'<svg viewBox="0 0 24 24"><path d="m3 3 18 18M10.6 10.6a2 2 0 0 0 2.8 2.8M9.9 4.2A10.8 10.8 0 0 1 12 4c6.5 0 10 8 10 8a17 17 0 0 1-2 3M6.6 6.6C3.5 8.5 2 12 2 12s3.5 8 10 8a10 10 0 0 0 4.2-.9"/></svg>',chevronUp:'<svg viewBox="0 0 24 24"><path d="m18 15-6-6-6 6"/></svg>',chevronDown:'<svg viewBox="0 0 24 24"><path d="m6 9 6 6 6-6"/></svg>',folder:'<svg viewBox="0 0 24 24"><path d="M3 6a2 2 0 0 1 2-2h5l2 2h7a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z"/></svg>'};
  let toastTimer=0,fitFrame=0,viewportFrame=0,lastViewportWidth=0,lastViewportHeight=0,viewportSettleGen=0;

  // 경로 `/app/{deviceId}` 에서 deviceId 를 읽어 릴레이의 /client/{deviceId} 로 연결한다.
  const deviceId=decodeURIComponent(location.pathname.split('/')[2]||'');

  function toast(text){const el=$('toast');el.textContent=text;el.classList.add('on');clearTimeout(toastTimer);toastTimer=setTimeout(()=>el.classList.remove('on'),2200)}
  function send(msg){if(state.socket&&state.socket.readyState===WebSocket.OPEN)state.socket.send(JSON.stringify(msg))}
  function fromB64(value){const raw=atob(value||'');const out=new Uint8Array(raw.length);for(let i=0;i<raw.length;i++)out[i]=raw.charCodeAt(i);return out}
  function copyText(value){if(navigator.clipboard&&window.isSecureContext)return navigator.clipboard.writeText(value).catch(()=>copyFallback(value));copyFallback(value);return Promise.resolve()}
  function copyFallback(value){const ta=document.createElement('textarea');ta.value=value;ta.style.cssText='position:fixed;left:-9999px;top:0';document.body.appendChild(ta);ta.select();try{document.execCommand('copy')}catch(e){}ta.remove()}
  function hasControl(){return !!state.clientId&&state.controllerId===state.clientId}
  function normalizeIme(value){try{return (value||'').normalize('NFC')}catch(e){return value||''}}

  function connect(){
    if(!deviceId){showEmpty('잘못된 접근입니다','올바른 PC 링크로 다시 접속해 주세요.','!');return}
    const protocol=location.protocol==='https:'?'wss:':'ws:',ws=new WebSocket(protocol+'//'+location.host+'/client/'+encodeURIComponent(deviceId));state.socket=ws;
    ws.onopen=()=>{state.reconnect=0;state.offline=false;$('connection').classList.add('online');$('connection').querySelector('span').textContent='연결됨';send({type:'refresh'})};
    ws.onmessage=e=>{try{handle(JSON.parse(e.data))}catch(err){console.warn(err)}};
    ws.onclose=()=>{if(state.socket!==ws)return;$('connection').classList.remove('online');$('connection').querySelector('span').textContent='연결 끊김';setTimeout(connect,Math.min(5000,800+state.reconnect++*500))};
  }

  function handle(msg){
    if(msg.type==='offline'){state.offline=true;showEmpty('PC가 오프라인입니다','DevezCode가 실행 중이고 원격 접속이 켜져 있는지 확인해 주세요.','⏻');return}
    if(msg.type==='hello'){state.offline=false;state.clientId=msg.clientId;state.controllerId=msg.controllerId;state.autoClaimPending=false;renderControl();if(state.selected)send({type:'subscribe',roomId:state.selected});return}
    if(msg.type==='sessions'){
      state.offline=false;state.sessions=msg.sessions||[];state.projects=msg.projects||[];state.folders=msg.folders||[];state.controllerId=msg.controllerId;state.theme=msg.terminalTheme;state.fontFamily=msg.fontFamily||state.fontFamily;state.baseFontSize=msg.fontSize||16;if(!state.fontSizePinned)state.fontSize=state.baseFontSize;
      applyUiTheme(msg.uiTheme,msg.terminalTheme,msg.appTheme);rememberExpansion();syncFontSizeControl();renderSessions();renderControl();syncSelectedMeta();return;
    }
    if(msg.type==='control'){state.controllerId=msg.controllerId;state.autoClaimPending=false;if(!hasControl()){state.resizeRequest=null;state.mobileGrid=false}renderControl();scheduleTerminalFit();return}
    if(msg.type==='starting'){if(msg.roomId===state.selected){state.starting=true;showEmpty('세션을 시작하는 중입니다','DevezCode의 기존 대화를 그대로 불러오고 있습니다.','…')}return}
    if(msg.type==='snapshot'){
      if(msg.roomId!==state.selected)return;state.starting=false;const session=sessionById(msg.roomId);if(!session)return;
      session.alive=true;session.cols=msg.cols;session.rows=msg.rows;state.resizeRequest=null;state.mobileGrid=false;ensureTerminal(session);const term=state.term;clearTimeout(state.outputTimer);state.outputTimer=0;state.outputChunks=[];term.reset();term.resize(Math.max(2,msg.cols),Math.max(2,msg.rows));sizeTerminal(msg.cols,msg.rows);term.write(fromB64(msg.data),()=>{if(state.term!==term)return;term.scrollToBottom();scheduleTerminalFit()});$('empty').classList.add('hidden');renderSessions();return;
    }
    if(msg.type==='output'){if(msg.roomId===state.selected&&state.term)queueOutput(fromB64(msg.data));return}
    if(msg.type==='size'){if(msg.roomId===state.selected&&state.term){const requested=state.resizeRequest&&state.resizeRequest.roomId===msg.roomId&&state.resizeRequest.cols===msg.cols&&state.resizeRequest.rows===msg.rows,current=state.mobileGrid&&state.term.cols===msg.cols&&state.term.rows===msg.rows;state.mobileGrid=!!(requested||current);if(requested)state.resizeRequest=null;const session=sessionById(msg.roomId);if(session){session.cols=msg.cols;session.rows=msg.rows}state.term.resize(Math.max(2,msg.cols),Math.max(2,msg.rows));sizeTerminal(msg.cols,msg.rows)}return}
    if(msg.type==='error'){state.starting=false;toast(msg.message||'요청을 처리하지 못했습니다.');syncSelectedMeta()}
  }

  function applyUiTheme(ui,term,appTheme){
    state.appTheme=appTheme||state.appTheme;
    if(ui)for(const [key,value] of Object.entries(ui))document.documentElement.style.setProperty('--'+key.replace(/[A-Z]/g,m=>'-'+m.toLowerCase()),value);
    if(term&&term.background)document.documentElement.style.setProperty('--term-bg',term.background);
    const meta=document.querySelector('meta[name=theme-color]');if(meta&&ui)meta.content=mobileQuery.matches?(ui.panel||ui.bg):ui.bg;
    document.documentElement.style.colorScheme=appTheme==='dark'?'dark':'light';if(state.term&&state.theme)state.term.options.theme=themeFor(state.agent);
  }
  function syncFontSizeControl(){
    const select=$('font-size'),value=String(state.fontSize);if(!select.querySelector('option[value="'+value+'"]')){const option=document.createElement('option');option.value=value;option.textContent=value+'px';select.appendChild(option)}select.value=value;
  }
  function setWebFontSize(value){
    const size=Math.max(9,Math.min(32,Number(value)||16));state.fontSize=size;state.fontSizePinned=true;try{localStorage.setItem('devez-dashboard-font-size',String(size))}catch(e){}
    if(state.term){state.mobileGrid=false;state.resizeRequest=null;state.term.options.fontSize=size;sizeTerminal(state.term.cols,state.term.rows);focusTerminalInput()}
  }
  function themeFor(agent){const t=Object.assign({},state.theme||{});if(agent==='gajae')t.selectionBackground=t.background&&t.background.toLowerCase()==='#f2ede6'?'#C2D8B0':t.background&&t.background.toLowerCase()==='#f8fafc'?'#C5D8F8':'#264F78';return t}
  function sessionById(id){return state.sessions.find(s=>s.roomId===id)}

  function rememberExpansion(){
    for(const p of state.projects)if(!state.seenProjects.has(p.path)){state.seenProjects.add(p.path);if(p.isExpanded)state.expandedProjects.add(p.path)}
    for(const f of state.folders)if(!state.seenFolders.has(f.id)){state.seenFolders.add(f.id);if(f.isExpanded)state.expandedFolders.add(f.id)}
    for(const p of state.projects)if(!state.seenHiddenProjects.has(p.path)){state.seenHiddenProjects.add(p.path);let value=p.showHiddenSessions!==false;try{const saved=localStorage.getItem('devez-dashboard-show-hidden:'+p.path);if(saved!=null)value=saved==='1'}catch(e){}state.showHiddenByProject.set(p.path,value)}
    for(const s of state.sessions)if(!state.seenSessions.has(s.roomId)){state.seenSessions.add(s.roomId);if(s.childrenExpanded===false)state.collapsedSessions.add(s.roomId)}
  }
  function makeButton(className){const b=document.createElement('button');b.type='button';b.className=className;return b}
  function renderSessions(){
    $('session-count').textContent=state.sessions.length;const list=$('session-list');list.textContent='';
    if(!state.projects.length&&!state.folders.length){const empty=document.createElement('div');empty.className='sidebar-empty';empty.textContent='등록된 프로젝트가 없습니다.';list.appendChild(empty);return}
    const sorted=(items)=>items.slice().sort((a,b)=>(a.rootOrder??2147483647)-(b.rootOrder??2147483647)),folderIds=new Set(state.folders.map(f=>f.id));
    const roots=[...state.folders.map(item=>({kind:'folder',item,rootOrder:item.rootOrder})),...state.projects.filter(p=>!p.folderId||!folderIds.has(p.folderId)).map(item=>({kind:'project',item,rootOrder:item.rootOrder}))];
    for(const root of sorted(roots)){
      if(root.kind==='project'){list.appendChild(renderProject(root.item));continue}
      const folder=root.item,projects=sorted(state.projects.filter(p=>p.folderId===folder.id));
      const group=document.createElement('section'),head=document.createElement('div'),projectsHost=document.createElement('div'),open=state.expandedFolders.has(folder.id);group.className='folder-card';head.className='folder-header';projectsHost.className='folder-projects';
      head.innerHTML='<span class="folder-icon">'+svg.folder+'</span><strong></strong><em></em><button type="button" class="tree-action" aria-label="폴더 접기 또는 펼치기">'+(open?svg.chevronUp:svg.chevronDown)+'</button>';head.querySelector('strong').textContent=folder.name;head.querySelector('em').textContent=projects.length+'개';
      const toggle=()=>{open?state.expandedFolders.delete(folder.id):state.expandedFolders.add(folder.id);renderSessions()};head.onclick=toggle;head.querySelector('button').onclick=e=>{e.stopPropagation();toggle()};
      group.appendChild(head);if(open&&projects.length){for(const p of projects)projectsHost.appendChild(renderProject(p));group.appendChild(projectsHost)}list.appendChild(group);
    }
  }
  function renderProject(project){
    const card=document.createElement('section'),header=document.createElement('div'),body=document.createElement('div'),open=state.expandedProjects.has(project.path),showHidden=state.showHiddenByProject.get(project.path)!==false;
    card.className='project-card'+((project.sessions||[]).some(s=>s.roomId===state.selected)?' active':'');header.className='project-header';body.className='project-body';
    header.innerHTML='<span class="project-copy"><strong></strong><small></small></span><span class="project-actions"><button type="button" class="tree-action hidden-toggle" aria-label="숨김 세션 보기 또는 숨기기">'+(showHidden?svg.eye:svg.eyeOff)+'</button><button type="button" class="tree-action project-toggle" aria-label="프로젝트 접기 또는 펼치기">'+(open?svg.chevronUp:svg.chevronDown)+'</button></span>';
    header.querySelector('strong').textContent=project.name;header.querySelector('small').textContent=project.path;
    const hiddenToggle=header.querySelector('.hidden-toggle');hiddenToggle.title=showHidden?'숨김 세션 숨기기':'숨김 세션 표시';hiddenToggle.onclick=e=>{e.stopPropagation();state.showHiddenByProject.set(project.path,!showHidden);try{localStorage.setItem('devez-dashboard-show-hidden:'+project.path,showHidden?'0':'1')}catch(err){}renderSessions()};
    header.querySelector('.project-toggle').onclick=e=>{e.stopPropagation();open?state.expandedProjects.delete(project.path):state.expandedProjects.add(project.path);renderSessions()};card.appendChild(header);
    if(open){renderProjectSessions(body,project,showHidden);card.appendChild(body)}return card;
  }
  function renderProjectSessions(host,project,showHidden){
    const sessions=project.sessions||[],ids=new Set(sessions.map(s=>s.roomId)),children=new Map(),index=new Map(sessions.map((s,i)=>[s.roomId,i]));
    for(const s of sessions){const parent=ids.has(s.parentId)?s.parentId:'';if(!children.has(parent))children.set(parent,[]);children.get(parent).push(s)}
    const ordered=items=>(items||[]).slice().sort((a,b)=>(a.hidden?1:0)-(b.hidden?1:0)||(index.get(a.roomId)-index.get(b.roomId))),roots=ordered(children.get(''));
    const hasVisible=(session,seen=new Set())=>{if(!session||seen.has(session.roomId))return false;seen.add(session.roomId);return !session.hidden||ordered(children.get(session.roomId)).some(child=>hasVisible(child,seen))};
    const normalRoots=roots.filter(root=>hasVisible(root)),hiddenRoots=roots.filter(root=>!hasVisible(root));
    renderSessionForest(host,normalRoots,children,showHidden,false);
    if(showHidden&&hiddenRoots.length){const divider=document.createElement('div'),hiddenHost=document.createElement('div');divider.className='hidden-divider';hiddenHost.className='hidden-session-group';host.appendChild(divider);renderSessionForest(hiddenHost,hiddenRoots,children,true,true);host.appendChild(hiddenHost)}
  }
  function renderSessionForest(host,roots,children,showHidden,hiddenGroup){
    const visited=new Set();
    const append=(session,depth)=>{if(!session||visited.has(session.roomId))return;visited.add(session.roomId);const kids=(children.get(session.roomId)||[]).slice().sort((a,b)=>(a.hidden?1:0)-(b.hidden?1:0)),rowVisible=hiddenGroup||showHidden||!session.hidden;
      if(rowVisible)host.appendChild(renderSessionRow(session,kids,depth));
      if(!state.collapsedSessions.has(session.roomId))for(const child of kids)append(child,depth+1);
    };
    for(const root of roots)append(root,0);
  }
  function renderSessionRow(session,kids,depth){
    const row=makeButton('session-card'+(depth>0?' child':'')+(session.roomId===state.selected?' active':'')+(session.alive?' live':'')+(session.hidden?' hidden-session':''));row.style.setProperty('--depth',depth);
    row.innerHTML='<span class="session-agent"></span><span class="session-state">'+(session.hidden?svg.eyeOff:'')+'</span><span class="session-copy"><strong></strong><small></small></span><span class="session-child-count"></span><span class="session-chevron"></span>';
    const agent=row.querySelector('.session-agent'),image=document.createElement('img');image.src=agentIconUrl(session.agent);image.alt='';image.draggable=false;agent.appendChild(image);row.querySelector('.session-copy strong').textContent=session.name;row.querySelector('.session-copy small').textContent=session.hidden?'숨김 세션':(session.alive?'실행 중':'탭하여 실행');
    const count=row.querySelector('.session-child-count'),toggle=row.querySelector('.session-chevron');if(kids.length){count.textContent=kids.length;toggle.innerHTML=state.collapsedSessions.has(session.roomId)?svg.chevronDown:svg.chevronUp;toggle.onclick=e=>{e.stopPropagation();state.collapsedSessions.has(session.roomId)?state.collapsedSessions.delete(session.roomId):state.collapsedSessions.add(session.roomId);renderSessions()}}
    row.onclick=()=>selectSession(session.roomId);return row;
  }
  function agentIconUrl(agent){
    const id=(agent||'claude').toLowerCase(),dark=state.appTheme==='dark';const file=id==='opencode'?(dark?'opencode_icon_white_50.png':'opencode_icon_black_50.png'):id==='grok'?(dark?'grok_icon_white_50.png':'grok_icon_black_50.png'):id==='codex'?'codex.png':id==='gajae'?'gajae_code.png':id==='antigravity'?'anti.png':id==='deepseek'?'deepseek.png':'claude_code.png';return '/agent-assets/'+file;
  }
  function selectSession(roomId){state.selected=roomId;state.starting=!sessionById(roomId)?.alive;disposeTerminal();renderSessions();syncSelectedMeta();send({type:'subscribe',roomId});closeSidebar()}
  function showEmpty(title,description,icon){const empty=$('empty');empty.querySelector('strong').textContent=title;empty.querySelector('span').textContent=description;empty.querySelector('.empty-icon').textContent=icon;empty.classList.remove('hidden')}
  function syncSelectedMeta(){
    if(state.offline)return;
    const s=sessionById(state.selected);if(!s){if(state.selected){state.selected=null;disposeTerminal()}$('session-header').classList.add('hidden');showEmpty('세션을 선택하세요','프로젝트의 실행 전 세션도 여기서 바로 열 수 있습니다.','›_');return}
    $('session-header').classList.remove('hidden');$('session-title').textContent=s.name;$('session-project').textContent=s.projectName;$('agent-pill').textContent=s.agent;$('size-label').textContent=s.cols+' × '+s.rows;
    if(state.starting)showEmpty('세션을 시작하는 중입니다','DevezCode의 기존 대화를 그대로 불러오고 있습니다.','…');else if(!state.term&&!s.alive)showEmpty('실행되지 않은 세션입니다','탭하면 DevezCode 세션을 시작합니다.','▶');
    if(state.term&&(state.term.cols!==s.cols||state.term.rows!==s.rows)){state.term.resize(Math.max(2,s.cols),Math.max(2,s.rows));sizeTerminal(s.cols,s.rows)}
  }

  function sendInput(data){if(!data)return;if(!hasControl()){toast('먼저 제어권을 가져오세요.');return}if(state.agent==='codex'&&state.term){state.follow=true;state.mobilePan=0;try{state.term.scrollToBottom()}catch(e){}applyMobilePan()}send({type:'input',roomId:state.selected,data})}
  // 모바일에서는 xterm의 숨은 textarea를 사용하지 않는다. 독립된 네이티브 textarea가 한글을 먼저
  // 조합한 뒤 확정 문자열만 PTY로 보내므로 xterm/Android 키보드별 이벤트 순서 차이에 영향받지 않는다.
  const mobileInput=$('mobile-ime-input'),hangulInitial='ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ',hangulVowel='ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ',hangulFinal=' ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ';
  const vowelPairs={'ㅗㅏ':'ㅘ','ㅗㅐ':'ㅙ','ㅗㅣ':'ㅚ','ㅜㅓ':'ㅝ','ㅜㅔ':'ㅞ','ㅜㅣ':'ㅟ','ㅡㅣ':'ㅢ'},finalPairs={'ㄱㅅ':'ㄳ','ㄴㅈ':'ㄵ','ㄴㅎ':'ㄶ','ㄹㄱ':'ㄺ','ㄹㅁ':'ㄻ','ㄹㅂ':'ㄼ','ㄹㅅ':'ㄽ','ㄹㅌ':'ㄾ','ㄹㅍ':'ㄿ','ㄹㅎ':'ㅀ','ㅂㅅ':'ㅄ'};
  let mobileInputTimer=0,mobileInputComposing=false,mobileCommitPending=false;
  function composeCompatibilityJamo(value){const chars=Array.from(value||''),out=[];for(let i=0;i<chars.length;){const initial=hangulInitial.indexOf(chars[i]);let vowel=hangulVowel.indexOf(chars[i+1]);if(initial<0||vowel<0){out.push(chars[i++]);continue}let vowelChar=chars[i+1];i+=2;const joinedVowel=vowelPairs[vowelChar+(chars[i]||'')];if(joinedVowel){vowelChar=joinedVowel;vowel=hangulVowel.indexOf(vowelChar);i++}let final=0;const firstFinal=hangulFinal.indexOf(chars[i]||''),next=chars[i+1];if(firstFinal>0&&(!next||hangulVowel.indexOf(next)<0)){const joinedFinal=finalPairs[(chars[i]||'')+(next||'')],after=chars[i+2];if(joinedFinal&&(!after||hangulVowel.indexOf(after)<0)){final=hangulFinal.indexOf(joinedFinal);i+=2}else{final=firstFinal;i++}}out.push(String.fromCharCode(0xAC00+(initial*21+vowel)*28+final))}return out.join('')}
  function updateMobileInputVisual(){mobileInput.classList.toggle('has-text',!!mobileInput.value)}
  function flushMobileInput(){clearTimeout(mobileInputTimer);mobileInputTimer=0;if(mobileInputComposing)return;const value=mobileInput.value;mobileInput.value='';mobileCommitPending=false;updateMobileInputVisual();if(value)sendInput(normalizeIme(composeCompatibilityJamo(value)))}
  function scheduleMobileInputFlush(){clearTimeout(mobileInputTimer);mobileInputTimer=setTimeout(flushMobileInput,700)}
  function focusTerminalInput(){if(mobileQuery.matches){settleVisualViewport();try{mobileInput.focus({preventScroll:true})}catch(e){mobileInput.focus()}}else if(state.term)state.term.focus()}
  function concatChunks(chunks){let length=0;for(const chunk of chunks)length+=chunk.length;const out=new Uint8Array(length);let offset=0;for(const chunk of chunks){out.set(chunk,offset);offset+=chunk.length}return out}
  function stripCursorVisibility(bytes){let count=0,last=0;for(let i=0;i<bytes.length;i++){if(i+5<bytes.length&&bytes[i]===27&&bytes[i+1]===91&&bytes[i+2]===63&&bytes[i+3]===50&&bytes[i+4]===53&&(bytes[i+5]===104||bytes[i+5]===108)){last=bytes[i+5]===104?1:2;i+=5;continue}count++}if(!last)return{bytes,last};const out=new Uint8Array(count);let offset=0;for(let i=0;i<bytes.length;i++){if(i+5<bytes.length&&bytes[i]===27&&bytes[i+1]===91&&bytes[i+2]===63&&bytes[i+3]===50&&bytes[i+4]===53&&(bytes[i+5]===104||bytes[i+5]===108)){i+=5;continue}out[offset++]=bytes[i]}return{bytes:out,last}}
  function scheduleCodexCursor(term){clearTimeout(state.cursorTimer);state.cursorTimer=setTimeout(()=>{state.cursorTimer=0;if(state.term===term)term.write('\x1b[?25h')},150)}
  function prepareCodexOutput(term,bytes){const result=stripCursorVisibility(bytes);if(!result.last){if(state.cursorTimer)scheduleCodexCursor(term);return result.bytes}clearTimeout(state.cursorTimer);state.cursorTimer=0;term.write('\x1b[?25l');if(result.last===1)scheduleCodexCursor(term);return result.bytes}
  function queueOutput(bytes){const term=state.term;if(!term)return;if(state.agent!=='codex'){term.write(bytes);return}state.outputChunks.push(bytes);if(!state.outputTimer)state.outputTimer=setTimeout(()=>{state.outputTimer=0;if(state.term!==term){state.outputChunks=[];return}const chunks=state.outputChunks;state.outputChunks=[];if(!chunks.length)return;term.write(prepareCodexOutput(term,concatChunks(chunks)),()=>{if(state.term===term&&state.follow)requestAnimationFrame(()=>{if(state.term===term&&state.follow)try{term.scrollToBottom()}catch(e){}})})},16)}
  function ensureTerminal(session){
    if(state.term&&state.agent===session.agent)return;disposeTerminal();state.agent=session.agent;
    const Ctor=session.agent==='codex'&&window.Terminal6?window.Terminal6:window.Terminal,term=new Ctor({theme:themeFor(session.agent),fontFamily:state.fontFamily+", Cascadia Mono, Consolas, 'D2Coding', 'NanumGothicCoding', 'Malgun Gothic', monospace",fontSize:state.fontSize,cursorBlink:true,allowProposedApi:true,scrollback:5000,windowsPty:{backend:'conpty',buildNumber:0}});
    const host=$('terminal');state.term=term;host.classList.add('ready');$('workspace').classList.add('has-terminal');term.open(host);
    // 모바일 키 입력은 독립 textarea가 담당한다. xterm의 숨은 textarea에서 나온 자모는 전부 무시한다.
    term.onData(data=>{if(!mobileQuery.matches)sendInput(normalizeIme(data))});
    term.attachCustomKeyEventHandler(e=>{if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='c'&&term.hasSelection()){copyText(term.getSelection());term.clearSelection();return false}return true});
    term.onScroll(()=>{if(Date.now()>state.userScrollUntil)return;const buffer=term.buffer.active;state.follow=buffer.viewportY>=buffer.baseY});
    const onWheel=e=>{state.userScrollUntil=Date.now()+500;if(e.deltaY<0)state.follow=false};
    let pointerId=-1,pointerY=0,pointerCarry=0,pointerDistance=0;
    const pointerOptions={capture:true,passive:false};
    const onPointerDown=e=>{if(!mobileQuery.matches||e.pointerType!=='touch'||!e.isPrimary)return;pointerId=e.pointerId;pointerY=e.clientY;pointerCarry=0;pointerDistance=0;state.userScrollUntil=Date.now()+1000;try{host.setPointerCapture(pointerId)}catch(_){}e.preventDefault();e.stopImmediatePropagation()};
    const onPointerMove=e=>{if(e.pointerId!==pointerId)return;const y=e.clientY,dy=y-pointerY;pointerY=y;pointerDistance+=Math.abs(dy);state.userScrollUntil=Date.now()+1000;let remainder=dy;if(remainder>0&&state.mobilePan<state.mobilePanMax){const used=Math.min(remainder,state.mobilePanMax-state.mobilePan);state.mobilePan+=used;remainder-=used}else if(remainder<0&&state.mobilePan>0){const used=Math.max(remainder,-state.mobilePan);state.mobilePan+=used;remainder-=used}applyMobilePan();pointerCarry+=remainder;const screen=host.querySelector('.xterm-screen'),cellHeight=screen?screen.offsetHeight/Math.max(1,term.rows):Math.max(12,state.fontSize*1.2),lines=Math.trunc(pointerCarry/Math.max(1,cellHeight));if(lines){term.scrollLines(-lines);pointerCarry-=lines*cellHeight;applyMobilePan()}e.preventDefault();e.stopImmediatePropagation()};
    const finishPointer=(e,focus)=>{if(e.pointerId!==pointerId)return;try{host.releasePointerCapture(pointerId)}catch(_){}state.userScrollUntil=Date.now()+500;if(focus&&pointerDistance<6)focusTerminalInput();else applyMobilePan();pointerId=-1;pointerY=pointerCarry=pointerDistance=0;e.preventDefault();e.stopImmediatePropagation()};
    const onPointerUp=e=>finishPointer(e,true),onPointerCancel=e=>finishPointer(e,false);
    host.addEventListener('wheel',onWheel,{passive:true});host.addEventListener('pointerdown',onPointerDown,pointerOptions);host.addEventListener('pointermove',onPointerMove,pointerOptions);host.addEventListener('pointerup',onPointerUp,pointerOptions);host.addEventListener('pointercancel',onPointerCancel,pointerOptions);
    state.terminalInputCleanup=()=>{host.removeEventListener('wheel',onWheel);host.removeEventListener('pointerdown',onPointerDown,pointerOptions);host.removeEventListener('pointermove',onPointerMove,pointerOptions);host.removeEventListener('pointerup',onPointerUp,pointerOptions);host.removeEventListener('pointercancel',onPointerCancel,pointerOptions)};
    term.element.addEventListener('pointerdown',e=>{if(e.pointerType!=='touch')setTimeout(focusTerminalInput,0)});if(!mobileQuery.matches)setTimeout(()=>term.focus(),0);
  }
  function sizeTerminal(cols,rows){
    const terminal=$('terminal'),pad=mobileQuery.matches?12:36;terminal.style.width=Math.max(240,Math.ceil(cols*state.fontSize*.66+pad))+'px';terminal.style.height=Math.max(120,Math.ceil(rows*state.fontSize*1.22+18))+'px';$('size-label').textContent=cols+' × '+rows;scheduleTerminalFit();
  }
  function scheduleTerminalFit(){cancelAnimationFrame(fitFrame);fitFrame=requestAnimationFrame(()=>requestAnimationFrame(fitTerminalToViewport))}
  function applyMobilePan(){
    if(!mobileQuery.matches||!state.mobileGrid)return;
    state.mobilePan=Math.max(0,Math.min(state.mobilePanMax,state.mobilePan));
    $('terminal-content').style.transform='translate3d(0,'+(-state.mobilePanMax+state.mobilePan)+'px,0)';
    const buffer=state.term&&state.term.buffer.active;state.follow=state.mobilePan<.5&&(!buffer||buffer.viewportY>=buffer.baseY);
  }
  function fitTerminalToViewport(){
    if(!state.term)return;
    const scroll=$('terminal-scroll'),stage=$('terminal-stage'),content=$('terminal-content'),terminal=$('terminal');
    content.style.transform='none';
    const screen=terminal.querySelector('.xterm-screen');
    if(!screen)return;
    const style=getComputedStyle(terminal),paddingX=parseFloat(style.paddingLeft)+parseFloat(style.paddingRight),paddingY=parseFloat(style.paddingTop)+parseFloat(style.paddingBottom);
    const naturalW=Math.max(1,Math.ceil(screen.offsetWidth+paddingX)),naturalH=Math.max(1,Math.ceil(screen.offsetHeight+paddingY));
    terminal.style.width=naturalW+'px';terminal.style.height=naturalH+'px';
    if(!mobileQuery.matches){stage.removeAttribute('style');content.removeAttribute('style');return}
    const viewportW=Math.max(1,scroll.clientWidth),viewportH=Math.max(1,scroll.clientHeight);
    const cellW=screen.offsetWidth/Math.max(1,state.term.cols),cellH=screen.offsetHeight/Math.max(1,state.term.rows);
    const targetCols=Math.max(20,Math.min(300,Math.floor((viewportW-paddingX)/Math.max(1,cellW))));
    // 모바일 키보드가 열려 viewport 높이만 바뀌어도 PTY rows를 다시 바꾸면 Codex가 매 키마다
    // 전체 재렌더하고 스크롤백이 리플로우된다. 첫 모바일 격자의 rows를 제어 종료까지 고정한다.
    const lockedRows=state.resizeRequest&&state.resizeRequest.roomId===state.selected?state.resizeRequest.rows:(state.mobileGrid?state.term.rows:0);
    const targetRows=lockedRows||Math.max(8,Math.min(200,Math.floor((viewportH-paddingY)/Math.max(1,cellH))));
    if(hasControl()){
      if(state.term.cols===targetCols&&state.term.rows===targetRows){state.resizeRequest=null;state.mobileGrid=true}
      else if(!state.resizeRequest||state.resizeRequest.roomId!==state.selected||state.resizeRequest.cols!==targetCols||state.resizeRequest.rows!==targetRows){
        state.resizeRequest={roomId:state.selected,cols:targetCols,rows:targetRows};state.mobileGrid=false;send({type:'resize',roomId:state.selected,cols:targetCols,rows:targetRows});
      }
    }else if(!state.controllerId&&state.clientId&&!state.autoClaimPending){state.autoClaimPending=true;send({type:'claimControl'})}
    if(state.mobileGrid){
      stage.style.width=viewportW+'px';stage.style.height=viewportH+'px';
      // PTY rows는 키보드가 열려도 유지한다. 대신 실제 터미널 높이는 그대로 두고 전체를 위로 밀어
      // 마지막 입력 줄이 줄어든 visual viewport의 하단에 항상 맞닿게 한다.
      state.mobilePanMax=Math.max(0,naturalH-viewportH);state.mobilePan=Math.min(state.mobilePan,state.mobilePanMax);
      content.style.width=viewportW+'px';content.style.height=naturalH+'px';
      terminal.style.width=viewportW+'px';terminal.style.height=naturalH+'px';
      applyMobilePan();
      return;
    }
    // 앱이 아직 새 resize 프로토콜을 처리하지 못하거나 다른 기기가 제어 중이면 안전한 축소 표시를 유지한다.
    const scale=Math.max(.01,viewportW/naturalW),scaledH=naturalH*scale;
    stage.style.width=viewportW+'px';stage.style.height=Math.max(viewportH,scaledH)+'px';
    content.style.width=naturalW+'px';content.style.height=naturalH+'px';
    content.style.transform='translate3d(0,0,0) scale('+scale+')';
  }
  function syncVisualViewport(){
    const viewport=window.visualViewport,root=document.documentElement;
    const width=Math.round(viewport?.width||window.innerWidth),height=Math.round(viewport?.height||window.innerHeight);
    root.style.setProperty('--viewport-width',width+'px');root.style.setProperty('--viewport-height',height+'px');
    root.style.setProperty('--viewport-left',Math.round(viewport?.offsetLeft||0)+'px');root.style.setProperty('--viewport-top',Math.round(viewport?.offsetTop||0)+'px');
    const sizeChanged=width!==lastViewportWidth||height!==lastViewportHeight;lastViewportWidth=width;lastViewportHeight=height;
    if(sizeChanged){cancelAnimationFrame(viewportFrame);viewportFrame=requestAnimationFrame(scheduleTerminalFit)}
  }
  function settleVisualViewport(){const gen=++viewportSettleGen;for(const delay of [0,60,140,260,440,700])setTimeout(()=>{if(gen!==viewportSettleGen)return;syncVisualViewport();scheduleTerminalFit()},delay)}
  function disposeTerminal(){clearTimeout(state.outputTimer);clearTimeout(state.cursorTimer);clearTimeout(mobileInputTimer);state.outputTimer=state.cursorTimer=mobileInputTimer=0;state.outputChunks=[];if(state.terminalInputCleanup){try{state.terminalInputCleanup()}catch(e){}state.terminalInputCleanup=null}mobileInput.value='';mobileInputComposing=mobileCommitPending=false;updateMobileInputVisual();if(state.term){try{state.term.dispose()}catch(e){}state.term=null}state.agent='';state.follow=true;state.resizeRequest=null;state.mobileGrid=false;state.mobilePan=state.mobilePanMax=0;$('workspace').classList.remove('has-terminal');$('terminal').textContent='';$('terminal').classList.remove('ready');$('terminal-content').removeAttribute('style');$('terminal-stage').removeAttribute('style')}
  function renderControl(){const active=hasControl(),b=$('control');b.classList.toggle('active',active);b.textContent=active?'제어 중':'제어권 가져오기'}
  function openSidebar(){$('sidebar').classList.add('open');$('scrim').classList.add('on')}function closeSidebar(){$('sidebar').classList.remove('open');$('scrim').classList.remove('on')}

  $('control').onclick=()=>{if(!hasControl())send({type:'claimControl'});else focusTerminalInput()};
  $('refresh').onclick=()=>send({type:'refresh'});$('sidebar-toggle').onclick=openSidebar;$('scrim').onclick=closeSidebar;
  $('font-size').addEventListener('change',e=>setWebFontSize(e.target.value));
  $('mobile-keys').addEventListener('click',e=>{const b=e.target.closest('button[data-code]');if(!b)return;const keys={esc:'\x1b',ctrlc:'\x03',up:'\x1b[A',down:'\x1b[B',left:'\x1b[D',right:'\x1b[C',enter:'\r'};flushMobileInput();sendInput(keys[b.dataset.code]||'');focusTerminalInput()});
  mobileInput.addEventListener('focus',settleVisualViewport);
  mobileInput.addEventListener('compositionstart',()=>{mobileInputComposing=true;mobileCommitPending=false;clearTimeout(mobileInputTimer);settleVisualViewport()});
  mobileInput.addEventListener('compositionupdate',updateMobileInputVisual);
  mobileInput.addEventListener('compositionend',()=>{mobileInputComposing=false;mobileCommitPending=true;updateMobileInputVisual();clearTimeout(mobileInputTimer);mobileInputTimer=setTimeout(flushMobileInput,0)});
  mobileInput.addEventListener('beforeinput',e=>{if(mobileInputComposing||e.isComposing)return;if(e.inputType==='deleteContentBackward'&&!mobileInput.value){e.preventDefault();sendInput('\x7f')}else if(e.inputType==='insertLineBreak'){e.preventDefault();flushMobileInput();sendInput('\r')}});
  mobileInput.addEventListener('input',e=>{updateMobileInputVisual();if(mobileInputComposing||e.isComposing)return;if(mobileCommitPending){flushMobileInput();return}const value=mobileInput.value;if(e.inputType==='insertFromPaste'||/[\s\r\n]$/.test(value)||!/[\u1100-\u11ff\u3131-\u318e\uac00-\ud7a3]/i.test(value))flushMobileInput();else scheduleMobileInputFlush()});
  mobileInput.addEventListener('keydown',e=>{if(mobileInputComposing||e.isComposing||e.keyCode===229)return;if(e.key==='Enter'){e.preventDefault();flushMobileInput();sendInput('\r')}else if(e.key==='Backspace'&&!mobileInput.value){e.preventDefault();sendInput('\x7f')}else if(e.key==='Escape'){e.preventDefault();sendInput('\x1b')}else if(e.ctrlKey&&e.key.toLowerCase()==='c'&&!mobileInput.value){e.preventDefault();sendInput('\x03')}});
  mobileInput.addEventListener('blur',()=>{if(!mobileInputComposing)flushMobileInput()});
  addEventListener('resize',syncVisualViewport);addEventListener('orientationchange',settleVisualViewport);mobileQuery.addEventListener?.('change',syncVisualViewport);
  if(window.visualViewport){visualViewport.addEventListener('resize',syncVisualViewport);visualViewport.addEventListener('scroll',syncVisualViewport)}
  if(window.ResizeObserver)new ResizeObserver(scheduleTerminalFit).observe($('terminal-scroll'));
  syncVisualViewport();connect();
})();
