# SocketJack Documentation Page Progress

## Current Scope

- Public docs routes: `/Documentation`, `/Doc`, `/Docs`, `/ReadMe`, `/Info`, `/Help`.
- Troubleshooting route: `/issues`.
- Documentation tree order: Get Started, Installation Guide, Migration Guide, APIs, All Source Members.
- Product split: SocketJack Library and JackLLM Workstation.

## Completed

- Reworked `SocketJack/html/Documentation.html` into a dark Microsoft Learn-style article layout.
- Added a tree-view navigation split between SocketJack Library and JackLLM Workstation.
- Added a Troubleshooting link that opens `/issues`.
- Added source-member scanning from the SocketJack GitHub raw source list with embedded API fallback entries.
- Added XML summaries for `HtmlPageResources` public methods used by the docs page.
- Added public SocketJack.com documentation aliases in `SocketJack-MagicMasterList`.
- Expanded the local JackLLM docs route aliases.
- Added `/issues` Q&A and error-reporting page with `/auto/api` tools-mode chat.
- Added issue chat session persistence and issue report storage.
- Added JACK-only reported-issues admin API and Admin tab.

## Limits Enforced

- Error text: 10,000 lines.
- Screenshots: 10 images.
- Admin issue visibility: authenticated user `JACK` only.

## Verification Checklist

- Build `SocketJack-MagicMasterList`.
- Build or test the docs route in `SocketJack.Tests`.
- Browser-check `/Documentation`, `/Doc`, `/Docs`, `/ReadMe`, `/Info`, `/Help`.
- Browser-check `/issues` chat layout, issue-report validation, and screenshot previews.
- Browser-check `/Admin` as `JACK` and as a non-JACK admin for issue tab visibility.
