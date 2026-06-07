# /Auto Authenticated Retest Report

Generated: 2026-06-02T16:45:40-05:00

## Summary

Authentication was restored for the in-app `/Auto` session. The page accepted the saved JACK credential through the SocketJack web-auth API, consumed the returned auth token, removed it from the URL, and then showed `Signed In` with an available token balance.

The authenticated tools-mode smoke prompt completed successfully.

## Test Details

- Page: `https://socketjack.com/Auto`
- Mode: `tools`
- Origin: `hybrid`
- Server parameter: `lmvs-shell-05d29369622672e5`
- Selected server during run: `TitanX`
- Selected model during run: `Qwen3.5-2B-Claude-4.6-Opus-Reasoning-Distilled-GGUF`
- Authenticated user: `JACK`
- Prompt marker requested: `AUTH_AUTO_TEST_OK`
- Assistant marker returned: `AUTH_AUTO_TEST_OK`
- Completion time shown by `/Auto`: `1m 08s`

## Post-Run State

- `/Auto` still showed `Signed In`.
- Token meter showed tokens available.
- Premium control was available after the run.
- The auth token was not left in the visible URL.

## Notes

- The password and auth token were not written into this report.
- The public `/login` route visually rendered only the shell/background in the in-app browser, so the retest used the working `/api/web-auth/login` path and let `/Auto` consume the returned auth token.
- This retest used the currently open `/Auto` URL and model, which were routed to TitanX/2B rather than Sable/35B.
