---
paths:
  - "src/Mimir.Server/Storage/**"
---

# Storage: EF migrations

EF migrations: `dotnet restore` first in a fresh worktree, then from `src/Mimir.Server`: `dotnet ef migrations add <Name> --output-dir Storage/Migrations`.
