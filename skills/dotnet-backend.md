# .NET Backend Review Guidance

Focus on meaningful issues in changed .NET backend code:

- Async/await: detect sync-over-async, missing awaits, fire-and-forget work, deadlock risks, and incorrect `ConfigureAwait` assumptions.
- Cancellation tokens: check important I/O, database, queue, and HTTP operations accept and pass cancellation where available.
- Dependency injection lifetimes: flag singleton services depending on scoped services, disposable transients that are not managed, and accidental service locator usage.
- Exception handling: look for swallowed exceptions, broad catches that hide failures, leaking sensitive details, and lost stack traces.
- HttpClient usage: flag per-request client creation, missing timeouts, missing cancellation, and incorrect handling of non-success responses.
- Concurrency: detect shared mutable state, unsafe static state, race conditions, and non-thread-safe service usage.
- Validation and security: verify untrusted input is validated, authorization is enforced, secrets are not logged, and injection risks are avoided.
- Resource disposal: ensure streams, timers, scopes, and other `IDisposable` or `IAsyncDisposable` resources are disposed correctly.
- ASP.NET Core: check middleware order, missing model validation, incorrect response handling, and improper background work from request scope.
