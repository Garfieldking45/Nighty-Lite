# Nighty Lite stats worker

Collects the anonymous start-up ping from the app and shows a private dashboard (active users, users per version).

## Deploy (free Cloudflare account)

```
cd stats-worker
npx wrangler login
npx wrangler kv namespace create STATS     # copy the id into wrangler.toml
npx wrangler secret put STATS_KEY          # choose a long random password
npx wrangler deploy                        # prints https://nighty-stats.<you>.workers.dev
```

1. Put `https://nighty-stats.<you>.workers.dev/ping` into `Endpoint` in `Services/StatsService.cs`, rebuild and publish a new release.
2. View stats at `https://nighty-stats.<you>.workers.dev/stats?key=YOUR_STATS_KEY` (add `&json=1` for raw numbers).

## What is stored

Random install ID, app version, first/last seen time. No IP address, name, PC info or Roblox account. Entries expire after 60 days without a ping.
