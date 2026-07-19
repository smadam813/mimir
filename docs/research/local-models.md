# Research: local model runtime and models

- **Ticket:** [#4](https://github.com/smadam813/mimir/issues/4)
- **Question:** How should Mimir serve local models for embeddings and distillation, fully offline, under Docker Compose on Windows 11 (Docker Desktop / WSL2)?
- **Hardware context:** RTX 4080 (16 GB VRAM) shared with dev workloads, 32 GB RAM, i9-13900KF.
- **Date:** 2026-07-19. All claims verified against primary sources (official docs, vendor model cards, NuGet/GitHub); each claim cites its source.

## TL;DR

Run **Ollama** as a single Docker Compose service (`ollama/ollama`, GPU via WSL2 passthrough). One container serves both `/v1/embeddings` and `/v1/chat/completions` against different models, loading them on demand and releasing VRAM when idle — the only runtime evaluated that does this out of the box. Models: **Qwen3-Embedding-0.6B** for embeddings, **Qwen3-8B** (`/no_think`) for distillation — both Apache-2.0, together ~8 GB VRAM resident, leaving headroom on the 16 GB card. .NET side: code against **Microsoft.Extensions.AI** (`IChatClient` / `IEmbeddingGenerator<string, Embedding<float>>`) with **OllamaSharp** as the concrete client; swapping runtimes later is a DI registration change.

## Platform baseline: GPU passthrough on Windows 11

This applies to every runtime and is the key platform fact:

- Docker Desktop GPU support is **WSL2-backend only**: NVIDIA GPU, current NVIDIA **Windows** driver (with WSL2 GPU paravirtualization), up-to-date WSL2 kernel (`wsl --update`); containers get the GPU with `--gpus=all`. No separate NVIDIA Container Toolkit install step appears in Docker Desktop's docs. ([Docker Desktop GPU docs](https://docs.docker.com/desktop/features/gpu/))
- Install **only the Windows driver** — "Do not install any Linux display driver in WSL"; it is surfaced inside WSL as `libcuda.so`. Under WSL2 only `--gpus all` is supported (no per-device filtering). ([NVIDIA CUDA on WSL User Guide](https://docs.nvidia.com/cuda/wsl-user-guide/index.html))
- In Compose, declare the GPU with `deploy.resources.reservations.devices` (`driver: nvidia`, `count: all`, `capabilities: [gpu]` — capabilities is mandatory) ([Compose GPU support](https://docs.docker.com/compose/how-tos/gpu-support/)), or the shorthand `gpus: all` service attribute ([Compose services reference](https://docs.docker.com/reference/compose-file/services/)).

So GPU passthrough works identically for all candidates; the differentiators are API surface, multi-model behavior, VRAM behavior on a shared GPU, and the offline story.

## Runtime comparison

### Ollama

- **Docker/WSL2:** official image `ollama/ollama`; FAQ states GPU acceleration works "in Linux or Windows (with WSL2)". Models persist in a volume at `/root/.ollama`, API on port 11434. ([Ollama Docker docs](https://docs.ollama.com/docker), [FAQ](https://docs.ollama.com/faq), [Docker Hub](https://hub.docker.com/r/ollama/ollama))
- **OpenAI-compatible API:** `/v1/chat/completions`, `/v1/completions`, `/v1/models`, `/v1/models/{model}`, and **`/v1/embeddings`** (string or array input) at `http://localhost:11434/v1` — one endpoint set covers both Mimir needs. ([OpenAI compatibility docs](https://docs.ollama.com/openai))
- **Offline:** pull once from the Ollama registry, then "Ollama operates entirely offline"; `OLLAMA_NO_CLOUD=1` disables cloud features; fully air-gapped import via `ollama create` from a local GGUF in a Modelfile. ([FAQ](https://docs.ollama.com/faq), [import docs](https://docs.ollama.com/import))
- **Embeddings + generation concurrently, one instance:** yes. `OLLAMA_MAX_LOADED_MODELS` (default 3 × GPU count), `OLLAMA_NUM_PARALLEL` (default 1), `OLLAMA_KEEP_ALIVE` (default 5 min; `-1` pins a model in memory). ([FAQ](https://docs.ollama.com/faq))
- **VRAM on a shared GPU:** quantized GGUF (llama.cpp engine underneath), models load on demand and **unload after idle keep-alive**, returning VRAM to dev workloads. ([FAQ](https://docs.ollama.com/faq))
- **License:** MIT. ([repo](https://github.com/ollama/ollama))

### llama.cpp `llama-server`

- **Docker/WSL2:** official images `ghcr.io/ggml-org/llama.cpp:server-cuda`; GGUF files mounted from a volume. Caveat: "GPU enabled images are not currently tested by CI beyond being built." ([docker.md](https://github.com/ggml-org/llama.cpp/blob/master/docs/docker.md))
- **API:** `/v1/chat/completions`, `/v1/completions`, `/v1/embeddings`, `/v1/models`, `/v1/rerank`; embedding models want `--embedding` mode ("use only with dedicated embedding models"). ([server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md))
- **Multi-model:** no longer strictly one model per process — a **router mode** (Dec 2025) loads models on demand from `--models-dir`, isolates each in a child process, caps concurrency (`--models-max`, default 4), LRU-evicts to free VRAM. New and less battle-tested than Ollama's scheduler. ([server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md), [ggml-org announcement](https://huggingface.co/blog/ggml-org/model-management-in-llamacpp))
- **Offline:** the purest story — plain GGUF files on a mounted volume, no registry or hub dependency.
- **License:** MIT. ([repo](https://github.com/ggml-org/llama.cpp))

### vLLM

- **Windows:** "vLLM does not support Windows natively" — Linux only; the Docker-on-WSL2 route works (official image `vllm/vllm-openai`, needs `--ipc=host`/shm and an HF cache volume). RTX 4080 (compute 8.9) meets the ≥7.5 requirement. ([GPU install docs](https://docs.vllm.ai/en/latest/getting_started/installation/gpu.html), [Docker deployment](https://docs.vllm.ai/en/latest/deployment/docker.html))
- **API:** `/v1/chat/completions`, `/v1/completions`, `/v1/models`, `/v1/embeddings` (embedding models only), plus rerank/score/audio. ([OpenAI-compatible server docs](https://docs.vllm.ai/en/latest/serving/online_serving/))
- **Multi-model:** **one model per server instance** (FAQ: run multiple instances for multiple models) — embeddings + generation means two containers. ([vLLM FAQ](https://docs.vllm.ai/en/latest/usage/faq/))
- **VRAM:** `gpu_memory_utilization` (default ~0.9; 0.92 in current engine-args docs) is **statically preallocated per instance** for weights + KV cache and held while the server runs — hostile to a GPU shared with dev work. GGUF support is "highly experimental". ([engine args](https://docs.vllm.ai/en/latest/configuration/engine_args.html), [optimization docs](https://docs.vllm.ai/en/latest/configuration/optimization.html), [GGUF quantization docs](https://docs.vllm.ai/en/latest/features/quantization/gguf.html))
- **Offline:** pre-populated HF cache volume + `HF_HUB_OFFLINE=1`, or a local model directory. ([supported models](https://docs.vllm.ai/en/latest/models/supported_models/), [HF Hub env vars](https://huggingface.co/docs/huggingface_hub/package_reference/environment_variables))
- **License:** Apache-2.0. ([repo](https://github.com/vllm-project/vllm))

### Others (brief)

- **LocalAI** — MIT, OpenAI drop-in wrapping llama.cpp/vLLM/whisper.cpp backends; CUDA images exist. Viable but an extra abstraction layer over the same llama.cpp engine with no concrete advantage here. ([localai.io](https://localai.io/), [containers guide](https://localai.io/installation/containers/))
- **HF Text Generation Inference** — ruled out: officially "in maintenance mode" (docs point to vLLM/SGLang/llama.cpp), and generation-only (no `/v1/embeddings`; TEI is a separate product). ([TGI docs](https://huggingface.co/docs/text-generation-inference/index))

### Summary table

| Criterion | Ollama | llama-server | vLLM | LocalAI | TGI |
|---|---|---|---|---|---|
| Docker Desktop + WSL2 GPU | Yes, documented | Yes (GPU images not CI-tested) | Yes via Docker/WSL2; no native Windows | Yes | Yes, but maintenance mode |
| `/v1/embeddings` + `/v1/chat/completions` | Both, one instance | Both (router mode or two services) | Both, but one model per instance | Both | Chat only |
| Embeddings + generation in one container | **Yes** (default 3 loaded models) | Yes (router mode, Dec 2025) | No — two containers | Yes | No |
| VRAM on shared GPU | On-demand load, idle unload | On-demand + LRU eviction | Static preallocation ~0.9/instance | Backend-dependent | — |
| Quantized GGUF | Native | Native | Experimental | Native | No |
| Fully offline | Pull-once volume; air-gap via GGUF import | Best: bare GGUF files | HF cache + `HF_HUB_OFFLINE=1` | Local files | HF cache |
| License | MIT | MIT | Apache-2.0 | MIT | Apache-2.0 |

## Model candidates

Sizes are Ollama library defaults (Q4_K_M for generation models; F16-ish for the small embedders).

### Embedding models

| Model | Params | Dims (MRL) | Max seq | Lang | License | Ollama tag (size) | Prefixes |
|---|---|---|---|---|---|---|---|
| [Qwen3-Embedding-0.6B](https://huggingface.co/Qwen/Qwen3-Embedding-0.6B) | 0.6B | 1024 (MRL 32–1024) | 32K | 100+ | Apache-2.0 | [`qwen3-embedding:0.6b`](https://ollama.com/library/qwen3-embedding) (639 MB) | optional instruction-aware queries (+1–5%) |
| [nomic-embed-text-v1.5](https://huggingface.co/nomic-ai/nomic-embed-text-v1.5) | 137M | 768 (MRL 64–768) | 8192 | EN | Apache-2.0 | [`nomic-embed-text`](https://ollama.com/library/nomic-embed-text) (274 MB) | **required**: `search_document:` / `search_query:` |
| [bge-m3](https://huggingface.co/BAAI/bge-m3) | 567M | 1024 | 8192 | 100+ | MIT | [`bge-m3`](https://ollama.com/library/bge-m3) (1.2 GB) | none |
| [arctic-embed-l-v2.0](https://huggingface.co/Snowflake/snowflake-arctic-embed-l-v2.0) | 568M | 1024 (MRL→256) | 8192 | 74 | Apache-2.0 | [`snowflake-arctic-embed2`](https://ollama.com/library/snowflake-arctic-embed2) (1.2 GB) | `query: ` on queries |
| [mxbai-embed-large-v1](https://huggingface.co/mixedbread-ai/mxbai-embed-large-v1) | 335M | 1024 (MRL) | **512** | EN | Apache-2.0 | [`mxbai-embed-large`](https://ollama.com/library/mxbai-embed-large) (670 MB) | query prompt required |
| [EmbeddingGemma-300m](https://huggingface.co/google/embeddinggemma-300m) | 300M | 768 (MRL 128–768) | **2048** | 100+ | Gemma terms | [`embeddinggemma`](https://ollama.com/library/embeddinggemma) (622 MB) | `task: … \| query:` prompts |
| [all-MiniLM-L6-v2](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2) (baseline) | 22.7M | 384 | **256** | EN | Apache-2.0 | [`all-minilm`](https://ollama.com/library/all-minilm) (46 MB) | none |

Notes: Qwen3-Embedding-0.6B leads this group on MTEB-multilingual (64.33 mean on its card; the 4B/8B siblings score higher still at 2.5/4.7 GB). mxbai, EmbeddingGemma, and MiniLM have short input windows (512/2048/256 tokens) that would force chunking of longer memory entries. Ollama's embedding pages list small default context windows (e.g. nomic shows 2K) — set `num_ctx` explicitly to use a model's full window.

### Generation models (distillation / summarization)

| Model | Params | Ollama size (~Q4_K_M) | Context | License |
|---|---|---|---|---|
| [Qwen3-4B](https://huggingface.co/Qwen/Qwen3-4B) | 4.0B | [`qwen3:4b`](https://ollama.com/library/qwen3) ~2.5 GB | 32K native / 128K YaRN (Ollama tag tracks [4B-Instruct-2507](https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507): 262K native, non-thinking) | Apache-2.0 |
| [Qwen3-8B](https://huggingface.co/Qwen/Qwen3-8B) | 8.2B | `qwen3:8b` 5.2 GB | 32K native / 128K YaRN | Apache-2.0 |
| [Qwen3-14B](https://huggingface.co/Qwen/Qwen3-14B) | 14.8B | `qwen3:14b` 9.3 GB | 32K native / 128K YaRN | Apache-2.0 |
| [Llama 3.1 8B Instruct](https://huggingface.co/meta-llama/Llama-3.1-8B-Instruct) | 8B | [`llama3.1:8b`](https://ollama.com/library/llama3.1) 4.9 GB | 128K | Llama 3.1 Community |
| [Gemma 3 4B](https://huggingface.co/google/gemma-3-12b-it) | 4B | [`gemma3:4b`](https://ollama.com/library/gemma3) 3.3 GB | 128K | Gemma terms |
| [Gemma 3 12B](https://huggingface.co/google/gemma-3-12b-it) | 12B | `gemma3:12b` 8.1 GB | 128K | Gemma terms |
| [Phi-4](https://huggingface.co/microsoft/phi-4) | 14B | [`phi4:14b`](https://ollama.com/library/phi4) 9.1 GB | **16K** | MIT |
| [Phi-4-mini](https://huggingface.co/microsoft/Phi-4-mini-instruct) | 3.8B | [`phi4-mini`](https://ollama.com/library/phi4-mini) 2.5 GB | 128K | MIT |
| [Mistral 7B v0.3](https://huggingface.co/mistralai/Mistral-7B-Instruct-v0.3) | 7B | [`mistral:7b`](https://ollama.com/library/mistral) 4.4 GB | 32K | Apache-2.0 |
| [Ministral 8B (2410)](https://huggingface.co/mistralai/Ministral-8B-Instruct-2410) | 8.0B | not in official Ollama library | 128K | **Mistral Research (non-commercial)** |
| [Mistral Nemo 12B](https://huggingface.co/mistralai/Mistral-Nemo-Instruct-2407) | 12B | [`mistral-nemo:12b`](https://ollama.com/library/mistral-nemo) 7.1 GB | 128K | Apache-2.0 |

Notes: Qwen3's hybrid thinking mode can be disabled per-request (`/no_think` or `enable_thinking=False`) for fast, deterministic summarization. Phi-4's 16K context limits long transcripts; Ministral 8B's research license disqualifies it. KV-cache overhead on top of weights: for Qwen3-8B's GQA config, FP16 KV ≈ ~0.6 GB @ 4K ctx, ~1.2 GB @ 8K, ~4.6 GB @ 32K (arithmetic from card configs); `OLLAMA_KV_CACHE_TYPE=q8_0` + flash attention roughly halves it. Ollama's default `num_ctx` is 4096 — raise it deliberately.

### VRAM budget (both models resident via keep-alive)

- **Primary combo:** Qwen3-8B Q4_K_M 5.2 GB + KV @ 8K ctx ≈ 1.2 GB + ~0.7 GB buffers ≈ ~7.1 GB; Qwen3-Embedding-0.6B ≈ 0.64 GB + ~0.3 GB ≈ ~1.0 GB. **Total ≈ 8.1 GB**, well under a ~12 GB budget on the 16 GB card.
- **Fallback combo:** Qwen3-4B (2.6 GB + ~1.5 GB KV/buffers) + nomic-embed-text (0.27 GB + overhead) ≈ **~4.7 GB** total, for when dev workloads squeeze the GPU.

## .NET clients

- **Microsoft.Extensions.AI (MEAI)** — GA since May 2025; stable 10.8.0 (July 2026). Core abstractions `IChatClient` and `IEmbeddingGenerator<string, Embedding<float>>`; middleware for function invocation, caching, and OpenTelemetry GenAI tracing composes over any provider. ([Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai), [.NET Blog GA post](https://devblogs.microsoft.com/dotnet/ai-vector-data-dotnet-extensions-ga/), [NuGet](https://www.nuget.org/packages/Microsoft.Extensions.AI))
- **OllamaSharp** — Microsoft's recommended Ollama client: the first-party `Microsoft.Extensions.AI.Ollama` package is **deprecated** with NuGet notice "the OllamaSharp package is recommended". `OllamaApiClient` implements both MEAI interfaces natively; covers the full native Ollama API including pull/list/delete models with progress — enabling model-provisioning automation the OpenAI-compat surface can't do. Actively maintained (5.4.x, July 2026; ~3.1M downloads); used in Microsoft's own local-model quickstart. ([deprecated package](https://www.nuget.org/packages/Microsoft.Extensions.AI.Ollama), [OllamaSharp repo](https://github.com/awaescher/OllamaSharp), [Learn quickstart](https://learn.microsoft.com/en-us/dotnet/ai/quickstarts/chat-local-model))
- **Official OpenAI .NET SDK** (`OpenAI`, 2.12.0 stable) — README documents custom base URL via `OpenAIClientOptions.Endpoint` for "a proxy or self-hosted OpenAI-compatible LLM"; `ChatClient` + `EmbeddingClient`. `Microsoft.Extensions.AI.OpenAI` (10.8.0 stable) wraps it for MEAI, explicitly supporting "OpenAI-compatible endpoints" — the swap path to llama-server or vLLM. ([openai-dotnet](https://github.com/openai/openai-dotnet), [NuGet MEAI.OpenAI](https://www.nuget.org/packages/Microsoft.Extensions.AI.OpenAI))
- **Semantic Kernel** — not the layer to bet on: its Ollama connector is still alpha (and just wraps OllamaSharp ≥ 5.4.12), custom OpenAI endpoints are experimental (`SKEXP0010`), and Microsoft has put SK into maintenance mode with feature work moving to the **Microsoft Agent Framework**, which itself builds on MEAI `IChatClient`. ([SK Ollama connector](https://www.nuget.org/packages/Microsoft.SemanticKernel.Connectors.Ollama), [SK chat completion docs](https://learn.microsoft.com/en-us/semantic-kernel/concepts/ai-services/chat-completion/), [SK and Agent Framework](https://devblogs.microsoft.com/agent-framework/semantic-kernel-and-microsoft-agent-framework/))
- **LlamaSharp** (in-process llama.cpp bindings, no server) — reserve option only if a zero-server single-process architecture becomes a requirement; ties inference lifetime and VRAM management to the app process, needs per-platform native backend packages, 0.x API churn. ([LLamaSharp repo](https://github.com/SciSharp/LLamaSharp))

## Recommendation

**Runtime: Ollama** (runner-up: llama-server router mode).

Ollama is the only evaluated runtime where a single Compose service, one port, and one volume serve both `/v1/embeddings` and `/v1/chat/completions` against different models — with on-demand loading and idle unloading that returns VRAM to dev workloads on the shared 16 GB card. Docker-with-WSL2 GPU is first-party documented; offline operation is pull-once (or air-gapped GGUF import); MIT-licensed. llama-server's new router mode is the fallback if Mimir later wants maximal per-model control and a bare-files air-gap story, at the cost of a newer, less proven scheduler and GPU images that aren't CI-tested. vLLM is the wrong shape here (one model per instance, static ~90% VRAM preallocation, no native Windows support, experimental GGUF); TGI is in maintenance mode and generation-only.

Reference Compose service (assembled from the cited official docs):

```yaml
services:
  ollama:
    image: ollama/ollama
    ports: ["11434:11434"]
    volumes: ["ollama:/root/.ollama"]
    environment:
      OLLAMA_KEEP_ALIVE: "10m"
      OLLAMA_NO_CLOUD: "1"
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: all
              capabilities: [gpu]
volumes:
  ollama:
```

**Models:**

| Role | Primary | Alternate |
|---|---|---|
| Embeddings | **Qwen3-Embedding-0.6B** (`qwen3-embedding:0.6b`) — best quality-per-MB, MRL 32–1024 dims, 32K ctx, Apache-2.0 | nomic-embed-text-v1.5 — 274 MB, proven, 8K ctx; requires `search_document:`/`search_query:` prefixes |
| Distillation | **Qwen3-8B** (`qwen3:8b`) — 5.2 GB, `/no_think` for fast non-reasoning summarization, 32K ctx, Apache-2.0 | Qwen3-4B (`qwen3:4b`, 2507 refresh) — ~2.5 GB, same license, 262K ctx, half the memory; or Gemma 3 12B (8.1 GB, 128K ctx) if Gemma terms are acceptable |

Both primaries resident ≈ 8 GB VRAM — comfortable headroom for dev workloads. Gemma 3 4B/12B are the 128K-context options if Gemma terms are acceptable; Phi-4 (16K ctx) and Ministral 8B (research license) are ruled out.

**.NET:** code against MEAI `IChatClient` / `IEmbeddingGenerator<string, Embedding<float>>`; register **OllamaSharp's `OllamaApiClient`** for Ollama (plus its native API for startup model provisioning). If the runtime ever changes, swap in `Microsoft.Extensions.AI.OpenAI` over the official OpenAI SDK with a custom `Endpoint` — a DI change, not a code change. Skip Semantic Kernel; keep LlamaSharp in reserve.

*Final decision is confirmed on the wayfinder map (#1); this document records the research and rationale.*
