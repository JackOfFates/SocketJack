# JackLLM Workstation for VS Code

Use the language models served by JackLLM Workstation directly in VS Code Chat and agent mode.

## Features

- Discovers chat-capable models from the local Workstation and adds them to VS Code's model picker.
- Streams responses through Workstation's OpenAI-compatible API.
- Supports VS Code agent tools by translating tool definitions, calls, and results.
- Carries model context, vision, tool-calling, availability, and load-state metadata into VS Code.
- Stores an optional bearer token in VS Code SecretStorage rather than settings JSON.
- Defaults to the local-first Workstation endpoint at `http://127.0.0.1:11436`.

## Use it

1. Start JackLLM Workstation.
2. Install this extension and open VS Code Chat.
3. Open the model picker, choose **Manage Models**, and enable **JackLLM Workstation** models.
4. Select a JackLLM model and use Ask, Edit, or Agent mode normally.

Run **JackLLM: Manage Workstation Provider** from the Command Palette to check status, refresh models, change the endpoint, store an API key, or open the Workstation UI.

## Requirements

- VS Code 1.104 or newer.
- JackLLM Workstation with `/api/models`, `/api/model-runtime/models`, and `/v1/chat/completions` available.

The default local setup does not require an API key. For an authenticated endpoint, run **JackLLM: Set API Key**.

## Settings

- `jackllm.endpoint`: Workstation base URL.
- `jackllm.requestTimeoutSeconds`: maximum chat request duration.
- `jackllm.defaultContextTokens`: fallback context size.
- `jackllm.defaultOutputTokens`: advertised and requested output limit.
