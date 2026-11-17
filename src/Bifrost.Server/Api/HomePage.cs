namespace Bifrost.Server.Api;

/// <summary>
/// Bifröst's own page, a first plain one (the real screens come later): who is connected, the people, the
/// extensions, and the questions waiting for an answer. It is only the page; everything it shows comes from the
/// API with the device's token, so it shows each person what they may see. Anything but a GET goes through
/// the app's <c>bifrost.send</c> bridge when there is one, because a web view cannot send a body itself.
/// </summary>
internal static class HomePage
{
    public const string Path = "/ui/";

    public const string Html = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Bifröst</title>
        <style>
          body{font:17px system-ui,sans-serif;margin:0;padding:16px;color:#1b1b1f;background:#fff}
          h1{font-size:22px;margin:0 0 4px}h2{font-size:18px;margin:20px 0 8px}.me{color:#555;font-size:14px;margin-bottom:8px}
          #msg{min-height:1.4em;color:#b00020}.card{border:1px solid #ccc;border-radius:10px;padding:10px 12px;margin-bottom:8px}
          .name{font-weight:600}.meta{color:#555;font-size:14px;margin-top:2px}
          .dot{display:inline-block;width:10px;height:10px;border-radius:50%;margin-right:8px;background:#aaa}.on{background:#2e9e43}
          .row{display:flex;gap:8px;margin-top:8px;flex-wrap:wrap}
          button{font:inherit;padding:8px 12px;border:0;border-radius:8px;background:#2b5fd9;color:#fff}button.quiet{background:#e3e3e8;color:#1b1b1f}
        </style></head>
        <body><h1>Bifröst</h1><div class="me" id="me"></div><div id="msg" role="status"></div>
        <div id="questions"></div>
        <h2>Devices</h2><div id="devices"></div>
        <div id="peopleBox"><h2>People</h2><div id="people"></div></div>
        <div id="extensionsBox"><h2>Extensions</h2><div id="extensions"></div></div>
        <script>
        const msg = t => document.getElementById('msg').textContent = t || '';
        const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
        // GETs are ordinary requests; anything else goes through the app's bridge when there is one.
        async function api(method, path, body) {
          if (method !== 'GET' && window.bifrost) {
            const r = JSON.parse(window.bifrost.send(method, path, body ? JSON.stringify(body) : ''));
            return { ok: r.status >= 200 && r.status < 300, status: r.status, json: async () => JSON.parse(r.body || 'null') };
          }
          return fetch(path, { method, headers: body ? { 'Content-Type': 'application/json' } : {}, body: body ? JSON.stringify(body) : undefined });
        }
        const ago = iso => {
          if (!iso) return 'never';
          const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
          return s < 90 ? 'just now' : s < 5400 ? Math.round(s / 60) + ' min ago' : s < 129600 ? Math.round(s / 3600) + ' h ago' : Math.round(s / 86400) + ' days ago';
        };
        const online = iso => iso && Date.now() - new Date(iso).getTime() < 3 * 60 * 1000; // the home notes a device about once a minute
        async function act(method, path, body) {
          const r = await api(method, path, body);
          msg(r.ok ? '' : (await r.json())?.title || `Refused (${r.status})`);
          refresh();
        }
        async function refresh() {
          try {
            // A member may not see everything an admin does; what is refused is simply left out.
            const get = async p => { const r = await api('GET', p); return r.ok ? r.json() : null; };
            const [me, devices, people, extensions, questions] = await Promise.all(
              ['/api/v1/me', '/api/v1/devices', '/api/v1/people', '/api/v1/extensions', '/api/v1/questions'].map(get));
            const name = Object.fromEntries((people || []).map(p => [p.id, p.displayName]));
            name[me.personId] = me.personName;
            document.getElementById('peopleBox').hidden = !people; // only an admin sees the people and the extensions
            document.getElementById('extensionsBox').hidden = !extensions;
            if (!me) throw new Error('no me');
            document.getElementById('me').textContent = `${me.personName} · ${me.role} · this is ${me.deviceName}`;
            document.getElementById('questions').innerHTML = ((questions && questions.items) || []).filter(q => q.state === 'open').map(q => `
              <div class="card"><div class="name">${esc(q.title)}</div><div class="meta">${esc(q.text)} · from ${esc(q.asker)}</div>
              <div class="row">${q.choices.map(c => `<button onclick="act('POST','/api/v1/questions/${q.id}/answer',{choice:'${esc(c.id)}'})">${esc(c.label)}</button>`).join('')}</div></div>`).join('');
            const shown = (devices || []).filter(d => !d.revoked).sort((a, b) => (online(b.lastSeenAt) ? 1 : 0) - (online(a.lastSeenAt) ? 1 : 0));
            document.getElementById('devices').innerHTML = shown.map(d => `
              <div class="card"><div class="name"><span class="dot ${d.isLocal || online(d.lastSeenAt) ? 'on' : ''}"></span>${esc(d.name)}</div>
              <div class="meta">${esc(name[d.personId] || '')} · ${esc(d.platform)}${d.kind === 'browser-fallback' ? ' · view only' : ''} · ${d.isLocal ? 'this computer' : 'seen ' + ago(d.lastSeenAt)}</div>
              ${d.isLocal ? '' : `<div class="row"><button class="quiet" onclick="if (confirm('Sign this device out?')) act('DELETE','/api/v1/devices/${d.id}')">Sign out</button></div>`}</div>`).join('') || '<p>No devices.</p>';
            document.getElementById('people').innerHTML = (people || []).map(p => `<div class="card"><div class="name">${esc(p.displayName)}</div><div class="meta">${esc(p.role)}</div></div>`).join('');
            document.getElementById('extensions').innerHTML = (extensions || []).map(e => `<div class="card"><div class="name"><span class="dot ${e.state === 'running' ? 'on' : ''}"></span>${esc(e.name)}</div><div class="meta">${esc(e.state)}${e.restarts ? ' · restarted ' + e.restarts + ' times' : ''}</div></div>`).join('') || '<p>None running.</p>';
            msg('');
          } catch (e) { msg('Could not reach the home.'); }
        }
        refresh(); setInterval(refresh, 3000);
        </script></body></html>
        """;
}
