namespace Sleipnir.Server.Hosting;

/// <summary>
/// A first plain page for trying the whole flow from a phone (the real screens come later): add a URL, see
/// the downloads and their progress, and (for an admin) approve or reject a waiting one. Relative addresses
/// throughout: under Bifröst it lives at <c>/x/sleipnir/</c>. Anything but a GET goes through the app's
/// <c>bifrost.send</c> bridge when there is one, because a web view cannot send a request body itself.
/// </summary>
internal static class WebPage
{
    public const string Html = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Sleipnir</title>
        <style>
          :root{color-scheme:light dark;--ground:#EAF3F2;--section:#D3E7E5;--ink:#111820;--ink2:#2c4745;--line:rgba(17,24,32,.18);--accent:#111820;--on-accent:#EAF3F2;--amber:#F3D9A4;--on-amber:#3B2A00;--err:#B3261E;--edge:rgba(255,255,255,.6)}
          @media(prefers-color-scheme:dark){:root{--ground:#0E161C;--section:#1C2A33;--ink:#E4EEED;--ink2:#A9C4C1;--line:rgba(228,238,237,.18);--accent:#79B9B5;--on-accent:#111820;--amber:#E3A857;--on-amber:#1D1400;--err:#F2B8B5;--edge:rgba(255,255,255,.12)}}
          body{font:16px/1.5 Roboto,system-ui,sans-serif;margin:0;padding:16px 16px 24px;color:var(--ink);background:var(--ground)}
          ::selection{background:var(--accent);color:var(--on-accent)}
          h1{font-size:24px;font-weight:500;margin:0 0 12px}
          form{display:flex;gap:8px;margin-bottom:8px}
          input{flex:1;min-width:0;min-height:48px;font:inherit;padding:0 16px;border:1px solid var(--ink2);border-radius:16px;background:var(--ground);color:var(--ink)}
          input::placeholder{color:var(--ink2);opacity:.8}
          button{font:inherit;font-weight:500;min-height:48px;padding:0 22px;border:0;border-radius:24px;background:var(--accent);color:var(--on-accent)}
          button.quiet{background:none;color:var(--ink);box-shadow:inset 0 0 0 1px var(--ink2)}
          input:focus-visible,button:focus-visible{outline:3px solid var(--ink);outline-offset:2px}
          #msg{min-height:1.5em;color:var(--err);font-size:14px;margin-bottom:8px}
          h2{font-size:20px;font-weight:500;margin:20px 0 8px}
          .sec{background:var(--section);border-radius:16px;padding:4px 16px;box-shadow:inset 0 1px 0 var(--edge)}
          .item{padding:12px 0;border-top:1px solid var(--line)}.item:first-child{border-top:0}
          .name{font-weight:500;word-break:break-all}.meta{color:var(--ink2);font-size:14px;margin:2px 0}
          .q{background:var(--amber);color:var(--on-amber);border-radius:16px;padding:16px;margin:0 0 12px;box-shadow:inset 0 1px 0 var(--edge)}.q .meta{color:inherit;opacity:.85}
          .q button{background:var(--on-amber);color:var(--amber)}.q button.quiet{background:none;color:var(--on-amber);box-shadow:inset 0 0 0 1px var(--on-amber)}
          progress{display:block;width:100%;height:8px;margin-top:8px;appearance:none;border:0;border-radius:4px;overflow:hidden;background:var(--line)}
          progress::-webkit-progress-bar{background:var(--line)}progress::-webkit-progress-value{background:var(--accent);border-radius:4px}progress::-moz-progress-bar{background:var(--accent)}
          .row{display:flex;gap:8px;margin-top:12px;flex-wrap:wrap}.empty{color:var(--ink2);padding:12px 0}
        </style></head>
        <body><h1 id="title">Sleipnir</h1>
        <form id="add"><input id="url" type="url" placeholder="https://…/file.zip" aria-label="Link to download" required><button>Add</button></form>
        <div id="msg" role="status"></div><div id="waiting"></div><div id="list"></div>
        <script>
        const msg = t => document.getElementById('msg').textContent = t || '';
        // GETs are ordinary requests; anything else goes through the app's bridge when there is one.
        async function api(method, path, body) {
          if (method !== 'GET' && window.bifrost) {
            const r = JSON.parse(window.bifrost.send(method, path, body ? JSON.stringify(body) : ''));
            return { ok: r.status >= 200 && r.status < 300, status: r.status, json: async () => JSON.parse(r.body || 'null') };
          }
          return fetch(path, { method, headers: body ? { 'Content-Type': 'application/json' } : {}, body: body ? JSON.stringify(body) : undefined });
        }
        const size = n => n > 1e9 ? (n / 1e9).toFixed(2) + ' GB' : n > 1e6 ? (n / 1e6).toFixed(1) + ' MB' : (n / 1e3).toFixed(0) + ' kB';
        const label = d => d.status === 'pending' ? (d.approval === 'waiting' ? 'waiting for approval' : d.approval === 'expired' ? 'waiting (no answer yet)' : 'refused') : d.status;
        const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
        async function act(id, what) {
          const r = await api('POST', `api/v1/downloads/${id}/${what}`);
          msg(r.ok ? '' : (await r.json())?.title || `Refused (${r.status})`);
          refresh();
        }
        let isAdmin = false;
        async function refresh() {
          try {
            isAdmin = (await (await api('GET', 'api/v1/me')).json()).isAdmin === true;
            const r = await api('GET', 'api/v1/downloads?limit=100');
            const page = await r.json();
            const items = page.items.slice().reverse();
            const meta = d => `${label(d)}${d.status === 'pending' ? '' : d.isSizeKnown ? ` · ${size(d.bytesDownloaded)} of ${size(d.totalBytes)}` : d.bytesDownloaded ? ` · ${size(d.bytesDownloaded)}` : ''}${d.errorMessage ? ' · ' + esc(d.errorMessage) : ''}`;
            // What needs an answer comes first, in the colour that means "this needs you".
            const asking = items.filter(d => d.status === 'pending' && d.approval !== 'rejected');
            document.getElementById('waiting').innerHTML = asking.map(d => `
              <div class="q"><div class="name">${esc(d.filename)}</div><div class="meta">${label(d)}</div>
              ${isAdmin ? `<div class="row"><button onclick="act('${d.id}','approve')">Approve</button><button class="quiet" onclick="act('${d.id}','reject')">Reject</button></div>` : ''}</div>`).join('');
            const rest = items.filter(d => !asking.includes(d));
            document.getElementById('list').innerHTML = rest.length ? `<h2>Downloads</h2><div class="sec">${rest.map(d => `
              <div class="item"><div class="name">${esc(d.filename)}</div><div class="meta">${meta(d)}</div>
              ${d.isSizeKnown && d.status !== 'pending' ? `<progress max="${d.totalBytes}" value="${d.bytesDownloaded}"></progress>` : ''}</div>`).join('')}</div>`
              : asking.length ? '' : '<p class="empty">No downloads yet. Paste a link above to add one.</p>';
          } catch (e) { msg('Could not load downloads.'); }
        }
        document.getElementById('add').onsubmit = async e => {
          e.preventDefault();
          const r = await api('POST', 'api/v1/downloads', { urls: [document.getElementById('url').value] });
          if (r.ok) { document.getElementById('url').value = ''; msg(''); } else msg((await r.json())?.title || `Refused (${r.status})`);
          refresh();
        };
        if (window.bifrost) document.getElementById('title').hidden = true;
        refresh(); setInterval(refresh, 1000);
        </script></body></html>
        """;
}
