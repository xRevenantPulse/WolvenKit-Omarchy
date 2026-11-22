# Proton content network connector

The content-only factory is wired into both production consumers: the per-call script updater client and the shared static template updater client. It does not enable binary self-updates. Binary updates in this downstream product are manual-only.

## Production contract

- Public API: `WolvenKit.App.Helpers.ProtonHttpClientFactory.CreateHttpClient()`.
- Only exact `WOLVENKIT_PROTON_COMPAT=1` selects the compatibility handler. Unset, `true`, `01`, whitespace and other values return literal `new HttpClient()`.
- Normal HTTP version/policy, request timeout, headers, proxy negotiation, TLS/SNI and certificate validation remain framework-owned. No production IP pin, IPv6 switch, interface preference, certificate callback or trust-store change is applied.
- The connection callback resolves the handler-selected endpoint, including a configured proxy. DNS results are deduplicated and interleaved by address family, preserving the first family and order within each family.
- At most two TCP attempts per connection, with a 200 ms stagger and immediate progression after failure. Each production attempt is canceled after two seconds. Overall DNS/TCP budget and handler ConnectTimeout are ten seconds.
- The winning raw NetworkStream owns its socket. Losing streams are disposed and losing faults observed. Attempt slots are recycled only when cancellation cleanup completes, avoiding a transient third socket. An abandoned resolver task's eventual fault is observed without delaying caller timeout/cancellation or initiating late dials.
- Injected dialers must honor cancellation for their individual deadline; the outer budget remains bounded for a non-cooperative test seam, whose late successful stream is disposed. Production Socket.ConnectAsync honors its token.
- This is TCP address fallback, not HTTP/TLS retry. A successful TCP connection followed by a TLS/HTTP stall does not cause another address to be tried. No universal Wine or IPv6 defect is claimed.

## Run from a public checkout

Use the SDK selected by the repository's root `global.json`. No private evidence directory or local SDK override is required by these commands. From the repository root:

```sh
dotnet run --project Tests/ProtonNetworkHarness/ProtonNetworkHarness.csproj -c Release

dotnet publish Tests/ProtonNetworkWindowsProbe/ProtonNetworkWindowsProbe.csproj \
  -c Release -o artifacts/proton-network-windows
```

The no-argument harness runs 23 named scenarios. It targets pure net10.0, links the actual connector/factory sources and needs no test-framework package or WPF. The sibling Windows project links the same sources/tests/probe and produces a framework-dependent win-x64 executable. Execute that bundle under matching Windows .NET 10 Desktop Runtime, or a separately validated Wine/Proton setup. Cross-compilation alone is not Windows execution evidence.

Coverage includes exact opt-in/defaults, family ordering and deduplication, deadlines/cancellation, a two-dial ceiling, winner/loser ownership and late faults, real loopback sockets, local HTTP/proxy behavior and standard TLS rejection. Late-DNS tests deliberately use test-only .NET 10 task-holder/continuation inspection and bounded GC; they fail closed if that internal runtime layout changes. No such reflection or global hook is introduced into production.

The existing self-signed server-certificate fixture can fail in Wine/CNG before a handshake. Preserve that failure: it is not proof of client certificate rejection and does not make the complete Windows suite green. Independent valid-SAN server/SNI/client rejection controls require their own evidence and must not bypass validation.

## GET-only diagnostic probe

Set the compatibility flag in the actual child process before constructing its client. A prefix environment value can override an intended Unix assignment; read the reported Windows flag before accepting a flag-off control. For example, on Windows:

```powershell
$env:WOLVENKIT_PROTON_COMPAT = '1'
.\artifacts\proton-network-windows\ProtonNetworkWindowsProbe.exe --probe https://wolvenkit.github.io/Wolvenkit-Resources/hash.txt --timeout-seconds=20
```

Omit `--addresses` for real DNS. `--addresses=BAD_IP,REACHABLE_IP` is a diagnostic DNS-candidate seam only; it preserves URL/Host/SNI, configured proxy and normal certificate checks, and is ignored outside exact flag1. `--cancel-ms=50` exercises earlier cancellation. Do not copy diagnostic address literals into production configuration.

The probe is GET-only, has an explicit cancellation budget and a 64 MiB body cap. ZIP central-directory counts are read in memory; nothing is extracted or installed. Error exit1 contains structured error metadata.

### Safe output schema

SchemaVersion2 reports endpoint metadata as `{scheme, host, port}`, not full URIs. Initial/final/proxy userinfo, paths, queries and fragments are omitted. Response bodies, unrestricted exception messages and stacks are never emitted. Errors retain structured type, HRESULT, HttpRequestError and cancellation metadata. Successful reads retain byte count, SHA-256 and optional ZIP entry count. DNS/IP and endpoint metadata remain visible, so this is data minimization, not anonymous logging.

For TLS failures use SecureConnectionError/authentication exception types where present, not removed free-form strings such as UntrustedRoot. Standard validation callbacks remain null. Do not disable validation to obtain diagnostics.

## Evidence and limits

The original isolated component was unwired during development; production integration subsequently connected both content consumers. Historical `.audit-evidence` scripts, SDK selectors, privacy fixtures, logs and published probe bundles are private laboratory artifacts and are not part of this checkout. Old commands referencing those paths are not public rerun instructions; use the commands above for the public harness.

Private loopback privacy tests used synthetic canaries across initial/redirect/proxy URIs, bodies, errors, cancellation and ZIP reports, scanning complete stdout/stderr including adapters. Those fixtures are not shipped here, and running the public harness is not a claim that those private checks also ran. Live resource GETs, actual cache commits, UI startup, physical Windows, all Linux distributions and indefinite stability are separate acceptance boundaries.
