# Purpose
- A C# Bootstrap project to setup initial configuration and initial data in the ci-lab environment.
- It must be idempotent in everything it does, so it doesn't create duplicates of things; ensure it uses identifiable names and markers to achieve this.
- As this is a generated testing environment, it is fine to store secrets in source control.

# Coding conventions & change policy:
- Follow modern C# practices:
    - Use nullable reference types and top-level statements.
    - Use var, new() and pattern matching where appropriate.
    - Do not use inner classes. Organise code into Services and Models (in appropriate folder structure).
- As these are console applications, do not use async/await unless the library code only supports async.
- Use dependency injection for services.
- Prefer `HttpClientFactory` for HTTP calls.
