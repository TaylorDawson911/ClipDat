# Quick Share

Quick Share uploads saved video clips through AnonMP4's documented `POST /upload` API. The upload streams directly from disk. The user must choose Upload unless they explicitly enable automatic uploads in Settings. The original clip is never deleted. Screenshots and files without a recognized video container signature are rejected.

The provider accepts only HTTPS watch links on `anonmp4.to` or `anonmp4.art` with the returned video ID in `/v/{id}`. The latter host was observed in a real API upload on 15 October 2026, despite the published example using `.to`. The info endpoint supplies playback status; an accepted upload is displayed as processing until `active` is confirmed. The API may be unreachable intermittently, so ClipDat must not claim playback readiness when that check fails.

Upload history is local. Deletion URLs returned by the API are encrypted with Windows DPAPI for the current user and never shown in logs or notifications. A saved link is reused only when its path, size, and modification time still match. Link expiration and remote deletion are not exposed because their behavior has not been verified.

Verification on 15 October 2026: mocked MP4 and MKV uploads, larger streamed upload progress, cancellation, rate limits, server errors, malformed JSON, invalid video, foreign URL rejection, history reuse, and processing status passed. A public MDN sample video was accepted by the live API using curl and returned an `anonmp4.art` watch URL. A subsequent .NET live request and info request timed out, so reliable end-to-end service availability remains unverified. No private recording was uploaded during testing.

API documentation: https://anonmp4.to/api-docs
