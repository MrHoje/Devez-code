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
            if (SettingsService.LoadBrowserMcpEnabled())
            {
                WriteScript();
                Register();
            }
            else Unregister();
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
                var servers = backend.Load().Where(s => !s.IsReadOnly).ToList();
                var existing = servers.FirstOrDefault(s =>
                    string.Equals(s.Name, ServerName, StringComparison.OrdinalIgnoreCase));
                var target = existing ?? new McpServer { Name = ServerName };
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

        let sock = null, token = '', nextId = 1, buf = '';
        const pending = new Map();

        function connect() {
          if (sock && !sock.destroyed) return Promise.resolve(sock);
          return new Promise((resolve, reject) => {
            const d = discovery();
            if (!d.pipe || !d.token) {
              reject(new Error('DevezCode 브리지를 찾을 수 없습니다. DevezCode 가 실행 중인지 확인하세요.'));
              return;
            }
            const s = net.connect({ path: '\\\\.\\pipe\\' + d.pipe });
            s.setEncoding('utf8');
            s.on('connect', () => { sock = s; token = d.token; resolve(s); });
            s.on('error', e => { sock = null; reject(new Error('브리지 연결 실패: ' + e.message)); });
            s.on('close', () => {
              sock = null;
              for (const p of pending.values()) p.reject(new Error('브리지 연결이 끊겼습니다.'));
              pending.clear();
            });
            s.on('data', chunk => {
              buf += chunk;
              let i;
              while ((i = buf.indexOf('\n')) >= 0) {
                const line = buf.slice(0, i); buf = buf.slice(i + 1);
                if (!line.trim()) continue;
                let msg; try { msg = JSON.parse(line); } catch (_) { continue; }
                const p = pending.get(msg.id);
                if (!p) continue;
                pending.delete(msg.id);
                if (msg.ok) p.resolve(msg.result); else p.reject(new Error(msg.error || '알 수 없는 오류'));
              }
            });
          });
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
          { name: 'browser_open', cmd: 'open',
            description: 'DevezCode 내장 브라우저(이 세션 전용 탭)에서 URL 을 열거나 검색어로 구글 검색한다. 브라우저 창을 따로 띄우지 않고 백그라운드 탭에서 처리한다.',
            inputSchema: S({ url: { type: 'string', description: 'URL 또는 검색어' },
                             timeoutMs: { type: 'integer', description: '로드 대기 한계(기본 30000)' } }, ['url']) },
          { name: 'browser_read', cmd: 'read',
            description: '현재 페이지의 본문 텍스트를 읽는다. 페이지 내용을 확인하는 기본 수단.',
            inputSchema: S({ maxChars: { type: 'integer', description: '최대 글자수(기본 20000)' } }) },
          { name: 'browser_links', cmd: 'links',
            description: '현재 페이지의 링크 목록을 "텍스트 | URL" 줄로 반환한다. 검색 결과에서 다음 페이지를 고를 때 사용.',
            inputSchema: S({ max: { type: 'integer', description: '최대 개수(기본 50)' } }) },
          { name: 'browser_click', cmd: 'click',
            description: 'CSS 선택자 또는 보이는 텍스트로 요소를 클릭한다.',
            inputSchema: S({ target: { type: 'string', description: 'CSS 선택자 또는 버튼/링크 텍스트' } }, ['target']) },
          { name: 'browser_fill', cmd: 'fill',
            description: '입력 요소에 값을 넣는다. submit=true 면 폼을 제출(Enter)한다.',
            inputSchema: S({ selector: { type: 'string', description: 'CSS 선택자' },
                             value: { type: 'string' },
                             submit: { type: 'boolean' } }, ['selector', 'value']) },
          { name: 'browser_wait', cmd: 'wait',
            description: '지정 텍스트가 페이지에 나타날 때까지 기다린다(지연 로딩/SPA 대응).',
            inputSchema: S({ text: { type: 'string' }, timeoutMs: { type: 'integer' } }, ['text']) },
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
                serverInfo: { name: 'devez-browser', version: '1.0.0' },
              });
              return;
            case 'ping': ok(id, {}); return;
            case 'tools/list':
              ok(id, { tools: TOOLS.map(t => ({ name: t.name, description: t.description, inputSchema: t.inputSchema })) });
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
