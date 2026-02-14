---
applyTo: '**/*.cs'
---

## C# Guidelines
- Follow modern C# practices.
- Use best practices for organsing C# code. Put services classes under a Services folder, controllers in a Controllers folder, etc. In this codebase, use the Entities folder for models.
  - Within the Services folder, create subfolders based on feature, purpose or domain, to group related service classes together. Divide functionlaity into sensible service classes, ensuring that any one service class does not contain unrelated functionality.
  - Similarly, within the Entities folder, create subfolders to group related models together.
- Use dependency injection for services.
- Use nullable reference types and top-level statements.
- Use var, new() and pattern matching where appropriate.
- Do not use inner classes. Organise code into Services and Models (in appropriate folder structure).
- Do not suffix async method name with "Async" unless there is also a sync version of the method.
- When parsing JSON returned from an API, use model classes to deserialize responses in a typesafe way. Place model files in a directory called `Entities` at the root of a project and only include properties that are used.
- Do not use JsonElement or dynamic types for JSON parsing unless it is not possible to deserialize in a typesafe way or it will make the code much more complex to do it statically.
- Do not use unicode characters for decorating Log messages.
- Organise private methods after public ones and private fields after public fields. All fields should come before all methods.
- Do not use anonymous types when returning from Controllers. Always define a record or POCO in the Entities folder tree.
