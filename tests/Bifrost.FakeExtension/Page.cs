// The page a client's web view loads to prove what it lets through. Relative addresses throughout: the page
// lives under /x/<name>/ and must work there.
internal static class Page
{
    public const string Html = """
        <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Bifröst test page</title><link rel="stylesheet" href="style.css"></head>
        <body><h1>Bifröst test page</h1><p id="where"></p><table id="results"></table><img id="pic" src="pixel.svg" width="8" height="8" alt="">
        <script src="app.js"></script></body></html>
        """;

    public const string Style = "body{--ok:yes;font:18px sans-serif;margin:16px}td{padding:6px 10px;border-bottom:1px solid #ccc}.pass{color:#080}.fail{color:#b00;font-weight:bold}";

    public const string Script = """
        const results = [];
        const row = (name, ok, note) => {
          results.push({ name, ok, note: note || '' });
          document.getElementById('results').insertAdjacentHTML('beforeend',
            `<tr><td>${name}</td><td class="${ok ? 'pass' : 'fail'}">${ok ? 'PASS' : 'FAIL'}</td><td>${note || ''}</td></tr>`);
        };
        document.getElementById('where').textContent = location.href;
        row('script ran', true);
        row('stylesheet applied', getComputedStyle(document.body).getPropertyValue('--ok').trim() === 'yes');
        const pic = document.getElementById('pic');
        const t = (p, ms) => Promise.race([p, new Promise((_, r) => setTimeout(() => r(new Error('timeout')), ms))]);
        (async () => {
          await new Promise(done => { if (pic.complete) done(); else { pic.onload = done; pic.onerror = done; } });
          row('image loaded', pic.naturalWidth > 0);
          try { const d = await (await fetch('data.json')).json(); row('fetch same origin', d.hello === 'from the extension'); }
          catch (e) { row('fetch same origin', false, String(e)); }
          try { await t(fetch('https://example.com/', { mode: 'no-cors' }), 4000); row('fetch to another site is blocked', false, 'it got through'); }
          catch (e) { row('fetch to another site is blocked', true, String(e)); }
          try {
            const url = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + location.pathname.replace(/[^/]*$/, '') + 'ws';
            const echoed = await t(new Promise((ok, no) => { const w = new WebSocket(url); w.onopen = () => w.send('ping'); w.onmessage = m => { ok(m.data); w.close(); }; w.onerror = () => no(new Error('socket error')); }), 6000);
            row('websocket to the extension', echoed === 'ping');
          } catch (e) { row('websocket to the extension', false, String(e)); }
          await fetch('report?r=' + encodeURIComponent(JSON.stringify(results))).catch(() => {});
        })();
        """;
}
