# DevStrider 1.0

Monorepo: **React + Vite** client, **Express + Socket.IO + MongoDB** API. Group workspaces for bids, interviews, statistics, profile badges, and feedback.

## Prerequisites

- **Node.js** 20 or newer  
- **MongoDB** running locally or a connection string you can put in `server/.env`

## Quick start (Windows)

From the repo root:

```powershell
.\boot.ps1
```

If execution policy blocks scripts:

```powershell
powershell -ExecutionPolicy Bypass -File .\boot.ps1
```

The script creates `server/.env` from `server/.env.example` if needed, runs `npm install`, then starts the API and the dev server.

- **App:** http://localhost:5173  
- **Health:** http://localhost:4000/api/health  

## Manual start

```bash
cp server/.env.example server/.env   # then edit JWT_SECRET, MONGODB_URI if needed
npm install
npm run dev
```

## Production build

```bash
npm run build
npm run start
```

The Express server serves the built SPA from `client/dist` when that folder exists, so users open **one URL** (the server, e.g. `http://localhost:4000`) for the UI, REST API, and Socket.IO. Set `CLIENT_ORIGIN` in `server/.env` to that same public URL (e.g. `http://localhost:4000` for local production, or your HTTPS domain behind a reverse proxy).

For development, `npm run dev` still runs Vite on port 5173 with API on 4000; keep `CLIENT_ORIGIN=http://localhost:5173` unless you change that setup.

### Server only (one machine, static IP / LAN)

You do **not** need Vite in production. Build once, then run only Node:

```bash
npm run build
npm run start
```

Or in one step: `npm run build && npm run start`.

In `server/.env`:

- **`HOST=0.0.0.0`** (default) — listen on all interfaces so clients can reach you at `http://<your-static-ip>:<PORT>`. Use `HOST=127.0.0.1` if you only want local access.
- **`CLIENT_ORIGIN`** — set to the exact URL people use in the browser, e.g. `http://203.0.113.50:4000`. If you open the app both as `http://localhost:4000` and `http://<ip>:4000`, use a comma-separated list: `http://localhost:4000,http://203.0.113.50:4000`.

Open the matching port in Windows Firewall if remote machines should connect.

### Linux + nginx (single host, static files served by Node)

The Express server already serves the built SPA from `client/dist`, so the deployment artifact is just the repo + a `client/dist/` folder. No separate static bucket, no separate frontend host — one process, one port, fronted by nginx for TLS and the public port.

```bash
# on the deploy box (one-time)
git clone <repo> /opt/devstrider
cd /opt/devstrider

# on every release
git pull
npm ci
npm run build               # produces client/dist/
NODE_ENV=production \
  PORT=4000 \
  HOST=127.0.0.1 \
  MONGODB_URI=... \
  JWT_SECRET=... \
  CLIENT_ORIGIN=https://your.domain \
  npm run start
```

- **`HOST=127.0.0.1`** so only nginx (same machine) can reach Node directly.
- **`CLIENT_ORIGIN`** is the public URL users type in the browser. Used for CORS on legitimate cross-origin calls (e.g. the Bid Assistant extension); same-origin traffic from the served SPA doesn't need it.
- **`NODE_ENV=production`** enables Apache-style (`combined`) request logs that line up with nginx's access log format.

Minimal nginx server block (TLS terminator + reverse proxy, including Socket.IO):

```nginx
server {
    listen 443 ssl http2;
    server_name your.domain;

    ssl_certificate     /etc/letsencrypt/live/your.domain/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/your.domain/privkey.pem;

    client_max_body_size 10m;

    location / {
        proxy_pass         http://127.0.0.1:4000;
        proxy_http_version 1.1;
        proxy_set_header   Host              $host;
        proxy_set_header   X-Real-IP         $remote_addr;
        proxy_set_header   X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;

        # Socket.IO needs the Upgrade headers
        proxy_set_header   Upgrade           $http_upgrade;
        proxy_set_header   Connection        "upgrade";
        proxy_read_timeout 95s;
    }
}

server {
    listen 80;
    server_name your.domain;
    return 301 https://$host$request_uri;
}
```

Optional systemd unit (`/etc/systemd/system/devstrider.service`) so Node survives reboots:

```ini
[Unit]
Description=DevStrider API + SPA
After=network.target

[Service]
Type=simple
WorkingDirectory=/opt/devstrider
EnvironmentFile=/opt/devstrider/server/.env
ExecStart=/usr/bin/npm run start
Restart=on-failure
User=devstrider

[Install]
WantedBy=multi-user.target
```

Reload + enable:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now devstrider
```

After this, your release flow is: `git pull && npm ci && npm run build && systemctl restart devstrider`.

## Bid Assistant integration (for group owners)

The **Bid Assistant** Chrome extension + optional desktop proxy can create or update **job links and the logged-in member’s bid** by calling the same JWT-protected API the web app uses.

### Record endpoint

| | |
|---|---|
| **Method & path** | `POST /api/integrations/bid-assistant/record-bid` |
| **Full URL (example)** | `https://<your-devstrider-host>/api/integrations/bid-assistant/record-bid` |
| **Content-Type** | `application/json` |

### Authentication

Use the **normal DevStrider session JWT** (the same value stored in the browser as `localStorage.devstrider_token` after login):

```http
Authorization: Bearer <JWT>
```

The server resolves the user from the token. That user must be a **member** of the `groupId` you send; otherwise the API returns **403**.

### Request body (JSON)

| Field | Required | Description |
|--------|----------|-------------|
| `groupId` | Yes | Your group’s MongoDB id (same id you see in group URLs / API paths). |
| `url` | Yes | Job posting URL (5–2048 chars). Matched to an existing group link by full normal form first, then by **origin + path without the query string** (so tracking `?` params on the live tab still match the link members saved). If needed, a **longest shared URL-prefix** fallback (≥24 chars) is used. |
| `jobDescription` | No | Private JD text stored on the member’s bid. |
| `gptResumeContent` | No | GPT / assistant output (e.g. tailored resume) stored on the bid; shown in the bid board **GPT res.** column. If `fastFeedInput` is omitted, a trailing fast-feed line at the **end** of this field is parsed and stripped (same rule as the extension). |
| `fastFeedInput` | No | One line: **`resumeId, Company, Role, skill1, …`** (comma-separated; no `[]`, so it is safe for file names). A legacy line wrapped in `[…]` is still accepted. When valid, the server sets **resume / company / role / stacks** and **status `applied`**. The Bid Assistant extension sends this automatically when that line is the last non-empty line of the assistant reply. |
| `sharedJobDescription` | No | Optional **group-visible** JD on the shared link row (only applied when non-empty on create/update paths that touch the link). |
| `comment` | No | Optional comment on the bid. |
| `origin` | No | Defaults to `"Bid Assistant"` if omitted. |

### Behavior (what owners should expect)

- **Same rules as the UI for new links:** new job URLs are only accepted during the **current UTC calendar day** (same “bidding window” as `POST /api/groups/:groupId/links`). Outside that window you get **403** with a message about the calendar day.
- **URL deduplication:** the same normalized URL in a group joins the existing **group link**; the integration then creates or updates **that member’s** `UserBid` on that link.
- **Updates:** if that user already has a bid on the link, the API **updates** JD / GPT text / comment / origin instead of failing with a duplicate error.
- **Realtime UI:** successful writes invalidate the bid board over Socket.IO like normal edits.
- **Server log:** each record attempt is still stored in the `BidAssistantActivity` collection (useful for support / audits); there is no in-app activity page for it.

### Bid Assistant user flow (extension)

1. **Job site — blue button:** extracts the JD, stores **URL + JD** in extension storage (`devstrider_pending`), copies JD to the clipboard, and runs the existing ChatGPT paste flow.
2. **ChatGPT — purple button:** runs **Word refresh** (desktop app) and, when that succeeds, **records the bid in DevStrider** in the same action. It sends the stored URL + JD plus the **latest assistant message** on the page as `gptResumeContent`. A successful sync is also written to `bidAssistantSessionCache` in extension storage.

### How Bid Assistant obtains the JWT

1. The member sets **DevStrider base URL** in the extension popup (must match the tab where they log in, e.g. `http://100.99.99.25:4000`).
2. With **desktop proxy** on (default), the extension sends `Authorization: Bearer <JWT>` to `http://127.0.0.1:8765/record-devstrider`, and the desktop app forwards the header to DevStrider (no API key on the server).
3. The extension reads **`devstrider_token`** from `localStorage` on an **open DevStrider tab** (via `chrome.scripting`), and caches it in extension storage.

Optional `server/.env`: `BID_ASSISTANT_EXTENSION_ID`, `CORS_EXTRA_ORIGINS` if you need stricter CORS for extension-only direct calls to the API.

## Version

**1.0.0** — see `package.json` and workspace packages.
