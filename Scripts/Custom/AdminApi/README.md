# Custom/AdminApi

Loopback HTTP API backing the shard editor. Ported last, because it needs every other system
present to have anything to project.

The listener runs on its own background thread and **never touches world state directly** — it
posts work through `Custom/Core/LoopQueue` and waits on a `TaskCompletionSource` with a timeout.
See CLAUDE.md §4.

Security is layered and all of it is required: bind loopback only, verify
`IPAddress.IsLoopback`, require the Host header to name loopback (anti-DNS-rebinding), reject
foreign Origins, emit no CORS headers, accept the bearer token only from the `Authorization`
header, compare it with `FixedTimeEquals`, and cap the body size. Path containment must keep the
trailing separator — and it matters more here than on ModernUO, because Mono's `HttpListener`
is fully managed and will not reject `..` for you.
