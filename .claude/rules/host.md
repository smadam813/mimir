---
paths:
  - "src/Mimir.Server/Program.cs"
  - "src/Mimir.Server/Modules/**"
  - "src/Mimir.Server/Models/**"
  - "src/Mimir.Server/Configuration/**"
---

# Host: binding, options, models

Kestrel binds `127.0.0.1` on `ServerOptions.Port` only when `ASPNETCORE_URLS` is unset — an explicit `ASPNETCORE_URLS` always wins, which is what lets the Compose container listen on 8080 while the host publishes 6464 (§12). Neither branch adds HTTPS or auth: localhost is v1's whole trust boundary. `Program.cs` is top-level statements with no seam a test can reach, which is why this is a rules line rather than a pin.

A new §11 section is one options class plus one `AddSection` line in `MimirOptionsRegistration`, and nothing else. Deliberately unenforced: `IOptions<T>` resolves whether or not the section was ever registered, so a forgotten `AddSection` line binds nothing, validates nothing, and fails nowhere.

All model access goes through the `Microsoft.Extensions.AI` abstractions, backed by OllamaSharp. The one exception is startup provisioning, which needs Ollama's native API (`IModelCatalog`) to list and pull — that need is the whole reason the native surface is used rather than the OpenAI-compatible one, and `ModelOptions`' names are what provisioning pulls. `ModelProvisioner` tracks pull progress in its own local array rather than reading it back off `IHealthState`, so it stays the sole author of its tile.
