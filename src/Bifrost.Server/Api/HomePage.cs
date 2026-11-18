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
          :root{color-scheme:light dark;--ground:#EAF3F2;--section:#D3E7E5;--ink:#111820;--ink2:#2c4745;--line:rgba(17,24,32,.18);--accent:#111820;--on-accent:#EAF3F2;--amber:#F3D9A4;--on-amber:#3B2A00;--err:#B3261E;--edge:rgba(255,255,255,.6)}
          @media(prefers-color-scheme:dark){:root{--ground:#0E161C;--section:#1C2A33;--ink:#E4EEED;--ink2:#A9C4C1;--line:rgba(228,238,237,.18);--accent:#79B9B5;--on-accent:#111820;--amber:#E3A857;--on-amber:#1D1400;--err:#F2B8B5;--edge:rgba(255,255,255,.12)}}
          body{font:16px/1.5 Roboto,system-ui,sans-serif;margin:0;padding:16px 16px 24px;color:var(--ink);background:var(--ground)}
          ::selection{background:var(--accent);color:var(--on-accent)}
          .who{font-size:24px;font-weight:500;margin:8px 0 0}.me{font-size:14px;color:var(--ink2);margin:0 0 8px}
          h2{font-size:20px;font-weight:500;margin:24px 0 8px}
          #msg{color:var(--err);font-size:14px}
          .sec{background:var(--section);border-radius:16px;padding:4px 16px;box-shadow:inset 0 1px 0 var(--edge)}
          .item{display:flex;align-items:center;gap:12px;min-height:56px;padding:10px 0;border-top:1px solid var(--line)}.item:first-child{border-top:0}
          .grow{flex:1;min-width:0}.name{font-weight:500}.meta{color:var(--ink2);font-size:14px}
          .dot{width:12px;height:12px;border-radius:50%;box-sizing:border-box;border:2px solid var(--ink);flex:none}.dot.on{background:var(--ink)}
          .q{background:var(--amber);color:var(--on-amber);border-radius:16px;padding:16px;margin:8px 0 12px;box-shadow:inset 0 1px 0 var(--edge)}.q .meta{color:inherit;opacity:.85}
          .row{display:flex;gap:8px;flex-wrap:wrap;margin-top:12px}
          button{font:inherit;font-weight:500;min-height:48px;padding:0 20px;border:0;border-radius:24px;background:var(--accent);color:var(--on-accent)}
          .q button{background:var(--on-amber);color:var(--amber)}
          button.quiet{background:none;color:var(--ink);box-shadow:inset 0 0 0 1px var(--ink2)}
          button:focus-visible{outline:3px solid var(--ink);outline-offset:2px}
        </style></head>
        <body><div class="me" id="me"></div><div id="msg" role="status"></div>
        <div id="questions"></div>
        <h2>Devices</h2><div class="sec" id="devices"></div>
        <div id="peopleBox"><h2>People</h2><div class="sec" id="people"></div></div>
        <div id="extensionsBox"><h2>Extensions</h2><div class="sec" id="extensions"></div></div>
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
            document.getElementById('me').innerHTML = `<div class="who">${esc(me.personName)}</div>${esc(me.role)} · signed in on ${esc(me.deviceName)}`;
            document.getElementById('questions').innerHTML = ((questions && questions.items) || []).filter(q => q.state === 'open').map(q => `
              <div class="q"><div class="name">${esc(q.title)}</div><div class="meta">${esc(q.text)} · from ${esc(q.asker)}</div>
              <div class="row">${q.choices.map(c => `<button onclick="act('POST','/api/v1/questions/${q.id}/answer',{choice:'${esc(c.id)}'})">${esc(c.label)}</button>`).join('')}</div></div>`).join('');
            const shown = (devices || []).filter(d => !d.revoked).sort((a, b) => (online(b.lastSeenAt) ? 1 : 0) - (online(a.lastSeenAt) ? 1 : 0));
            document.getElementById('devices').innerHTML = shown.map(d => `
              <div class="item"><span class="dot ${d.isLocal || online(d.lastSeenAt) ? 'on' : ''}"></span><div class="grow"><div class="name">${esc(d.name)}</div>
              <div class="meta">${esc(name[d.personId] || '')} · ${esc(String(d.platform).replace(/\s*\(.*\)/, ''))}${d.kind === 'browser-fallback' ? ' · view only' : ''} · ${d.isLocal ? 'online' : 'seen ' + ago(d.lastSeenAt)}</div></div>
              ${d.isLocal ? '' : `<button class="quiet" onclick="if (confirm('Sign this device out?')) act('DELETE','/api/v1/devices/${d.id}')">Sign out</button>`}</div>`).join('') || '<p class="meta">No devices.</p>';
            document.getElementById('people').innerHTML = (people || []).map(p => `<div class="item"><div class="grow"><div class="name">${esc(p.displayName)}</div><div class="meta">${esc(p.role)}</div></div></div>`).join('');
            document.getElementById('extensions').innerHTML = (extensions || []).map(e => `<div class="item"><span class="dot ${e.state === 'running' ? 'on' : ''}"></span><div class="grow"><div class="name">${esc(e.name)}</div><div class="meta">${esc(e.state)}${e.restarts ? ' · restarted ' + e.restarts + ' times' : ''}</div></div></div>`).join('') || '<p class="meta">None running.</p>';
            msg('');
          } catch (e) { msg('Could not reach the home.'); }
        }
        refresh(); setInterval(refresh, 3000);
        </script></body></html>
        """;
}
