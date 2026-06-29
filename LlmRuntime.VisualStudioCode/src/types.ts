export interface WorkstationModel {
  id: string;
  name: string;
  contextTokens: number;
  outputTokens: number;
  supportsTools: boolean;
  supportsImages: boolean;
  loaded: boolean;
  available: boolean;
  enabled: boolean;
  status: string;
}

export interface OpenAiToolCall {
  index?: number;
  id?: string;
  type?: string;
  function?: {
    name?: string;
    arguments?: string;
  };
}

export interface OpenAiStreamChoice {
  delta?: {
    content?: string | null;
    reasoning?: string | null;
    reasoning_content?: string | null;
    tool_calls?: OpenAiToolCall[];
  };
  message?: {
    content?: string | null;
    reasoning?: string | null;
    reasoning_content?: string | null;
    tool_calls?: OpenAiToolCall[];
  };
}

export interface OpenAiResponse {
  choices?: OpenAiStreamChoice[];
  error?: { message?: string };
}
