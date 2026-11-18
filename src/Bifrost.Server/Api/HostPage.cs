namespace Bifrost.Server.Api;

/// <summary>
/// The host screen (ADR 003): where the admin, on the home machine, pairs devices, looks after people, answers
/// questions and runs extensions. Three plain files served from the host's own address so the page can run with
/// <c>script-src 'self'</c> and no inline script. Every value from the API goes through <c>esc</c> before it is
/// put in the page.
/// </summary>
internal static class HostPage
{
    public static byte[] Mark()
    {
        using var stream = typeof(HostPage).Assembly.GetManifestResourceStream("bifrost-mark.png")!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    public const string Expired = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Bifröst</title><meta name="referrer" content="no-referrer">
        <style>body{font:18px/1.5 system-ui,sans-serif;margin:0;min-height:100vh;display:grid;place-items:center;background:#EAF3F2;color:#111820}
        main{max-width:30em;padding:24px}code{background:#D3E7E5;padding:2px 8px;border-radius:6px}
        @media(prefers-color-scheme:dark){body{background:#0E161C;color:#E4EEED}code{background:#1C2A33}}</style></head>
        <body><main><h1>This link has expired or was already used</h1>
        <p>To open the host screen, run <code>bifrost open</code> in a terminal on this machine. The link it makes works once, for two minutes.</p></main></body></html>
        """;

    public const string Html = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Bifröst</title><link rel="icon" href="/host/mark.png"><link rel="stylesheet" href="/host/app.css"></head>
        <body>
        <header class="bar"><img src="/host/mark.png" alt="" width="40" height="45">
          <div class="who"><strong id="homeName">Bifröst</strong><span id="area">Host screen</span></div>
          <svg id="arcs" class="arcs" viewBox="0 0 40 24" role="img" aria-label="Connected to the home"><title>Connected to the home</title>
            <path d="M3 21a17 17 0 0 1 34 0"/><path d="M10 21a10 10 0 0 1 20 0"/><path d="M16.5 21a3.5 3.5 0 0 1 7 0"/></svg></header>
        <div class="shell">
          <nav aria-label="Host screen"><a href="#pair">Pair a device <span id="joinCount" class="count" hidden></span></a><a href="#people">People and devices</a>
            <a href="#questions">Questions <span id="count" class="count" hidden></span></a><a href="#extensions">Extensions</a><a href="#home">Home</a>
            <button id="logout" class="quiet">Close this screen</button></nav>
          <main id="main" tabindex="-1"></main>
        </div>
        <script src="/host/app.js"></script></body></html>
        """;

    public const string Css = """
        :root{color-scheme:light dark;--teal:#79B9B5;--ink:#111820;--ground:#EAF3F2;--section:#D3E7E5;--text:#111820;--text2:#2c4745;--line:rgba(17,24,32,.18);--accent:#111820;--on-accent:#EAF3F2;--amber:#F3D9A4;--on-amber:#3B2A00;--err:#B3261E;--edge:rgba(255,255,255,.6)}
        @media(prefers-color-scheme:dark){:root{--ground:#0E161C;--section:#1C2A33;--text:#E4EEED;--text2:#A9C4C1;--line:rgba(228,238,237,.18);--accent:#79B9B5;--on-accent:#111820;--amber:#E3A857;--on-amber:#1D1400;--err:#F2B8B5;--edge:rgba(255,255,255,.12)}}
        *{box-sizing:border-box}body{margin:0;font:16px/1.5 Roboto,system-ui,sans-serif;background:var(--ground);color:var(--text)}
        ::selection{background:var(--accent);color:var(--on-accent)}
        .bar{display:flex;align-items:center;gap:16px;padding:12px 24px;background:var(--teal);color:var(--ink)}
        .who{display:flex;flex-direction:column;flex:1;min-width:0}.who strong{font-size:20px;font-weight:500}.who span{font-size:14px;color:#1F3B3A}
        .arcs{width:44px;height:26px;fill:none;stroke:var(--ink);stroke-width:3.4;stroke-linecap:round}.arcs.off{opacity:.35}
        .shell{display:grid;grid-template-columns:240px minmax(0,1fr);min-height:calc(100vh - 69px)}
        nav{display:flex;flex-direction:column;gap:4px;padding:16px 12px;background:var(--section)}
        nav a{display:flex;justify-content:space-between;align-items:center;min-height:48px;padding:0 16px;border-radius:24px;color:var(--text);text-decoration:none}
        nav a[aria-current=page]{background:var(--teal);color:var(--ink);font-weight:500}
        nav button{margin-top:auto}
        main{padding:24px 32px 48px;max-width:920px;outline:none}
        h1{font-size:28px;font-weight:500;margin:0 0 4px}h2{font-size:20px;font-weight:500;margin:32px 0 8px}p{margin:8px 0}.lead{color:var(--text2);margin:0 0 20px}
        .sec{background:var(--section);border-radius:16px;padding:4px 20px;box-shadow:inset 0 1px 0 var(--edge)}.pad{padding:16px 20px}
        .item{display:flex;align-items:center;gap:12px;min-height:60px;padding:10px 0;border-top:1px solid var(--line)}.item:first-child{border-top:0}
        .grow{flex:1;min-width:0}.name{font-weight:500}.meta{color:var(--text2);font-size:14px}
        .dot{width:12px;height:12px;border-radius:50%;border:2px solid var(--text);flex:none}.dot.on{background:var(--text)}
        .q{background:var(--amber);color:var(--on-amber);border-radius:16px;padding:16px 20px;margin:0 0 12px;box-shadow:inset 0 1px 0 var(--edge)}
        .q .meta{color:inherit;opacity:.85}
        .row{display:flex;gap:8px;flex-wrap:wrap;margin-top:12px;align-items:center}
        button,select,input[type=text]{font:inherit;min-height:48px}
        button{padding:0 22px;border:0;border-radius:24px;background:var(--accent);color:var(--on-accent);font-weight:500;cursor:pointer}
        button.quiet{background:none;color:var(--text);box-shadow:inset 0 0 0 1px var(--text2)}
        button.danger{background:none;color:var(--err);box-shadow:inset 0 0 0 1px var(--err)}
        .q button{background:var(--on-amber);color:var(--amber)}button:disabled{opacity:.5;cursor:default}
        select,input[type=text]{padding:0 14px;border-radius:16px;border:1px solid var(--text2);background:var(--ground);color:var(--text);min-width:16em}
        button:focus-visible,select:focus-visible,input:focus-visible,a:focus-visible{outline:3px solid var(--text);outline-offset:2px}
        label{display:block;font-size:14px;color:var(--text2);margin:12px 0 4px}.radio{display:flex;gap:20px;margin:8px 0}.radio label{display:flex;gap:8px;align-items:center;margin:0;color:var(--text);font-size:16px}
        .qr{display:flex;gap:28px;align-items:flex-start;flex-wrap:wrap;margin-top:16px}.qr .code{background:#fff;border-radius:16px;padding:12px;width:280px;height:280px}.qr .code svg{width:100%;height:100%}
        .qr.expired .code{opacity:.15}.link{font:14px/1.4 ui-monospace,monospace;word-break:break-all;background:var(--ground);border-radius:12px;padding:10px 12px;max-width:34em}
        .key{font:17px/1.6 ui-monospace,monospace;letter-spacing:.04em}.msg{color:var(--err);min-height:1.5em}code{font:15px ui-monospace,monospace;background:var(--ground);padding:2px 8px;border-radius:6px}
        .count{background:var(--amber);color:var(--on-amber);border-radius:12px;padding:0 8px;font-size:14px}
        @media(max-width:760px){.shell{grid-template-columns:1fr}nav{flex-direction:row;flex-wrap:wrap}nav button{margin-top:0}main{padding:20px 16px 40px}}
        """;

    public const string Script = """
        'use strict';
        const $ = s => document.querySelector(s);
        const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
        const groups = hex => (String(hex).toUpperCase().match(/.{1,4}/g) || []).join(' ');
        const ago = iso => {
          if (!iso) return 'never';
          const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
          return s < 90 ? 'just now' : s < 5400 ? Math.round(s / 60) + ' min ago' : s < 129600 ? Math.round(s / 3600) + ' h ago' : Math.round(s / 86400) + ' days ago';
        };
        const online = iso => iso && Date.now() - new Date(iso).getTime() < 3 * 60 * 1000;
        async function api(method, path, body) {
          const r = await fetch('/api/v1' + path, { method, headers: body ? { 'Content-Type': 'application/json' } : {}, body: body ? JSON.stringify(body) : undefined });
          if (r.status === 401) { location.href = '/host/login'; throw new Error('signed out'); }
          const data = r.status === 204 || r.status === 202 ? null : await r.json().catch(() => null);
          if (!r.ok) throw Object.assign(new Error(data?.title || `Refused (${r.status})`), { code: data?.code });
          return data;
        }
        const state = { people: [], devices: [], questions: [], extensions: [], joins: [], sources: [], info: null, invite: null, timer: null };
        let msg = '', busy = '';
        const setArcs = ok => { const a = $('#arcs'); a.classList.toggle('off', !ok); a.setAttribute('aria-label', ok ? 'Connected to the home' : 'Not connected'); };
        async function load() {
          try {
            const [people, devices, questions, extensions, info] = await Promise.all(['/people', '/devices', '/questions', '/extensions', '/host/info'].map(p => api('GET', p)));
            // The home network may be off: then nobody can ask to join, which is not an error.
            const joins = await api('GET', '/join-requests').catch(e => (e.code === 'lan_off' ? [] : Promise.reject(e)));
            const sources = await api('GET', '/extensions/sources').catch(() => []);
            Object.assign(state, { people, devices, questions: questions.items, extensions, info, joins, sources });
            $('#homeName').textContent = info.homeName;
            const c = $('#count'); c.hidden = !questions.items.length; c.textContent = questions.items.length;
            const j = $('#joinCount'); j.hidden = !joins.length; j.textContent = joins.length;
            setArcs(true);
          } catch (e) { setArcs(false); msg = 'Could not reach the home.'; }
          draw();
        }
        const route = () => (location.hash || '#pair').slice(1);
        const person = id => state.people.find(p => p.id === id)?.displayName || '';
        const views = {
          pair() {
            const opts = '<option value="" selected disabled>Choose a person…</option>' + state.people.map(p => `<option value="${esc(p.id)}">${esc(p.displayName)} (${esc(p.role)})</option>`).join('');
            const inv = state.invite;
            const joins = state.joins.length ? `<p class="meta">Each device shows the same code on its own screen. Approve only if they match.</p>` + state.joins.map(j => `<div class="sec pad join">
                <div class="key">${esc(groups(j.code))}</div><div class="name">${esc(j.name)}</div><div class="meta">${esc(j.platform)}</div>
                <div class="row"><select data-for="${esc(j.id)}" aria-label="Who is ${esc(j.name)} for?"><option value="" selected disabled>Choose a person…</option>${state.people.map(p => `<option value="${esc(p.id)}">${esc(p.displayName)} (${esc(p.role)})</option>`).join('')}</select>
                <button data-approve="${esc(j.id)}">Approve</button><button class="danger" data-refuse="${esc(j.id)}">Refuse</button></div></div>`).join('')
              : '<div class="sec pad"><p class="meta">None. A device with no camera, such as a TV, asks to join and shows a code here.</p></div>';
            return `<h1>Pair a device</h1><p class="lead">Choose who the device belongs to. It gets that person's role, so pair a family member as a member.</p>
              <div class="sec pad"><label for="who">Who is this device for?</label>
              <div class="row"><select id="who">${opts}</select><button id="show">Show pairing code</button></div>
              <details><summary>Add a person first</summary><label for="newName">Name</label><input type="text" id="newName" maxlength="40" autocomplete="off">
                <div class="radio" role="radiogroup" aria-label="Role"><label><input type="radio" name="role" value="member" checked> Member</label><label><input type="radio" name="role" value="admin"> Admin</label></div>
                <button id="add" class="quiet">Add person</button></details>
              <p class="msg" role="status">${esc(msg)}</p></div>
              ${inv ? `<div class="qr ${inv.expired ? 'expired' : ''}"><div class="code" role="img" aria-label="Pairing QR code">${inv.qrSvg}</div>
                <div><p class="name">${esc(inv.person)} · ${esc(inv.role)}</p><p class="meta" id="left">${inv.expired ? 'Expired. Make a new code.' : ''}</p>
                <p>In the Bifröst app, tap <strong>Scan QR code</strong>. It works once.</p><div class="link" id="link">${esc(inv.link)}</div>
                <div class="row"><button id="copy" class="quiet">Copy link</button></div></div></div>` : ''}
              <h2>Devices asking to join</h2>${joins}`;
          },
          people() {
            const people = state.people.map(p => `<div class="item"><div class="grow"><div class="name">${esc(p.displayName)}</div><div class="meta">${esc(p.role)}</div></div>
              <button class="quiet" data-role="${esc(p.id)}" data-to="${p.role === 'admin' ? 'member' : 'admin'}">Make ${p.role === 'admin' ? 'member' : 'admin'}</button>
              <button class="danger" data-del="${esc(p.id)}">Remove</button></div>`).join('');
            const devices = state.devices.filter(d => !d.revoked).sort((a, b) => (online(b.lastSeenAt) ? 1 : 0) - (online(a.lastSeenAt) ? 1 : 0)).map(d => `<div class="item">
              <span class="dot ${d.isLocal || online(d.lastSeenAt) ? 'on' : ''}"></span><div class="grow"><div class="name">${esc(d.name)}</div>
              <div class="meta">${esc(person(d.personId))} · ${esc(String(d.platform).replace(/\s*\(.*\)/, ''))}${d.kind === 'browser-fallback' ? ' · view only' : ''} · ${d.isLocal ? 'this computer' : (online(d.lastSeenAt) ? 'online' : 'seen ' + ago(d.lastSeenAt))}</div></div>
              ${d.isLocal ? '' : `<button class="quiet" data-out="${esc(d.id)}">Sign out</button>`}</div>`).join('');
            return `<h1>People and devices</h1><p class="lead">Everyone paired with this home.</p><h2>People</h2><div class="sec">${people}</div><p class="msg" role="status">${esc(msg)}</p><h2>Devices</h2><div class="sec">${devices || '<p class="meta pad">No devices.</p>'}</div>`;
          },
          questions() {
            const list = state.questions.map(q => `<div class="q"><div class="name">${esc(q.title)}</div><div class="meta">${esc(q.text)} · from ${esc(q.asker)}</div>
              <div class="row">${q.choices.map(c => `<button data-q="${esc(q.id)}" data-c="${esc(c.id)}">${esc(c.label)}</button>`).join('')}</div></div>`).join('');
            return `<h1>Questions</h1><p class="lead">Requests that need an answer from you.</p>${list || '<div class="sec pad"><p class="meta">Nothing is waiting.</p></div>'}<p class="msg" role="status">${esc(msg)}</p>`;
          },
          extensions() {
            const rows = state.extensions.map(e => { const up = e.state === 'running' || e.state === 'starting'; return `<div class="item"><span class="dot ${e.state === 'running' ? 'on' : ''}"></span>
              <div class="grow"><div class="name">${esc(e.name)}</div><div class="meta">${esc(e.state)}${e.restarts ? ' · restarted ' + e.restarts + ' times' : ''}</div></div>
              ${up ? `<button class="quiet" data-ext="${esc(e.name)}" data-do="restart">Restart</button><button class="quiet" data-ext="${esc(e.name)}" data-do="stop">Stop</button>` : `<button data-ext="${esc(e.name)}" data-do="start">Start</button>`}</div>`; }).join('');
            return `<h1>Extensions</h1><p class="lead">The programs this home runs.</p><div class="sec">${rows || '<p class="meta pad">No extension is set up yet.</p>'}</div><p class="msg" role="status">${esc(msg)}</p>
              <h2>Get an extension</h2><p class="lead">Installed from the latest signed release in its GitHub repository. Bifröst checks the signature before anything is unpacked.</p>
              <div class="sec">${state.sources.map(x => `<div class="item"><div class="grow"><div class="name">${esc(x.name)}</div><div class="meta">${esc(x.repository)}${x.installed ? ' · installed ' + esc(x.installed) : ''}</div></div>
                <button class="${x.installed ? 'quiet' : ''}" data-latest="${esc(x.name)}" ${busy ? 'disabled' : ''}>${busy === x.name ? 'Installing…' : x.installed ? 'Update now' : 'Install'}</button></div>`).join('') || '<p class="meta pad">Nothing to install from.</p>'}</div>
              <div class="row"><button class="quiet" id="check" ${busy ? 'disabled' : ''}>${busy === 'check' ? 'Checking…' : 'Check for updates'}</button></div><p class="msg" role="status">${esc(msg)}</p>
              <p class="meta">An update is also offered as a question once a day. To install from a file instead, run <code>bifrost extension install --from &lt;file&gt;</code>.</p>`;
          },
          home() {
            const i = state.info; if (!i) return '<h1>Home</h1>';
            return `<h1>Home</h1><p class="lead">What this home is called and how devices reach it.</p>
              <div class="sec"><div class="item"><div class="grow"><div class="name">Name</div><div class="meta">${esc(i.homeName)} on ${esc(i.machine)}</div></div></div>
              <div class="item"><div class="grow"><div class="name">Home network</div><div class="meta">${i.lan ? 'On, port ' + esc(i.lan.port) + (i.lan.fallbackPort ? ' · view-only browser fallback on port ' + esc(i.lan.fallbackPort) : '') : 'Off. Devices cannot pair until it is turned on.'}</div></div></div>
              ${i.lan ? `<div class="item"><div class="grow"><div class="name">Home key</div><div class="meta">Devices check this when they pair.</div><div class="key">${esc(groups(i.lan.fingerprint))}</div></div></div>` : ''}
              <div class="item"><div class="grow"><div class="name">Version</div><div class="meta">${esc(i.version)}</div></div></div></div>
              <h2>Change the home</h2><div class="sec pad"><p>Renaming the home, turning the home network on, and rotating the home key change how every device reaches it, so they stay in a terminal on this machine, behind the operating system's password prompt.</p>
              <p>Rotate the key (signs every device out):</p><p><code>bifrost identity rotate</code></p></div>`;
          },
        };
        function draw(force) {
          const view = views[route()] ? route() : 'pair';
          for (const a of document.querySelectorAll('nav a')) {
            if (a.getAttribute('href') === '#' + view) a.setAttribute('aria-current', 'page'); else a.removeAttribute('aria-current');
          }
          const main = $('#main'), sig = state.people.map(p => p.id + p.role).join() + '|' + state.joins.map(j => j.id).join();
          // The pairing form is not redrawn under the person's hands: only when the view, or who can be chosen, changed.
          if (force || view !== 'pair' || main.dataset.view !== view || main.dataset.sig !== sig) {
            const who = $('#who')?.value, name = $('#newName')?.value;
            main.innerHTML = views[view]();
            main.dataset.view = view; main.dataset.sig = sig;
            if (who && $('#who')) $('#who').value = who;
            if (name && $('#newName')) $('#newName').value = name;
          }
          $('#area').textContent = { pair: 'Pair a device', people: 'People and devices', questions: 'Questions', extensions: 'Extensions', home: 'Home' }[view];
          tick();
        }
        function tick() {
          const inv = state.invite, left = $('#left');
          if (!inv || !left) return;
          const s = Math.max(0, Math.round((new Date(inv.expiresAt) - Date.now()) / 1000));
          if (s === 0 && !inv.expired) { inv.expired = true; draw(true); return; }
          if (!inv.expired) left.textContent = `Expires in ${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}. Works once.`;
        }
        async function act(fn, working, who) {
          msg = working || ''; busy = who || ''; if (working) draw(true);
          try { await fn(); } catch (e) { if (e.message !== 'signed out') msg = e.message; }
          busy = ''; await load(); draw(true);
        }
        document.addEventListener('click', e => {
          const t = e.target.closest('button'); if (!t) return;
          if (t.id === 'logout') { api('POST', '/host/logout').finally(() => { document.body.innerHTML = '<main style="padding:48px"><h1>This screen is closed</h1><p>Run <code>bifrost open</code> to open it again.</p></main>'; }); }
          else if (t.id === 'show') act(async () => {
            const id = $('#who').value; if (!id) throw new Error('Choose who the device is for first.');
            const r = await api('POST', '/invitations?qr=svg', { personId: id }); const p = state.people.find(x => x.id === id);
            state.invite = { ...r, person: p.displayName, role: p.role, expired: false };
          });
          else if (t.id === 'add') act(async () => {
            const created = await api('POST', '/people', { displayName: $('#newName').value.trim(), role: document.querySelector('input[name=role]:checked').value });
            state.people.push(created); await load(); const sel = $('#who'); if (sel) sel.value = created.id;
          });
          else if (t.id === 'copy') navigator.clipboard?.writeText(state.invite.link).then(() => { t.textContent = 'Copied'; });
          else if (t.dataset.approve) act(async () => {
            const person = document.querySelector(`select[data-for="${CSS.escape(t.dataset.approve)}"]`).value;
            if (!person) throw new Error('Choose who the device is for first.');
            await api('POST', `/join-requests/${t.dataset.approve}/approve`, { personId: person });
          });
          else if (t.dataset.refuse) act(() => api('DELETE', `/join-requests/${t.dataset.refuse}`));
          else if (t.dataset.role) act(() => api('PATCH', `/people/${t.dataset.role}`, { role: t.dataset.to }));
          else if (t.dataset.del) { const p = state.people.find(x => x.id === t.dataset.del); if (confirm(`Remove ${p.displayName}? Their devices are signed out.`)) act(() => api('DELETE', `/people/${t.dataset.del}?confirm=true`)); }
          else if (t.dataset.out) { if (confirm('Sign this device out?')) act(() => api('DELETE', `/devices/${t.dataset.out}`)); }
          else if (t.dataset.q) act(() => api('POST', `/questions/${t.dataset.q}/answer`, { choice: t.dataset.c }));
          else if (t.id === 'check') act(async () => { const found = await api('POST', '/extensions/check'); msg = found.length ? 'An update was offered. See Questions.' : 'Everything is up to date.'; }, 'Looking for updates…', 'check');
          else if (t.dataset.latest) act(async () => { const r = await api('POST', `/extensions/${t.dataset.latest}/install-latest`); msg = `Installed ${r.name} ${r.version}. ` + (r.restart === 'bifrost' ? 'Restart Bifröst to start it.' : 'Restart it to run the new version.'); }, 'Downloading from GitHub and checking the signature. This can take a minute…', t.dataset.latest);
          else if (t.dataset.ext) act(() => api('POST', `/extensions/${t.dataset.ext}/${t.dataset.do}`));
        });
        window.addEventListener('hashchange', () => { msg = ''; draw(true); $('#main').focus(); });
        load(); setInterval(load, 3000); setInterval(tick, 1000);
        """;
}
