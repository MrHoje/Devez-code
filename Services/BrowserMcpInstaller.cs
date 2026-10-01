using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>세션(에이전트)이 내장 브라우저를 쓰게 해 주는 MCP 서버(node 스크립트)의 생성·등록 담당.
/// <para>스크립트는 <c>%AppData%\DevezCode\bridge\devez-browser-mcp.js</c> 에 앱이 직접 쓴다(항상 최신으로 덮어씀).
/// 각 에이전트 설정에는 <c>devez-browser</c> 라는 이름의 local MCP 서버로 등록된다.</para></summary>
public static class BrowserMcpInstaller
{
    public const string ServerName = "devez-browser";

    public static string ScriptDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "bridge");

    public static string ScriptPath => Path.Combine(ScriptDir, "devez-browser-mcp.js");

    /// <summary>설정(BrowserMcpEnabled)에 맞춰 스크립트를 깔거나 등록을 해제한다. 앱 시작 시 + 토글 시 호출.</summary>
    public static void Sync()
    {
        try
        {
            bool enabled = SettingsService.LoadBrowserMcpEnabled();
            if (enabled)
            {
                WriteScript();
                Register();
            }
            else Unregister();
            // 슬래시로 부르는 스킬 문서도 같은 설정을 따른다.
            BrowserSkillInstaller.Sync(enabled);
        }
        catch (Exception ex)
        {
            DiagLog.Write("BrowserMcpInstaller sync failed: " + ex.Message);
        }
    }

    /// <summary>node 스크립트를 최신 내용으로 기록. node 는 소스를 항상 UTF-8 로 읽으므로 BOM 없이 쓴다.</summary>
    public static void WriteScript()
    {
        Directory.CreateDirectory(ScriptDir);
        var content = ScriptContent();
        // 내용이 같으면 건드리지 않는다(에이전트가 감시 중인 파일의 불필요한 mtime 변경 방지).
        if (File.Exists(ScriptPath) && File.ReadAllText(ScriptPath) == content) return;
        File.WriteAllText(ScriptPath, content, new UTF8Encoding(false));
    }

    private static void Register()
    {
        var node = ResolveNode();
        foreach (var backend in McpBackendRegistry.Available)
        {
            try
            {
                if (backend is CodexMcpBackend codex)
                {
                    codex.EnsureLocalServer(ServerName, new[] { node, ScriptPath });
                    continue;
                }

                var servers = backend.Load().Where(s => !s.IsReadOnly).ToList();
                var existing = servers.FirstOrDefault(s =>
                    string.Equals(s.Name, ServerName, StringComparison.OrdinalIgnoreCase));
                var target = existing ?? new McpServer { Name = ServerName };
                bool changed = existing == null
                    || target.Type != McpServerType.Local
                    || !target.Command.SequenceEqual(new[] { node, ScriptPath }, StringComparer.Ordinal)
                    || !target.Enabled;
                if (!changed) continue;

                target.Type = McpServerType.Local;
                target.Command = new List<string> { node, ScriptPath };
                target.Enabled = true;
                if (existing == null) servers.Add(target);
                backend.Save(servers);
            }
            catch (Exception ex)
            {
                DiagLog.Write($"BrowserMcp register({backend.Id}) failed: " + ex.Message);
            }
        }
    }

    private static void Unregister()
    {
        foreach (var backend in McpBackendRegistry.Available)
        {
            try
            {
                if (backend is CodexMcpBackend codex)
                {
                    codex.RemoveServer(ServerName);
                    continue;
                }

                var servers = backend.Load().Where(s => !s.IsReadOnly).ToList();
                int removed = servers.RemoveAll(s =>
                    string.Equals(s.Name, ServerName, StringComparison.OrdinalIgnoreCase));
                if (removed > 0) backend.Save(servers);
            }
            catch (Exception ex)
            {
                DiagLog.Write($"BrowserMcp unregister({backend.Id}) failed: " + ex.Message);
            }
        }
    }

    /// <summary>node 실행 파일 절대경로. 못 찾으면 "node"(PATH 의존)로 폴백.</summary>
    private static string ResolveNode()
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? Array.Empty<string>();
        foreach (var dir in paths)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir.Trim(), "node.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* 잘못된 PATH 항목 무시 */ }
        }
        return "node";
    }

    private static string ScriptContent() => """
        'use strict';
        // DevezCode 가 자동 생성하는 MCP(stdio) 서버 — 세션이 DevezCode 내장 브라우저를 조작한다.
        // 직접 수정하지 마십시오. 앱 시작 시 덮어써집니다.
        const net = require('net');
        const fs = require('fs');
        const path = require('path');

        const ROOM = process.env.DEVEZCODE_ROOM_ID || '';

        function discovery() {
          const pipe = process.env.DEVEZCODE_BRIDGE_PIPE || '';
          const token = process.env.DEVEZCODE_BRIDGE_TOKEN || '';
          if (pipe && token) return { pipe, token };
          try {
            const p = path.join(process.env.APPDATA || '', 'DevezCode', 'browser-bridge.json');
            const j = JSON.parse(fs.readFileSync(p, 'utf8'));
            return { pipe: j.pipe || '', token: j.token || '' };
          } catch (_) { return { pipe: '', token: '' }; }
        }

        let sock = null, connectPromise = null, token = '', nextId = 1;
        const pending = new Map();

        function connect() {
          if (sock && !sock.destroyed) return Promise.resolve(sock);
          if (connectPromise) return connectPromise;

          const attempt = new Promise((resolve, reject) => {
            const d = discovery();
            if (!d.pipe || !d.token) {
              reject(new Error('DevezCode 브리지를 찾을 수 없습니다. DevezCode 가 실행 중인지 확인하세요.'));
              return;
            }
            const s = net.connect({ path: '\\\\.\\pipe\\' + d.pipe });
            let responseBuf = '';
            s.setEncoding('utf8');
            s.on('connect', () => { sock = s; token = d.token; resolve(s); });
            s.on('error', e => {
              if (sock === s) sock = null;
              reject(new Error('브리지 연결 실패: ' + e.message));
            });
            s.on('close', () => {
              if (sock !== s) return;
              sock = null;
              for (const p of pending.values()) p.reject(new Error('브리지 연결이 끊겼습니다.'));
              pending.clear();
            });
            s.on('data', chunk => {
              responseBuf += chunk;
              let i;
              while ((i = responseBuf.indexOf('\n')) >= 0) {
                const line = responseBuf.slice(0, i); responseBuf = responseBuf.slice(i + 1);
                if (!line.trim()) continue;
                let msg; try { msg = JSON.parse(line); } catch (_) { continue; }
                const p = pending.get(msg.id);
                if (!p) continue;
                pending.delete(msg.id);
                if (msg.ok) p.resolve(msg.result); else p.reject(new Error(msg.error || '알 수 없는 오류'));
              }
            });
          });
          connectPromise = attempt;
          attempt.then(
            () => { if (connectPromise === attempt) connectPromise = null; },
            () => { if (connectPromise === attempt) connectPromise = null; });
          return attempt;
        }

        async function send(cmd, args) {
          const s = await connect();
          const id = nextId++;
          return new Promise((resolve, reject) => {
            pending.set(id, { resolve, reject });
            s.write(JSON.stringify({ id, token, room: ROOM, cmd, args: args || {} }) + '\n');
          });
        }

        const S = (props, required) => ({ type: 'object', properties: props, required: required || [] });
        const TOOLS = [
          { name: 'browser_tabs', cmd: 'tabs',
            description: '현재 세션에 아직 연결되지 않은 일반 브라우저 탭 목록을 반환한다. 탭 선택이 필요할 때만 사용하며, 반환된 tabId는 browser_use_tab에 그대로 전달한다.',
            inputSchema: S({}) },
          { name: 'browser_use_tab', cmd: 'use_tab',
            description: '사용자가 명시적으로 선택한 열린 브라우저 탭을 이 세션에 연결한다. browser_tabs 또는 선택 필요 응답에 나온 tabId만 사용한다. 사용자의 선택 없이 임의로 호출하지 마라.',
            inputSchema: S({ tabId: { type: 'string', description: 'browser_tabs에 나온 tabId' } }, ['tabId']) },
          { name: 'browser_new_tab', cmd: 'new_tab',
            description: '사용자가 새 전용 브라우저 탭 생성을 선택했을 때 호출한다. 기존 탭이 있어도 사용자의 선택 없이 호출하지 마라.',
            inputSchema: S({}) },
          { name: 'browser_use_mini', cmd: 'use_mini',
            description: 'DevezCode 미니 브라우저(메인 창 위에 떠 있는 작은 브라우저 창)를 이 세션의 제어 대상으로 연결한다. 사용자가 미니 브라우저에서 보고 있는 페이지를 다루라고 했을 때만 호출한다. 사용자가 숨겨 둔 창은 화면 캡처까지 숨긴 채로 조작한다. 미니 창은 하나뿐이라 다른 세션이 쓰는 중이면 거절된다. 연결 후에는 browser_open/read/click 등 모든 브라우저 도구가 미니 창에 적용된다.',
            inputSchema: S({}) },
          { name: 'browser_open', cmd: 'open',
            description: 'DevezCode 내장 브라우저에서 URL을 열거나 검색어로 구글 검색한다. 아직 이 세션에 탭이 연결되지 않았는데 일반 브라우저 탭이 열려 있으면 선택 필요 목록이 반환된다. 그때는 탭을 임의로 고르지 말고 사용자에게 기존 탭 사용 또는 새 전용 탭 생성을 물어본 뒤 browser_use_tab 또는 browser_new_tab을 호출하고 다시 시도한다.',
            inputSchema: S({ url: { type: 'string', description: 'URL 또는 검색어' },
                             timeoutMs: { type: 'integer', description: '로드 대기 한계(기본 30000)' } }, ['url']) },
          { name: 'browser_read', cmd: 'read',
            description: '현재 페이지의 본문 텍스트를 읽는다. 페이지 내용을 확인하는 기본 수단.',
            inputSchema: S({ maxChars: { type: 'integer', description: '최대 글자수(기본 20000)' } }) },
          { name: 'browser_links', cmd: 'links',
            description: '현재 페이지의 링크 목록을 "텍스트 | URL" 줄로 반환한다. 검색 결과에서 다음 페이지를 고를 때 사용.',
            inputSchema: S({ max: { type: 'integer', description: '최대 개수(기본 50)' } }) },
          { name: 'browser_click', cmd: 'click',
            description: 'CSS 선택자 또는 보이는 텍스트로 요소를 클릭한다. 대상이 아직 없으면 기본 3초까지 기다린다.',
            inputSchema: S({ target: { type: 'string', description: 'CSS 선택자 또는 버튼/링크 텍스트' },
                             timeoutMs: { type: 'integer', description: '대상 등장 대기(기본 3000)' } }, ['target']) },
          { name: 'browser_fill', cmd: 'fill',
            description: '입력 요소에 값을 넣는다(신뢰된 입력이라 React 등에서도 반영됨). submit=true 면 Enter 키를 보낸다. 전송 버튼을 눌러야 하는 UI 라면 이 호출과 browser_click 을 반드시 별도 호출로 나눠라 — 입력 직후 같은 호출에서 버튼을 찾으면 아직 렌더 전이라 못 찾는다.',
            inputSchema: S({ selector: { type: 'string', description: 'CSS 선택자' },
                             value: { type: 'string' },
                             submit: { type: 'boolean' } }, ['selector', 'value']) },
          { name: 'browser_press', cmd: 'press',
            description: '키를 보낸다(Enter/Tab/Escape/ArrowDown/문자 등). JS 합성 이벤트가 아니라 브라우저 입력 파이프라인을 타므로 프레임워크가 무시하지 않는다.',
            inputSchema: S({ key: { type: 'string', description: 'Enter, Tab, Escape, ArrowDown, a 등' },
                             ctrl: { type: 'boolean' }, shift: { type: 'boolean' }, alt: { type: 'boolean' } }, ['key']) },
          { name: 'browser_wait', cmd: 'wait',
            description: '지정 텍스트가 페이지에 나타날 때까지 기다린다(지연 로딩/SPA 대응).',
            inputSchema: S({ text: { type: 'string' }, timeoutMs: { type: 'integer' } }, ['text']) },
          { name: 'browser_wait_selector', cmd: 'wait_selector',
            description: 'CSS 선택자가 나타날 때까지 기다린다. 입력 후 전송 버튼이 렌더되길 기다릴 때 사용.',
            inputSchema: S({ selector: { type: 'string' }, timeoutMs: { type: 'integer' } }, ['selector']) },
          { name: 'browser_current', cmd: 'current',
            description: '현재 페이지의 제목과 URL 을 반환한다.', inputSchema: S({}) },
          { name: 'browser_back', cmd: 'back', description: '이전 페이지로 돌아간다.', inputSchema: S({}) },
          { name: 'browser_reload', cmd: 'reload', description: '현재 페이지를 새로고침한다.', inputSchema: S({}) },
          { name: 'browser_screenshot', cmd: 'screenshot',
            description: '현재 페이지를 PNG 로 캡처한다.', inputSchema: S({}) },
          { name: 'browser_eval', cmd: 'eval',
            description: '현재 페이지에서 임의 JavaScript 를 실행하고 결과를 반환한다. 다른 도구로 안 되는 경우에만 사용.',
            inputSchema: S({ script: { type: 'string' } }, ['script']) },
        ];

        function write(obj) { process.stdout.write(JSON.stringify(obj) + '\n'); }
        const ok = (id, result) => write({ jsonrpc: '2.0', id, result });
        const fail = (id, code, message) => write({ jsonrpc: '2.0', id, error: { code, message } });

        async function callTool(id, params) {
          if (!ROOM) {
            ok(id, { content: [{ type: 'text', text: '오류: DevezCode 세션 안에서만 사용할 수 있는 도구입니다.' }], isError: true });
            return;
          }
          const def = TOOLS.find(t => t.name === (params && params.name));
          if (!def) { fail(id, -32602, '알 수 없는 도구: ' + (params && params.name)); return; }
          try {
            const result = await send(def.cmd, (params && params.arguments) || {});
            if (def.cmd === 'screenshot') {
              ok(id, { content: [{ type: 'image', data: result, mimeType: 'image/png' }] });
            } else {
              ok(id, { content: [{ type: 'text', text: String(result === '' ? '(빈 결과)' : result) }] });
            }
          } catch (e) {
            ok(id, { content: [{ type: 'text', text: '오류: ' + e.message }], isError: true });
          }
        }

        async function handle(msg) {
          const { id, method, params } = msg;
          if (method === undefined) return;                 // 응답 메시지는 무시
          if (method.startsWith('notifications/')) return;  // 알림은 응답 없음
          switch (method) {
            case 'initialize':
              ok(id, {
                protocolVersion: (params && params.protocolVersion) || '2025-06-18',
                capabilities: { tools: {} },
                serverInfo: { name: 'devez-browser', version: '1.2.0' },
              });
              return;
            case 'ping': ok(id, {}); return;
            case 'tools/list':
              // DevezCode 세션 밖(외부 터미널에서 직접 실행 등)에서는 도구를 아예 노출하지 않는다 —
              // 어느 방인지 알 수 없어 호출해도 오류로 끝나므로, 목록에 띄워 혼동을 주지 않는다.
              ok(id, { tools: ROOM
                ? TOOLS.map(t => ({ name: t.name, description: t.description, inputSchema: t.inputSchema }))
                : [] });
              return;
            case 'tools/call': await callTool(id, params); return;
            default:
              if (id !== undefined) fail(id, -32601, '지원하지 않는 메서드: ' + method);
          }
        }

        let stdinBuf = '';
        process.stdin.setEncoding('utf8');
        process.stdin.on('data', d => {
          stdinBuf += d;
          let i;
          while ((i = stdinBuf.indexOf('\n')) >= 0) {
            const line = stdinBuf.slice(0, i); stdinBuf = stdinBuf.slice(i + 1);
            if (!line.trim()) continue;
            let msg; try { msg = JSON.parse(line); } catch (_) { continue; }
            handle(msg).catch(e => { if (msg && msg.id !== undefined) fail(msg.id, -32603, e.message); });
          }
        });
        process.stdin.on('end', () => process.exit(0));
        """;
}
