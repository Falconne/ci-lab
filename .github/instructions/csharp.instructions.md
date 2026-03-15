---
applyTo: '**/*.cs'
---

# C# Guidelines
- Follow modern C# practices.
- Use best practices for organsing C# code. Put services classes under a Services folder, controllers in a Controllers folder, etc. In this codebase, use the Entities folder for models.
  - Within the Services folder, create subfolders based on feature, purpose or domain, to group related service classes together. Divide functionlaity into sensible service classes, ensuring that any one service class does not contain unrelated functionality.
  - Similarly, within the Entities folder, create subfolders to group related models together.
- Use dependency injection for services.
- Use var, new() and pattern matching where appropriate.
- Do not use inner classes. Organise code into Services and Models (in appropriate folder structure).
- Do not suffix async method names with "Async" unless there is also a sync version of the method.
- When parsing JSON returned from an API, use model classes to deserialize responses in a typesafe way. Place model files in a directory called `Entities` at the root of a project and only include properties that are used.
- Prefer using `GetFromJsonAsync` to fetch endpoints using HttpClient, directly deserialising to a model over using the barebones `SendAsync` unless there is a clear advantage to doing so (e.g. where NotFound is an expected status and we don't want to throw on that). Handle and log any bad reponse code or json parsing exceptions.
- Do not use JsonElement or dynamic types for JSON parsing unless it is not possible to deserialize in a typesafe way or it will make the code much more complex to do it statically.
- Do not use unicode characters for decorating Log messages.
- Organise private methods after public ones and private fields after public fields. All fields should come before all methods.
- When inlining methods, review the resulting code to see if it should be tidied up now that the code is inlined.
- Controllers should return strong types where possible. For example, prefer returning `ActionResult<T>` over just `IActionResult`.
- Avoid the `volatile` keyword. Performance is not critical so locks are fine.
- When an unexpected condition happens, log an error rather than just a warning. Warnings should only be used for conceivable events which don't need to be investigated, such as an external service being temporarily unavailable, but we are retrying anyway.
- Use the hlper methods in the Util project whever possible to simplify code, particularly `IsEmpty``, `IsNotEmpty` and `NotAny`.