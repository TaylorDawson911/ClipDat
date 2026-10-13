# Clip sharing feasibility and architecture

## Feasibility

ClipDat can host a temporary HTTP endpoint on the user's PC and stream a selected clip with byte-range support. That endpoint can serve a browser on the same PC, and it can be stopped or revoked locally.

A friend on another network needs a public route to the PC and a browser-trusted HTTPS certificate for a safe, normal-browser link. Public IPv6 and router port mapping may provide a route on some networks, but neither supplies a trusted certificate or proves that inbound traffic reaches the host. CGNAT, router policy, Windows Firewall, and ISP filtering also vary. Reliable public HTTPS cannot be established for arbitrary users without a certificate and some form of publicly trusted identity/configuration. A hosted certificate or reachability service, central rendezvous, or relay would violate this product's constraints.

ClipDat therefore reports **Direct sharing unavailable**. The Share Clip panel is available for the latest saved replay, but it does not start the loopback listener, create a token, or copy a link. It reports external reachability as unavailable and keeps Copy Link and Stop Sharing disabled. The expiration selector records a session preference for future direct links; it does not create an active share. ClipDat does not attempt router mapping, alter firewall rules, contact a probe service, or upload clip data.

## Implemented architecture

- `LocalClipShareServer` remains an isolated loopback-only component and is not started by the Share Clip UI.
- If used by a local-only workflow in the future, each token maps to one explicitly selected file; the endpoint is not suitable for peer sharing.
- The server supports `GET`, `HEAD`, and one HTTP byte range for seeking. It rejects multi-range requests and serves no directory listing or other path.
- Safety limits include two concurrent connections, an 8 KiB request-header limit, an eight-second header deadline, a 30-second write deadline, and an approximately 8 MiB/s per-connection transfer cap.
- The server binds loopback only and is constructed only after a finalized clip exists and the user requests a local link.

## Not implemented

Public direct sharing, browser-trusted HTTPS, external reachability verification, IPv6 endpoint discovery, UPnP IGD, NAT-PMP, PCP, and firewall configuration are not implemented. Multiple active public shares, public-link expiry and revocation are consequently unavailable. ClipDat must continue reporting public direct sharing unavailable until a compliant design can provide those pieces without a third-party relay or hosted service.

## Verification

The local server smoke test verified loopback binding, `HEAD`, byte-range response contents, multi-range rejection, token revocation, expiry, and clean shutdown. The ClipDat x64 Debug build completed with zero errors. The packaged app was restarted from the generated MSIX registration.
