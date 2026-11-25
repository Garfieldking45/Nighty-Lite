// Nighty Lite anonymous stats. Stores only: random install id -> {version, first seen, last seen}.
// No IPs, no user agents, no other data are stored.
const ID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const VER_RE = /^\d{1,3}\.\d{1,3}\.\d{1,5}$/;
const DAY = 86400000;
const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));

export default {
  async fetch(req, env) {
    const url = new URL(req.url);

    if (url.pathname === "/ping" && req.method === "POST") {
      let b;
      try { b = await req.json(); } catch { return new Response("bad", { status: 400 }); }
      if (!ID_RE.test(b?.id ?? "") || !VER_RE.test(b?.v ?? "")) return new Response("bad", { status: 400 });
      const id = b.id.toLowerCase();
      const now = Date.now();
      const old = await env.STATS.get(id, "json");
      await env.STATS.put(id, JSON.stringify({ v: b.v, first: old?.first ?? now, last: now }),
        { expirationTtl: 60 * 86400 }); // forget installs not seen for 60 days
      return new Response("ok");
    }

    if (url.pathname === "/stats") {
      // Private: open /stats?key=YOUR_STATS_KEY  (set with: npx wrangler secret put STATS_KEY)
      if (!env.STATS_KEY || url.searchParams.get("key") !== env.STATS_KEY) return new Response("Not found", { status: 404 });

      const rows = [];
      let cursor;
      do {
        const page = await env.STATS.list({ cursor });
        for (const k of page.keys) rows.push(await env.STATS.get(k.name, "json"));
        cursor = page.list_complete ? undefined : page.cursor;
      } while (cursor);

      const now = Date.now();
      const live = rows.filter(Boolean);
      const within = (d) => live.filter((r) => now - r.last < d * DAY).length;
      const byVer = {};
      for (const r of live.filter((r) => now - r.last < 7 * DAY)) byVer[r.v] = (byVer[r.v] ?? 0) + 1;
      const data = { total: live.length, active24h: within(1), active7d: within(7), active30d: within(30), activeByVersion7d: byVer };

      if (url.searchParams.has("json")) return Response.json(data);
      const vers = Object.entries(byVer).sort((a, b) => b[0].localeCompare(a[0], undefined, { numeric: true }));
      const html = `<!doctype html><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1">
<title>Nighty Lite stats</title>
<style>body{font:16px system-ui;background:#111;color:#eee;max-width:560px;margin:40px auto;padding:0 16px}
.g{display:grid;grid-template-columns:repeat(2,1fr);gap:12px}.c{background:#1b1b1b;border-radius:10px;padding:14px}
.c b{display:block;font-size:30px}table{width:100%;border-collapse:collapse;margin-top:8px}td,th{padding:8px;text-align:left;border-bottom:1px solid #2a2a2a}</style>
<h1>Nighty Lite users</h1>
<div class=g><div class=c><b>${data.active24h}</b>active last 24h</div><div class=c><b>${data.active7d}</b>active last 7 days</div>
<div class=c><b>${data.active30d}</b>active last 30 days</div><div class=c><b>${data.total}</b>installs seen (60 days)</div></div>
<h2>Versions (active last 7 days)</h2>
<table><tr><th>Version<th>Users</tr>${vers.map(([v, n]) => `<tr><td>${esc(v)}<td>${n}`).join("") || "<tr><td colspan=2>No data yet"}</table>`;
      return new Response(html, { headers: { "content-type": "text/html; charset=utf-8", "cache-control": "no-store" } });
    }

    return new Response("Not found", { status: 404 });
  },
};
