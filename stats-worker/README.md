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

Per install (random ID only): app version, Windows 10/11, first and last seen, the day each feature was last used, and error counts for its last few versions. Per day: active, new and updated installs, errors and failed updates. No IP address, name, settings, hotkeys, games or Roblox account. Installs expire after 60 days without a ping; daily totals after 400 days.

## Dashboard

Active users (24h / 7d / 30d), daily active / new / updated charts, 7- and 30-day retention, share on the latest version, versions with error counts, feature usage, Windows split and update failures. It refreshes every minute.
