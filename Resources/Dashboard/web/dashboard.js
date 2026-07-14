(function(){
  'use strict';
  const $=id=>document.getElementById(id);
  const state={socket:null,clientId:null,controllerId:null,sessions:[],projects:[],folders:[],selected:null,term:null,agent:'',theme:null,fontFamily:'Cascadia Mono',fontSize:16,reconnect:0,starting:false,ime:null,expandedProjects:new Set(),seenProjects:new Set(),expandedFolders:new Set(),seenFolders:new Set(),collapsedSessions:new Set()};
  let toastTimer=0,fitFrame=0;

  function toast(text){const el=$('toast');el.textContent=text;el.classList.add('on');clearTimeout(toastTimer);toastTimer=setTimeout(()=>el.classList.remove('on'),2200)}
  function send(msg){if(state.socket&&state.socket.readyState===WebSocket.OPEN)state.socket.send(JSON.stringify(msg))}
  function fromB64(value){const raw=atob(value||'');const out=new Uint8Array(raw.length);for(let i=0;i<raw.length;i++)out[i]=raw.charCodeAt(i);return out}
  function copyText(value){if(navigator.clipboard&&window.isSecureContext)return navigator.clipboard.writeText(value).catch(()=>copyFallback(value));copyFallback(value);return Promise.resolve()}
  function copyFallback(value){const ta=document.createElement('textarea');ta.value=value;ta.style.cssText='position:fixed;left:-9999px;top:0';document.body.appendChild(ta);ta.select();try{document.execCommand('copy')}catch(e){}ta.remove()}
  function hasControl(){return !!state.clientId&&state.controllerId===state.clientId}
  function normalizeIme(value){try{return (value||'').normalize('NFC')}catch(e){return value||''}}

  function connect(){
    const protocol=location.protocol==='https:'?'wss:':'ws:',ws=new WebSocket(protocol+'//'+location.host+'/ws');state.socket=ws;
    ws.onopen=()=>{state.reconnect=0;$('connection').classList.add('online');$('connection').querySelector('span').textContent='연결됨'};
    ws.onmessage=e=>{try{handle(JSON.parse(e.data))}catch(err){console.warn(err)}};
    ws.onclose=()=>{if(state.socket!==ws)return;$('connection').classList.remove('online');$('connection').querySelector('span').textContent='연결 끊김';setTimeout(connect,Math.min(5000,800+state.reconnect++*500))};
  }

  function handle(msg){
    if(msg.type==='hello'){state.clientId=msg.clientId;state.controllerId=msg.controllerId;renderControl();return}
    if(msg.type==='sessions'){
      state.sessions=msg.sessions||[];state.projects=msg.projects||[];state.folders=msg.folders||[];state.controllerId=msg.controllerId;state.theme=msg.terminalTheme;state.fontFamily=msg.fontFamily||state.fontFamily;state.fontSize=msg.fontSize||16;
      applyUiTheme(msg.uiTheme,msg.terminalTheme,msg.appTheme);rememberExpansion();renderSessions();renderControl();syncSelectedMeta();return;
    }
    if(msg.type==='control'){state.controllerId=msg.controllerId;renderControl();return}
    if(msg.type==='starting'){if(msg.roomId===state.selected){state.starting=true;showEmpty('세션을 시작하는 중입니다','DevezCode의 기존 대화를 그대로 불러오고 있습니다.','…')}return}
    if(msg.type==='snapshot'){
      if(msg.roomId!==state.selected)return;state.starting=false;const session=sessionById(msg.roomId);if(!session)return;
      session.alive=true;session.cols=msg.cols;session.rows=msg.rows;ensureTerminal(session);state.term.reset();state.term.resize(Math.max(2,msg.cols),Math.max(2,msg.rows));sizeTerminal(msg.cols,msg.rows);state.term.write(fromB64(msg.data));state.term.scrollToBottom();$('empty').classList.add('hidden');renderSessions();return;
    }
    if(msg.type==='output'){if(msg.roomId===state.selected&&state.term)state.term.write(fromB64(msg.data));return}
    if(msg.type==='size'){if(msg.roomId===state.selected&&state.term){state.term.resize(Math.max(2,msg.cols),Math.max(2,msg.rows));sizeTerminal(msg.cols,msg.rows)}return}
    if(msg.type==='error'){state.starting=false;toast(msg.message||'요청을 처리하지 못했습니다.');syncSelectedMeta()}
  }

  function applyUiTheme(ui,term,appTheme){
    if(ui)for(const [key,value] of Object.entries(ui))document.documentElement.style.setProperty('--'+key.replace(/[A-Z]/g,m=>'-'+m.toLowerCase()),value);
    if(term&&term.background)document.documentElement.style.setProperty('--term-bg',term.background);
    const meta=document.querySelector('meta[name=theme-color]');if(meta&&ui)meta.content=ui.bg;
    document.documentElement.style.colorScheme=appTheme==='dark'?'dark':'light';if(state.term&&state.theme)state.term.options.theme=themeFor(state.agent);
  }
  function themeFor(agent){const t=Object.assign({},state.theme||{});if(agent==='gajae')t.selectionBackground=t.background&&t.background.toLowerCase()==='#f2ede6'?'#C2D8B0':t.background&&t.background.toLowerCase()==='#f8fafc'?'#C5D8F8':'#264F78';return t}
  function sessionById(id){return state.sessions.find(s=>s.roomId===id)}

  function rememberExpansion(){
    for(const p of state.projects)if(!state.seenProjects.has(p.path)){state.seenProjects.add(p.path);if(p.isExpanded)state.expandedProjects.add(p.path)}
    for(const f of state.folders)if(!state.seenFolders.has(f.id)){state.seenFolders.add(f.id);if(f.isExpanded)state.expandedFolders.add(f.id)}
    for(const s of state.sessions)if(s.childrenExpanded===false&&!state.collapsedSessions.has(s.roomId))state.collapsedSessions.add(s.roomId);
  }
  function makeButton(className){const b=document.createElement('button');b.type='button';b.className=className;return b}
  function renderSessions(){
    $('session-count').textContent=state.sessions.length;const list=$('session-list');list.textContent='';
    if(!state.projects.length){const empty=document.createElement('div');empty.className='sidebar-empty';empty.textContent='등록된 프로젝트가 없습니다.';list.appendChild(empty);return}
    const sorted=(items)=>items.slice().sort((a,b)=>(a.rootOrder??2147483647)-(b.rootOrder??2147483647)),folderIds=new Set(state.folders.map(f=>f.id));
    const roots=[...state.folders.map(item=>({kind:'folder',item,rootOrder:item.rootOrder})),...state.projects.filter(p=>!p.folderId||!folderIds.has(p.folderId)).map(item=>({kind:'project',item,rootOrder:item.rootOrder}))];
    for(const root of sorted(roots)){
      if(root.kind==='project'){list.appendChild(renderProject(root.item,false));continue}const folder=root.item,projects=sorted(state.projects.filter(p=>p.folderId===folder.id));if(!projects.length)continue;
      const group=document.createElement('section');group.className='folder-group';const head=makeButton('folder-head'),open=state.expandedFolders.has(folder.id);head.innerHTML='<i></i><span class="folder-glyph">▰</span><strong></strong><em></em>';head.querySelector('i').textContent=open?'⌄':'›';head.querySelector('strong').textContent=folder.name;head.querySelector('em').textContent=projects.length;
      head.onclick=()=>{open?state.expandedFolders.delete(folder.id):state.expandedFolders.add(folder.id);renderSessions()};group.appendChild(head);if(open)for(const p of projects)group.appendChild(renderProject(p,true));list.appendChild(group);
    }
  }
  function renderProject(project,nested){
    const section=document.createElement('section');section.className='project-group'+(nested?' nested':'');const open=state.expandedProjects.has(project.path),head=makeButton('project-head');
    head.innerHTML='<i></i><span class="project-glyph">⌘</span><span class="project-copy"><strong></strong><small></small></span><em></em>';head.querySelector('i').textContent=open?'⌄':'›';head.querySelector('strong').textContent=project.name;head.querySelector('small').textContent=project.path;head.querySelector('em').textContent=(project.sessions||[]).length;
    head.onclick=()=>{open?state.expandedProjects.delete(project.path):state.expandedProjects.add(project.path);renderSessions()};section.appendChild(head);
    if(open){const tree=document.createElement('div');tree.className='project-sessions';renderSessionTree(tree,project.sessions||[]);section.appendChild(tree)}return section;
  }
  function renderSessionTree(host,sessions){
    const ids=new Set(sessions.map(s=>s.roomId)),children=new Map();for(const s of sessions){const parent=ids.has(s.parentId)?s.parentId:'';if(!children.has(parent))children.set(parent,[]);children.get(parent).push(s)}
    const visited=new Set();function append(parent,depth){for(const s of children.get(parent)||[]){if(visited.has(s.roomId))continue;visited.add(s.roomId);const kids=children.get(s.roomId)||[],row=makeButton('session-card'+(s.roomId===state.selected?' active':'')+(s.alive?' live':'')+(s.hidden?' hidden-session':''));row.style.setProperty('--depth',depth);row.innerHTML='<span class="tree-toggle"></span><i class="live-dot"></i><span class="session-copy"><strong></strong><span></span></span><em class="agent-mini"></em>';const toggle=row.querySelector('.tree-toggle');toggle.textContent=kids.length?(state.collapsedSessions.has(s.roomId)?'›':'⌄'):'';toggle.onclick=e=>{e.stopPropagation();state.collapsedSessions.has(s.roomId)?state.collapsedSessions.delete(s.roomId):state.collapsedSessions.add(s.roomId);renderSessions()};row.querySelector('.session-copy strong').textContent=s.name;row.querySelector('.session-copy span').textContent=s.alive?'실행 중':(s.hidden?'숨김 · 탭하여 실행':'탭하여 실행');row.querySelector('.agent-mini').textContent=s.agent;row.onclick=()=>selectSession(s.roomId);host.appendChild(row);if(!state.collapsedSessions.has(s.roomId))append(s.roomId,depth+1)}}append('',0);for(const s of sessions)if(!visited.has(s.roomId)){if(!children.has(''))children.set('',[]);children.get('').push(s)}append('',0);
  }
  function selectSession(roomId){state.selected=roomId;state.starting=!sessionById(roomId)?.alive;disposeTerminal();renderSessions();syncSelectedMeta();send({type:'subscribe',roomId});closeSidebar()}
  function showEmpty(title,description,icon){const empty=$('empty');empty.querySelector('strong').textContent=title;empty.querySelector('span').textContent=description;empty.querySelector('.empty-icon').textContent=icon;empty.classList.remove('hidden')}
  function syncSelectedMeta(){
    const s=sessionById(state.selected);if(!s){if(state.selected){state.selected=null;disposeTerminal()}$('session-header').classList.add('hidden');showEmpty('세션을 선택하세요','프로젝트의 실행 전 세션도 여기서 바로 열 수 있습니다.','›_');return}
    $('session-header').classList.remove('hidden');$('session-title').textContent=s.name;$('session-project').textContent=s.projectName;$('agent-pill').textContent=s.agent;$('size-label').textContent=s.cols+' × '+s.rows;
    if(state.starting)showEmpty('세션을 시작하는 중입니다','DevezCode의 기존 대화를 그대로 불러오고 있습니다.','…');else if(!state.term&&!s.alive)showEmpty('실행되지 않은 세션입니다','탭하면 DevezCode 세션을 시작합니다.','▶');
    if(state.term&&(state.term.cols!==s.cols||state.term.rows!==s.rows)){state.term.resize(Math.max(2,s.cols),Math.max(2,s.rows));sizeTerminal(s.cols,s.rows)}
  }

  function sendInput(data){if(!data)return;if(!hasControl()){toast('먼저 제어권을 가져오세요.');return}send({type:'input',roomId:state.selected,data})}
  function installImeBridge(term){
    const textarea=term.element&&term.element.querySelector('.xterm-helper-textarea');if(!textarea)return;
    const ime={composing:false,last:'',skip:'',skipUntil:0};state.ime=ime;
    textarea.addEventListener('compositionstart',e=>{ime.composing=true;ime.last=e.data||''},true);
    textarea.addEventListener('compositionupdate',e=>{ime.composing=true;ime.last=e.data||ime.last},true);
    textarea.addEventListener('beforeinput',e=>{if(e.isComposing||e.inputType==='insertCompositionText'){ime.composing=true;ime.last=e.data||ime.last}},true);
    textarea.addEventListener('compositionend',e=>{const committed=normalizeIme(e.data||ime.last);ime.composing=false;ime.last='';if(!committed)return;ime.skip=committed;ime.skipUntil=performance.now()+250;sendInput(committed)},true);
  }
  function ensureTerminal(session){
    if(state.term&&state.agent===session.agent)return;disposeTerminal();state.agent=session.agent;
    const Ctor=session.agent==='codex'&&window.Terminal6?window.Terminal6:window.Terminal,term=new Ctor({theme:themeFor(session.agent),fontFamily:state.fontFamily+", Cascadia Mono, Consolas, 'D2Coding', 'NanumGothicCoding', 'Malgun Gothic', monospace",fontSize:state.fontSize,cursorBlink:true,allowProposedApi:true,scrollback:5000,windowsPty:{backend:'conpty',buildNumber:0}});
    state.term=term;$('terminal').classList.add('ready');term.open($('terminal'));installImeBridge(term);
    term.onData(data=>{const ime=state.ime;if(ime&&ime.composing)return;const normalized=normalizeIme(data);if(ime&&ime.skip&&performance.now()<ime.skipUntil&&normalized===ime.skip){ime.skip='';return}sendInput(data)});
    term.attachCustomKeyEventHandler(e=>{if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='c'&&term.hasSelection()){copyText(term.getSelection());term.clearSelection();return false}return true});
    term.element.addEventListener('pointerdown',()=>setTimeout(()=>term.focus(),0));setTimeout(()=>term.focus(),0);
  }
  function sizeTerminal(cols,rows){
    const terminal=$('terminal'),pad=matchMedia('(max-width:720px)').matches?12:36;terminal.style.width=Math.max(240,Math.ceil(cols*state.fontSize*.66+pad))+'px';terminal.style.height=Math.max(120,Math.ceil(rows*state.fontSize*1.22+18))+'px';$('size-label').textContent=cols+' × '+rows;cancelAnimationFrame(fitFrame);fitFrame=requestAnimationFrame(()=>requestAnimationFrame(fitTerminalToViewport));
  }
  function fitTerminalToViewport(){
    if(!state.term)return;const mobile=matchMedia('(max-width:720px)').matches,scroll=$('terminal-scroll'),stage=$('terminal-stage'),terminal=$('terminal');terminal.style.transform='none';const screen=terminal.querySelector('.xterm-screen');const naturalW=Math.max(parseFloat(terminal.style.width)||0,(screen?.scrollWidth||0)+(mobile?12:28)),naturalH=Math.max(parseFloat(terminal.style.height)||0,(screen?.scrollHeight||0)+12);const scale=mobile?Math.min(1,(scroll.clientWidth-4)/naturalW,(scroll.clientHeight-4)/naturalH):1;terminal.style.transform='scale('+Math.max(.1,scale)+')';stage.style.width=Math.ceil(naturalW*scale)+'px';stage.style.height=Math.ceil(naturalH*scale)+'px';
  }
  function disposeTerminal(){if(state.term){try{state.term.dispose()}catch(e){}state.term=null}state.agent='';state.ime=null;$('terminal').textContent='';$('terminal').classList.remove('ready');$('terminal').style.transform='none';$('terminal-stage').removeAttribute('style')}
  function renderControl(){const active=hasControl(),b=$('control');b.classList.toggle('active',active);b.textContent=active?'제어 중':'제어권 가져오기'}
  function openSidebar(){$('sidebar').classList.add('open');$('scrim').classList.add('on')}function closeSidebar(){$('sidebar').classList.remove('open');$('scrim').classList.remove('on')}

  $('control').onclick=()=>{if(!hasControl())send({type:'claimControl'});else state.term&&state.term.focus()};
  $('refresh').onclick=()=>send({type:'refresh'});$('sidebar-toggle').onclick=openSidebar;$('scrim').onclick=closeSidebar;
  $('mobile-keys').addEventListener('click',e=>{const b=e.target.closest('button[data-code]');if(!b)return;const keys={esc:'\x1b',ctrlc:'\x03',up:'\x1b[A',down:'\x1b[B',left:'\x1b[D',right:'\x1b[C',enter:'\r'};sendInput(keys[b.dataset.code]||'');state.term&&state.term.focus()});
  addEventListener('resize',fitTerminalToViewport);if(window.visualViewport)visualViewport.addEventListener('resize',fitTerminalToViewport);connect();
})();
