# Demoing Steel Flex from another network

A public HTTPS link that opens on any phone or laptop, on any network. Up in
about a minute, dead the moment you stop it.

## One-time setup

```bash
brew install cloudflared
cd gym_frontend && npm run demo:build
```

Rebuild (`npm run demo:build`) only when the frontend code changes. The tunnel
hostname is **not** baked into the bundle, so a new link needs no rebuild.

## Running the demo

Three terminals, in this order.

```bash
# 1 — the API
cd gymapis && dotnet run

# 2 — the built site
cd gym_frontend && npm run demo:preview

# 3 — the public link
cloudflared tunnel --url http://localhost:4173
```

Terminal 3 prints a box containing a URL like:

```
https://four-random-words.trycloudflare.com
```

That is the link. Open it anywhere.

### Stopping

If you started them in terminals as above, `Ctrl-C` in each — terminal 3 first,
and the link dies immediately.

If they are running detached (started in the background, no terminal to
interrupt), stop them by name:

```bash
pkill -f "cloudflared tunnel"     # kills the public link — do this first
pkill -f "vite preview"           # frontend
pkill -f "gymapis/bin/Debug"      # API
```

Killing the tunnel alone is enough to take the demo off the internet; the other
two are then just local servers. To confirm nothing is left listening:

```bash
lsof -nP -iTCP:4173 -sTCP:LISTEN   # frontend — silent means stopped
lsof -nP -iTCP:5023 -sTCP:LISTEN   # API
pgrep -fl "cloudflared tunnel"     # silent means the link is dead
```

### Checking what is running

```bash
pgrep -fl "cloudflared tunnel" ; pgrep -fl "vite preview" ; pgrep -fl "gymapis/bin/Debug"
```

To see the current public URL again without restarting anything, look at the
terminal cloudflared is printing to — it shows the link in a box at startup.

## Sign-in

```
admin@steelflex.com
<the demo password — not in this file, see below>
```

The password is deliberately **not written down here**. It was already changed
once from the one in `CLAUDE.md`, for the reason a tunnel makes obvious: the
demo puts `/login` on the public internet, and a password committed to a
repository is a published password. This repository is public, so the same
argument now applies to this file.

Keep it in your shell profile or a password manager, never in the repo.
Everything that logs in — `pytest`, `scripts/seed_demo_site.py` — reads it from
the environment, so export it in those shells:

```bash
export GYMAPI_OWNER_PASSWORD='...'
```

If you no longer know it, reset it rather than hunting for it: set
`Seed__OwnerPassword` for a **new** owner email and restart the API, or update
`users.password_hash` directly. There is no way to read the existing one back —
it is a PBKDF2 hash, which is the point.

## How it fits together

```
        the internet                    your Mac
                                ┌───────────────────────────┐
  phone ──https──▶ Cloudflare ──▶ vite preview :4173         │
                                │        │ proxy            │
                                │        ▼                  │
                                │   API 127.0.0.1:5023      │
                                └───────────────────────────┘
```

Vite forwards `/api` and `/uploads` to the API over loopback
(`vite.config.ts`). Three consequences worth knowing:

- **One origin.** The browser only ever talks to Vite, so there is no CORS entry
  to add and no second tunnel to run.
- **The API is never exposed.** It stays bound to `127.0.0.1`; only the frontend
  port is published. Verify with `lsof -nP -iTCP:5023 -sTCP:LISTEN`.
- **Nothing is hostname-specific.** `.env.demo` sets `VITE_API_BASE_URL=/api`,
  a relative path, so the same build works on localhost, on a LAN address, or
  behind whatever random hostname the tunnel hands you today.

## When it goes wrong

| Symptom | Cause |
|---|---|
| **"Blocked request. This host is not allowed."** | Vite rejects unknown `Host` headers. `allowedHosts` in `vite.config.ts` covers `.trycloudflare.com`; a different tunnel provider needs adding there. |
| Page loads, **images are broken** | The API is not running, or is not on 5023. The `/uploads` proxy has nothing to forward to. |
| Page loads, **no content at all** | Same cause — check terminal 1. |
| **Link suddenly dead** | The Mac slept, or terminal 3 closed. Re-run the tunnel; you get a new URL. |
| **Login fails** | `GYMAPI_OWNER_PASSWORD`, not the one in `CLAUDE.md`. |

## Before you demo

- Disable sleep, or keep the lid open — sleeping drops the tunnel.
- Open the link on your own phone first. Cold-loading over a tunnel is slower
  than localhost; you want to see that once before a client does.
- Check the content is there: `curl -s $URL/api/public | head -c 200`.

## Security, honestly

The link is random and unlisted, and it only lives as long as terminal 3. For a
supervised demo that is a reasonable trade.

Two things it does not do:

- **The login rate limit is loose here.** `RateLimitPolicies.Auth` now covers
  login, forgot-password and reset-password — but `appsettings.Development.json`
  raises the ceiling to 2000 per window so the test suite can run, and a demo
  runs in Development. Production inherits the real ceiling, 10 per 5 minutes.
  Still not something to leave running unattended.
- **The demo data is fabricated**, including transformation testimonials that
  carry consent nobody gave. See the banner in `scripts/seed_demo_site.py`, and
  run it with `--clear` before this database is used for anything real.

Stop the tunnel when the demo ends. That is the whole mitigation, and it works.
