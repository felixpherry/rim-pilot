# JSON dependency and ingress boundary

[Wire v1](../protocol/wire-v1.md) defines executable envelope/authentication/framing/DTO contracts. [Bounded transport](transport.md) and [bootstrap CLI routes](bootstrap-commands.md) now use them; baseline observations remain unavailable. Both endpoint projects and the asset-free test executable link the same `shared/Json/*.cs` and `shared/Protocol/*.cs`. Transport calls `StrictJson.Parse` through the concrete request reader before mapping any request, including authentication. Do not introduce a second parser with different acceptance rules.

## Pinned redistributable candidate

- **Newtonsoft.Json 13.0.3**, MIT, upstream repository commit `0a2e291c0d9c0c7675d445703e51750363a549ef` (package metadata). Original notice: `licenses/Newtonsoft.Json.LICENSE.md`.
- `shared/Json/JsonDependency.props` requires exact NuGet version `[13.0.3]`, checked-in content-hash lock files and locked restore. Intentional upgrades require updating the pin/locks and repeating the audit/tests.
- Automatic package assets are excluded. All three projects explicitly reference **`lib/net45/Newtonsoft.Json.dll`**, including the modern CLI/tests, rather than silently selecting a different runtime asset. This asset has no NuGet dependencies. Its SHA-256 is `e1e27af7b07eeedf5ce71a9255f0422816a6fc5849a483c6714e1b472044fa9d`.
- Mod copies this licensed dependency, not any game/Unity/Harmony binary. CLI bundles the dependency in its single-file executable. Both build/publish outputs retain the MIT notice under `licenses/`; [complete-package verification](package.md) also requires it. Package downloads stay in private NuGet cache; no binary is checked in.
- Compilation against installed Mono reference assemblies and pure tests on .NET do **not** prove Unity Mono loading. A separate [isolated full-profile Unity Mono menu run](../tests/coverage/session-adversarial.md#isolated-menu-runtime-proof) exercised the packaged dependency through real auth/health/capabilities and three auth negatives. It does not prove the full strict-ingress/Unicode/numeric matrix or every shared-AppDomain conflict under Mono; those remain deferred to isolated adversarial tests.

## Why a strict preflight exists

Audited 13.0.3 `JsonTextReader` accepts comments, trailing commas, single quotes, non-finite numbers and other JavaScript extensions. `JsonLoadSettings` defaults to replacing duplicate property names and ignoring comments. These defaults cannot define an authentication boundary.

`StrictJson` first checks byte length, decodes UTF-8 with exception fallback, then validates grammar and all quotas **before** Json.NET materializes a token tree. The preflight admits only JSON punctuation/literals, double-quoted strings, strict decimal numeric grammar and JSON's four whitespace characters. It rejects comments anywhere, trailing commas/data/extra roots, BOM, sparse arrays, constructors, undefined, non-finite literals and unpaired Unicode surrogates. Duplicate property names compare ordinally after escape decoding, separately for every object. Unicode normalization is not applied; distinct case or normalization forms remain distinct names.

The second pass uses only `JsonTextReader` + `JToken.Load`: explicit depth, invariant culture, no date coercion, double floats, duplicate-name errors and no multiple roots. No `JsonSerializer`, `Deserialize`, `ToObject`, reflection, binders, custom converters or global serializer defaults participate. `$type`/`$values` are inert property names; explicit DTO field allowlists reject them unless a future concrete schema deliberately names them. No runtime type comes from client input.

## Finite ingress ceilings

| Resource | Maximum |
| --- | ---: |
| UTF-8 document bytes | 65,536 |
| Nested object/array containers (root container counts as 1) | 16 |
| Decoded string/property name UTF-16 code units | 4,096 |
| Number lexeme characters | 64 |
| Properties per object | 64 |
| Items per array | 256 |
| Document nodes (every value plus every property name) | 4,096 |

Limits are inclusive. Integers must fit signed Int64; fractional/exponent forms must parse as finite IEEE-754 double. Ordinary double rounding/underflow is not an exact-decimal contract. Concrete DTOs may tighten these bounds, must reject inappropriate types/ranges, and must not coerce `2.0`, `"2"` or booleans into an integer field. Dates stay strings. These remain request/auth ceilings, not the framing specification. `StrictJson.ParseResponse` uses the same grammar/structural limits with 262,144 ordinary-response bytes or 4,194,304 screenshot-response bytes; only screenshots permit 16,384-unit strings for bounded base64 chunks. Explicit response DTO readers keep ordinary labels/fields smaller even inside that profile. Requests must never use the larger response profile. See wire v1 for framing and response/capture budgets.

Preflight recursion, name sets, strings, decoder buffers and final tree all have finite document-wide bounds. This API receives an already allocated byte array: **transport must reject oversized frame lengths before allocating/reading that array**, apply deadlines/concurrency/rate bounds, and call this parser on every path. A bounded parser alone does not bound a connection or main-thread queue.

## Explicit mapping only

`JsonFields` checks an object's complete, case-sensitive field allowlist before a mapper reads required primitives. Mappers construct named DTOs explicitly, validate every nested object/array and impose method-specific string/count/range limits. Missing and explicit null remain distinct; unsupported fields are not silently discarded. No generic token-to-object deserialization or raw game-object serialization is allowed. Test-only `ProbeDto` demonstrates this pattern; `shared/Protocol/Requests.cs` and `ResponseContracts.cs` now apply it to named product wire DTOs. Do not expose parsed `JToken` trees as arbitrary dispatcher intents or reflect over them.

Parser/mapping failures use a fixed diagnostic and a small internal failure category. They retain no input text, property names, token, JSON-reader exception, path or credential-bearing inner exception. Later protocol code must map these categories to its own secret-safe errors; it must never log raw requests or exception details from alternative parsing paths.

## Checks (no proprietary assets needed)

```sh
./scripts/dotnet run --project tests/JsonIngress -c Release
python3 -m unittest discover -s tests -p 'test_*.py' -v
```

Behavioral fixtures cover strict grammar and the permissive upstream control, nested/escaped duplicate keys, comments/trailing commas/non-finite values, numeric overflow, malformed UTF-8, Unicode/surrogates, exact quota boundaries and overflow (bytes/depth/strings/names/numbers/collections/global nodes), trailing content, type metadata, explicit mapping with no coercion, unknown fields, culture/date stability, global serializer-default isolation and secret-safe errors. Tests verify they loaded the net45 asset, but run on .NET 10, not Mono. Pin/license/linkage guards supplement these tests; they do not constitute product transport or game proof.
