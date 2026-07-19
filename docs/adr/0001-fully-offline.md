# Fully offline — no cloud AI calls

Mimir's data never leaves the machine and its intelligence never depends on the network: distillation and embeddings run on local models (Ollama), even though the Anthropic API would be cheaper to integrate and the session content already transits Anthropic via Claude Code itself. Decided at map charting ([#1](https://github.com/smadam813/mimir/issues/1)); it shapes the model runtime ([#4](https://github.com/smadam813/mimir/issues/4)), the hardware envelope, and every pipeline that touches an LLM.

**Consequences**: local model quality bounds distillation quality; the Ollama container and GPU passthrough are load-bearing infrastructure; nothing in the codebase may add a cloud AI dependency without revisiting this ADR.
