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
          body{font:17px system-ui,sans-serif;margin:0;padding:16px;color:#1b1b1f;background:#fff}
          h1{font-size:22px;margin:0 0 12px}
          form{display:flex;gap:8px;margin-bottom:12px}
          input{flex:1;font:inherit;padding:10px;border:1px solid #888;border-radius:8px;min-width:0}
          button{font:inherit;padding:10px 14px;border:0;border-radius:8px;background:#2b5fd9;color:#fff}
          button.quiet{background:#e3e3e8;color:#1b1b1f}
          #msg{min-height:1.4em;color:#b00020;margin-bottom:8px}
          .card{border:1px solid #ccc;border-radius:10px;padding:10px 12px;margin-bottom:10px}
          .name{font-weight:600;word-break:break-all}.meta{color:#555;font-size:14px;margin:4px 0}
          progress{width:100%;height:10px}.row{display:flex;gap:8px;margin-top:8px}
        </style></head>
        <body><h1>Sleipnir</h1>
        <form id="add"><input id="url" type="url" placeholder="https://…/file.zip" required><button>Add</button></form>
        <div id="msg" role="status"></div><div id="list"></div>
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
        async function refresh() {
          try {
            const r = await api('GET', 'api/v1/downloads?limit=100');
            const page = await r.json();
            document.getElementById('list').innerHTML = page.items.slice().reverse().map(d => `
              <div class="card"><div class="name">${esc(d.filename)}</div>
              <div class="meta">${label(d)}${d.status === 'pending' ? '' : d.isSizeKnown ? ` · ${size(d.bytesDownloaded)} of ${size(d.totalBytes)}` : d.bytesDownloaded ? ` · ${size(d.bytesDownloaded)}` : ''}${d.errorMessage ? ' · ' + esc(d.errorMessage) : ''}</div>
              ${d.isSizeKnown && d.status !== 'pending' ? `<progress max="${d.totalBytes}" value="${d.bytesDownloaded}"></progress>` : ''}
              ${d.status === 'pending' && d.approval !== 'rejected' ? `<div class="row"><button onclick="act('${d.id}','approve')">Approve</button><button class="quiet" onclick="act('${d.id}','reject')">Reject</button></div>` : ''}
              </div>`).join('') || '<p>No downloads yet.</p>';
          } catch (e) { msg('Could not load downloads.'); }
        }
        document.getElementById('add').onsubmit = async e => {
          e.preventDefault();
          const r = await api('POST', 'api/v1/downloads', { urls: [document.getElementById('url').value] });
          if (r.ok) { document.getElementById('url').value = ''; msg(''); } else msg((await r.json())?.title || `Refused (${r.status})`);
          refresh();
        };
        refresh(); setInterval(refresh, 1000);
        </script></body></html>
        """;
}
