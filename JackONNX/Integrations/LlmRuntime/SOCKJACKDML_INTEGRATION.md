# SockJackDml and JackONNX

SockJackDml is the no-fallback native DirectML path for LlmRuntime GGUF model loading and LLM inference work.

JackONNX stays focused on ONNX media generation through ONNX Runtime providers. The connection point is LlmRuntime:

```text
JackLLM
  -> LlmRuntime
      -> SockJackDml / DirectMlRunner for local LLM DirectML execution
      -> JackONNX for local ONNX image, audio, and video media execution
```

JackONNX should surface SockJackDml status only as part of the broader LlmRuntime runtime view. It should not duplicate SockJackDml's GGUF model-load ABI or turn media model setup into a model acquisition workflow.

Current SockJackDml ABI facts JackONNX should respect:

- `SockJackDmlProbe` reports native DirectML availability.
- `SockJackDmlRunIdentityFloat32` provides a native DirectML identity smoke test.
- `SockJackDmlLoadModel` and `SockJackDmlUnloadModel` provide the native model-load lifetime boundary.
- `LlmRuntime.DirectMlRunner` owns the no-fallback DirectML runner behavior.

JackONNX's DirectML package remains the ONNX Runtime DirectML provider path.

