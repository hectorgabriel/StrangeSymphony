# Hybrid models: Claude and a self-hosted model in one workflow

`claude.model_by_state` picks the model per tracker status. Put a routing proxy in front of Claude Code, and one Symphony instance can run some tasks on Claude and others on a model you host.

```
                                   ┌──▶ api.anthropic.com      (claude-* models)
claude --model <per status> ──▶ LiteLLM proxy
                                   └──▶ your server            (qwen3.8-27b-deep)
```

Symphony itself only chooses the `--model` flag. The proxy decides where each model name goes.

## 1. Choose the model per status

```yaml
claude:
  model: opus                      # every status not listed below
  model_by_state:
    Por Analizar: opus
    Plan Aprobado: sonnet
    Por Documentar: qwen3.8-27b-deep
```

- Keys are tracker statuses, matched the same way as `active_states` (trimmed, case-insensitive). A key that isn't an active status gets a startup warning, because no session ever starts there.
- Values are passed to `claude --model` exactly as written. Aliases (`opus`, `sonnet`) and full IDs both work.
- A blank or non-string value is a config error. If the workflow is invalid on reload, the last good config stays in effect.
- **A session keeps its model.** When a task moves from one active status to another status routed to a different model (for example Plan Aprobado → Por Probar), the worker ends the session after the current turn. The next dispatch, about one second later, starts a fresh session with the new status's model and its full prompt. When both statuses use the same model, the session continues as before.
- The dashboard event `session_started` shows the model each session uses.

On its own, `model_by_state` already lets you mix Claude models (for example Opus for planning and Sonnet for code) with your normal Claude login. The proxy below is only needed to bring in a non-Anthropic model.

## 2. Run the routing proxy

Claude Code speaks the Anthropic Messages API (`/v1/messages`). [LiteLLM](https://docs.litellm.ai/) exposes that API and routes by model name, including to OpenAI-compatible servers such as vLLM, SGLang, llama.cpp or Ollama. A minimal `litellm.yaml`:

```yaml
model_list:
  - model_name: claude-*                      # Claude Code expands opus/sonnet/haiku to claude-... IDs
    litellm_params:
      model: anthropic/claude-*
      api_key: os.environ/ANTHROPIC_API_KEY
  - model_name: qwen3.8-27b-deep
    litellm_params:
      model: openai/qwen3.8-27b-deep          # the name your server serves it under
      api_base: http://qwen-host:8000/v1
      api_key: os.environ/QWEN_API_KEY

general_settings:
  master_key: os.environ/LITELLM_MASTER_KEY
```

```bash
litellm --config litellm.yaml --port 4000
```

Check the exact provider prefixes against the LiteLLM documentation for your version.

## 3. Point Claude Code at the proxy

Set these variables in the environment Symphony starts from. Every agent session inherits them:

```bash
export ANTHROPIC_BASE_URL=http://127.0.0.1:4000
export ANTHROPIC_AUTH_TOKEN=$LITELLM_MASTER_KEY
```

On Windows, use `setx` and open a new terminal, as with the tracker token. Keep the keys out of `WORKFLOW.md`.

Claude Code also makes small background calls with its fast model. Those calls go to `claude-haiku-*` through the proxy, which the wildcard route above covers. To keep them on your server instead, also set `ANTHROPIC_DEFAULT_HAIKU_MODEL=qwen3.8-27b-deep`.

## Authentication

With `ANTHROPIC_BASE_URL` set, Claude Code doesn't use your Pro, Max or Team login. **Claude traffic is billed to the `ANTHROPIC_API_KEY` the proxy holds.** This is the "API key mode" in [IMPLEMENTATION.md](IMPLEMENTATION.md#authentication-and-accounts). It suits a shared or central host. If you'd rather keep a subscription login for the Claude statuses, run a second Symphony instance without the proxy variables for those statuses.

## Inside one task

Role agents in `.claude/agents/*.md` can set `model:` in their frontmatter. Behind the proxy, those subagents follow the same routing, so a Claude session can hand a subtask to the hosted model, or the other way round.

## Before relying on a self-hosted model

- **Context window:** Claude Code's system prompt and tool definitions are large. Serve at least 64k tokens of context, ideally 128k.
- **Tool calling:** the session depends on reliable multi-step tool use. Start with a low-risk status (documentation, for example) and watch for sessions that stall or loop.
- **Timeouts:** local inference is slower. Raise `claude.stall_timeout_ms` and `claude.turn_timeout_ms` if sessions get stopped as stalled.
- **Token counts:** if the server omits `usage` in its responses, the dashboard shows zero tokens for those sessions. Nothing else depends on it.
