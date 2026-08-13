# .NET Testing Review Guidance

Focus on important testing gaps introduced by the change:

- Missing tests for new business rules, error paths, security checks, concurrency behavior, and persistence behavior.
- Weak assertions that only verify no exception was thrown or assert implementation details instead of observable behavior.
- Incorrect async test usage, including `async void`, missing awaits, blocking on tasks, or tests that can pass before work completes.
- Excessive mocking that hides integration problems or verifies internal calls without validating outcomes.
- Tests coupled to implementation details that will break during safe refactoring.
- Missing edge cases for null, empty, boundary values, duplicate data, invalid input, authorization failures, and cancellation.
- Non-deterministic tests caused by real time, random values, ordering assumptions, external services, shared state, or parallel interference.
