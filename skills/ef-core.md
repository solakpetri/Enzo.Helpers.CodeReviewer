# EF Core Review Guidance

Focus on EF Core issues in changed data access code:

- Concurrent operations on the same `DbContext`; EF Core contexts are not thread-safe and operations must not overlap.
- DbContext lifetime problems, including singleton capture, use after disposal, long-lived contexts, and request-scoped contexts used by background work.
- N+1 queries caused by lazy loading, per-row queries, or missing projection/include strategy.
- Unnecessary tracking for read-only queries; prefer `AsNoTracking` when entities are not updated.
- Premature materialization before filtering, sorting, grouping, or paging.
- Incorrect `SaveChanges` or `SaveChangesAsync` usage, including missing calls, too many calls, or ignoring cancellation.
- Transaction problems where related writes can partially commit or where ambient transactions are misused.
- Query inefficiencies such as client-side evaluation, unbounded result sets, missing paging, or loading full entities when projections are enough.
- Entity tracking conflicts from attaching duplicate entity instances, mixing tracked and detached graphs, or overwriting concurrency tokens.
