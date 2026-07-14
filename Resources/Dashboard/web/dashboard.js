(function(){
  'use strict';
  const $=id=>document.getElementById(id);
  const state={socket:null,clientId:null,controllerId:null,sessions:[],selected:null,term:null,agent:'',theme:null,fontFamily:'Cascadia Mono',fontSize:16,reconnect:0};
  let toastTimer=0;

  function toast(text){const el=$('toast');el.textContent=text;el.classList.add('on');clearTimeout(toastTimer);toastTimer=setTimeout(()=>el.classList.remove('on'),2200)}
  function send(msg){if(state.socket&&state.socket.readyState===WebSocket.OPEN)state.socket.send(JSON.stringify(msg))}
  function fromB64(value){const raw=atob(value||'');const out=new Uint8Array(raw.length);for(let i=0;i<raw.length;i++)out[i]=raw.charCodeAt(i);return out}
  function copyText(value){
    if(navigator.clipboard&&window.isSecureContext)return navigator.clipboard.writeText(value).catch(()=>copyFallback(value));
    copyFallback(value);return Promise.resolve();
  }
  function copyFallback(value){const ta=document.createElement('textarea');ta.value=value;ta.style.cssText='position:fixed;left:-9999px;top:0';document.body.appendChild(ta);ta.select();try{document.execCommand('copy')}catch(e){}ta.remove()}
  function hasControl(){return !!state.clientId&&state.controllerId===state.clientId}

  function connect(){
    const protocol=location.protocol==='https:'?'wss:':'ws:';
    const ws=new WebSocket(protocol+'//'+location.host+'/ws');state.socket=ws;
    ws.onopen=()=>{state.reconnect=0;$('connection').classList.add('online');$('connection').querySelector('span').textContent='연결됨'};
    ws.onmessage=e=>{try{handle(JSON.parse(e.data))}catch(err){console.warn(err)}};
    ws.onclose=()=>{if(state.socket!==ws)return;$('connection').classList.remove('online');$('connection').querySelector('span').textContent='연결 끊김';setTimeout(connect,Math.min(5000,800+state.reconnect++*500))};
  }

  function handle(msg){
    if(msg.type==='hello'){state.clientId=msg.clientId;state.controllerId=msg.controllerId;renderControl();return}
    if(msg.type==='sessions'){state.sessions=msg.sessions||[];state.controllerId=msg.controllerId;state.theme=msg.terminalTheme;state.fontFamily=msg.fontFamily||state.fontFamily;state.fontSize=msg.fontSize||16;applyUiTheme(msg.uiTheme,msg.terminalTheme,msg.appTheme);renderSessions();renderControl();syncSelectedMeta();return}
    if(msg.type==='control'){state.controllerId=msg.controllerId;renderControl();return}
    if(msg.type==='snapshot'){if(msg.roomId!==state.selected)return;ensureTerminal(sessionById(msg.roomId));state.term.reset();state.term.resize(Math.max(2,msg.cols),Math.max(2,msg.rows));sizeTerminal(msg.cols,msg.rows);state.term.write(fromB64(msg.data));state.term.scrollToBottom();return}
    if(msg.type==='output'){if(msg.roomId===state.selected&&state.term)state.term.write(fromB64(msg.data));return}
    if(msg.type==='size'){if(msg.roomId===state.selected&&state.term){state.term.resize(Math.max(2,msg.cols),Math.max(2,msg.rows));sizeTerminal(msg.cols,msg.rows)}return}
    if(msg.type==='error')toast(msg.message||'요청을 처리하지 못했습니다.');
  }

  function applyUiTheme(ui,term,appTheme){
    if(ui)for(const [key,value] of Object.entries(ui))document.documentElement.style.setProperty('--'+key.replace(/[A-Z]/g,m=>'-'+m.toLowerCase()),value);
    if(term&&term.background)document.documentElement.style.setProperty('--term-bg',term.background);
    const meta=document.querySelector('meta[name=theme-color]');if(meta&&ui)meta.content=ui.bg;
    document.documentElement.style.colorScheme=appTheme==='dark'?'dark':'light';
    if(state.term&&state.theme)state.term.options.theme=themeFor(state.agent);
  }
  function themeFor(agent){const t=Object.assign({},state.theme||{});if(agent==='gajae')t.selectionBackground=t.background&&t.background.toLowerCase()==='#f2ede6'?'#C2D8B0':t.background&&t.background.toLowerCase()==='#f8fafc'?'#C5D8F8':'#264F78';return t}
  function sessionById(id){return state.sessions.find(s=>s.roomId===id)}

  function renderSessions(){
    $('session-count').textContent=state.sessions.length;const list=$('session-list');list.textContent='';
    if(!state.sessions.length){const empty=document.createElement('div');empty.className='empty-state';empty.style.position='static';empty.style.height='180px';empty.innerHTML='<strong>실행 중인 세션이 없습니다</strong><span>DevezCode에서 세션을 열면 표시됩니다.</span>';list.appendChild(empty);return}
    for(const s of state.sessions){const b=document.createElement('button');b.className='session-card'+(s.roomId===state.selected?' active':'');b.innerHTML='<i class="live-dot"></i><span class="session-copy"><strong></strong><span></span></span><em class="agent-mini"></em>';b.querySelector('strong').textContent=s.name;b.querySelector('.session-copy span').textContent=s.projectName||'DevezCode';b.querySelector('em').textContent=s.agent;b.onclick=()=>selectSession(s.roomId);list.appendChild(b)}
  }
  function selectSession(roomId){state.selected=roomId;renderSessions();syncSelectedMeta();send({type:'subscribe',roomId});closeSidebar()}
  function syncSelectedMeta(){
    const s=sessionById(state.selected);if(!s){if(state.selected){state.selected=null;disposeTerminal()}$('session-header').classList.add('hidden');$('empty').classList.remove('hidden');return}
    $('session-header').classList.remove('hidden');$('empty').classList.add('hidden');$('session-title').textContent=s.name;$('session-project').textContent=s.projectName;$('agent-pill').textContent=s.agent;$('size-label').textContent=s.cols+' × '+s.rows;
    if(state.term&&(state.term.cols!==s.cols||state.term.rows!==s.rows)){state.term.resize(Math.max(2,s.cols),Math.max(2,s.rows));sizeTerminal(s.cols,s.rows)}
  }

  function ensureTerminal(session){
    if(state.term&&state.agent===session.agent)return;disposeTerminal();state.agent=session.agent;
    const Ctor=session.agent==='codex'&&window.Terminal6?window.Terminal6:window.Terminal;
    const term=new Ctor({theme:themeFor(session.agent),fontFamily:state.fontFamily+", Cascadia Mono, Consolas, 'D2Coding', 'NanumGothicCoding', 'Malgun Gothic', monospace",fontSize:state.fontSize,cursorBlink:true,allowProposedApi:true,scrollback:5000,windowsPty:{backend:'conpty',buildNumber:0}});
    state.term=term;$('terminal').classList.add('ready');term.open($('terminal'));term.onData(data=>{if(!hasControl()){toast('먼저 제어권을 가져오세요.');return}send({type:'input',roomId:state.selected,data})});
    term.attachCustomKeyEventHandler(e=>{if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='c'&&term.hasSelection()){copyText(term.getSelection());term.clearSelection();return false}return true});
    setTimeout(()=>term.focus(),0);
  }
  function sizeTerminal(cols,rows){const el=$('terminal');el.style.width=Math.max(360,Math.ceil(cols*state.fontSize*.66+32))+'px';el.style.height=Math.max(180,Math.ceil(rows*state.fontSize*1.22+18))+'px';$('size-label').textContent=cols+' × '+rows}
  function disposeTerminal(){if(state.term){try{state.term.dispose()}catch(e){}state.term=null}state.agent='';$('terminal').textContent='';$('terminal').classList.remove('ready')}
  function renderControl(){const active=hasControl(),b=$('control');b.classList.toggle('active',active);b.textContent=active?'제어 중':'제어권 가져오기'}
  function openSidebar(){$('sidebar').classList.add('open');$('scrim').classList.add('on')}function closeSidebar(){$('sidebar').classList.remove('open');$('scrim').classList.remove('on')}

  $('control').onclick=()=>{if(!hasControl())send({type:'claimControl'});else state.term&&state.term.focus()};
  $('refresh').onclick=()=>send({type:'refresh'});$('sidebar-toggle').onclick=openSidebar;$('scrim').onclick=closeSidebar;
  $('mobile-keys').addEventListener('click',e=>{const b=e.target.closest('button[data-code]');if(!b)return;if(!hasControl()){toast('먼저 제어권을 가져오세요.');return}const keys={esc:'\x1b',ctrlc:'\x03',up:'\x1b[A',down:'\x1b[B',left:'\x1b[D',right:'\x1b[C',enter:'\r'};send({type:'input',roomId:state.selected,data:keys[b.dataset.code]||''});state.term&&state.term.focus()});
  connect();
})();
