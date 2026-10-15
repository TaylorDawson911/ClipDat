# Quick Share feasibility and architecture

## Current status

ClipDat has a loopback-only clip server, but no public Quick Share service is active. The UI deliberately does not generate or copy a public URL. The existing listener is not started by the UI. This avoids presenting a localhost link as if a friend could open it.

## Cloudflare prerequisite

The requested model uses a named Cloudflare Tunnel to carry HTTPS requests to a ClipDat listener bound to `127.0.0.1`. It requires a Cloudflare account, a domain with a public hostname routed to the tunnel, the official `cloudflared` client, and tunnel credentials. Temporary Quick Tunnels are intended for development and cannot be treated as a production hostname.

Cloudflare's Tunnel FAQ (checked 15 October 2026) states that public-hostname routes on Free, Pro and Business plans require a specified paid service for video and other large files. That service can require hosting the video on Cloudflare, which conflicts with ClipDat's local-only video requirement. A private-network route avoids that restriction but requires the viewer to install a Cloudflare client, which conflicts with opening the clip in a normal browser. Therefore public Cloudflare video sharing must stay disabled unless the user's Cloudflare agreement explicitly permits PC-hosted video through its public Tunnel route.

Sources:

- https://developers.cloudflare.com/cloudflare-one/faq/cloudflare-tunnels-faq/#large-file-and-streaming-traffic-through-tunnel
- https://developers.cloudflare.com/tunnel/get-started/
- https://developers.cloudflare.com/tunnel/features/locally-managed-tunnels/create-local-tunnel/

## Set up the Cloudflare side

The tunnel ID and `cloudflared.exe service install` command shown by the dashboard indicate a **remotely managed** tunnel. The command contains a secret tunnel token. If it has been pasted into a chat or issue, rotate it in the Cloudflare dashboard before using it. Do not add the token to ClipDat settings, source control, diagnostic logs or screenshots. Do not install the Windows service for an optional, on-demand sharing feature; a permanent service would keep the tunnel connected after ClipDat closes.

1. Check Cloudflare's current [video delivery rule](https://developers.cloudflare.com/cloudflare-one/faq/cloudflare-tunnels-faq/#large-file-and-streaming-traffic-through-tunnel). Confirm with Cloudflare that your plan or agreement permits public delivery of **PC-hosted** clips through Tunnel. A standard Free, Pro or Business public hostname route does not by itself satisfy this requirement. Cloudflare Stream or R2 would store video with Cloudflare and would not meet the local-only goal.
2. Add a domain to the Cloudflare account if you do not already have one. A public application route needs a domain on Cloudflare. Choose a dedicated hostname such as `clips.example.com` under that domain.
3. In the Cloudflare dashboard, open **Networking → Tunnels**, select the ClipDat tunnel, then **Routes → Add route → Published application**. Set the hostname to the dedicated subdomain. The service URL must eventually point to ClipDat's loopback listener; ClipDat does not currently provide a stable service port or tunnel integration, so there is no valid value to enter yet. Do not point it at another local service.
4. Once an on-demand tunnel integration exists, use the official `cloudflared` binary with a newly rotated token stored outside source control, and have ClipDat start it only for an explicit share. Test the actual HTTPS hostname from outside the PC before enabling Copy Link.

Official setup: https://developers.cloudflare.com/tunnel/get-started/

## Proposed deployment

1. The user routes a dedicated hostname to a named tunnel using Cloudflare's documented setup. ClipDat does not install a background service or store an account certificate.
2. For the existing remotely managed tunnel, ClipDat needs a safe way to set its origin URL to the current loopback port. The tunnel token alone cannot edit a published application route. A separately scoped Cloudflare API credential or a reserved local port with collision checks would be needed. The alternative is a new locally managed tunnel with a user-owned credentials file and a temporary ingress configuration. Neither path is implemented yet.
3. On an explicit Quick Share action, ClipDat validates one saved video, starts the listener on a loopback port, and launches one official `cloudflared` child that routes only the dedicated hostname to that port. The catch-all ingress rule returns 404. ClipDat must not log or copy tunnel credentials.
4. ClipDat probes the public HTTPS hostname using the new token and also checks rejection of an invalid token. Only a validated response enables Copy Link. A process exit or failed probe makes all links unavailable.
5. Each link maps to one file, has its own 256-bit random token and expiry, and can be revoked independently. Expiry is enforced by the HTTP server on every request. When the last share ends, ClipDat stops the listener and its child tunnel process.
6. The browser page has no third-party scripts, images, analytics or redirects. MP4 can use native playback; MKV needs an explicit download or a separate bounded remux operation. The original recording is never uploaded into cloud storage.

## Verification gate

Local server and UI tests can be run without Cloudflare credentials. A real public-link success test additionally needs a permitted Cloudflare plan or agreement, a configured domain and named tunnel, and a live `cloudflared` installation. Until these exist, ClipDat must report **Quick Share unavailable** and must never copy a purported public URL.
