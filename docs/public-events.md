# Public event adapter

`events.read` uses the ordinary producer scheduler, scope/safety admission and final release fence. Source audit and host tests plus configured net472 compilation are **not Mono/game acceptance**. CLI observation commands and real menu/paused/live comparisons remain separate tasks.

## Installed-source boundary

Audited installed 1.6 sources: `LetterStack.ReceiveLetter(Letter,...)`, `Messages.Message(Message,bool)`, `Messages.Update/Clear`, `Archive.Add/Remove/CheckCullArchivables`, `Letter/ChoiceLetter/StandardLetter`, `TaggedString/ColoredText`, and existing root/current/scene/loading readiness closure. No decompiled code is distributed.

- Only canonical object overloads are hooked. String/TaggedString overloads converge there; no overload replay. Delayed letters are observed only when subsequently delivered, never when queued. Restored history is not enumerated.
- Letters require exact `StandardLetter`, absent identity in a bounded pre-delivery live list, successful matching in-flight `Archive.Add`, and the same list growing by one with that letter at its tail after successful canonical return. Other letter classes remain unaudited, not empty substitute payloads.
- Messages require exact `Message`, absent identity before delivery, and the same live list ending with that message after successful return, with the installed 12-entry eviction rule. Rejected equal-text duplicates and timer refreshes do not satisfy this proof. Distinct later accepted messages may carry identical text.
- One eligible canonical call is tracked at a time. Nested calls are omitted; observer calls, candidates, fields and elapsed CPU are finite per Update. Finalizer retries balance state once. Exceptions, missing prefix evidence, cross-thread calls and exhausted accounting fail closed.
- Current root, destroyed flag, game/source identity, loading/Scribe state and developer safety are rechecked before and after acceptance. Payload getters run only after acceptance and the second safety check. Required methods must have no other patches; hooked methods must have exactly this adapter's prefix/finalizer. Unknown patches disable projection, not grant assembly-wide trust.

## Payload and availability

Only bounded nonvirtual `Letter.Label`, `ChoiceLetter.Text` and `TaggedString.RawText`, or `Message.text`, supply payloads. Sanitization strips known presentation-only Unity/Verse tags, including faction/ideo tag arguments, without resolving world references or populating color caches. Unknown markup, invalid Unicode/control text, more than 4,096 raw characters, labels over 512 or text over 2,048 are omitted, never truncated. No debugInfo, look-target projection, tooltip, dialog opening, dismissal, timer update, sound or acceptance call occurs in bridge code.

A fixed 256-slot identity witness table tracks only retained accepted sources. Successful canonical `Archive.Add` supplies archive availability; all `Archive.Remove` calls conservatively revoke it, including automatic culling. No archive membership scan is needed, even with unlimited pinned source history. Letter removal revokes live-letter availability. Canonical message eviction and `Messages.Update/Clear` refresh the at-most-12 live-message set; event release refreshes it again. Text expires once neither live nor archive availability remains, producing a gap for affected old readers. Witness refs are private, bounded and never serialized; ring contains only primitive DTOs. Uncooperative direct raw-list writes outside audited paths remain a runtime compatibility risk.

## Retention, scheduling and safety

Ring limits: 256 entries, 262,144 encoded payload bytes; pages at most 64 items and below response byte ceiling; 128 reusable opaque cursor boundaries with five-second expiry. First read starts oldest available history without pre-bridge completeness claims. Readers do not consume shared history. Overflow/removal gives affected cursors `event_gap` without count/reason; unknown, evicted, reset or expired tokens give `cursor_expired`. Repeated tail reads reuse the boundary. Lifecycle strings are fixed `observation_ready`, `scope_changed`, `observation_resumed`.

Hook CPU, scanned candidates and nested fields debit the same session Update allowances used by producers (2 ms, 128 candidates, 1,024 fields), not new per-call budgets. Ring/witness storage has separate fixed retention caps. Bounds can cause omission/reset or delayed/refused reads; they do not certify hard preemption of a managed stage or Harmony metadata allocation. More than the remaining candidate allowance in the raw letter list cannot be scanned.

Ordinary derived-Update/GUI brackets still revoke every observation/render lease, but preserve already-accepted historical text. History is independently availability-checked on release. Explicit window/developer/scale/transition brackets, unsafe/incomplete safety samples, readiness/source/geometry changes, failures and missing evidence clear history and witnesses immediately. This is not a completed-render witness and cannot detect arbitrary third-party debug flag writes that turn on and off entirely inside an uninstrumented callback. Full-profile hook order, useful throughput and actual Mono UI equivalence remain unverified.
